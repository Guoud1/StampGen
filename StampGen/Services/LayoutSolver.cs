using System.Text.Json;
using System.Text.Json.Serialization;

namespace StampGen.Services;

/// <summary>Геометрия раскладки: рабочая область, экземпляры оттисков (с копиями), прилипание, авто-расстановка.</summary>
public static class LayoutSolver
{
    public class Instance
    {
        public Guid StampId { get; set; }
        public string StampName { get; set; } = "";
        public StampKind Kind { get; set; }
        public int CopyIndex { get; set; }
        /// <summary>Левый верхний угол НЕповёрнутого прямоугольника в координатах пластины, мм.</summary>
        public double AnchorX { get; set; }
        public double AnchorY { get; set; }
        /// <summary>Размеры НЕповёрнутого прямоугольника, мм.</summary>
        public double W { get; set; }
        public double H { get; set; }
        public double RotDeg { get; set; }
        public bool Mirror { get; set; }
        /// <summary>Габаритный бокс (после поворота), мм.</summary>
        public double Bx { get; set; }
        public double By { get; set; }
        public double Bw { get; set; }
        public double Bh { get; set; }
        /// <summary>Источник картинки для 2D: путь к PNG-превью или data-URL svg.</summary>
        public string Src { get; set; } = "";
        public bool IsSvgData { get; set; }
        /// <summary>Для 3D: текст SVG (свои значки и текстовые оттиски).</summary>
        public string? SvgText { get; set; }
        /// <summary>Глубина рельефа этого оттиска, мм (своя или общая).</summary>
        public double DepthMm { get; set; } = 0.02;
        /// <summary>Шершавое дно.</summary>
        public bool Rough { get; set; }
        /// <summary>Дельта шероховатости, мм.</summary>
        public double RoughMm { get; set; }
    }

    public record WorkArea(double X0, double Y0, double X1, double Y1);

    /// <summary>Рабочая область: верхняя грань пластины минус верхняя фаска (по периметру) и минус поля по периметру.</summary>
    public static WorkArea GetWorkArea(PlateParams p)
    {
        double inset = p.TopChamfer ? PlateParams.TopChamferSize : 0;
        double x0 = inset + Math.Max(0, p.MarginLeft);
        double y0 = inset + Math.Max(0, p.MarginTop);
        double x1 = p.Width - inset - Math.Max(0, p.MarginRight);
        double y1 = p.Depth - inset - Math.Max(0, p.MarginBottom);
        // если поля схлопнули область — оставляем узкую полосу в центре
        if (x1 < x0) { double m = (x0 + x1) / 2; x0 = m; x1 = m + 0.01; }
        if (y1 < y0) { double m = (y0 + y1) / 2; y0 = m; y1 = m + 0.01; }
        return new WorkArea(x0, y0, x1, y1);
    }

    public static (double Bw, double Bh) RotatedBBox(double w, double h, double deg)
    {
        double a = deg * Math.PI / 180.0;
        double c = Math.Abs(Math.Cos(a)), s = Math.Abs(Math.Sin(a));
        return (w * c + h * s, w * s + h * c);
    }

    /// <summary>Прилипание слайдера угла к кратным 45° (±2°).</summary>
    public static double SnapAngle(double deg)
    {
        double v = Math.Round(deg * 2) / 2.0;
        double k = Math.Round(v / 45.0);
        if (Math.Abs(v - k * 45.0) <= 2.0 && Math.Abs(k * 45.0) <= 360.0) v = (k * 45.0) % 360.0;
        return (v + 360.0) % 360.0;
    }

    /// <summary>
    /// Пересчитывает PosX/PosY так, чтобы при повороте оттиск оставался на месте
    /// (позиция хранится как левый-верх габаритного бокса, оси всегда — оси пластины).
    /// </summary>
    public static void KeepCenterOnRotate(StampItem st, PlateParams p)
    {
        var wa = GetWorkArea(p);
        var (bw0, bh0) = RotatedBBox(st.Width, st.Height, st.RotationDeg);
        double cx = wa.X0 + Math.Clamp(st.PosX, 0, Math.Max(0, wa.X1 - wa.X0 - bw0)) + bw0 / 2;
        double cy = wa.Y0 + Math.Clamp(st.PosY, 0, Math.Max(0, wa.Y1 - wa.Y0 - bh0)) + bh0 / 2;
        SetPositionFromCenter(st, cx, cy, wa);
    }

