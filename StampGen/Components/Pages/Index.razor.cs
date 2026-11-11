using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using StampGen.Services;

// StampGen — логика главной страницы (редактор пластины, оттиски, экспорт).
// © Guoud (iljamakeev@yandex.ru). Все права защищены. Условия: LICENSE.md.
namespace StampGen.Components.Pages;

/// <summary>Логика главной страницы генератора.</summary>
public partial class Index : ComponentBase, IDisposable
{
    /// <summary>v1.8: спец-значение выбора библиотеки «Все библиотеки» в окне добавления оттисков.</summary>
    public const string SelAllLibsValue = "ALL";
    [Inject] public StampState S { get; set; } = default!;
    [Inject] public StampLibrary Lib { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;

    // --- UI-состояние ---
    private bool _saveAsOpen, _openOpen, _addOpen, _stlOpen, _aboutOpen;
    private bool _libsOpen, _newOpen;              // v1.7: окно «Библиотеки» и подтверждение «Новый»
    private bool _importBusy;                      // v1.7: идёт импорт zip-библиотеки
    private ImportReport? _importReport;           // v1.7: отчёт последнего импорта
    private string _saveAsFormat = "native";
    private string _saveAsName = "";
    private string? _openError;
    private string? _reliefMsg;
    private bool _disposed;
    private bool _prefsLoaded;

    private string _selLib = "", _selCat = "";
    private readonly HashSet<string> _selected = new();
    private readonly List<string> _addMessages = new();
    private Dictionary<string, LibStamp> _pathIndex = new();

    private class CustomPending
    {
        public string TempId = Guid.NewGuid().ToString("N");
        public string Name = "";
        public double Aspect = 1;
        public string DataUrl = "";
        public string Svg = "";
    }
    private readonly List<CustomPending> _customPending = new();

    /// <summary>Миниатюры своих SVG для карточек (customSvgId -> data-url).</summary>
    private readonly Dictionary<string, string> _customThumbs = new();

    /// <summary>Сообщения по конкретному оттиску (текст/глубина/шероховатость).</summary>
    private readonly Dictionary<Guid, string> _stampMsgs = new();

    // ==================== undo/redo (v1.3) ====================
    // Стек снимков «ПОСЛЕ изменения» (StampState.Clone сохраняет Id оттисков):
    //   _items[0] — исходное состояние, каждое завершённое изменение добавляет пост-снимок;
    //   undo — шаг назад по снимкам, redo — шаг вперёд. Серия однотипных тиков слайдера
    //   схлопывается в один шаг (перезаписью верхнего снимка).
    private class UndoStack
    {
        private readonly List<StampState> _items = new();
        private int _pos = -1;                       // текущий снимок (= актуальное состояние)
        private const int MaxDepth = 60;
        private (string Tag, DateTime Utc)? _lastCoalesce;

        public bool CanUndo => _pos > 0;
        public bool CanRedo => _pos < _items.Count - 1;

        /// <summary>Запомнить состояние ПОСЛЕ изменения (или базовое при старте/открытии).</summary>
        public void Push(StampState postState)
        {
            if (_pos < _items.Count - 1) _items.RemoveRange(_pos + 1, _items.Count - _pos - 1); // отрезаем redo-ветку
            _items.Add(postState);
            if (_items.Count > MaxDepth) _items.RemoveAt(0);
            _pos = _items.Count - 1;
            _lastCoalesce = null;
        }

        /// <summary>Серия однотипных изменений (движение слайдера) — один шаг отмены:
        /// пока тот же тег и пауза меньше окна — верхний пост-снимок перезаписывается.</summary>
        public void PushCoalesced(Func<StampState> snapshot, string tag)
        {
            var now = DateTime.UtcNow;
            if (_lastCoalesce is { } lc && lc.Tag == tag && (now - lc.Utc).TotalMilliseconds < 1500
                && _pos >= 0 && _pos == _items.Count - 1)
            {
                _items[_pos] = snapshot();   // серия слайдера = один шаг отмены
                _lastCoalesce = (tag, now);
                return;
            }
            _lastCoalesce = (tag, now);
            Push(snapshot());
        }

        public StampState? Undo()
        {
            if (!CanUndo) return null;
            _lastCoalesce = null;
            return _items[--_pos];
        }

        public StampState? Redo()
        {
            if (!CanRedo) return null;
            _lastCoalesce = null;
            return _items[++_pos];
        }

        public void Clear() { _items.Clear(); _pos = -1; _lastCoalesce = null; }
    }

    private readonly UndoStack _undo = new();
    private bool _canUndo, _canRedo;

    // ==================== мульти-выбор оттисков (v1.3) ====================
    /// <summary>Оттиски, отмеченные галочками: правка параметра одного применяется ко всем.</summary>
    private readonly HashSet<Guid> _batchSel = new();

    // --- STL ---
    private string _stlName = "plate.stl";
    private int _stlQuality = 3;
    private double _stlProgress;
    private bool _stlBusy;
    private bool _stlDone;      // v1.5: STL скачан — можно закрыть окно кнопкой «Готово»
    private string _stlStatus = "";
    private string _stlPhase = "";
    private double _estSec = -1;
    private string _stlElapsedText = "";
    private DateTime _stlStartedUtc;
    private PeriodicTimer? _stlTick;
    private CancellationTokenSource? _stlCts;   // v1.6: отмена расчёта STL

    // --- v2.1: кэш ТОЧНОГО меша STL (глубина рельефа без усиления) ---
    // Если параметры проекта не менялись с прошлого сохранения, STL скачивается
    // из памяти мгновенно — без повторного построения. Отпечаток геометрии
    // пересчитывается при каждом сохранении: не совпал — строим заново.
    private float[]? _stlMeshCache;
    private string _stlCacheSig = "";
    private MeshReport? _stlCacheReport;

    // --- v1.6: построение 3D-вида на СЕРВЕРЕ (раньше считал браузер и «вис») ---
    private bool _viewBusy;
    private bool _plateOnly3d;           // показана только пластина (до нажатия «Обновить»)
    private string _viewPhase = "";
    private double _viewPct;
    private string _viewElapsed = "";
    private DateTime _viewStartedUtc;
    private PeriodicTimer? _viewTick;
    private CancellationTokenSource? _viewCts;
    private string? _viewError;

    // v1.8: кэш готового меша. Первая генерация — по кнопке «Обновить»; результат
    // ХРАНИТСЯ до следующего нажатия той же кнопки. Переключение 2D<->3D (и уход в
    // настройки) просто показывает готовый меш заново — ничего не пересчитывается.
    // Сброс — только при «Новый»/открытии проекта/восстановлении автосохранения.
    private float[]? _lastMesh;
    private (double W, double D, double H)? _lastMeshPlate;
    private int _lastMeshQuality, _lastMeshExag;

    // v2.1: отпечаток геометрии и отчёт для меша 3D-вида — по ним при сохранении STL
    // проверяем, можно ли взять уже построенную модель из памяти (усиление x1 = точная модель)
    private string? _viewMeshSig;
    private MeshReport? _viewMeshReport;

    /// <summary>v2.1: краткий отчёт построения меша (для статуса и кэша STL).</summary>
    private sealed class MeshReport
    {
        public long Triangles;
        public bool Watertight;
        public long OpenBefore;
        public long OpenAfter;
        public static MeshReport From(MeshBuildResult m) => new()
        { Triangles = m.Triangles, Watertight = m.Watertight, OpenBefore = m.OpenEdgesBefore, OpenAfter = m.OpenEdgesAfter };
    }

    /// <summary>Сообщение при неудаче 3D-отрисовки (просьба пользователя).
    /// Программа останавливает отрисовку и извиняется вместо краха.</summary>
    private const string View3dApology =
        "Не получилось построить 3D-модель на этих настройках — компьютеру тяжело. " +
        "Поставьте качество «черновик», усиление рельефа x10 и уменьшите число или сложность оттисков.";

    private static int CurveSegmentsOf(int quality) => quality switch { 1 => 4, 2 => 8, 3 => 14, _ => 24 };

    // --- автосохранение и настройки ---
    private bool _rendered;
    private bool _prefsDirty = true;
    private bool _dirtySinceAutosave;
    private DateTime _lastAutosaveUtc = DateTime.UtcNow;
    private string _autosaveStatus = "";
    private bool _autosaveFound;
    private string _autosaveTime = "";
    private string _autosaveName = "";
    private Guid? _flashCard;   // подсветка карточки после клика на схеме
    private DotNetObjectReference<Index>? _self;

    // ==================== жизненный цикл ====================

    protected override void OnInitialized()
    {
        S.OnChange += OnStateChanged;
        RebuildPathIndex();
        // v1.8: стартуем с «Все библиотеки» и «Все категории»
        _selLib = SelAllLibsValue;
        _selCat = "";
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _rendered = true;
            _self = DotNetObjectReference.Create(this);
            try { await JS.InvokeVoidAsync("stampGen3d.init", "view3d", _self); }
            catch (JSException) { /* 3D недоступен */ }
            try { await JS.InvokeVoidAsync("stampGen.attach2d", "canvas2d", _self); }
            catch (JSException) { /* события 2D недоступны */ }
            try { await JS.InvokeVoidAsync("stampGen.attachHotkeys", _self); }
            catch (JSException) { /* горячие клавиши недоступны */ }

            // настройки (пластина/рельеф/вид) из прошлой сессии + проверка автосохранения
            await LoadPrefsSafe();
            await CheckAutosaveSafe();
            _prefsLoaded = true;
            // базовый снимок для undo: без него первый Ctrl+Z не срабатывает (_pos==0)
            _undo.Push(S.Clone());
            SyncUndoButtons();
            StateHasChanged();   // перерисовать UI с применёнными настройками

            await Redraw2D();
            if (S.Is3D) await Update3D();
            StartBackgroundLoops();
        }
        else if (_rendered && !S.Is3D)
        {
            // после каждого рендера в 2D перерисовываем (на случай смены размеров контейнера)
            await Redraw2D();
        }
    }

