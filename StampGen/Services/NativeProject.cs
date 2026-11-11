using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// StampGen — формат нативного проекта (.stampgen): сохранение и загрузка.
// © Guoud (iljamakeev@yandex.ru). Все права защищены. Условия: LICENSE.md.
namespace StampGen.Services;

/// <summary>«Родной» формат проекта: ZIP-контейнер .stampgen = project.json + svg/*.svg + text/*.svg.</summary>
public static class NativeProject
{
    public const string Extension = ".stampgen";
    public const string Mime = "application/zip";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private class Dto
    {
        public string App { get; set; } = "StampGen";
        public int Format { get; set; } = 2;
        public string Name { get; set; } = "";
        public double W { get; set; }
        public double D { get; set; }
        public double H { get; set; }
        public bool TopChamfer { get; set; }
        public double ChH { get; set; }
        public double ChA { get; set; }
        public double Margin { get; set; }           // формат 1: только верхнее поле
        public double MarginTop { get; set; }
        public double MarginBottom { get; set; }
        public double MarginLeft { get; set; }
        public double MarginRight { get; set; }
        public double ReliefMm { get; set; }
        public string ReliefUnit { get; set; } = "micron";
        public double ArrangeGap { get; set; } = 1.5;   // v1.3: минимальный зазор авто-расстановки
        public List<StampDto> Stamps { get; set; } = new();
    }

    private class StampDto
    {
        public string Source { get; set; } = "base";
        public string Kind { get; set; } = "icon";
        public string Rass { get; set; } = "";
        public string Otryad { get; set; } = "";
        public string Name { get; set; } = "";
        public string SvgPath { get; set; } = "";
        public string PreviewPath { get; set; } = "";
        public string CustomSvgId { get; set; } = "";
        public string CustomFileName { get; set; } = "";
        public string Text { get; set; } = "";
        public string FontKey { get; set; } = "sans";
        public bool TextBold { get; set; }
        public double Aspect { get; set; } = 1;
        public double W { get; set; }
        public double H { get; set; }
        public bool KeepProportions { get; set; } = true;
        public double Px { get; set; }
        public double Py { get; set; }
        public double Rot { get; set; }
        public bool Mirror { get; set; }
        public double? DepthMm { get; set; }
        public bool RoughBottom { get; set; }
        public int RoughDeltaUm { get; set; }
        public bool CopiesEnabled { get; set; }
        public int CopiesCount { get; set; }
        public double CopiesStep { get; set; }
        public bool CopiesStepPercent { get; set; }
        public double CopiesSpacing { get; set; }
        public double RowAngleDeg { get; set; }
    }