    public static void SetPositionFromCenter(StampItem st, double cx, double cy, WorkArea wa)
    {
        var (bw, bh) = RotatedBBox(st.Width, st.Height, st.RotationDeg);
        double maxPx = Math.Max(0, wa.X1 - wa.X0 - bw), maxPy = Math.Max(0, wa.Y1 - wa.Y0 - bh);
        st.PosX = Math.Clamp(cx - bw / 2 - wa.X0, 0, maxPx);
        st.PosY = Math.Clamp(cy - bh / 2 - wa.Y0, 0, maxPy);
    }

    /// <summary>Экземпляры одного оттиска (оригинал + копии), прижатые к рабочей области.</summary>
    public static List<Instance> PlaceStamp(StampItem st, PlateParams p, WorkArea wa)
    {
        var res = new List<Instance>();
        double w0 = st.Width, h0 = st.Height;
        var (bw, bh) = RotatedBBox(w0, h0, st.RotationDeg);
        // позиция оригинала задаётся слайдерами 0..workW-bw (оси — оси пластины, от поворота не зависят)
        double maxPx = Math.Max(0, wa.X1 - wa.X0 - bw), maxPy = Math.Max(0, wa.Y1 - wa.Y0 - bh);
        double px = Math.Clamp(st.PosX, 0, maxPx), py = Math.Clamp(st.PosY, 0, maxPy);
        double cx = wa.X0 + px + bw / 2, cy = wa.Y0 + py + bh / 2;   // центр оригинала
        (cx, cy) = ClampCenter(cx, cy, bw, bh, wa);

        AddInstance(res, st, 0, w0, h0, cx, cy);
        if (st.CopiesEnabled && st.CopiesCount > 0)
        {
            // направление ряда — в осях пластины
            double dirX = Math.Cos(st.RowAngleDeg * Math.PI / 180.0);
            double dirY = Math.Sin(st.RowAngleDeg * Math.PI / 180.0);
            double prevCx = cx, prevCy = cy;
            var (prevBw, prevBh) = (bw, bh);
            for (int i = 1; i <= Math.Clamp(st.CopiesCount, 1, 200); i++)
            {
                double f = st.CopyScale(i);
                double wi = Math.Max(0.1, w0 * f), hi = Math.Max(0.1, h0 * f);
                // дистанция считается по габаритному прямоугольнику (деталь вписана в прямоугольник),
                // спроецированному на направление ряда: край-к-краю + зазор
                double a = st.RowAngleDeg * Math.PI / 180.0;
                double ca = Math.Abs(Math.Cos(a)), sa = Math.Abs(Math.Sin(a));
                var (curBw, curBh) = RotatedBBox(wi, hi, st.RotationDeg);
                double projPrev = prevBw * ca + prevBh * sa;
                double projCur = curBw * ca + curBh * sa;
                double dist = projPrev / 2 + Math.Max(0, st.CopiesSpacing) + projCur / 2;
                double cxi = prevCx + dirX * dist;
                double cyi = prevCy + dirY * dist;
                // копия должна попадать в рабочую область; чуть вылезающую — прижимаем внутрь
                bool fits = cxi - curBw / 2 >= wa.X0 - 0.01 && cxi + curBw / 2 <= wa.X1 + 0.01 &&
                            cyi - curBh / 2 >= wa.Y0 - 0.01 && cyi + curBh / 2 <= wa.Y1 + 0.01;
                if (!fits)
                {
                    if (curBw > wa.X1 - wa.X0 + 0.01 || curBh > wa.Y1 - wa.Y0 + 0.01)
                        break; // копия физически не помещается
                    (cxi, cyi) = ClampCenter(cxi, cyi, curBw, curBh, wa);
                }
                AddInstance(res, st, i, wi, hi, cxi, cyi);
                prevBw = curBw; prevBh = curBh;
                prevCx = cxi; prevCy = cyi;
            }
        }
        return res;
    }

