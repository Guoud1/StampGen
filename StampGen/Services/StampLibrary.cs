using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Clipper2Lib;

// StampGen — каталог библиотек штампов (папка stamp/).
// © Guoud (iljamakeev@yandex.ru). Все права защищены. Условия: LICENSE.md.
// v1.7: БИБЛИОТЕКИ ШТАМПОВ (вместо вшитого набора — теперь программа стартует
// СОВСЕМ БЕЗ оттисков). Пользователь импортирует библиотеки zip-архивами
// кнопкой «Добавить штампы» или в окне «Библиотеки» (импорт/экспорт/удаление).
//
// v1.8: ВЛОЖЕННЫЕ БИБЛИОТЕКИ — можно импортировать один zip, внутри которого
// лежит несколько zip-библиотек (например, сборник по фракциям: «Империум»,
// «Хаос», «Ксеносы»); каждая внутренняя библиотека добавляется отдельно,
// ровно как при импорте каждого архива по одному. Глубина вложенности до 3.
//
// Всё хранится в папке stamp/ рядом с программой:
//   stamp/<Имя>.zip   — библиотека в формате zip (перенос, экспорт, бэкап)
//   stamp/<Имя>/      — распакованная библиотека (можно править руками):
//     library.json    — метаданные: имя + список штампов (файл/имя/категория)
//     stamps/**.svg   — штампы (уже приведены к ч/б с границами 1%)
//
// СТРУКТУРА ZIP-БИБЛИОТЕКИ (простая, чтобы править «ручками»):
//   library.json          — необязателен (без него имена берутся из имён файлов,
//                           категории — из подпапок внутри stamps/)
//   stamps/*.svg          — штампы корневой категории «Штампы»
//   stamps/<Категория>/*.svg — штампы категории (папки — любые, в т.ч. русские)
//
// ИМПОРТ (правило пользователя): каждый svg переводится в ч/б — тёмные цвета
// становятся чёрным, светлые (яркость ≥ 0.85) — белым («дырки»), а на КАЖДОМ
// переходе одного цвета в другой добавляется белая граница шириной 1% от
// среднего размера оттиска: gap = 0.01 * (W + H) / 2 (в единицах viewBox,
// поэтому при масштабировании оттиска граница масштабируется вместе с ним).
// =============================================================================
namespace StampGen.Services;

public class LibInfo
{
    public string Id { get; init; } = "";        // имя папки/zip (без .zip)
    public string Name { get; set; } = "";       // отображаемое имя
    public string Dir { get; init; } = "";       // полный путь к папке библиотеки
    public string ZipPath { get; init; } = "";   // полный путь к zip-копии
    public int StampCount { get; set; }
}

public class LibStamp
{
    public string LibId { get; init; } = "";
    public string File { get; init; } = "";      // относительный путь внутри библиотеки
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public double Aspect { get; init; } = 1;

    /// <summary>Глобальный путь для StampItem.SvgPath: «&lt;libId&gt;/&lt;file&gt;».</summary>
    public string SvgPath => LibId + "/" + File;
}

public class ImportReport
{
    public bool Ok;
    public string? Error;
    public string LibName = "";
    public int Added;
    public int Failed;
    public List<string> Warnings = new();
}

/// <summary>Синглтон-каталог библиотек штампов (папка stamp/).</summary>
public class StampLibrary
{
    private readonly string _root;                                    // …/stamp
    private readonly object _lock = new();
    private List<LibInfo> _libs = new();
    private readonly Dictionary<string, List<LibStamp>> _byLib = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LibStamp> _index = new(StringComparer.Ordinal); // svgPath -> штамп
    private readonly Dictionary<string, string?> _svgCache = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public StampLibrary()
    {
        _root = StampDirFor();
        Load();
    }

    /// <summary>Тестовый/продвинутый сценарий: явная папка библиотек.</summary>
    public StampLibrary(string rootDir)
    {
        _root = rootDir;
        Load();
    }

    public static string StampDirFor()
    {
        // Приоритет: переменная окружения -> папка stamp рядом с программой.
        var custom = Environment.GetEnvironmentVariable("STAMPGEN_STAMP_DIR");
        if (!string.IsNullOrWhiteSpace(custom)) return custom;
        return Path.Combine(AppContext.BaseDirectory, "stamp");
    }

