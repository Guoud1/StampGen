// StampGen — модели данных приложения (пластина, оттиск, состояние).
// © Guoud (iljamakeev@yandex.ru). Все права защищены. Условия: LICENSE.md.
namespace StampGen.Services;

/// <summary>Параметры основания (пластины). Все размеры в мм.</summary>
public class PlateParams
{
    public const double MinSide = 5.0;
    public const double MinHeight = 1.0;
    public const double TopChamferSize = 1.0;   // верхняя фаска 1x1 мм
    public const double MaxChamferAngle = 80.0;

    public double Width { get; set; } = 130.0;
    public double Depth { get; set; } = 80.0;
    public double Height { get; set; } = 3.0;

    /// <summary>Фаска сверху (1x1 мм, 45°).</summary>
    public bool TopChamfer { get; set; }

    /// <summary>Высота нижней фаски/каймы, мм (0..Height - (TopChamfer?1:0)).</summary>
    public double BottomChamferHeight { get; set; } = 2.0;

    /// <summary>Угол нижней кромки, градусы: -80..+80, шаг 0.5. 0 = прямой угол.
    /// Плюс — фаска, минус — «кайма» (цоколь).</summary>
    public double BottomChamferAngleDeg { get; set; } = 0;

    /// <summary>Поля по периметру, мм (от края пластины, а сверху — от кромки верхней фаски).</summary>
    public double MarginTop { get; set; } = 5.0;
    public double MarginBottom { get; set; } = 5.0;
    public double MarginLeft { get; set; } = 5.0;
    public double MarginRight { get; set; } = 5.0;

    /// <summary>Единое поле по всему периметру (v1.3): один параметр на всю подложку,
    /// все четыре стороны всегда одинаковые. Сеттер синхронизирует четыре поля сразу.</summary>
    public double Margin
    {
        get => MarginTop;
        set { var v = Math.Max(0, value); MarginTop = v; MarginBottom = v; MarginLeft = v; MarginRight = v; }
    }

    /// <summary>Унифицировать поля старого проекта: берём максимум, чтобы ничего не вылезло.</summary>
    public void UnifyMargins() => Margin = Math.Max(0, Math.Max(Math.Max(MarginTop, MarginBottom), Math.Max(MarginLeft, MarginRight)));

    /// <summary>Максимальная высота нижней фаски с учётом верхней.</summary>
    public double MaxBottomChamferHeight => Math.Max(0.1, Height - (TopChamfer ? TopChamferSize : 0));

    /// <summary>Горизонтальный вылет нижней фаски/каймы (мм) — на весь периметр.</summary>
    public double BottomOffsetMm
    {
        get
        {
            if (Math.Abs(BottomChamferAngleDeg) < 0.01 || BottomChamferHeight <= 0) return 0;
            double o = Math.Tan(Math.Abs(BottomChamferAngleDeg) * Math.PI / 180.0) * BottomChamferHeight;
            double lim = Math.Min(Width, Depth) / 2.0 - 0.2;
            return Math.Clamp(o, 0, Math.Max(0, lim));
        }
    }

    public PlateParams Clone() => (PlateParams)MemberwiseClone();
}

public enum ReliefUnit { Micron, Mm }

public enum StampSource { Base, Custom }

/// <summary>Тип оттиска: значок из базы / свой SVG / текст.</summary>
public enum StampKind { Icon, Text }

/// <summary>Оттиск в проекте.</summary>
public class StampItem
{
    public const double MinSize = 0.035;   // минимальный размер оттиска, мм (35 мкм)

    /// <summary>Идентификатор: init — чтобы клон (undo) сохранял тот же Id.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();
    public StampSource Source { get; set; } = StampSource.Base;
    public StampKind Kind { get; set; } = StampKind.Icon;

    // Для значков из базы
    public string Rass { get; set; } = "";
    public string Otryad { get; set; } = "";
    public string Name { get; set; } = "";
    public string SvgPath { get; set; } = "";        // icons/...
    public string PreviewPath { get; set; } = "";    // previews/...

    // Для своих SVG
    public string CustomSvgId { get; set; } = "";
    public string CustomFileName { get; set; } = "";

    // Для текстовых оттисков
    public string Text { get; set; } = "Подпись";
    public string FontKey { get; set; } = "sans";
    public bool TextBold { get; set; }

    /// <summary>Отношение сторон исходного SVG (ширина/высота).</summary>
    public double Aspect { get; set; } = 1.0;

    public double Width { get; set; } = 5.5;   // мм
    public double Height { get; set; } = 5.5;  // мм
    public bool KeepProportions { get; set; } = true;

    /// <summary>Положение ЛЕВОГО ВЕРХНЕГО угла габаритного бокса (с учётом поворота) внутри рабочей области, мм.</summary>
    public double PosX { get; set; } = 0;
    public double PosY { get; set; } = 0;

    public double RotationDeg { get; set; } = 0;   // 0..360, шаг 0.5, прилипание к 45°
    public bool Mirrored { get; set; }

    /// <summary>Своя глубина рельефа этого оттиска, мм. null — использовать общую.</summary>
    public double? DepthMm { get; set; }

    /// <summary>Шершавое дно: рельеф на дне кармана.</summary>
    public bool RoughBottom { get; set; }