    private static (double, double) ClampCenter(double cx, double cy, double bw, double bh, WorkArea wa)
    {
        double minX = wa.X0 + bw / 2, maxX = wa.X1 - bw / 2;
        double minY = wa.Y0 + bh / 2, maxY = wa.Y1 - bh / 2;
        cx = minX <= maxX ? Math.Clamp(cx, minX, maxX) : (wa.X0 + wa.X1) / 2;
        cy = minY <= maxY ? Math.Clamp(cy, minY, maxY) : (wa.Y0 + wa.Y1) / 2;
        return (cx, cy);
    }

    private static void AddInstance(List<Instance> res, StampItem st,
        int idx, double w, double h, double cx, double cy)
    {
        var (bw, bh) = RotatedBBox(w, h, st.RotationDeg);
        res.Add(new Instance
        {
            StampId = st.Id,
            StampName = st.DisplayName,
            Kind = st.Kind,
            CopyIndex = idx,
            AnchorX = cx - w / 2, AnchorY = cy - h / 2,
            W = w, H = h, RotDeg = st.RotationDeg, Mirror = st.Mirrored,
            Bx = cx - bw / 2, By = cy - bh / 2, Bw = bw, Bh = bh,
            DepthMm = st.DepthMm ?? 0.02,
            Rough = st.RoughBottom,
            RoughMm = st.RoughBottom ? Math.Max(0, st.RoughDeltaUm) / 1000.0 : 0
        });
    }

    /// <summary>Все экземпляры всех оттисков + предупреждения о пересечениях/невлезании.</summary>
    public static (List<Instance> instances, List<string> warnings) BuildLayout(StampState state)
    {
        var wa = GetWorkArea(state.Plate);
        var all = new List<Instance>();
        var warns = new List<string>();
        foreach (var st in state.Stamps)
        {
            var placed = PlaceStamp(st, state.Plate, wa);
            if (st.CopiesEnabled && placed.Count < st.CopiesCount + 1)
                warns.Add($"«{st.DisplayName}»: в рабочую область влезло {placed.Count} из {st.CopiesCount + 1} шт.");
            all.AddRange(placed);
        }
        // пересечения (габаритные боксы)
        for (int i = 0; i < all.Count; i++)
            for (int j = i + 1; j < all.Count; j++)
                if (Overlap(all[i], all[j]) && all[i].StampId != all[j].StampId)
                {
                    warns.Add($"«{all[i].StampName}» и «{all[j].StampName}» пересекаются.");
                    break;
                }
        state.Warnings.Clear();
        state.Warnings.AddRange(warns.Distinct().Take(8));
        return (all, warns);
    }

    private static bool Overlap(Instance a, Instance b)
        => a.Bx < b.Bx + b.Bw && b.Bx < a.Bx + a.Bw && a.By < b.By + b.Bh && b.By < a.By + a.Bh;

    public static void ResetPositions(StampState state)
    {
        foreach (var st in state.Stamps) { st.PosX = 0; st.PosY = 0; }
    }

    /// <summary>
    /// Габарит группы «оригинал + копии» (без прижатия к рабочей области) относительно
    /// левого-верх угла габаритного бокса ОРИГИНАЛА. Оттиск и его копии — единое целое.
    /// </summary>
    public static (double Dx, double Dy, double W, double H) GroupFootprint(StampItem st)
    {
        double w0 = st.Width, h0 = st.Height;
        var (bw0, bh0) = RotatedBBox(w0, h0, st.RotationDeg);
        double minX = 0, minY = 0, maxX = bw0, maxY = bh0;   // бокс оригинала в его же координатах
        if (st.CopiesEnabled && st.CopiesCount > 0)
        {
            double dirX = Math.Cos(st.RowAngleDeg * Math.PI / 180.0);
            double dirY = Math.Sin(st.RowAngleDeg * Math.PI / 180.0);
            double prevCx = bw0 / 2, prevCy = bh0 / 2;       // центр оригинала в координатах его бокса
            var (prevBw, prevBh) = (bw0, bh0);
            for (int i = 1; i <= Math.Clamp(st.CopiesCount, 1, 200); i++)
            {
                double f = st.CopyScale(i);
                double wi = Math.Max(0.1, w0 * f), hi = Math.Max(0.1, h0 * f);
                double a = st.RowAngleDeg * Math.PI / 180.0;
                double ca = Math.Abs(Math.Cos(a)), sa = Math.Abs(Math.Sin(a));
                var (curBw, curBh) = RotatedBBox(wi, hi, st.RotationDeg);
                double projPrev = prevBw * ca + prevBh * sa;
                double projCur = curBw * ca + curBh * sa;
                double dist = projPrev / 2 + Math.Max(0, st.CopiesSpacing) + projCur / 2;
                double cxi = prevCx + dirX * dist;
                double cyi = prevCy + dirY * dist;
                minX = Math.Min(minX, cxi - curBw / 2);
                minY = Math.Min(minY, cyi - curBh / 2);
                maxX = Math.Max(maxX, cxi + curBw / 2);
                maxY = Math.Max(maxY, cyi + curBh / 2);
                prevBw = curBw; prevBh = curBh; prevCx = cxi; prevCy = cyi;
            }
        }
        return (minX, minY, Math.Max(0.1, maxX - minX), Math.Max(0.1, maxY - minY));
    }