    public string RootDir => _root;

    // ================= чтение =================

    public IReadOnlyList<LibInfo> Libraries { get { lock (_lock) return _libs.ToList(); } }

    public LibInfo? Library(string libId)
    {
        lock (_lock) return _libs.FirstOrDefault(l => l.Id == libId);
    }

    public List<string> Categories(string libId)
    {
        lock (_lock)
        {
            if (!_byLib.TryGetValue(libId, out var list)) return new List<string>();
            return list.Select(s => s.Category).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>Штампы библиотеки; category == null — все категории.</summary>
    public List<LibStamp> Stamps(string libId, string? category = null)
    {
        lock (_lock)
        {
            if (!_byLib.TryGetValue(libId, out var list)) return new List<LibStamp>();
            return category == null
                ? list.ToList()
                : list.Where(s => s.Category == category).ToList();
        }
    }

    public bool Exists(string svgPath) => SvgText(svgPath) != null;

    /// <summary>URL штампа для <img>/canvas: раздаём физические файлы endpoint-ом /stamp-file.</summary>
    public static string PreviewUrl(string svgPath) => "stamp-file/" + svgPath;

    /// <summary>SVG-текст штампа по глобальному пути «&lt;libId&gt;/&lt;file&gt;» (кэшируется).</summary>
    public string? SvgText(string svgPath)
    {
        if (string.IsNullOrEmpty(svgPath)) return null;
        lock (_lock)
        {
            if (_svgCache.TryGetValue(svgPath, out var cached)) return cached;
        }
        string? text = null;
        var (libId, file) = SplitPath(svgPath);
        if (libId != null && file != null)
        {
            var lib = Library(libId);
            if (lib != null)
            {
                var full = SafeJoin(lib.Dir, file);
                if (full != null && File.Exists(full))
                {
                    try { text = File.ReadAllText(full); } catch { text = null; }
                }
            }
        }
        lock (_lock) _svgCache[svgPath] = text;
        return text;
    }

    public Task<string?> GetSvgTextAsync(string svgPath) => Task.FromResult(SvgText(svgPath));

    public Task<double> GetAspectAsync(string svgPath)
    {
        var st = Find(svgPath);
        if (st != null) return Task.FromResult(st.Aspect);
        var svg = SvgText(svgPath);
        return Task.FromResult(svg == null ? 1.0 : SvgSanitizer.AspectOnly(svg));
    }

    public LibStamp? Find(string svgPath)
    {
        lock (_lock) return _index.TryGetValue(svgPath, out var s) ? s : null;
    }

    /// <summary>v1.7: миграция старых проектов. Пути формата «icons/…» — от вшитого
    /// набора (до v1.7); после импорта библиотеки ищем штамп по ИМЕНИ ФАЙЛА.
    /// Возвращает найденный штамп или null.</summary>
    public LibStamp? ResolveByFileName(string svgPath)
    {
        if (string.IsNullOrEmpty(svgPath)) return null;
        var baseName = svgPath.Replace('\\', '/').Split('/').LastOrDefault() ?? "";
        if (baseName.Length == 0 || !baseName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return null;
        lock (_lock)
        {
            foreach (var lib in _libs)
            {
                if (!_byLib.TryGetValue(lib.Id, out var list)) continue;
                foreach (var s in list)
                    if (s.File.EndsWith("/" + baseName, StringComparison.OrdinalIgnoreCase) ||
                        s.File.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                        return s;
            }
        }
        return null;
    }

    private static (string?, string?) SplitPath(string svgPath)
    {
        var i = svgPath.IndexOf('/');
        return i <= 0 ? (null, null) : (svgPath[..i], svgPath[(i + 1)..]);
    }

    private static string? SafeJoin(string dir, string rel)
    {
        var full = Path.GetFullPath(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)));
        var baseFull = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        return full.StartsWith(baseFull, StringComparison.Ordinal) ? full : null;
    }

    // ================= сканирование папки stamp/ =================

    /// <summary>Перечитать папку stamp/ (после импорта/удаления или ручной правки).</summary>
    public void Load()
    {
        var libs = new List<LibInfo>();
        var byLib = new Dictionary<string, List<LibStamp>>(StringComparer.Ordinal);
        var index = new Dictionary<string, LibStamp>(StringComparer.Ordinal);
        try
        {
            Directory.CreateDirectory(_root);

            // 1. zip без папки — распаковываем (пользователь мог подложить архив руками).
            //    v1.8: если внутри архива есть другие zip — поднимаем их на верхний уровень:
            //    каждая внутренняя библиотека станет отдельной (как при импорте по одному).
            var pendingZips = new Queue<string>(Directory.EnumerateFiles(_root, "*.zip", SearchOption.TopDirectoryOnly));
            int guard = 0;
            while (pendingZips.Count > 0 && guard++ < 64)
            {
                var zipPath = pendingZips.Dequeue();
                var id = Path.GetFileNameWithoutExtension(zipPath);
                if (string.IsNullOrWhiteSpace(id) || id.StartsWith(".", StringComparison.Ordinal)) continue;
                var dir = Path.Combine(_root, id);
                if (!Directory.Exists(dir) || !File.Exists(Path.Combine(dir, "library.json")))
                {
                    var inner = UnpackZip(zipPath, dir);
                    foreach (var iz in inner)
                    {
                        var izName = Path.GetFileNameWithoutExtension(iz);
                        var target = UniqueFileName(_root, SafeName(izName, "библиотека") + ".zip");
                        File.Move(iz, target);
                        pendingZips.Enqueue(target);
                    }
                    // после вынимания вложенных архивов пустая папка не нужна
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        try { Directory.Delete(dir); } catch { }
                }
            }

            // 2. папки библиотек
            foreach (var dir in Directory.EnumerateDirectories(_root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var id = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
                if (id.StartsWith(".", StringComparison.Ordinal)) continue;
                try
                {
                    var (name, stamps) = LoadLibraryDir(id, dir);
                    if (stamps.Count == 0) continue;   // пустых библиотек в каталог не показываем
                    libs.Add(new LibInfo
                    {
                        Id = id,
                        Name = name,
                        Dir = dir,
                        ZipPath = Path.Combine(_root, id + ".zip"),
                        StampCount = stamps.Count
                    });
                    byLib[id] = stamps;
                    foreach (var s in stamps) index[s.SvgPath] = s;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"StampLibrary: не удалось прочитать «{id}»: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("StampLibrary: " + ex.Message);
        }
        lock (_lock)
        {
            _libs = libs;
            _byLib.Clear();
            foreach (var (k, v) in byLib) _byLib[k] = v;
            _index.Clear();
            foreach (var (k, v) in index) _index[k] = v;
            _svgCache.Clear();
        }
        Console.WriteLine($"StampLibrary: библиотек {_libs.Count}, штампов {_index.Count} (папка {_root})");
    }

    /// <summary>Распаковать zip; вернуть список zip-архивов, найденных ВНУТРИ (v1.8: контейнеры).</summary>
    private static List<string> UnpackZip(string zipPath, string target)
    {
        var innerZips = new List<string>();
        try
        {
            Directory.CreateDirectory(target);
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var e in archive.Entries)
            {
                if (string.IsNullOrEmpty(e.Name)) continue;
                var dest = Path.GetFullPath(Path.Combine(target, e.FullName));
                if (!dest.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, true);
                if (e.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    innerZips.Add(dest);
            }
            Console.WriteLine($"StampLibrary: «{Path.GetFileName(zipPath)}» распакован в {Path.GetFileName(target)}/" +
                              (innerZips.Count > 0 ? $" (найдено вложенных zip: {innerZips.Count})" : ""));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"StampLibrary: не удалось распаковать {zipPath}: {ex.Message}");
        }
        return innerZips;
    }

    private (string Name, List<LibStamp> Stamps) LoadLibraryDir(string libId, string dir)
    {
        string name = libId;
        var declared = new List<(string File, string Name, string Category)>();
        var jsonFile = Path.Combine(dir, "library.json");
        if (File.Exists(jsonFile))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(jsonFile));
                var root = doc.RootElement;
                if (root.TryGetProperty("name", out var nm) && nm.GetString() is { } n && n.Trim().Length > 0)
                    name = n.Trim();
                if (root.TryGetProperty("stamps", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in arr.EnumerateArray())
                    {
                        var file = it.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "";
                        if (file.Length == 0) continue;
                        var sname = it.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "";
                        var cat = it.TryGetProperty("category", out var sc) ? sc.GetString() ?? "" : "";
                        declared.Add((file, sname, cat));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"StampLibrary: битый library.json в «{libId}» ({ex.Message}) — читаю папку как есть.");
            }
        }

        var stamps = new List<LibStamp>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (declared.Count > 0)
        {
            foreach (var (file, sname, cat) in declared)
            {
                if (seen.Add(file))
                    stamps.Add(new LibStamp
                    {
                        LibId = libId,
                        File = file.Replace('\\', '/'),
                        Name = string.IsNullOrWhiteSpace(sname) ? Path.GetFileNameWithoutExtension(file) : sname,
                        Category = string.IsNullOrWhiteSpace(cat) ? CategoryOfFile(file) : cat,
                        Aspect = AspectOfFile(Path.Combine(dir, file))
                    });
            }
        }
        else
        {
            // library.json нет/пуст — автоскан: все *.svg внутри библиотеки
            foreach (var f in Directory.EnumerateFiles(dir, "*.svg", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
                if (rel.StartsWith(".", StringComparison.Ordinal)) continue;
                if (!seen.Add(rel)) continue;
                stamps.Add(new LibStamp
                {
                    LibId = libId,
                    File = rel,
                    Name = Path.GetFileNameWithoutExtension(f),
                    Category = CategoryOfFile(rel),
                    Aspect = AspectOfFile(f)
                });
            }
            // и сразу создаём library.json, чтобы структура была полной
            WriteLibraryJson(dir, name, stamps);
        }
        return (name, stamps);
    }

    private static string CategoryOfFile(string relFile)
    {
        // stamps/<Категория>/x.svg -> «Категория»; stamps/x.svg -> «Штампы»
        var parts = relFile.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[0].Equals("stamps", StringComparison.OrdinalIgnoreCase))
            return parts[^2];
        return "Штампы";
    }

    private static double AspectOfFile(string file)
    {
        try
        {
            if (!File.Exists(file)) return 1;
            return SvgSanitizer.AspectOnly(File.ReadAllText(file));
        }
        catch { return 1; }
    }

    private static void WriteLibraryJson(string dir, string name, List<LibStamp> stamps)
    {
        try
        {
            var dto = new LibraryJson
            {
                name = name,
                stamps = stamps.Select(s => new LibraryJson.StampEntry
                { file = s.File, name = s.Name, category = s.Category }).ToList()
            };
            File.WriteAllText(Path.Combine(dir, "library.json"),
                JsonSerializer.Serialize(dto, JsonOpts), new UTF8Encoding(false));
        }
        catch { /* некритично */ }
    }

    private class LibraryJson
    {
        public string name { get; set; } = "";
        public int format { get; set; } = 2;
        public string generator { get; set; } = "StampGen v1.8";
        public List<StampEntry> stamps { get; set; } = new();
        public class StampEntry
        {
            public string file { get; set; } = "";
            public string name { get; set; } = "";
            public string category { get; set; } = "";
        }
    }

    // ================= импорт zip-библиотеки =================

    public Task<ImportReport> ImportAsync(byte[] zipData, string fileName) => Task.Run(() => Import(zipData, fileName));

    /// <summary>Максимальная глубина вложенности zip (zip с zip-ами) — защита от архивов-матрёшек.</summary>
    private const int MaxZipDepth = 3;

    /// <summary>
    /// v1.8: импорт zip-библиотеки. Если внутри архива лежат ДРУГИЕ zip-архивы —
    /// это КОНТЕЙНЕР: каждая внутренняя библиотека импортируется отдельно,
    /// ровно как если бы пользователь добавлял каждый архив по одному
    /// (например: сборник по фракциям -> «Империум», «Хаос», «Ксеносы»…).
    /// </summary>
    private ImportReport Import(byte[] zipData, string fileName, int depth = 0)
    {
        var report = new ImportReport { Ok = false };
        string? tempDir = null;
        try
        {
            var baseName = UniqueLibId(SafeName(Path.GetFileNameWithoutExtension(fileName), "Библиотека"));
            report.LibName = baseName;

            // 1. распаковываем во временную папку
            tempDir = Path.Combine(Path.GetTempPath(), "stampgen-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            using (var ms = new MemoryStream(zipData))
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (var e in archive.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;
                    var dest = Path.GetFullPath(Path.Combine(tempDir, e.FullName));
                    if (!dest.StartsWith(tempDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    e.ExtractToFile(dest, true);
                }
            }

            // 2. v1.8: внутри есть zip-архивы? -> контейнер, импортируем каждый отдельно
            var innerZips = Directory.EnumerateFiles(tempDir, "*.zip", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (innerZips.Count > 0)
            {
                if (depth >= MaxZipDepth)
                {
                    report.Error = $"Слишком глубокая вложенность архивов (больше {MaxZipDepth}).";
                    return report;
                }
                int added = 0, failed = 0, libs = 0;
                var problems = new List<string>();
                foreach (var inner in innerZips)
                {
                    byte[] data;
                    try { data = File.ReadAllBytes(inner); }
                    catch (Exception ex) { problems.Add($"{Path.GetFileName(inner)}: не удалось прочитать ({ex.Message})"); failed++; continue; }
                    var sub = Import(data, Path.GetFileName(inner), depth + 1);
                    if (sub.Ok) { added += sub.Added; failed += sub.Failed; libs++; }
                    else
                    {
                        failed++;
                        problems.Add($"{sub.LibName}: {sub.Error}");
                    }
                    foreach (var w in sub.Warnings.Take(10)) report.Warnings.Add(w);
                }
                report.Ok = libs > 0;
                report.Added = added;
                report.Failed = failed;
                report.LibName = Path.GetFileNameWithoutExtension(fileName);
                if (report.Ok)
                    report.Warnings.Insert(0, $"Контейнер «{report.LibName}»: добавлено библиотек — {libs}, штампов — {added}.");
                foreach (var p in problems.Take(10)) report.Warnings.Add(p);
                if (!report.Ok)
                    report.Error = $"В контейнере «{report.LibName}» не удалось импортировать ни одну библиотеку.";
                return report;
            }

            // 3. метаданные (library.json необязателен)
            string libName = baseName;
            var declared = new Dictionary<string, (string Name, string Category)>(StringComparer.OrdinalIgnoreCase);
            var jsonPath = Directory.EnumerateFiles(tempDir, "library.json", SearchOption.AllDirectories).FirstOrDefault();
            if (jsonPath != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("name", out var nm) && nm.GetString() is { } n && n.Trim().Length > 0)
                        libName = n.Trim();
                    if (root.TryGetProperty("stamps", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var it in arr.EnumerateArray())
                        {
                            var f = it.TryGetProperty("file", out var ff) ? ff.GetString() ?? "" : "";
                            if (f.Length == 0) continue;
                            declared[f.Replace('\\', '/')] = (
                                it.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "",
                                it.TryGetProperty("category", out var sc) ? sc.GetString() ?? "" : "");
                        }
                }
                catch { /* битый json — работаем автосканом */ }
            }

            // 3. обрабатываем каждый svg: ч/б + граница 1% на переходах цвета
            var target = Path.Combine(_root, baseName);
            var stampsDir = Path.Combine(target, "stamps");
            Directory.CreateDirectory(stampsDir);
            var result = new List<LibStamp>();
            var svgs = Directory.EnumerateFiles(tempDir, "*.svg", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (svgs.Count == 0)
            {
                report.Error = "В архиве нет ни одного .svg-штампа. Структура: library.json + папка stamps/ с svg-файлами.";
                return report;
            }

            int curveSeg = 8;
            foreach (var src in svgs)
            {
                var rel = Path.GetRelativePath(tempDir, src).Replace('\\', '/');
                if (rel.Split('/').Any(p => p.StartsWith(".", StringComparison.Ordinal))) continue;   // .__MACOSX и пр.
                try
                {
                    var text = File.ReadAllText(src);
                    if (text.Length > 10_000_000) { report.Warnings.Add($"{rel}: файл слишком большой — пропущен."); report.Failed++; continue; }

                    var (processed, aspect, warn) = ProcessStampSvg(text, curveSeg);
                    if (processed == null)
                    {
                        report.Warnings.Add($"{rel}: {warn ?? "не удалось разобрать SVG"}");
                        report.Failed++;
                        continue;
                    }
                    if (warn != null) report.Warnings.Add($"{rel}: {warn}");

                    // целевой файл: stamps/<категория — подпапка>/<имя>.svg
                    var declaredEntry = declared.TryGetValue(rel, out var de) ? de : (Name: "", Category: "");
                    string category = declaredEntry.Category is { Length: > 0 } c ? c : CategoryOfImport(rel);
                    string fname = SafeName(Path.GetFileNameWithoutExtension(src), "штамп") + ".svg";
                    var subDir = stampsDir;
                    if (category != "Штампы" && category.Length > 0)
                        subDir = Path.Combine(stampsDir, SafeName(category, "категория"));
                    Directory.CreateDirectory(subDir);
                    var destFile = UniqueFileName(subDir, fname);
                    File.WriteAllText(destFile, processed, new UTF8Encoding(false));

                    var relTarget = Path.GetRelativePath(target, destFile).Replace('\\', '/');
                    result.Add(new LibStamp
                    {
                        LibId = baseName,
                        File = relTarget,
                        Name = declaredEntry.Name is { Length: > 0 } n2 ? n2 : Path.GetFileNameWithoutExtension(src),
                        Category = category,
                        Aspect = aspect > 0 ? aspect : 1
                    });
                    report.Added++;
                }
                catch (Exception ex)
                {
                    report.Warnings.Add($"{rel}: {ex.Message}");
                    report.Failed++;
                }
            }

            if (report.Added == 0)
            {
                report.Error = "Ни один штамп не удалось импортировать." +
                               (report.Warnings.Count > 0 ? " Причины: " + report.Warnings[0] : "");
                try { Directory.Delete(target, true); } catch { }
                return report;
            }

            // 4. library.json + zip-копия (библиотека хранится и архивом, и папкой)
            WriteLibraryJson(target, libName, result);
            try
            {
                var zipDest = Path.Combine(_root, baseName + ".zip");
                if (File.Exists(zipDest)) File.Delete(zipDest);
                using var fs = new FileStream(zipDest, FileMode.CreateNew);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                AddFileToZip(zip, "library.json", JsonSerializer.Serialize(new LibraryJson
                {
                    name = libName,
                    stamps = result.Select(s => new LibraryJson.StampEntry
                    { file = s.File, name = s.Name, category = s.Category }).ToList()
                }, JsonOpts));
                foreach (var s in result)
                {
                    var full = SafeJoin(target, s.File);
                    if (full != null && File.Exists(full))
                        zip.CreateEntryFromFile(full, s.File, CompressionLevel.Optimal);
                }
            }
            catch (Exception ex)
            {
                report.Warnings.Add("zip-копия не создана: " + ex.Message);
            }

            report.Ok = true;
            Load();   // перечитываем каталог
            return report;
        }
        catch (Exception ex)
        {
            report.Error = "Импорт не удался: " + ex.Message;
            return report;
        }
        finally
        {
            try { if (tempDir != null) Directory.Delete(tempDir, true); } catch { }
        }
    }

    private string UniqueLibId(string id)
    {
        lock (_lock)
        {
            if (!_libs.Any(l => l.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) && !Directory.Exists(Path.Combine(_root, id)))
                return id;
            for (int i = 2; ; i++)
            {
                var alt = $"{id} ({i})";
                if (!_libs.Any(l => l.Id.Equals(alt, StringComparison.OrdinalIgnoreCase)) && !Directory.Exists(Path.Combine(_root, alt)))
                    return alt;
            }
        }
    }

    private static string UniqueFileName(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            var alt = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(alt)) return alt;
        }
    }

    private static string CategoryOfImport(string relPath)
    {
        // v1.7: категория — непосредственная папка-родитель штампа из архива
        // (icons/chaos/chaos_legions/x.svg -> «chaos_legions»; stamps/x.svg -> «Штампы»).
        var parts = relPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[^1].EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            var parent = parts[^2];
            if (!parent.Equals("stamps", StringComparison.OrdinalIgnoreCase))
                return parent;
        }
        return "Штампы";
    }

    private static string SafeName(string s, string fallback)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Trim())
            if (char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')
                sb.Append(c);
        var r = sb.ToString().Trim();
        return r.Length == 0 ? fallback : r;
    }

    private static void AddFileToZip(ZipArchive zip, string entryName, string content)
    {
        var e = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    // ================= ч/б + граница 1% =================

    /// <summary>
    /// Обработка одного штампа при импорте (правило пользователя):
    ///  1) все цвета -> ч/б (тёмные — чёрным, светлые (яркость ≥ 0.85) — белым);
    ///  2) на каждом переходе одного цвета в другой — белая граница шириной
    ///     1% среднего размера оттиска: gap = 0.01 * (W + H) / 2 (viewBox-единицы).
    /// Граница добавляется белыми фигурами ПОВЕРХ геометрии — и браузер, и
    /// SvgShapes (3D/STL/PDF) вырезают её одинаково, оттиски не сливаются.
    /// </summary>
    public static (string? Svg, double Aspect, string? Warning) ProcessStampSvg(string text, int curveSeg)
    {
        string? warning = null;

        // 1. группы по цветам исходного SVG
        var grouped = SvgShapes.ParseGrouped(text, curveSeg);
        if (grouped == null) return (null, 1, "в SVG не найдено фигур для гравировки");

        double gap = 0.01 * (grouped.Box.W + grouped.Box.H) / 2.0;

        // 2. полосы-границы между КАЖДОЙ парой разных цветов
        PathsD bands = new();
        if (grouped.Ink.Count >= 2 && gap > 0)
        {
            var bboxes = grouped.Ink.Select(kv => (kv.Value, Clipper.GetBounds(kv.Value))).ToList();
            for (int i = 0; i < grouped.Ink.Count; i++)
            {
                for (int j = i + 1; j < grouped.Ink.Count; j++)
                {
                    var (pa, ba) = bboxes[i];
                    var (pb, bb) = bboxes[j];
                    // дешёвая проверка: расширенные bbox перекрываются?
                    double h = gap / 2;
                    if (ba.right + h < bb.left - h || bb.right + h < ba.left - h ||
                        ba.bottom + h < bb.top - h || bb.bottom + h < ba.top - h)
                        continue;
                    var band = SvgShapes.GapBand(pa, pb, gap);
                    if (band.Count > 0) bands.AddRange(band);
                }
            }
            if (bands.Count > 0)
            {
                bands = UnionAll(bands);
                warning = $"разных цветов: {grouped.Ink.Count} — добавлены границы {gap:0.###} ед. (1% размера)";
            }
        }

        // 3. приводим цвета файла к ч/б (структура файла сохраняется — можно править руками)
        XDocument? doc = null;
        try { doc = XDocument.Parse(text); } catch { }
        if (doc?.Root == null || !doc.Root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
            return (null, 1, "не удалось разобрать SVG");
        NormalizeDocToBlackWhite(doc);

        // 4. вшиваем белые границы поверх геометрии — элементами XDocument
        // (работает и с префиксами xmlns:svg — старая вставка строкой ломала документ)
        if (bands.Count > 0)
        {
            var d = SvgShapes.ToSvgPathD(bands);
            if (d.Length > 0)
            {
                var ns = doc.Root.Name.Namespace;
                var g = new XElement(ns + "g",
                    new XAttribute("fill", "#ffffff"),
                    new XAttribute("stroke", "none"),
                    new XElement(ns + "path", new XAttribute("d", d)));
                doc.Root.Add(g);
            }
        }
        string outSvg = doc.ToString(SaveOptions.DisableFormatting);

        double aspect = SvgSanitizer.AspectOnly(outSvg);
        return (outSvg, aspect, warning);
    }

    private static PathsD UnionAll(PathsD subject)
    {
        var cd = new ClipperD(2);
        cd.AddSubject(subject);
        var sol = new PathsD();
        cd.Execute(ClipType.Union, FillRule.NonZero, sol);
        return sol;
    }

    private static readonly string[] RemovedTags =
    {
        "script", "foreignObject", "image", "defs", "linearGradient", "radialGradient",
        "pattern", "mask", "filter", "clipPath", "text", "tspan", "style", "use"
    };

    /// <summary>Цвета файла -> ч/б: тёмные «#000000», светлые «#ffffff» (не «none» —
    /// белое остаётся «дыркой» и в браузере, и в 3D-парсере). Прочее как SvgSanitizer.</summary>
    public static string? NormalizeToBlackWhite(string content)
    {
        try
        {
            var doc = XDocument.Parse(content);
            if (doc.Root == null || !doc.Root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase)) return null;
            NormalizeDocToBlackWhite(doc);
            return doc.ToString(SaveOptions.DisableFormatting);
        }
        catch { return null; }
    }

    private static void NormalizeDocToBlackWhite(XDocument doc)
    {
        var root = doc.Root!;
        foreach (var el in root.Descendants().Where(e => RemovedTags.Contains(e.Name.LocalName)).ToList())
            el.Remove();

        foreach (var el in root.Descendants().ToList())
        {
            // почти прозрачные элементы удаляем целиком (иначе после снятия opacity станут чёрными)
            double? op = ReadOpacity(el.Attribute("opacity")?.Value);
            if (op is < 0.05) { el.Remove(); continue; }

            foreach (var attr in el.Attributes().ToList())
            {
                var n = attr.Name.LocalName.ToLowerInvariant();
                if (n is "opacity" or "fill-opacity" or "stroke-opacity" or "filter" or "mask" or "clip-path")
                    attr.Remove();
                else if (n == "fill" || n == "stroke")
                    attr.Value = BwColor(attr.Value);
                else if (n == "style")
                    attr.Value = BwStyle(attr.Value);
            }
        }
    }

    private static double? ReadOpacity(string? raw)
        => raw != null && double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string BwStyle(string style)
    {
        var parts = style.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var outp = new List<string>();
        foreach (var p0 in parts)
        {
            var p = p0.Trim();
            int ci = p.IndexOf(':');
            if (ci <= 0) continue;
            var name = p[..ci].Trim().ToLowerInvariant();
            var val = p[(ci + 1)..].Trim();
            if (name is "opacity" or "fill-opacity" or "stroke-opacity") continue;
            if (name is "fill" or "stroke") val = BwColor(val);
            outp.Add($"{name}:{val}");
        }
        return string.Join("; ", outp);
    }

    /// <summary>Тёмный -> #000000, светлый (≥0.85) -> #ffffff, none/transparent -> none.</summary>
    public static string BwColor(string value)
    {
        var v = value.Trim();
        if (v.Length == 0) return "none";
        if (v.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return "none";
        if (v.StartsWith("url(")) return "#000000";
        double lum = SvgShapes.Luminance(v) ?? 0;   // незнакомый формат — считаем чернилами
        return lum >= 0.85 ? "#ffffff" : "#000000";
    }

    // ================= экспорт / удаление =================

    /// <summary>Собрать zip библиотеки (library.json + stamps/**) для скачивания.</summary>
    public byte[]? ExportZip(string libId)
    {
        var lib = Library(libId);
        if (lib == null) return null;
        try
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                foreach (var f in Directory.EnumerateFiles(lib.Dir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(lib.Dir, f).Replace('\\', '/');
                    if (rel.Split('/').Any(p => p.StartsWith(".", StringComparison.Ordinal))) continue;
                    zip.CreateEntryFromFile(f, rel, CompressionLevel.Optimal);
                }
            }
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"StampLibrary: экспорт «{libId}» не удался: {ex.Message}");
            return null;
        }
    }

    /// <summary>Удалить библиотеку (папка + zip). Вернуть false, если не нашлась.</summary>
    public bool Delete(string libId)
    {
        var lib = Library(libId);
        if (lib == null) return false;
        try { if (Directory.Exists(lib.Dir)) Directory.Delete(lib.Dir, true); } catch (Exception ex) { Console.Error.WriteLine("StampLibrary: " + ex.Message); }
        try { if (File.Exists(lib.ZipPath)) File.Delete(lib.ZipPath); } catch (Exception ex) { Console.Error.WriteLine("StampLibrary: " + ex.Message); }
        Load();
        return true;
    }

    /// <summary>Переименовать библиотеку (имя показывается в списке; пишется в library.json).</summary>
    public bool Rename(string libId, string newName)
    {
        var lib = Library(libId);
        newName = newName.Trim();
        if (lib == null || newName.Length == 0) return false;
        try
        {
            var jsonFile = Path.Combine(lib.Dir, "library.json");
            LibraryJson dto;
            if (File.Exists(jsonFile))
            {
                dto = JsonSerializer.Deserialize<LibraryJson>(File.ReadAllText(jsonFile), JsonOpts) ?? new LibraryJson();
            }
            else dto = new LibraryJson { name = lib.Name };
            dto.name = newName;
            File.WriteAllText(jsonFile, JsonSerializer.Serialize(dto, JsonOpts), new UTF8Encoding(false));
            Load();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("StampLibrary: " + ex.Message);
            return false;
        }
    }
}