    public static byte[] Save(StampState state)
    {
        var dto = new Dto
        {
            Name = state.ProjectName,
            W = state.Plate.Width, D = state.Plate.Depth, H = state.Plate.Height,
            TopChamfer = state.Plate.TopChamfer,
            ChH = state.Plate.BottomChamferHeight, ChA = state.Plate.BottomChamferAngleDeg,
            Margin = state.Plate.MarginTop,
            MarginTop = state.Plate.MarginTop,
            MarginBottom = state.Plate.MarginBottom,
            MarginLeft = state.Plate.MarginLeft,
            MarginRight = state.Plate.MarginRight,
            ReliefMm = state.ReliefMm,
            ReliefUnit = state.ReliefUnit == ReliefUnit.Micron ? "micron" : "mm",
            ArrangeGap = state.ArrangeGap
        };
        foreach (var st in state.Stamps)
            dto.Stamps.Add(new StampDto
            {
                Source = st.Source == StampSource.Base ? "base" : "custom",
                Kind = st.Kind == StampKind.Text ? "text" : "icon",
                Rass = st.Rass, Otryad = st.Otryad, Name = st.Name,
                SvgPath = st.SvgPath, PreviewPath = st.PreviewPath,
                CustomSvgId = st.CustomSvgId, CustomFileName = st.CustomFileName,
                Text = st.Text, FontKey = st.FontKey, TextBold = st.TextBold,
                Aspect = st.Aspect, W = st.Width, H = st.Height,
                KeepProportions = st.KeepProportions, Px = st.PosX, Py = st.PosY,
                Rot = st.RotationDeg, Mirror = st.Mirrored,
                DepthMm = st.DepthMm, RoughBottom = st.RoughBottom, RoughDeltaUm = st.RoughDeltaUm,
                CopiesEnabled = st.CopiesEnabled, CopiesCount = st.CopiesCount,
                CopiesStep = st.CopiesStep, CopiesStepPercent = st.CopiesStepPercent,
                CopiesSpacing = st.CopiesSpacing, RowAngleDeg = st.RowAngleDeg
            });

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry("project.json", CompressionLevel.Optimal);
            using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                w.Write(JsonSerializer.Serialize(dto, JsonOpts));

            foreach (var (id, svg) in state.CustomSvgs)
            {
                var e = zip.CreateEntry($"svg/custom-{id}.svg", CompressionLevel.Optimal);
                using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false)))
                    w.Write(svg);
            }

            foreach (var (id, svg) in state.TextSvgs)
            {
                var e = zip.CreateEntry($"text/{id}.svg", CompressionLevel.Optimal);
                using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false)))
                    w.Write(svg);
            }
        }
        return ms.ToArray();
    }

    /// <summary>Открыть .stampgen (формат 1 или 2). Бросает InvalidOperationException с понятной причиной.</summary>
    public static void Open(byte[] data, StampState state)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read); }
        catch { throw new InvalidOperationException("Файл не является архивом проекта (.stampgen)."); }

        var entry = zip.GetEntry("project.json");
        if (entry == null)
        {
            zip.Dispose();
            throw new InvalidOperationException("В архиве нет project.json — это не файл проекта StampGen.");
        }
        string json;
        using (var r = new StreamReader(entry.Open(), Encoding.UTF8)) json = r.ReadToEnd();
        Dto? dto;
        try { dto = JsonSerializer.Deserialize<Dto>(json, JsonOpts); }
        catch (Exception ex) { zip.Dispose(); throw new InvalidOperationException($"Ошибка project.json: {ex.Message}"); }
        if (dto == null) { zip.Dispose(); throw new InvalidOperationException("project.json пустой."); }

        state.ProjectName = string.IsNullOrWhiteSpace(dto.Name) ? "Проект" : dto.Name;
        // формат 1 хранил только верхнее поле
        double mt = dto.MarginTop > 0 ? dto.MarginTop : dto.Margin;
        state.Plate = new PlateParams
        {
            Width = dto.W, Depth = dto.D, Height = dto.H,
            TopChamfer = dto.TopChamfer,
            BottomChamferHeight = dto.ChH, BottomChamferAngleDeg = dto.ChA,
            MarginTop = mt,
            MarginBottom = dto.MarginBottom,
            MarginLeft = dto.MarginLeft,
            MarginRight = dto.MarginRight
        };
        // v1.3: поля — единый параметр на всю подложку; в старых файлах стороны могли
        // различаться — унифицируем максимумом, чтобы ни один оттиск не вылез за край.
        state.Plate.UnifyMargins();
        state.ArrangeGap = dto.ArrangeGap > 0 ? dto.ArrangeGap : 1.5;
        state.ReliefMm = dto.ReliefMm > 0 ? dto.ReliefMm : 0.02;
        state.ReliefUnit = dto.ReliefUnit == "mm" ? ReliefUnit.Mm : ReliefUnit.Micron;

        state.Stamps.Clear();
        state.CustomSvgs.Clear();
        state.TextSvgs.Clear();

        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("svg/", StringComparison.Ordinal)))
        {
            var id = Path.GetFileNameWithoutExtension(e.FullName).Replace("custom-", "", StringComparison.Ordinal);
            using var r = new StreamReader(e.Open(), Encoding.UTF8);
            state.CustomSvgs[id] = r.ReadToEnd();
        }

        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("text/", StringComparison.Ordinal)))
        {
            var id = Path.GetFileNameWithoutExtension(e.FullName);
            using var r = new StreamReader(e.Open(), Encoding.UTF8);
            state.TextSvgs[id] = r.ReadToEnd();
        }

        foreach (var sd in dto.Stamps)
        {
            var st = new StampItem
            {
                Source = sd.Source == "custom" ? StampSource.Custom : StampSource.Base,
                Kind = sd.Kind == "text" ? StampKind.Text : StampKind.Icon,
                Rass = sd.Rass, Otryad = sd.Otryad, Name = sd.Name,
                SvgPath = sd.SvgPath, PreviewPath = sd.PreviewPath,
                CustomSvgId = sd.CustomSvgId, CustomFileName = sd.CustomFileName,
                Text = string.IsNullOrWhiteSpace(sd.Text) ? "Подпись" : sd.Text,
                FontKey = string.IsNullOrWhiteSpace(sd.FontKey) ? "sans" : sd.FontKey,
                TextBold = sd.TextBold,
                Aspect = sd.Aspect > 0 ? sd.Aspect : 1,
                Width = sd.W, Height = sd.H, KeepProportions = sd.KeepProportions,
                PosX = sd.Px, PosY = sd.Py, RotationDeg = sd.Rot, Mirrored = sd.Mirror,
                DepthMm = sd.DepthMm, RoughBottom = sd.RoughBottom, RoughDeltaUm = sd.RoughDeltaUm,
                CopiesEnabled = sd.CopiesEnabled, CopiesCount = sd.CopiesCount,
                CopiesStep = sd.CopiesStep, CopiesStepPercent = sd.CopiesStepPercent,
                CopiesSpacing = sd.CopiesSpacing, RowAngleDeg = sd.RowAngleDeg
            };
            state.Stamps.Add(st);
        }
        zip.Dispose();
    }
}