    /// <summary>Авто-расстановка: полочная укладка ГРУПП «оригинал + копии» (копии учитываются).
    /// Зазор между оттисками — параметр пользователя (state.ArrangeGap, мм), минимум 0.</summary>
    public static void AutoArrange(StampState state)
    {
        var wa = GetWorkArea(state.Plate);
        double gap = Math.Clamp(state.ArrangeGap, 0, 100);
        var items = state.Stamps
            .Select(st =>
            {
                var (dx, dy, gw, gh) = GroupFootprint(st);
                return (st, dx, dy, gw, gh);
            })
            .OrderByDescending(t => t.gh).ThenByDescending(t => t.gw).ToList();
        double x = wa.X0, y = wa.Y0, rowH = 0;
        foreach (var (st, dx, dy, gw0, gh0) in items)
        {
            double gw = Math.Min(gw0, wa.X1 - wa.X0);
            double gh = Math.Min(gh0, wa.Y1 - wa.Y0);
            if (x + gw > wa.X1 + 0.01 && x > wa.X0) { x = wa.X0; y += rowH + gap; rowH = 0; }
            // позиция оригинала так, чтобы вся группа (с копиями) встала в (x..x+gw, y..y+gh)
            double maxPx = Math.Max(0, wa.X1 - wa.X0 - gw);
            double maxPy = Math.Max(0, wa.Y1 - wa.Y0 - gh);
            st.PosX = Math.Clamp(x - wa.X0 - dx, 0, maxPx);
            st.PosY = Math.Clamp(y - wa.Y0 - dy, 0, maxPy);
            x += gw + gap; rowH = Math.Max(rowH, gh);
        }
    }

    // ---------- JSON для JS (2D/3D/STL) ----------

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string SceneJson(StampState state, List<Instance> instances, bool for3d,
        IReadOnlyCollection<Guid>? batchSel = null)
    {
        var p = state.Plate;
        var wa = GetWorkArea(p);
        var obj = new Dictionary<string, object?>
        {
            ["w"] = p.Width, ["d"] = p.Depth, ["h"] = p.Height,
            ["topChamfer"] = p.TopChamfer,
            ["chH"] = p.BottomChamferHeight, ["chA"] = p.BottomChamferAngleDeg,
            ["bottomOffset"] = p.BottomOffsetMm,
            ["margins"] = new[] { Math.Max(0, p.MarginTop), Math.Max(0, p.MarginRight), Math.Max(0, p.MarginBottom), Math.Max(0, p.MarginLeft) },
            ["work"] = new[] { wa.X0, wa.Y0, wa.X1, wa.Y1 },
            ["reliefMm"] = Math.Max(0.001, state.ReliefMm),
            ["sel"] = state.SelectedStampId?.ToString(),
            ["instances"] = instances.Select(i =>
            {
                var stOf = state.Stamps.FirstOrDefault(s => s.Id == i.StampId);
                return new Dictionary<string, object?>
            {
                ["id"] = i.StampId.ToString(),
                ["name"] = i.StampName,
                ["kind"] = i.Kind == StampKind.Text ? "text" : "icon",
                ["copy"] = i.CopyIndex,
                ["px"] = stOf?.PosX ?? 0, ["py"] = stOf?.PosY ?? 0,
                ["ax"] = i.AnchorX, ["ay"] = i.AnchorY,
                ["w"] = i.W, ["h"] = i.H,
                ["bx"] = i.Bx, ["by"] = i.By, ["bw"] = i.Bw, ["bh"] = i.Bh,
                ["rot"] = i.RotDeg, ["mirror"] = i.Mirror,
                ["depth"] = Math.Max(0.001, i.DepthMm),
                ["rough"] = i.Rough,
                ["roughMm"] = i.RoughMm,
                ["src"] = for3d ? null : i.Src,
                ["isSvg"] = for3d ? null : (bool?)i.IsSvgData,
                ["svgUrl"] = null,
                ["svgText"] = for3d ? i.SvgText : null
            };
            }).ToList()
        };
        // v1.5: идентификаторы выбранных оттисков — подсветка на 2D-схеме (ключ только при выборе)
        if (batchSel is { Count: > 0 })
            obj["batch"] = batchSel.Select(x => x.ToString()).ToArray();
        return JsonSerializer.Serialize(obj, JsonOpts);
    }