    private void StartBackgroundLoops()
    {
        _ = BackgroundLoopAsync();
    }

    /// <summary>Фоновый цикл: сохранение настроек (debounce) и автосохранение проекта.</summary>
    private async Task BackgroundLoopAsync()
    {
        while (!_disposed)
        {
            await Task.Delay(2000);
            try
            {
                if (_prefsDirty && _prefsLoaded)
                {
                    _prefsDirty = false;
                    var json = PrefsJson();
                    await InvokeAsync(async () => await JS.InvokeVoidAsync("stampGen.prefsSet", json));
                }
                if (S.AutosaveSec > 0 && _dirtySinceAutosave &&
                    (DateTime.UtcNow - _lastAutosaveUtc).TotalSeconds >= S.AutosaveSec)
                {
                    var data = NativeProject.Save(S);
                    if (data.Length <= 4_500_000)
                    {
                        var b64 = Convert.ToBase64String(data);
                        await InvokeAsync(async () =>
                        {
                            await JS.InvokeVoidAsync("stampGen.autosaveSave", b64, S.ProjectName);
                            _dirtySinceAutosave = false;
                            _lastAutosaveUtc = DateTime.UtcNow;
                            _autosaveStatus = "сохранено в " + DateTime.Now.ToString("HH:mm:ss");
                            StateHasChanged();
                        });
                    }
                    else
                    {
                        _dirtySinceAutosave = false;
                        _autosaveStatus = "проект слишком большой для автосохранения";
                        await InvokeAsync(StateHasChanged);
                    }
                }
            }
            catch (JSDisconnectedException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception) { /* сеть/переподключение — попробуем в следующий раз */ }
        }
    }

    private void OnStateChanged()
    {
        InvokeAsync(async () =>
        {
            if (_rendered)
            {
                // v1.8: Dirty3D здесь больше не ставим — уведомления приходят и на
                // переключение вида/прочие действия, которые модель не меняют.
                // Признак «есть изменения» выставляет N() — только при реальной правке.
                await Redraw2D();
            }
            StateHasChanged();
        });
    }

    /// <summary>Обновить доступность кнопок Отменить/Вернуть.</summary>
    private void SyncUndoButtons()
    {
        _canUndo = _undo.CanUndo;
        _canRedo = _undo.CanRedo;
    }

    /// <summary>Снимок состояния (пост-снимок для стека undo).</summary>
    private StampState Snapshot() => S.Clone();

    private void N()
    {
        S.Dirty3D = true;
        _prefsDirty = true;
        _dirtySinceAutosave = true;
        SyncUndoButtons();
        S.Notify();
    }

    // ==================== undo / redo (v1.3) ====================

    public void Undo()
    {
        var snap = _undo.Undo();
        if (snap == null) return;
        ApplySnapshot(snap);
    }

    public void Redo()
    {
        var snap = _undo.Redo();
        if (snap == null) return;
        ApplySnapshot(snap);
    }

    private void ApplySnapshot(StampState snap)
    {
        // Восстанавливаем всё, кроме вида (Is3D) и имени файла
        bool is3d = S.Is3D;
        string projFile = S.ProjectFileName ?? "";
        int autosaveSec = S.AutosaveSec;
        var restored = snap.Clone();
        S.ProjectName = restored.ProjectName;
        S.ProjectFileName = projFile;
        S.Plate = restored.Plate;
        S.ReliefMm = restored.ReliefMm;
        S.ReliefUnit = restored.ReliefUnit;
        S.Quality = restored.Quality;
        S.ReliefExaggeration = restored.ReliefExaggeration;
        S.AutosaveSec = autosaveSec;
        S.ArrangeGap = restored.ArrangeGap;
        S.Stamps.Clear();
        S.Stamps.AddRange(restored.Stamps);
        S.CustomSvgs.Clear();
        foreach (var (k, v) in restored.CustomSvgs) S.CustomSvgs[k] = v;
        S.TextSvgs.Clear();
        foreach (var (k, v) in restored.TextSvgs) S.TextSvgs[k] = v;
        S.Warnings.Clear();
        S.Warnings.AddRange(restored.Warnings);
        S.Is3D = is3d;
        _batchSel.RemoveWhere(id => S.Stamps.All(x => x.Id != id));   // мёртвые галочки — долой
        EnsureAllTextSvgs();
        N();
        StateHasChanged();
    }

    /// <summary>Горячие клавиши из JS: Ctrl+Z — отмена, Ctrl+Y / Ctrl+Shift+Z — возврат.</summary>
    [JSInvokable]
    public Task UndoHotkey() => InvokeAsync(() => { Undo(); StateHasChanged(); });

    [JSInvokable]
    public Task RedoHotkey() => InvokeAsync(() => { Redo(); StateHasChanged(); });

    private void RebuildPathIndex()
    {
        _pathIndex = new Dictionary<string, LibStamp>(StringComparer.Ordinal);
        foreach (var lib in Lib.Libraries)
            foreach (var st in Lib.Stamps(lib.Id))
                _pathIndex[st.SvgPath] = st;
    }

    // ==================== отрисовка ====================