    /// <summary>Дельта шероховатости, мкм (дно колеблется от глубины до глубины + дельта).</summary>
    public int RoughDeltaUm { get; set; } = 30;

    // Копии
    public bool CopiesEnabled { get; set; }
    public int CopiesCount { get; set; } = 2;          // число КОПИЙ (не считая оригинала)
    public double CopiesStep { get; set; } = 0.5;      // мм или %
    public bool CopiesStepPercent { get; set; }
    public double CopiesSpacing { get; set; } = 1.0;   // расстояние между моделями, мм
    public double RowAngleDeg { get; set; } = 0;       // угол поворота ряда

    /// <summary>Служебное: карточка развёрнута (UI).</summary>
    public bool Expanded { get; set; }

    public string DisplayName => Kind == StampKind.Text
        ? (Text.Length > 22 ? Text[..22] + "…" : Text)
        : Source == StampSource.Base ? Name : CustomFileName;

    /// <summary>Масштаб копии i (i=0 — оригинал).</summary>
    public double CopyScale(int i)
    {
        if (i <= 0) return 1;
        return CopiesStepPercent
            ? Math.Pow(1 + CopiesStep / 100.0, i)
            : 1 + i * CopiesStep / Math.Max(0.001, Width);
    }

    public static StampItem CloneStamp(StampItem st) => new()
    {
        Id = st.Id,   // сохраняем Id: undo/redo и мульти-выбор держат ссылки на оттиски
        Source = st.Source,
        Kind = st.Kind,
        Rass = st.Rass, Otryad = st.Otryad, Name = st.Name,
        SvgPath = st.SvgPath, PreviewPath = st.PreviewPath,
        CustomSvgId = st.CustomSvgId, CustomFileName = st.CustomFileName,
        Text = st.Text, FontKey = st.FontKey, TextBold = st.TextBold,
        Aspect = st.Aspect, Width = st.Width, Height = st.Height,
        KeepProportions = st.KeepProportions, PosX = st.PosX, PosY = st.PosY,
        RotationDeg = st.RotationDeg, Mirrored = st.Mirrored,
        DepthMm = st.DepthMm, RoughBottom = st.RoughBottom, RoughDeltaUm = st.RoughDeltaUm,
        CopiesEnabled = st.CopiesEnabled, CopiesCount = st.CopiesCount,
        CopiesStep = st.CopiesStep, CopiesStepPercent = st.CopiesStepPercent,
        CopiesSpacing = st.CopiesSpacing, RowAngleDeg = st.RowAngleDeg,
        Expanded = st.Expanded
    };
}

/// <summary>Состояние проекта. Scoped — на соединение (circuit).</summary>
public class StampState
{
    public event Action? OnChange;

    public string ProjectName { get; set; } = "Новый проект";
    public string? ProjectFileName { get; set; }   // имя последнего сохранённого .stampgen

    public PlateParams Plate { get; set; } = new();

    /// <summary>Глубина рельефа в мм (каноническое хранение).</summary>
    public double ReliefMm { get; set; } = 0.02;

    public ReliefUnit ReliefUnit { get; set; } = ReliefUnit.Micron;

    public List<StampItem> Stamps { get; } = new();

    /// <summary>Свои SVG: id -> svg-текст (уже ЧБ).</summary>
    public Dictionary<string, string> CustomSvgs { get; } = new(StringComparer.Ordinal);

    /// <summary>SVG оттисков-текстов: stampId -> svg-текст (генерируется TextGlyphs).</summary>
    public Dictionary<string, string> TextSvgs { get; } = new(StringComparer.Ordinal);

    /// <summary>Предупреждения последней проверки раскладки.</summary>
    public List<string> Warnings { get; } = new();

    // Вид и настройки приложения (сохраняются в браузере, не в проекте)
    public bool Is3D { get; set; }
    public int Quality { get; set; } = 1;              // 1..4; v1.6: по умолчанию «черновик»
    public double ReliefExaggeration { get; set; } = 10; // v1.6: по умолчанию ×10 (защита слабых ПК)
    public bool Dirty3D { get; set; } = true;          // надо обновить 3D

    /// <summary>Оттиск, выбранный кликом по 2D-схеме (UI, не сохраняется).</summary>
    public Guid? SelectedStampId { get; set; }

    /// <summary>Интервал автосохранения, секунд (0 — выключено).</summary>
    public int AutosaveSec { get; set; } = 60;

    /// <summary>Минимальный зазор между оттисками для авто-расстановки, мм (v1.3).</summary>
    public double ArrangeGap { get; set; } = 1.5;

    public void Notify() => OnChange?.Invoke();

    public StampState Clone()
    {
        var s = new StampState
        {
            ProjectName = ProjectName,
            ProjectFileName = ProjectFileName,
            Plate = Plate.Clone(),
            ReliefMm = ReliefMm,
            ReliefUnit = ReliefUnit,
            Is3D = Is3D,
            Quality = Quality,
            ReliefExaggeration = ReliefExaggeration,
            AutosaveSec = AutosaveSec,
            ArrangeGap = ArrangeGap
        };
        s.Stamps.AddRange(Stamps.Select(StampItem.CloneStamp));
        foreach (var (k, v) in CustomSvgs) s.CustomSvgs[k] = v;
        foreach (var (k, v) in TextSvgs) s.TextSvgs[k] = v;
        s.Warnings.AddRange(Warnings);
        return s;
    }
}