    public static async Task<List<Instance>> FillSourcesAsync(
        StampState state, List<Instance> instances, StampLibrary lib, bool for3d = false)
    {
        foreach (var i in instances)
        {
            var st = state.Stamps.FirstOrDefault(s => s.Id == i.StampId);
            if (st == null) continue;
            i.DepthMm = Math.Max(0.001, st.DepthMm ?? state.ReliefMm);
            i.Rough = st.RoughBottom;
            i.RoughMm = st.RoughBottom ? Math.Max(0, st.RoughDeltaUm) / 1000.0 : 0;

            if (st.Kind == StampKind.Text)
            {
                if (state.TextSvgs.TryGetValue(st.Id.ToString(), out var tsvg))
                {
                    if (for3d) { i.SvgText = tsvg; i.Src = ""; i.IsSvgData = false; }
                    else
                    {
                        i.Src = "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(tsvg));
                        i.IsSvgData = true; i.SvgText = null;
                    }
                }
                continue;
            }

            if (st.Source == StampSource.Base)
            {
                // v1.7: штамп из библиотеки (папка stamp/). SVG достаём на сервере —
                // и для 3D (текст в парсер), и для 2D (ссылка на endpoint /stamp-file).
                var svg = await lib.GetSvgTextAsync(st.SvgPath);
                if (svg == null)
                {
                    // миграция старого проекта (пути «icons/…» вшитого набора):
                    // ищем штамп по имени файла в импортированных библиотеках
                    var legacy = lib.ResolveByFileName(st.SvgPath);
                    if (legacy != null)
                    {
                        st.SvgPath = legacy.SvgPath;
                        st.PreviewPath = legacy.SvgPath;
                        st.Otryad = legacy.Category;
                        st.Rass = lib.Library(legacy.LibId)?.Name ?? legacy.LibId;
                        svg = await lib.GetSvgTextAsync(st.SvgPath);
                    }
                }
                if (svg == null)
                {
                    // библиотека с этим штампом не импортирована — честно предупреждаем,
                    // оттиск пропускается в 3D/экспортах, но остаётся в проекте
                    var missMsg = $"«{st.DisplayName}»: библиотека не импортирована — оттиск пропущен (добавьте zip в «Библиотеках»)";
                    if (!state.Warnings.Contains(missMsg))
                        state.Warnings.Add(missMsg);
                    i.Src = "";
                    i.IsSvgData = false;
                    i.SvgText = null;
                    continue;
                }
                if (for3d)
                {
                    i.SvgText = svg;
                    i.Src = "";
                    i.IsSvgData = false;
                }
                else
                {
                    i.Src = StampLibrary.PreviewUrl(st.SvgPath);
                    i.IsSvgData = false;
                    i.SvgText = null;
                }
            }
            else if (state.CustomSvgs.TryGetValue(st.CustomSvgId, out var customSvg))
            {
                if (for3d)
                {
                    i.SvgText = customSvg;
                    i.Src = "";
                }
                else
                {
                    i.Src = "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(customSvg));
                    i.IsSvgData = true;
                    i.SvgText = null;
                }
            }
        }
        await Task.CompletedTask;
        return instances;
    }
}