    private async Task Redraw2D()
    {
        try
        {
            var (inst, _) = LayoutSolver.BuildLayout(S);
            await LayoutSolver.FillSourcesAsync(S, inst, Lib, for3d: false);
            // v1.5: batch-выбор передаём в схему — выбранные оттиски подсвечиваются зелёным
            var json = LayoutSolver.SceneJson(S, inst, for3d: false, _batchSel);
            await JS.InvokeVoidAsync("stampGen.draw2d", "canvas2d", json);
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
    }

    private async Task Update3D()
    {
        if (_viewBusy) return;
        _viewBusy = true;
        _viewError = null;
        _viewPhase = "Подготовка…";
        _viewPct = 0;
        _viewElapsed = "0:00";
        _viewStartedUtc = DateTime.UtcNow;
        _viewCts?.Dispose();
        _viewCts = new CancellationTokenSource();
        var cts = _viewCts;
        StartViewTimer();
        StateHasChanged();
        try
        {
            var (inst, _) = LayoutSolver.BuildLayout(S);
            await LayoutSolver.FillSourcesAsync(S, inst, Lib, for3d: true);
            var plate = S.Plate.Clone();
            var relief = S.ReliefMm;
            var quality = S.Quality;
            var exag = (double)Math.Max(1, S.ReliefExaggeration);
            // v1.6: меш строит СЕРВЕР (все ядра ПК), браузер только показывает готовое
            var result = await Task.Run(() => MeshGen.Build(plate, inst, relief,
                new MeshGen.Options
                {
                    CurveSegments = CurveSegmentsOf(quality),
                    DepthScale = exag,
                    MaxTriangles = 1_600_000,
                    TimeoutSec = 240
                },
                (phase, pct) => _ = InvokeAsync(() =>
                {
                    if (phase != _viewPhase || Math.Abs(pct - _viewPct) > 0.005)
                    {
                        _viewPhase = phase;
                        _viewPct = pct;
                        StateHasChanged();
                    }
                }), cts.Token));
            _estSec = result.BuildMs / 1000 * 1.5 + result.Triangles / 400000.0;
            S.Dirty3D = false;
            _plateOnly3d = false;
            // v1.8: запоминаем готовый меш — до следующего нажатия «Обновить»
            _lastMesh = result.Positions;
            _lastMeshPlate = (S.Plate.Width, S.Plate.Depth, S.Plate.Height);
            _lastMeshQuality = S.Quality;
            _lastMeshExag = (int)S.ReliefExaggeration;
            // v2.1: отпечаток геометрии этого меша — если сохранение STL будет
            // с теми же параметрами и усилением x1, модель возьмём из памяти
            _viewMeshSig = GeometrySignature(inst, CurveSegmentsOf(S.Quality));
            _viewMeshReport = MeshReport.From(result);
            await ShowMeshAsync(result.Positions);
        }
        catch (OperationCanceledException)
        {
            _viewPhase = "Отменено";
            try { await JS.InvokeVoidAsync("stampGen3d.clear"); } catch (JSDisconnectedException) { }
        }
        catch (MeshTooHeavyException ex)
        {
            _viewError = View3dApology + " (" + ex.Message + ")";
            try { await JS.InvokeVoidAsync("stampGen3d.clear"); } catch (JSDisconnectedException) { }
        }
        catch (JSDisconnectedException) { }
        catch (Exception ex)
        {
            _viewError = View3dApology + " (" + ex.Message + ")";
            try { await JS.InvokeVoidAsync("stampGen3d.clear"); } catch (JSDisconnectedException) { }
        }
        finally
        {
            StopViewTimer();
            _viewBusy = false;
            StateHasChanged();
        }
    }

    private async Task CancelView3d()
    {
        _viewCts?.Cancel();
    }

    private void DismissView3dError()
    {
        _viewError = null;
        StateHasChanged();
    }

    /// <summary>JS: WebGL-контекст потерян (GPU не справился) — останавливаем и извиняемся.</summary>
    [JSInvokable]
    public Task View3dContextLost() => InvokeAsync(() =>
    {
        _viewError = View3dApology;
        StateHasChanged();
    });

    /// <summary>Отправить готовый меш (float xyz) в браузер бинарным потоком.</summary>
    private async Task ShowMeshAsync(float[] positions)
    {
        var buf = new byte[positions.Length * 4];
        Buffer.BlockCopy(positions, 0, buf, 0, buf.Length);
        var ms = new MemoryStream(buf, false);
        using var streamRef = new DotNetStreamReference(ms);
        await JS.InvokeVoidAsync("stampGen3d.showMesh", streamRef,
            new { w = S.Plate.Width, d = S.Plate.Depth, h = S.Plate.Height });
    }

    private void StartViewTimer()
    {
        _viewTick?.Dispose();
        _viewTick = new PeriodicTimer(TimeSpan.FromSeconds(1));
        _ = ViewElapsedLoopAsync(_viewTick);
    }

    private void StopViewTimer() { _viewTick?.Dispose(); _viewTick = null; }

    private async Task ViewElapsedLoopAsync(PeriodicTimer timer)
    {
        try
        {
            while (await timer.WaitForNextTickAsync())
            {
                var ts = DateTime.UtcNow - _viewStartedUtc;
                _viewElapsed = $"{(int)ts.TotalMinutes}:{ts.Seconds:00}";
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
    }

    private async Task SetView(bool is3d)
    {
        S.Is3D = is3d;
        if (is3d)
        {
            // v1.8: если модель уже построена кнопкой «Обновить» — показываем её МГНОВЕННО,
            // без пересчёта (и при повторных переключениях тоже). Только пластина —
            // если полной модели ещё не было.
            if (_lastMesh is { Length: > 0 } mesh)
            {
                _plateOnly3d = false;
                await ShowMeshAsync(mesh);
            }
            else
            {
                await ShowPlateOnlyAsync();
            }
            S.Notify();
        }
        else
        {
            S.Notify();
        }
        StateHasChanged();
    }

    /// <summary>v1.7: только пластина (без оттисков) — при входе в 3D без готовой модели
    /// и после «Новый». v1.8: если есть кэш полной модели, вместо этого показываем его.</summary>
    private async Task ShowPlateOnlyAsync()
    {
        _viewError = null;
        if (_lastMesh is { Length: > 0 } cached)
        {
            _plateOnly3d = false;
            await ShowMeshAsync(cached);
            return;
        }
        _plateOnly3d = true;
        try
        {
            var plateOnly = MeshGen.Build(S.Plate, new List<LayoutSolver.Instance>(), S.ReliefMm,
                new MeshGen.Options { CurveSegments = 4, DepthScale = 1, MaxTriangles = 200_000, TimeoutSec = 60 },
                null, default);
            await ShowMeshAsync(plateOnly.Positions);
        }
        catch (Exception ex)
        {
            _viewError = View3dApology + " (" + ex.Message + ")";
        }
        S.Dirty3D = true;   // полная модель ещё не построена — нужен «Обновить»
    }

    /// <summary>v1.8: сбросить кэш 3D (новый проект / открытие другого проекта).</summary>
    private void ClearMeshCache()
    {
        _lastMesh = null;
        _lastMeshPlate = null;
        _plateOnly3d = false;
    }

    // ==================== STL: расчёт на СЕРВЕРЕ (v1.6) ====================
    // Раньше STL строил браузер (three-bvh-csg) — Chrome показывал «Страница не отвечает».
    // Теперь MeshGen.Build на сервере приложения (ПК пользователя), отмена и лимиты;
    // файл уходит в браузер бинарным потоком и скачивается.

    private void StartElapsedTimer()
    {
        _stlStartedUtc = DateTime.UtcNow;
        _stlTick?.Dispose();
        _stlTick = new PeriodicTimer(TimeSpan.FromSeconds(1));
        _ = ElapsedLoopAsync(_stlTick);
    }

    private void StopElapsedTimer()
    {
        _stlTick?.Dispose();
        _stlTick = null;
    }

    private async Task ElapsedLoopAsync(PeriodicTimer timer)
    {
        try
        {
            while (await timer.WaitForNextTickAsync())
            {
                var ts = DateTime.UtcNow - _stlStartedUtc;
                _stlElapsedText = $"{(int)ts.TotalMinutes}:{ts.Seconds:00}";
                StateHasChanged();
            }
        }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
    }

    // ==================== числа ====================

    private static double ParseNum(string raw)
    {
        var s = raw.Trim().Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    }

    private void ReliefChanged(ChangeEventArgs e) => SetRelief(e.Value?.ToString() ?? "");

    private void SetRelief(string raw)
    {
        var v = ParseNum(raw);
        if (double.IsNaN(v) || v <= 0)
        {
            _reliefMsg = "Введите число больше 0.";
            StateHasChanged();
            return;
        }
        if (S.ReliefUnit == ReliefUnit.Micron)
        {
            v = Math.Round(v);                       // мкм — целые
            S.ReliefMm = v / 1000.0;
        }
        else
        {
            S.ReliefMm = v;
        }
        ClampRelief();
        _undo.PushCoalesced(Snapshot, "relief");   // пост-снимок
        N();
    }

    private void ClampRelief()
    {
        if (S.ReliefMm > S.Plate.Height)
        {
            S.ReliefMm = S.Plate.Height;
            _reliefMsg = $"Прорезание не может быть больше толщины пластины — уменьшено до {Fmt(S.ReliefMm)} мм.";
        }
        else if (S.ReliefMm <= 0)
        {
            _reliefMsg = "Глубина должна быть больше 0.";
        }
        else
        {
            _reliefMsg = null;
        }
    }

    private void SetReliefUnit(ReliefUnit u)
    {
        _undo.Push(Snapshot());
        S.ReliefUnit = u;
        ClampRelief();
        N();
    }

    // ==================== основание ====================

    /// <summary>Финализация после любого изменения пластины (клампы + перерисовка). Снимок undo делают обработчики ДО изменения.</summary>
    private void AfterPlateSize()
    {
        S.Plate.Width = Math.Clamp(S.Plate.Width, PlateParams.MinSide, 500);
        S.Plate.Depth = Math.Clamp(S.Plate.Depth, PlateParams.MinSide, 500);
        S.Plate.Height = Math.Clamp(S.Plate.Height, PlateParams.MinHeight, 50);
        // v1.3: единое поле — сеттер Margin синхронизирует все четыре стороны
        S.Plate.Margin = Math.Max(0, S.Plate.Margin);
        if (S.Plate.BottomChamferHeight > S.Plate.MaxBottomChamferHeight)
            S.Plate.BottomChamferHeight = S.Plate.MaxBottomChamferHeight;
        ClampRelief();
        N();
    }

    // Обработчики пластины: пост-снимок после клампов.
    private void PlateWidthInput(ChangeEventArgs e) { S.Plate.Width = NumOf(e); AfterPlateSize(); _undo.PushCoalesced(Snapshot, "plate"); }
    private void PlateDepthInput(ChangeEventArgs e) { S.Plate.Depth = NumOf(e); AfterPlateSize(); _undo.PushCoalesced(Snapshot, "plate"); }
    private void PlateHeightInput(ChangeEventArgs e) { S.Plate.Height = NumOf(e); AfterPlateSize(); _undo.PushCoalesced(Snapshot, "plate"); }
    private void PlateMarginInput(ChangeEventArgs e) { S.Plate.Margin = NumOf(e); AfterPlateSize(); _undo.PushCoalesced(Snapshot, "plate"); }
    private void PlateChamferHInput(ChangeEventArgs e) { S.Plate.BottomChamferHeight = NumOf(e); AfterPlateSize(); _undo.PushCoalesced(Snapshot, "plate"); }
    private void PlateChamferAInput(ChangeEventArgs e) { S.Plate.BottomChamferAngleDeg = NumOf(e); AfterPlateSize(); _undo.PushCoalesced(Snapshot, "plate"); }
    private void TopChamferChanged(ChangeEventArgs e)
    {
        S.Plate.TopChamfer = e.Value is bool b && b;
        AfterPlateSize();
        _undo.Push(Snapshot());
    }

    // ==================== оттиски: карточки ====================

    private void ToggleExpand(StampItem st) { st.Expanded = !st.Expanded; StateHasChanged(); }

    // ==================== мульти-выбор (v1.3) ====================
    // Правка параметра одного оттиска автоматически применяется ко всем отмеченным.

    private void ToggleBatch(StampItem st, ChangeEventArgs e)
    {
        bool on = e.Value is bool b && b;
        if (on) _batchSel.Add(st.Id); else _batchSel.Remove(st.Id);
        StateHasChanged();
        _ = Redraw2DAsync();   // v1.5: подсветка выбора на 2D-схеме
    }

    private void ClearBatch()
    {
        _batchSel.Clear();
        StateHasChanged();
        _ = Redraw2DAsync();   // v1.5
    }

    /// <summary>Перерисовать 2D-схему без ожидания (обновление подсветки выбора).</summary>
    private async Task Redraw2DAsync() => await Redraw2D();

    /// <summary>Активен ли массовый режим для этого оттиска (выбрано ≥2, включая редактируемый).</summary>
    private bool BatchActive(StampItem edited) => _batchSel.Count >= 2 && _batchSel.Contains(edited.Id);

    // ================================================================
    // v1.4 ГЛАВНОЕ ПРАВИЛО ГРУППЫ:
    // у выделенных оттисков синхронизируются ВСЕ параметры без исключений:
    // размеры, пропорции, угол, зеркало, рельеф (глубина, шершавое дно,
    // дельта), КОПИИ (галочка, число, шаг, единицы, расстояние, угол ряда)
    // и текст (содержание, шрифт, жирность). Единственное исключение —
    // ПОЛОЖЕНИЕ: сдвиг применяется ОТНОСИТЕЛЬНО текущих координат КАЖДОГО
    // оттиска (см. ShiftBatch).
    // ================================================================
    private void BroadcastBatch(StampItem src)
    {
        if (!BatchActive(src)) return;
        foreach (var id in _batchSel)
        {
            if (id == src.Id) continue;
            var t = S.Stamps.FirstOrDefault(x => x.Id == id);
            if (t == null) continue;
            t.Width = src.Width;
            t.Height = src.Height;
            t.KeepProportions = src.KeepProportions;
            t.RotationDeg = src.RotationDeg;
            t.Mirrored = src.Mirrored;
            t.DepthMm = src.DepthMm;
            t.RoughBottom = src.RoughBottom;
            t.RoughDeltaUm = src.RoughDeltaUm;
            // v1.4: копии — тоже общие (галочка «Сделать копии» и все её параметры)
            t.CopiesEnabled = src.CopiesEnabled;
            t.CopiesCount = src.CopiesCount;
            t.CopiesStep = src.CopiesStep;
            t.CopiesStepPercent = src.CopiesStepPercent;
            t.CopiesSpacing = src.CopiesSpacing;
            t.RowAngleDeg = src.RowAngleDeg;
            // v1.4: текстовые параметры — тоже общие
            if (src.Kind == StampKind.Text)
            {
                t.Text = src.Text;
                t.FontKey = src.FontKey;
                t.TextBold = src.TextBold;
                ApplyTextSvg(t, resize: false);
            }
            ClampStampInsideWork(t);
        }
    }

    /// <summary>v1.4: сдвиг всех выбранных ОТНОСИТЕЛЬНО их текущих координат
    /// (единственное исключение из полного синхрона параметров).</summary>
    private void ShiftBatch(StampItem src, double dPx, double dPy)
    {
        if (Math.Abs(dPx) < 1e-9 && Math.Abs(dPy) < 1e-9) return;
        if (!BatchActive(src)) return;
        foreach (var id in _batchSel)
        {
            if (id == src.Id) continue;
            var t = S.Stamps.FirstOrDefault(x => x.Id == id);
            if (t == null) continue;
            t.PosX = Math.Max(0, t.PosX + dPx);
            t.PosY = Math.Max(0, t.PosY + dPy);
            ClampStampInsideWork(t);
        }
    }

    private void RemoveStamp(Guid id)
    {
        RemoveStampCore(id);
        _undo.Push(Snapshot());   // пост-снимок (после удаления)
        N();
    }

    /// <summary>Само удаление без снимка undo/уведомления (серия удалений = один шаг отмены).</summary>
    private void RemoveStampCore(Guid id)
    {
        var st = S.Stamps.FirstOrDefault(x => x.Id == id);
        if (st != null) S.Stamps.Remove(st);
        _stampMsgs.Remove(id);
        _batchSel.Remove(id);
        if (S.SelectedStampId == id) S.SelectedStampId = null;
    }

    /// <summary>v1.5: Ctrl+клик по оттиску на 2D-схеме — то же, что галочка выбора.
    /// null (клик по пустому месту с Ctrl) — снять выбор.</summary>
    [JSInvokable]
    public async Task CanvasCtrlClick(string? stampId)
    {
        if (Guid.TryParse(stampId, out var id) && S.Stamps.Any(x => x.Id == id))
        {
            if (!_batchSel.Add(id)) _batchSel.Remove(id);   // переключить галочку
            S.SelectedStampId = id;
        }
        else
        {
            _batchSel.Clear();
        }
        StateHasChanged();
        await Redraw2D();   // подсветка выбора на схеме
    }

    /// <summary>v1.5: горячая клавиша Delete — удалить выбранные оттиски
    /// (вся пачка выделенных = один шаг отмены; без выделения — активный оттиск).</summary>
    [JSInvokable]
    public Task DeleteHotkey() => InvokeAsync(() =>
    {
        var ids = _batchSel.Count > 0
            ? _batchSel.Where(id => S.Stamps.Any(x => x.Id == id)).ToList()
            : (S.SelectedStampId is Guid sid ? new List<Guid> { sid } : new List<Guid>());
        if (ids.Count == 0) return;
        foreach (var id in ids) RemoveStampCore(id);
        _undo.Push(Snapshot());   // один пост-снимок на всю пачку
        N();
        StateHasChanged();
    });

    // ==================== v1.5: кнопки сворачивания/выделения ====================

    private void CollapseAll() { foreach (var st in S.Stamps) st.Expanded = false; StateHasChanged(); }

    private void ExpandAll() { foreach (var st in S.Stamps) st.Expanded = true; StateHasChanged(); }

    /// <summary>Выбрать все оттиски (аналог проставления всех галочек).</summary>
    private async Task SelectAllStamps()
    {
        _batchSel.Clear();
        foreach (var st in S.Stamps) _batchSel.Add(st.Id);
        StateHasChanged();
        await Redraw2D();
    }

    // ==================== 2D-схема: клик и перетаскивание мышью ====================

    /// <summary>Клик по оттиску на 2D-схеме: выбираем его и разворачиваем окно свойств (карточку).</summary>
    [JSInvokable]
    public async Task CanvasClick(string? stampId)
    {
        if (Guid.TryParse(stampId, out var id))
        {
            var st = S.Stamps.FirstOrDefault(x => x.Id == id);
            if (st != null)
            {
                S.SelectedStampId = id;
                st.Expanded = true;   // развернуть окно свойств этого оттиска
                _flashCard = id;
                _ = ClearFlashLater();
                StateHasChanged();
                try { await JS.InvokeVoidAsync("stampGen.scrollStampIntoView", stampId); }
                catch (JSDisconnectedException) { }
                catch (JSException) { }
            }
        }
        else
        {
            S.SelectedStampId = null;
            StateHasChanged();
        }
    }

    private async Task ClearFlashLater()
    {
        await Task.Delay(1600);
        if (!_disposed && _flashCard != null)
        {
            _flashCard = null;
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>Начало перетаскивания на 2D-схеме: снимок делается в CanvasDragTo (пост-снимок серии).</summary>
    [JSInvokable]
    public Task CanvasDragBegin(string stampId) => Task.CompletedTask;

    /// <summary>Перетаскивание на 2D-схеме: новая позиция ЛЕВОГО-ВЕРХ угла бокса, мм (вся группа с копиями).
    /// Позиция приходит уже зажатой из JS; здесь страхуем клампом по рабочей области.</summary>
    [JSInvokable]
    public Task CanvasDragTo(string stampId, double px, double py)
    {
        var st = S.Stamps.FirstOrDefault(x => x.Id.ToString() == stampId);
        if (st == null) return Task.CompletedTask;
        var wa = LayoutSolver.GetWorkArea(S.Plate);
        var (dx, dy, gw, gh) = LayoutSolver.GroupFootprint(st);
        double maxPx = Math.Max(0, wa.X1 - wa.X0 - gw);
        double maxPy = Math.Max(0, wa.Y1 - wa.Y0 - gh);
        double oldX = st.PosX, oldY = st.PosY;
        st.PosX = Math.Clamp(px + dx, 0, maxPx) - dx;
        st.PosY = Math.Clamp(py + dy, 0, maxPy) - dy;
        ShiftBatch(st, st.PosX - oldX, st.PosY - oldY);   // v1.4: тащим ВСЮ группу, сдвиг относительный
        S.SelectedStampId = st.Id;
        _undo.PushCoalesced(Snapshot, $"drag:{stampId}");   // весь жест = один шаг отмены
        N();
        return Task.CompletedTask;
    }

    // ==================== размеры оттиска (v1.3): поле, которое меняют, — ведущее ====================
    // Глюк v1.2 «нельзя уменьшить»: старый AfterSize всегда вытягивал бóльшую сторону обратно.
    // Теперь ширина и высота имеют свои обработчики: уменьшение работает в обе стороны.
    // Снимок undo берётся ДО присвоения нового значения (ручной @oninput/@onchange).

    private static double NumOf(ChangeEventArgs e)
    {
        var v = ParseNum(e.Value?.ToString() ?? "");
        return double.IsNaN(v) ? 0 : v;
    }

    private void WidthInput(StampItem st, ChangeEventArgs e)
    {
        st.Width = Math.Clamp(NumOf(e), StampItem.MinSize, 500);
        if (st.KeepProportions && st.Aspect > 0)
            st.Height = Math.Clamp(st.Width / st.Aspect, StampItem.MinSize, 500);
        ClampStampInsideWork(st);
        BroadcastBatch(st);
        _undo.PushCoalesced(Snapshot, $"size:{st.Id}");   // пост-снимок (серия слайдера = один шаг)
        N();
    }

    private void HeightInput(StampItem st, ChangeEventArgs e)
    {
        st.Height = Math.Clamp(NumOf(e), StampItem.MinSize, 500);
        if (st.KeepProportions && st.Aspect > 0)
            st.Width = Math.Clamp(st.Height * st.Aspect, StampItem.MinSize, 500);
        ClampStampInsideWork(st);
        BroadcastBatch(st);
        _undo.PushCoalesced(Snapshot, $"size:{st.Id}");
        N();
    }

    /// <summary>Переключение «сохранять пропорции» — сам размер не трогаем.</summary>
    private void AfterPropToggle(StampItem st)
    {
        BroadcastBatch(st);   // v1.4: «сохранять пропорции» — общий параметр группы
        N();
    }

    private void PosXInput(StampItem st, ChangeEventArgs e)
    {
        double oldV = st.PosX;
        st.PosX = Math.Clamp(NumOf(e), 0, MaxPosX(st));
        ShiftBatch(st, st.PosX - oldV, 0);   // v1.4: сдвиг группы относительно текущих координат
        _undo.PushCoalesced(Snapshot, $"pos:{st.Id}");
        N();
    }

    private void PosYInput(StampItem st, ChangeEventArgs e)
    {
        double oldV = st.PosY;
        st.PosY = Math.Clamp(NumOf(e), 0, MaxPosY(st));
        ShiftBatch(st, 0, st.PosY - oldV);   // v1.4: сдвиг группы относительно текущих координат
        _undo.PushCoalesced(Snapshot, $"pos:{st.Id}");
        N();
    }

    private void RotInput(StampItem st, ChangeEventArgs e)
    {
        st.RotationDeg = LayoutSolver.SnapAngle(NumOf(e));
        // при повороте оттиск остаётся на месте: центр габаритного бокса сохраняется
        LayoutSolver.KeepCenterOnRotate(st, S.Plate);
        BroadcastBatch(st);
        _undo.PushCoalesced(Snapshot, $"rot:{st.Id}");
        N();
    }

    private void AfterRowAngle(StampItem st)
    {
        st.RowAngleDeg = LayoutSolver.SnapAngle(st.RowAngleDeg);
        BroadcastBatch(st);   // v1.4: угол ряда — общий параметр группы
        _undo.PushCoalesced(Snapshot, $"rowang:{st.Id}");
        N();
    }

    private void Mirror(StampItem st)
    {
        st.Mirrored = !st.Mirrored;
        BroadcastBatch(st);
        _undo.Push(Snapshot());
        N();
    }

    // v1.4: копии — общие для всей группы; любое изменение копий синхронизируется
    private void CopyUnitChanged(StampItem st, ChangeEventArgs e)
    {
        st.CopiesStepPercent = (e.Value?.ToString() == "%");
        BroadcastBatch(st);
        _undo.Push(Snapshot());
        N();
    }

    private void AfterCopiesToggle(StampItem st)
    {
        BroadcastBatch(st);
        _undo.Push(Snapshot());
        N();
    }

    private void AfterCopies(StampItem st)
    {
        BroadcastBatch(st);
        _undo.PushCoalesced(Snapshot, $"copies:{st.Id}");
        N();
    }

    private void ArrangeGapChanged()
    {
        S.ArrangeGap = Math.Clamp(S.ArrangeGap, 0, 100);
        _prefsDirty = true;
        StateHasChanged();
    }

    private void DoResetPositions()
    {
        LayoutSolver.ResetPositions(S);
        _undo.Push(Snapshot());
        N();
    }

    private void DoAutoArrange()
    {
        LayoutSolver.AutoArrange(S);
        _undo.Push(Snapshot());
        N();
    }

    // ==================== рельеф оттиска ====================

    private void DepthChanged(StampItem st, ChangeEventArgs e)
    {
        var raw = e.Value?.ToString() ?? "";
        _stampMsgs.Remove(st.Id);
        if (string.IsNullOrWhiteSpace(raw))
        {
            st.DepthMm = null;   // общая глубина
            BroadcastBatch(st);
            _undo.Push(Snapshot());   // пост-снимок
            N();
            return;
        }
        var v = ParseNum(raw);
        if (double.IsNaN(v) || v <= 0)
        {
            _stampMsgs[st.Id] = "Глубина должна быть числом больше 0 (или пусто — общая).";
            StateHasChanged();
            return;
        }
        if (S.ReliefUnit == ReliefUnit.Micron) v = Math.Round(v) / 1000.0;
        st.DepthMm = v;
        ValidateStampDepth(st);
        BroadcastBatch(st);
        _undo.Push(Snapshot());   // пост-снимок
        N();
    }

    private void AfterRough(StampItem st)
    {
        st.RoughDeltaUm = Math.Clamp(st.RoughDeltaUm, 1, 2000);
        ValidateStampDepth(st);
        BroadcastBatch(st);   // v1.4: шершавое дно и дельта — общие
        _undo.PushCoalesced(Snapshot, $"rough:{st.Id}");
        N();
    }

    private void ValidateStampDepth(StampItem st)
    {
        double total = (st.DepthMm ?? S.ReliefMm) + (st.RoughBottom ? st.RoughDeltaUm / 1000.0 : 0);
        if (total > S.Plate.Height)
        {
            if (st.RoughBottom)
            {
                int maxDelta = (int)Math.Floor((S.Plate.Height - (st.DepthMm ?? S.ReliefMm)) * 1000);
                if (maxDelta < 0) maxDelta = 0;
                st.RoughDeltaUm = Math.Max(1, maxDelta);
                _stampMsgs[st.Id] = $"Глубина + шероховатость не должна превышать толщину пластины ({Fmt(S.Plate.Height)} мм) — дельта уменьшена до {st.RoughDeltaUm} мкм.";
            }
            else
            {
                st.DepthMm = S.Plate.Height;
                _stampMsgs[st.Id] = $"Прорезание не может быть больше толщины пластины — глубина уменьшена до {Fmt(S.Plate.Height)} мм.";
            }
        }
        StateHasChanged();
    }

    // ==================== текстовые оттиски ====================

    private void AddTextStamp()
    {
        var st = new StampItem
        {
            Kind = StampKind.Text,
            Source = StampSource.Custom,
            Text = "Подпись",
            FontKey = "sans",
            Expanded = true
        };
        st.Height = 5.5;
        st.Width = 20;
        st.Aspect = st.Width / st.Height;
        S.Stamps.Add(st);
        ApplyTextSvg(st, resize: true);
        _undo.Push(Snapshot());   // пост-снимок (после добавления)
        N();
    }

    private void TextChanged(StampItem st, ChangeEventArgs e)
    {
        st.Text = e.Value?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(st.Text)) st.Text = " ";
        // при наборе текста подгоняем ширину по новой пропорции и прижимаем к полям:
        // якорь — левый-верх угол, текст не вылезает за рабочую область
        ApplyTextSvg(st, resize: st.KeepProportions);
        BroadcastBatch(st);   // v1.4: текст и его параметры — общие для группы
        _undo.Push(Snapshot());   // пост-снимок
        N();
    }

    private void FontChanged(StampItem st, ChangeEventArgs e)
    {
        st.FontKey = e.Value?.ToString() ?? "sans";
        ApplyTextSvg(st, resize: true);
        BroadcastBatch(st);   // v1.4: шрифт — общий параметр группы
        _undo.Push(Snapshot());
        N();
    }

    private void TextStyleChanged(StampItem st)
    {
        ApplyTextSvg(st, resize: true);
        BroadcastBatch(st);   // v1.4: жирность — общий параметр группы
        _undo.Push(Snapshot());
        N();
    }

    /// <summary>Пересобирает SVG текстового оттиска; при resize — подгоняет пропорции.
    /// Якорь — ЛЕВЫЙ-ВЕРХНИЙ угол бокса: если оттиск перестал влезать вправо/вниз —
    /// прижимаем внутрь рабочей области (текст не вылезает за поля).</summary>
    private void ApplyTextSvg(StampItem st, bool resize)
    {
        try
        {
            var (svg, aspect) = TextGlyphs.Build(st.Text, st.FontKey, st.TextBold);
            S.TextSvgs[st.Id.ToString()] = svg;
            _stampMsgs.Remove(st.Id);
            if (aspect > 0) st.Aspect = aspect;   // истинная пропорция «чернил» текста
            if (resize)
            {
                // высоту сохраняем, ширину пересчитываем; не влезает — ужимаем по рабочей области
                double newW = st.Height * aspect;
                if (newW > WorkW) { newW = Math.Max(0.5, WorkW); st.Height = Math.Max(0.1, newW / aspect); }
                st.Width = newW;
            }
            ClampStampInsideWork(st);
        }
        catch (Exception ex)
        {
            _stampMsgs[st.Id] = "Текст: " + ex.Message;
        }
        StateHasChanged();
    }

    /// <summary>Гарантирует, что габаритный бокс оттиска (с копиями) не выходит из рабочей области.</summary>
    private void ClampStampInsideWork(StampItem st)
    {
        var wa = LayoutSolver.GetWorkArea(S.Plate);
        var (dx, dy, gw, gh) = LayoutSolver.GroupFootprint(st);
        double maxPx = Math.Max(0, wa.X1 - wa.X0 - gw);
        double maxPy = Math.Max(0, wa.Y1 - wa.Y0 - gh);
        st.PosX = Math.Clamp(st.PosX + dx, 0, maxPx) - dx;
        st.PosY = Math.Clamp(st.PosY + dy, 0, maxPy) - dy;
    }

    /// <summary>Гарантирует наличие SVG для текстовых оттисков (после открытия проекта).</summary>
    private void EnsureAllTextSvgs()
    {
        foreach (var st in S.Stamps.Where(s => s.Kind == StampKind.Text))
        {
            if (!S.TextSvgs.ContainsKey(st.Id.ToString()))
                ApplyTextSvg(st, resize: false);
        }
    }

    // ==================== модалка добавления ====================

    private void OpenAdd()
    {
        _addOpen = true;
        _selected.Clear();
        _customPending.Clear();
        _addMessages.Clear();
        // v1.8: по умолчанию — «Все библиотеки» и «Все категории» (сразу видно все оттиски)
        if (_selLib != SelAllLibsValue && Lib.Libraries.All(l => l.Id != _selLib))
            _selLib = SelAllLibsValue;
        _selCat = "";
        StateHasChanged();
    }

    private void CloseAdd() { _addOpen = false; StateHasChanged(); }

    private void SelectLibChanged(ChangeEventArgs e)
    {
        _selLib = e.Value?.ToString() ?? "";
        // v1.8: «Все библиотеки» — начинаем со всех категорий; конкретная — с первой её категории
        _selCat = _selLib == SelAllLibsValue
            ? ""
            : Lib.Categories(_selLib).FirstOrDefault() ?? "";
        StateHasChanged();
    }

    private void SelectCatChanged(ChangeEventArgs e)
    {
        _selCat = e.Value?.ToString() ?? "";
        StateHasChanged();
    }

    private void ToggleSelect(string path)
    {
        if (!_selected.Remove(path)) _selected.Add(path);
        StateHasChanged();
    }

    private void RemovePending(string tempId)
    {
        _customPending.RemoveAll(c => c.TempId == tempId);
        _selected.Remove("custom:" + tempId);
        StateHasChanged();
    }

    private async Task UploadSvg(InputFileChangeEventArgs e)
    {
        foreach (var f in e.GetMultipleFiles(30))
        {
            if (!f.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                _addMessages.Add($"{f.Name}: это не SVG-файл (нужен .svg)");
                continue;
            }
            try
            {
                await using var rs = f.OpenReadStream(10_000_000);
                using var ms = new MemoryStream();
                await rs.CopyToAsync(ms);
                var text = Encoding.UTF8.GetString(ms.ToArray());
                var res = SvgSanitizer.Sanitize(text, f.Name);
                if (!res.Ok)
                {
                    _addMessages.Add($"{f.Name}: {res.Error}");
                    continue;
                }
                var cp = new CustomPending
                {
                    Name = f.Name,
                    Aspect = res.Aspect,
                    Svg = res.Svg!,
                    DataUrl = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(res.Svg!))
                };
                _customPending.Add(cp);
                _selected.Add("custom:" + cp.TempId);
                foreach (var w in res.Warnings.Take(3))
                    _addMessages.Add($"{f.Name}: {w}");
            }
            catch (Exception ex)
            {
                _addMessages.Add($"{f.Name}: {ex.Message}");
            }
        }
        StateHasChanged();
    }

    private async Task AddSelectedToProject()
    {
        foreach (var sel in _selected)
        {
            if (sel.StartsWith("custom:", StringComparison.Ordinal))
            {
                var cp = _customPending.FirstOrDefault(c => "custom:" + c.TempId == sel);
                if (cp == null) continue;
                var id = Guid.NewGuid().ToString("N");
                S.CustomSvgs[id] = cp.Svg;
                _customThumbs[id] = cp.DataUrl;
                S.Stamps.Add(MakeStamp(cp.Aspect, st =>
                {
                    st.Source = StampSource.Custom;
                    st.CustomSvgId = id;
                    st.CustomFileName = cp.Name;
                }));
            }
            else
            {
                if (_pathIndex.TryGetValue(sel, out var info))
                {
                    // v1.7: штамп из библиотеки (папка stamp/)
                    S.Stamps.Add(MakeStamp(info.Aspect, st =>
                    {
                        st.Source = StampSource.Base;
                        st.Rass = Lib.Library(info.LibId)?.Name ?? info.LibId;
                        st.Otryad = info.Category;
                        st.Name = info.Name;
                        st.SvgPath = info.SvgPath;
                        st.PreviewPath = info.SvgPath;   // миниатюра = сам svg (endpoint /stamp-file)
                    }));
                }
            }
        }
        _addOpen = false;
        _undo.Push(Snapshot());   // пост-снимок (вся пачка новых оттисков = один шаг отмены)
        N();
    }

    private static StampItem MakeStamp(double aspect, Action<StampItem> init)
    {
        var st = new StampItem { Aspect = aspect > 0 ? aspect : 1 };
        if (st.Aspect >= 1) { st.Height = 5.5; st.Width = Math.Round(5.5 * st.Aspect, 2); }
        else { st.Width = 5.5; st.Height = Math.Round(5.5 / st.Aspect, 2); }
        st.PosX = 0; st.PosY = 0;
        init(st);
        return st;
    }

    // ==================== v1.7: библиотеки штампов ====================
    // Кнопка «Добавить штампы» — импорт zip-библиотеки; окно «Библиотеки» —
    // импорт/экспорт/удаление. Файлы хранятся в папке stamp/ рядом с программой.

    private void OpenLibs() { _libsOpen = true; _importReport = null; StateHasChanged(); }
    private void CloseLibs() { _libsOpen = false; StateHasChanged(); }

    /// <summary>Открыть выбор zip-файла (скрытый input, кликаем через JS).</summary>
    private async Task AddStampsClick()
    {
        try { await JS.InvokeVoidAsync("stampGen.clickFile", "lib-zip-input"); }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
    }

    private async Task ImportLibraryZip(InputFileChangeEventArgs e)
    {
        if (_importBusy) return;
        _importBusy = true;
        _importReport = null;
        StateHasChanged();
        try
        {
            await using var rs = e.File.OpenReadStream(120_000_000);
            using var ms = new MemoryStream();
            await rs.CopyToAsync(ms);
            var rep = await Lib.ImportAsync(ms.ToArray(), e.File.Name);
            _importReport = rep;
            if (rep.Ok)
            {
                RebuildPathIndex();
                // v1.8: после импорта показываем «Все библиотеки» — сразу видно всё новое
                _selLib = SelAllLibsValue;
                _selCat = "";
                _libsOpen = true;   // если импорт шёл не из окна библиотек — показываем отчёт там
                N();   // проекты со старыми оттисками могли «ожить» — перерисовка/предупреждения
            }
        }
        catch (Exception ex)
        {
            _importReport = new ImportReport { Ok = false, Error = "Импорт не удался: " + ex.Message, LibName = e.File.Name };
        }
        finally
        {
            _importBusy = false;
            try { await JS.InvokeVoidAsync("stampGen.resetFile", "lib-zip-input"); }
            catch { /* не критично */ }
            StateHasChanged();
        }
    }

    private async Task ExportLibraryZip(string libId)
    {
        var data = Lib.ExportZip(libId);
        if (data == null) return;
        await Download(SafeFileName(libId) + ".zip", data, "application/zip");
    }

    private void DeleteLibrary(string libId)
    {
        Lib.Delete(libId);
        RebuildPathIndex();
        if (Lib.Libraries.All(l => l.Id != _selLib))
            _selLib = SelAllLibsValue;
        _selCat = "";
        N();   // у части оттисков могла пропасть библиотека — предупреждения/перерисовка
        StateHasChanged();
    }

    private void ReloadLibraries()
    {
        Lib.Load();
        RebuildPathIndex();
        if (_selLib != SelAllLibsValue && Lib.Libraries.All(l => l.Id != _selLib))
            _selLib = SelAllLibsValue;
        _selCat = "";
        StateHasChanged();
    }

    // ==================== v1.7: новый проект ====================
    // Кнопка «Новый» рядом с «Сохранить». Классика: сначала предложение сохранить
    // текущий проект (Сохранить и создать / Не сохранять / Отмена).

    private void OpenNew() { _newOpen = true; StateHasChanged(); }
    private void CancelNew() { _newOpen = false; StateHasChanged(); }

    private async Task NewSaveAndCreate()
    {
        _newOpen = false;
        await SaveQuick();
        await ResetProject();
    }

    private async Task NewDiscardAndCreate()
    {
        _newOpen = false;
        await ResetProject();
    }

    /// <summary>Полный сброс к чистому «Новому проекту» (пластина по умолчанию, без оттисков).</summary>
    private async Task ResetProject()
    {
        S.ProjectName = "Новый проект";
        S.ProjectFileName = null;
        S.Plate = new PlateParams();
        S.ReliefMm = 0.02;
        S.ReliefUnit = ReliefUnit.Micron;
        S.Stamps.Clear();
        S.CustomSvgs.Clear();
        S.TextSvgs.Clear();
        S.Warnings.Clear();
        S.SelectedStampId = null;
        _batchSel.Clear();
        _stampMsgs.Clear();
        _customThumbs.Clear();
        _reliefMsg = null;
        _undo.Clear();
        _undo.Push(S.Clone());   // базовый снимок нового проекта
        SyncUndoButtons();
        _prefsDirty = true;
        _dirtySinceAutosave = true;
        ClearMeshCache();   // v1.8: новый проект — старая 3D-модель больше не актуальна
        if (S.Is3D)
            await ShowPlateOnlyAsync();   // снова только пластина + настройки
        N();
        StateHasChanged();
    }

    // ==================== сохранение / открытие ====================

    private void ProjectNameChanged(ChangeEventArgs e)
    {
        S.ProjectName = e.Value?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(S.ProjectName)) S.ProjectName = "Проект";
        N();
    }

    private void ToggleSaveAs() { _saveAsOpen = !_saveAsOpen; _openOpen = false; StateHasChanged(); }
    private void ToggleOpen() { _openOpen = !_openOpen; _saveAsOpen = false; StateHasChanged(); }

    private void SaveAsNameChanged(ChangeEventArgs e)
    {
        _saveAsName = e.Value?.ToString() ?? "";
        StateHasChanged();
    }

    private async Task SaveQuick()
    {
        var name = SafeFileName(S.ProjectName) + NativeProject.Extension;
        await Download(name, NativeProject.Save(S), NativeProject.Mime);
        S.ProjectFileName = name;
        _autosaveStatus = "сохранено вручную";
        _dirtySinceAutosave = false;
        _lastAutosaveUtc = DateTime.UtcNow;
    }

    private async Task SaveAsNow()
    {
        var fname = SafeFileName(string.IsNullOrWhiteSpace(_saveAsName) ? S.ProjectName : _saveAsName);
        try
        {
            switch (_saveAsFormat)
            {
                case FmtNative:
                    await Download(fname + NativeProject.Extension, NativeProject.Save(S), NativeProject.Mime);
                    S.ProjectFileName = fname + NativeProject.Extension;
                    break;
                case FmtSvg:
                {
                    var (inst, _) = LayoutSolver.BuildLayout(S);
                    await LayoutSolver.FillSourcesAsync(S, inst, Lib);
                    var svg = await SvgExport.BuildAsync(S, inst, Lib);
                    await Download(fname + ".svg", Encoding.UTF8.GetBytes(svg), "image/svg+xml");
                    break;
                }
                case FmtPdf:
                {
                    // v1.4: PDF содержит сами оттиски (векторная графика), а не схему мест и размеров
                    var (inst, _) = LayoutSolver.BuildLayout(S);
                    await LayoutSolver.FillSourcesAsync(S, inst, Lib);
                    var pdf = await PdfExport.BuildAsync(S, inst, Lib);
                    await Download(fname + ".pdf", Encoding.UTF8.GetBytes(pdf), "application/pdf");
                    break;
                }
                case FmtPng:
                case FmtJpg:
                {
                    // v1.6: растровые картинки из ТОГО ЖЕ вектора, что SVG и PDF;
                    // PNG — прозрачный фон, JPG — белый
                    var (inst, _) = LayoutSolver.BuildLayout(S);
                    await LayoutSolver.FillSourcesAsync(S, inst, Lib);
                    var svg = await SvgExport.BuildAsync(S, inst, Lib);
                    var ext = _saveAsFormat == FmtPng ? ".png" : ".jpg";
                    await JS.InvokeVoidAsync("stampGen.exportRaster", svg, _saveAsFormat, fname + ext, 20);
                    break;
                }
            }
            _saveAsOpen = false;
        }
        catch (Exception ex)
        {
            _openError = "Не удалось сохранить: " + ex.Message;
            _openOpen = true;
        }
        StateHasChanged();
    }

    private async Task Download(string name, byte[] data, string mime)
        => await JS.InvokeVoidAsync("stampGen.download", name, mime, Convert.ToBase64String(data));

    private static string SafeFileName(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
            if (char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')
                sb.Append(c);
        var r = sb.ToString().Trim();
        return r.Length == 0 ? "project" : r;
    }

    private async Task OpenProjectFile(InputFileChangeEventArgs e)
    {
        try
        {
            var f = e.File;
            await using var rs = f.OpenReadStream(80_000_000);
            using var ms = new MemoryStream();
            await rs.CopyToAsync(ms);
            NativeProject.Open(ms.ToArray(), S);
            _openOpen = false;
            _openError = null;
            ClearMeshCache();   // v1.8: открыт другой проект — старая 3D-модель не показываем
            _undo.Clear();   // новый проект — история отмен не относится
            _undo.Push(S.Clone());   // базовый снимок нового проекта
            SyncUndoButtons();
            foreach (var st in S.Stamps.Where(x => x.Source == StampSource.Custom && x.Kind != StampKind.Text))
            {
                if (S.CustomSvgs.TryGetValue(st.CustomSvgId, out var svg))
                    _customThumbs[st.CustomSvgId] = "data:image/svg+xml;base64," +
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
            }
            EnsureAllTextSvgs();
            N();
        }
        catch (Exception ex)
        {
            _openError = ex.Message;
        }
        StateHasChanged();
    }

    // ==================== настройки + автосохранение (localStorage) ====================

    private async Task LoadPrefsSafe()
    {
        try
        {
            var json = await JS.InvokeAsync<string?>("stampGen.prefsGet");
            if (!string.IsNullOrEmpty(json) && S.Stamps.Count == 0)
                ApplyPrefs(json);
        }
        catch { /* localStorage недоступен — не критично */ }
    }

    private void ApplyPrefs(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("plate", out var pl))
            {
                S.Plate.Width = GetD(pl, "w", S.Plate.Width);
                S.Plate.Depth = GetD(pl, "d", S.Plate.Depth);
                S.Plate.Height = GetD(pl, "h", S.Plate.Height);
                if (pl.TryGetProperty("topCh", out var tc) && tc.ValueKind == JsonValueKind.True) S.Plate.TopChamfer = true;
                if (pl.TryGetProperty("topCh", out var tc2) && tc2.ValueKind == JsonValueKind.False) S.Plate.TopChamfer = false;
                S.Plate.BottomChamferHeight = GetD(pl, "chH", S.Plate.BottomChamferHeight);
                S.Plate.BottomChamferAngleDeg = GetD(pl, "chA", S.Plate.BottomChamferAngleDeg);
                S.Plate.MarginTop = GetD(pl, "mT", S.Plate.MarginTop);
                S.Plate.MarginBottom = GetD(pl, "mB", S.Plate.MarginBottom);
                S.Plate.MarginLeft = GetD(pl, "mL", S.Plate.MarginLeft);
                S.Plate.MarginRight = GetD(pl, "mR", S.Plate.MarginRight);
                // v1.3: поля — единый параметр; в старых настройках могли различаться — берём максимум
                S.Plate.UnifyMargins();
            }
            S.ReliefMm = GetD(root, "reliefMm", S.ReliefMm);
            if (root.TryGetProperty("reliefUnit", out var ru) && ru.GetString() == "mm") S.ReliefUnit = ReliefUnit.Mm;
            else if (root.TryGetProperty("reliefUnit", out var ru2) && ru2.GetString() == "micron") S.ReliefUnit = ReliefUnit.Micron;
            // v1.6: качество и усиление рельефа больше не хранятся — при каждом запуске
            // по умолчанию «черновик» и ×10 (защита слабых ПК от тяжёлой первой сборки)
            S.AutosaveSec = (int)GetD(root, "autosaveSec", S.AutosaveSec);
            S.ArrangeGap = GetD(root, "arrangeGap", S.ArrangeGap);
            ClampRelief();
        }
        catch { /* битые настройки — игнорируем */ }
    }

    private static double GetD(JsonElement el, string name, double def)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : def;

    private string PrefsJson()
    {
        var obj = new
        {
            plate = new
            {
                w = S.Plate.Width, d = S.Plate.Depth, h = S.Plate.Height,
                topCh = S.Plate.TopChamfer,
                chH = S.Plate.BottomChamferHeight, chA = S.Plate.BottomChamferAngleDeg,
                mT = S.Plate.MarginTop, mB = S.Plate.MarginTop, mL = S.Plate.MarginTop, mR = S.Plate.MarginTop
            },
            reliefMm = S.ReliefMm,
            reliefUnit = S.ReliefUnit == ReliefUnit.Micron ? "micron" : "mm",
            // v1.6: quality/exag больше не сохраняются (всегда стартуем с черновика и ×10)
            autosaveSec = S.AutosaveSec,
            arrangeGap = S.ArrangeGap
        };
        return JsonSerializer.Serialize(obj);
    }

    private async Task CheckAutosaveSafe()
    {
        try
        {
            var meta = await JS.InvokeAsync<string?>("stampGen.autosaveMeta");
            if (!string.IsNullOrEmpty(meta))
            {
                using var doc = JsonDocument.Parse(meta);
                _autosaveFound = true;
                _autosaveTime = doc.RootElement.TryGetProperty("t", out var t) ? t.GetString() ?? "" : "";
                _autosaveName = doc.RootElement.TryGetProperty("n", out var n) ? n.GetString() ?? "" : "";
            }
            else _autosaveFound = false;
        }
        catch { _autosaveFound = false; }
    }

    private void AutosaveChanged(ChangeEventArgs e)
    {
        S.AutosaveSec = int.TryParse(e.Value?.ToString(), out var v) ? v : 0;
        _prefsDirty = true;
        _autosaveStatus = S.AutosaveSec > 0 ? "включено" : "выключено";
        StateHasChanged();
    }

    private async Task RestoreAutosave()
    {
        try
        {
            var b64 = await JS.InvokeAsync<string?>("stampGen.autosaveLoad");
            if (string.IsNullOrEmpty(b64)) { _autosaveFound = false; return; }
            NativeProject.Open(Convert.FromBase64String(b64), S);
            ClearMeshCache();   // v1.8: восстановлен другой проект — кэш 3D сбрасываем
            _undo.Clear();   // восстановление автосохранения — новая история
            _undo.Push(S.Clone());
            SyncUndoButtons();
            foreach (var st in S.Stamps.Where(x => x.Source == StampSource.Custom && x.Kind != StampKind.Text))
            {
                if (S.CustomSvgs.TryGetValue(st.CustomSvgId, out var svg))
                    _customThumbs[st.CustomSvgId] = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
            }
            EnsureAllTextSvgs();
            _autosaveFound = false;
            _autosaveStatus = "восстановлено";
            N();
        }
        catch (Exception ex)
        {
            _autosaveStatus = "Ошибка восстановления: " + ex.Message;
        }
        StateHasChanged();
    }

    private async Task DiscardAutosave()
    {
        try { await JS.InvokeVoidAsync("stampGen.autosaveClear"); } catch { }
        _autosaveFound = false;
        StateHasChanged();
    }

    // ==================== STL ====================

    private void OpenStl()
    {
        _stlName = SafeFileName(S.ProjectName) + ".stl";
        _stlProgress = 0;
        _stlStatus = "";
        _stlPhase = "";
        _stlDone = false;   // v1.5
        _stlOpen = true;
        StateHasChanged();
    }

    private void CloseStl()
    {
        if (!_stlBusy)
        {
            _stlOpen = false;
            _stlProgress = 0;
            _stlStatus = "";
            _stlPhase = "";
            StopElapsedTimer();
        }
        StateHasChanged();
    }

    private void StlNameChanged(ChangeEventArgs e)
    {
        _stlName = e.Value?.ToString() ?? "plate.stl";
        StateHasChanged();
    }

    private void StlQualityChanged(ChangeEventArgs e)
    {
        _stlQuality = int.TryParse(e.Value?.ToString(), out var q) ? Math.Clamp(q, 1, 4) : 3;
        StateHasChanged();
    }

    private async Task StartStl()
    {
        if (_stlBusy) return;
        _stlBusy = true;
        _stlDone = false;
        _stlProgress = 0;
        _stlElapsedText = "0:00";
        _stlPhase = "Подготовка геометрии…";
        _stlStatus = "Проверяем, строилась ли уже такая модель…";
        _stlStartedUtc = DateTime.UtcNow;
        _stlCts?.Dispose();
        _stlCts = new CancellationTokenSource();
        var cts = _stlCts;
        StartElapsedTimer();
        StateHasChanged();
        try
        {
            var (inst, _) = LayoutSolver.BuildLayout(S);
            await LayoutSolver.FillSourcesAsync(S, inst, Lib, for3d: true);
            var plate = S.Plate.Clone();
            var relief = S.ReliefMm;
            var quality = _stlQuality;
            var segs = CurveSegmentsOf(quality);
            // v2.1: отпечаток геометрии — по нему решаем, можно ли взять модель из памяти
            var sig = GeometrySignature(inst, segs);

            float[] positions;
            MeshReport report;
            bool fromMemory = false;

            if (_stlMeshCache is { Length: > 0 } cached && _stlCacheSig == sig && _stlCacheReport != null)
            {
                // 1) STL с этими же параметрами уже считали — отдаём из памяти
                positions = cached;
                report = _stlCacheReport;
                fromMemory = true;
            }
            else if (_lastMesh is { Length: > 0 } viewed && _viewMeshSig == sig
                     && _lastMeshExag == 1 && _viewMeshReport != null)
            {
                // 2) 3D-вид построен при усилении x1 и том же качестве — это и есть
                //    точная модель: скачиваем её из памяти, ничего не пересчитываем
                positions = viewed;
                report = _viewMeshReport;
                _stlMeshCache = viewed;   // и запоминаем для следующих сохранений
                _stlCacheSig = sig;
                _stlCacheReport = report;
                fromMemory = true;
            }
            else
            {
                // параметры изменились — строим точную модель по параметрам STL
                _stlPhase = "Строю пластину (с фасками) и вырезаю оттиски…";
                _stlStatus = "Параметры изменились — считаем точную модель заново.";
                _stlProgress = 0.01;
                StateHasChanged();
                // v1.6: расчёт на сервере, прогресс — через InvokeAsync
                var mesh = await Task.Run(() => MeshGen.Build(plate, inst, relief,
                    new MeshGen.Options
                    {
                        CurveSegments = segs,
                        DepthScale = null,                 // точные глубины (как напечатается)
                        MaxTriangles = 3_500_000,
                        TimeoutSec = 900
                    },
                    (phase, pct) => _ = InvokeAsync(() =>
                    {
                        _stlPhase = phase;
                        if (pct > _stlProgress) _stlProgress = Math.Clamp(pct, 0, 0.9);
                        StateHasChanged();
                    }), cts.Token));
                positions = mesh.Positions;
                report = MeshReport.From(mesh);
                // v2.1: запоминаем точный меш — следующее сохранение с теми же
                // параметрами скачается из памяти мгновенно (лимит ~50 МБ памяти)
                if (mesh.Triangles <= 1_600_000)
                {
                    _stlMeshCache = mesh.Positions;
                    _stlCacheSig = sig;
                    _stlCacheReport = report;
                }
                else
                {
                    _stlMeshCache = null;
                    _stlCacheSig = "";
                    _stlCacheReport = null;
                }
            }

            if (fromMemory)
            {
                _stlPhase = "Беру модель из памяти…";
                _stlProgress = 0.92;
                StateHasChanged();
            }
            _stlPhase = "Пишу STL-файл…";
            _stlProgress = Math.Max(_stlProgress, 0.92);
            StateHasChanged();
            var stl = await Task.Run(() => MeshGen.ToBinaryStl(positions));
            _stlProgress = 0.97;
            var name = string.IsNullOrWhiteSpace(_stlName) ? "plate.stl" : _stlName.Trim();
            if (!name.EndsWith(".stl", StringComparison.OrdinalIgnoreCase)) name += ".stl";
            var ms = new MemoryStream(stl, false);
            using var streamRef = new DotNetStreamReference(ms);
            await JS.InvokeVoidAsync("stampGen.downloadStream", name, "model/stl", streamRef);
            _stlBusy = false;
            _stlProgress = 1;
            _stlPhase = "Готово ✓";
            // v2.1: короткий понятный статус — откуда модель + замкнутость граней (watertight)
            var srcText = fromMemory
                ? "модель взята из памяти — параметры не менялись, пересчёт не требовался"
                : "модель построена заново по текущим параметрам";
            var sealText = report.Watertight
                ? "замкнутость граней: все грани закрыты ✓"
                : $"замкнутость граней: ВНИМАНИЕ — осталось незамкнутых граней: {report.OpenAfter}";
            if (report.OpenBefore > 0)
                sealText += $" (найдено щелей: {report.OpenBefore} — исправлено автоматически)";
            _stlStatus = $"Готово. {report.Triangles:N0} треугольников, {stl.Length / 1e6:0.0} МБ — файл скачан. {srcText}. {sealText}";
            _stlDone = true;   // кнопка «Готово» — закрыть окно
            StopElapsedTimer();
        }
        catch (OperationCanceledException)
        {
            _stlBusy = false;
            _stlPhase = "Отменено";
            _stlStatus = "Расчёт отменён.";
            StopElapsedTimer();
        }
        catch (MeshTooHeavyException ex)
        {
            _stlBusy = false;
            _stlPhase = "Слишком тяжело";
            _stlStatus = "Не получилось: расчёт STL на этих параметрах оказался слишком тяжёлым (" + ex.Message +
                         "). Поставьте качество ниже или уменьшите число/сложность оттисков.";
            StopElapsedTimer();
        }
        catch (JSDisconnectedException) { _stlBusy = false; }
        catch (Exception ex)
        {
            _stlBusy = false;
            _stlPhase = "Ошибка";
            _stlStatus = "Ошибка: " + ex.Message;
            StopElapsedTimer();
        }
        StateHasChanged();
    }

    /// <summary>v1.6: отмена расчёта STL (кнопка в окне).</summary>
    private void CancelStl() => _stlCts?.Cancel();

    /// <summary>v2.1: отпечаток геометрии для кэша STL — параметры пластины, рельефа,
    /// качество и все оттиски (позиция, размеры, глубины, содержимое SVG).
    /// Отпечаток совпал — геометрия та же, готовый меш можно взять из памяти.</summary>
    private string GeometrySignature(List<LayoutSolver.Instance> inst, int curveSegments)
    {
        var sb = new StringBuilder(512);
        var p = S.Plate;
        sb.Append("v21|")
          .Append(p.Width.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
          .Append(p.Depth.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
          .Append(p.Height.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
          .Append(p.TopChamfer ? 'T' : '-').Append(',')
          .Append(p.BottomChamferHeight.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
          .Append(p.BottomChamferAngleDeg.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
          .Append(S.ReliefMm.ToString("0.#####", CultureInfo.InvariantCulture)).Append(',')
          .Append(curveSegments);
        foreach (var i in inst)
        {
            if (i.SvgText == null) continue;   // без SVG оттиск в геометрию не попадает
            sb.Append('|').Append(i.StampId.ToString("N")).Append(',')
              .Append(i.CopyIndex).Append(',')
              .Append(i.AnchorX.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
              .Append(i.AnchorY.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
              .Append(i.W.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
              .Append(i.H.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
              .Append(i.RotDeg.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
              .Append(i.Mirror ? 'M' : '-').Append(',')
              .Append(i.DepthMm.ToString("0.#####", CultureInfo.InvariantCulture)).Append(',')
              .Append(i.RoughMm.ToString("0.#####", CultureInfo.InvariantCulture)).Append(',')
              .Append(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(i.SvgText))));
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        _disposed = true;
        S.OnChange -= OnStateChanged;
        _self?.Dispose();
        StopElapsedTimer();
        StopViewTimer();
        try { _viewCts?.Cancel(); } catch { }
        try { _stlCts?.Cancel(); } catch { }
        _viewCts?.Dispose();
        _stlCts?.Dispose();
    }
}
