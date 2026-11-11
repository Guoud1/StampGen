using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Clipper2Lib;

// StampGen — серверный разбор SVG в плоские контуры (ядро геометрии).
// © Guoud (iljamakeev@yandex.ru). Все права защищены. Условия: LICENSE.md.
// v1.6: СЕРВЕРНЫЙ разбор SVG в плоские контуры (вместо SVGLoader в браузере).
// Раньше 3D-модель строил браузер (three.js + three-bvh-csg) — на слабом ПК это
// подвешивало вкладку («Страница не отвечает»). Теперь контуры оттисков, булевы
// операции и меш целиком строит СЕРВЕР (та же машина, все ядра ПК), а браузер
// только показывает готовые треугольники.
// Поддерживается то, что встречается в наборах и текстовых оттисках:
//   path (M/L/H/V/C/S/Q/T/A/Z, относительные), rect/circle/ellipse/line/
//   polygon/polyline, трансформации (matrix/translate/scale/rotate/skew),
//   наследование fill/stroke, fill-rule (evenodd/nonzero), viewBox,
//   обводка без заливки -> офсет контура (Clipper InflatePaths).
// Растровые изображения, градиенты и <use> не поддерживаются (не встречаются).
// =============================================================================

namespace StampGen.Services;

/// <summary>SVG -> контуры (Clipper2). Координаты — в единицах viewBox, y вниз.</summary>
public static class SvgShapes
{
    public readonly record struct Vb(double X, double Y, double W, double H);

    /// <summary>Разобранный SVG: объединённые залитые области + параметры viewBox.</summary>
    public sealed class ShapeSet
    {
        public PathsD Shapes = new();   // залитые области и «ленты» обводки (объединены)
        public Vb Box = new(0, 0, 100, 100);
    }

    private const int Prec = 5;   // 0.01 нм —Clipper precision (мм-координаты)

    private static readonly ConcurrentDictionary<(int Hash, int Seg), ShapeSet?> Cache = new();

    /// <summary>Union с повышенной точностью.</summary>
    private static PathsD UnionAll(PathsD subject, FillRule rule)
    {
        var cd = new ClipperD(Prec);
        cd.AddSubject(subject);
        var sol = new PathsD();
        cd.Execute(ClipType.Union, rule, sol);
        return sol;
    }

    // v1.7 ФИКС: раньше хэш считался по ВЫБОРочным символам (шаг s.Length/4096) —
    // у больших наборов значков вероятны коллизии, а кэш по коллизии отдавал ЧУЖИЕ
    // контуры (не тот значок на 3D/в STL). Теперь FNV-1a 64 по КАЖДОМУ символу:
    // для типичных SVG (1–300 КБ) это микросекунды, зато коллизии практически исключены.
    private static int HashStr(string s)
    {
        unchecked
        {
            ulong h = 1469598103934665603UL;   // FNV-1a 64, смещение
            foreach (var ch in s) { h ^= ch; h *= 1099511628211UL; }
            return (int)(h ^ (h >> 32));
        }
    }

    /// <summary>Разобрать SVG в контуры (кэш по содержимому + детализации).</summary>
    public static ShapeSet? Parse(string svgText, int curveSegments)
    {
        var key = (HashStr(svgText), curveSegments);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var res = ParseInner(svgText, curveSegments);
        if (Cache.Count > 600) Cache.Clear();
        Cache[key] = res;
        return res;
    }

    /// <summary>Диагностика разбора (используется тестами).</summary>
    public static Action<string>? DebugLog;

    private static ShapeSet? ParseInner(string svgText, int seg)
    {
        XDocument doc;
        try { doc = XDocument.Parse(svgText); }
        catch (Exception ex) { DebugLog?.Invoke("xml: " + ex.Message); return null; }
        var root = doc.Root;
        if (root == null || root.Name.LocalName != "svg") { DebugLog?.Invoke("root: " + root?.Name.LocalName); return null; }

        var vb = ReadViewBox(root);
        if (vb.W <= 0 || vb.H <= 0) { DebugLog?.Invoke($"vb: {vb.W}x{vb.H}"); return null; }

        var fills = new List<PathOut>();      // залитые контуры
        var strokes = new List<StrokeOut>();  // обводки без заливки
        // стиль корня: SVG по умолчанию заливает чёрным; непрозрачность — полная
        var rootStyle = Style.Def();
        rootStyle.Fill = "#000000";
        Walk(root, Matrix3.Identity(), rootStyle, fills, strokes, seg);

        var result = new ShapeSet { Box = vb };
        var ink = new List<PathsD>();      // чёрные/тёмные — добавляем
        var white = new List<PathsD>();    // белые — вырезаем (ими рисуют «дырки» поверх чёрного)

        foreach (var f in fills)
        {
            if (f.Subs.Count == 0) continue;
            var paths = ToPathsD(f.Subs);
            var rule = f.EvenOdd ? FillRule.EvenOdd : FillRule.NonZero;
            var u = f.Subs.Count > 1 ? UnionAll(paths, rule) : paths;
            if (u.Count == 0) continue;
            (f.IsWhite ? white : ink).Add(u);
        }
        foreach (var s in strokes)
        {
            if (s.Subs.Count == 0) continue;
            double half = Math.Max(0.01, s.Width / 2.0);
            // открытые подконтуры — плоские концы; замкнутые — КОЛЬЦО
            // (EndType.Polygon раздувает замкнутый путь в сплошную фигуру,
            //  поэтому кольцо = внешний офсет минус внутренний)
            for (int i = 0; i < s.Subs.Count; i++)
            {
                var one = ToPathsD(new List<List<Pt>> { s.Subs[i] });
                PathsD shape;
                if (s.Closed[i])
                {
                    var outer = Clipper.InflatePaths(one, half, JoinType.Round, EndType.Polygon, 2.0, Prec);
                    var inner = Clipper.InflatePaths(one, -half, JoinType.Round, EndType.Polygon, 2.0, Prec);
                    shape = inner.Count == 0
                        ? outer
                        : Clipper.Difference(outer, inner, FillRule.NonZero, Prec);
                }
                else
                {
                    shape = Clipper.InflatePaths(one, half, JoinType.Round, EndType.Butt, 2.0, Prec);
                }
                if (shape.Count == 0) continue;
                (s.IsWhite ? white : ink).Add(shape);
            }
        }

        var inkUnion = new PathsD();
        foreach (var g in ink) inkUnion.AddRange(g);
        DebugLog?.Invoke($"parse: fills={fills.Count} strokes={strokes.Count} ink={inkUnion.Count} white={white.Count}");
        if (inkUnion.Count == 0) return null;
        result.Shapes = UnionAll(inkUnion, FillRule.NonZero);
        if (white.Count > 0)
        {
            var wu = new PathsD();
            foreach (var g in white) wu.AddRange(g);
            var whiteUnion = UnionAll(wu, FillRule.NonZero);
            var cut = Clipper.Difference(result.Shapes, whiteUnion, FillRule.NonZero, Prec);
            if (cut.Count > 0) result.Shapes = cut;   // если белое полностью покрыло чёрное — оставляем как есть
        }
        if (result.Shapes.Count == 0) { DebugLog?.Invoke("union empty"); return null; }
        return result;
    }

    // ================= v1.7: группировка по цветам (импорт библиотек) =================

    /// <summary>Разобранный SVG с СОХРАНЕНИЕМ цветов: тёмные группы по цветам
    /// (для вычисления границ между разными цветами) + светлые (дырки).</summary>
    public sealed class GroupedSet
    {
        public Vb Box = new(0, 0, 100, 100);
        /// <summary>Тёмные группы: цвет (как в файле, нижний регистр) -> объединённые контуры.</summary>
        public List<KeyValuePair<string, PathsD>> Ink = new();
        /// <summary>Объединённые светлые области (белые и близкие — «дырки»).</summary>
        public PathsD White = new();
    }

    /// <summary>Разобрать SVG в группы по цветам без склейки разных цветов.
    /// Используется при импорте библиотек (ЧБ + граница 1% на переходах цвета).</summary>
    public static GroupedSet? ParseGrouped(string svgText, int curveSegments)
    {
        XDocument doc;
        try { doc = XDocument.Parse(svgText); }
        catch (Exception ex) { DebugLog?.Invoke("grouped xml: " + ex.Message); return null; }
        var root = doc.Root;
        if (root == null || root.Name.LocalName != "svg") { DebugLog?.Invoke("grouped root: " + root?.Name.LocalName); return null; }

        var vb = ReadViewBox(root);
        if (vb.W <= 0 || vb.H <= 0) { DebugLog?.Invoke($"grouped vb: {vb.W}x{vb.H}"); return null; }

        var fills = new List<PathOut>();
        var strokes = new List<StrokeOut>();
        var rootStyle = Style.Def();
        rootStyle.Fill = "#000000";
        Walk(root, Matrix3.Identity(), rootStyle, fills, strokes, curveSegments);

        var res = new GroupedSet { Box = vb };
        var inkByColor = new Dictionary<string, List<PathsD>>(StringComparer.Ordinal);
        var white = new List<PathsD>();

        foreach (var f in fills)
        {
            if (f.Subs.Count == 0) continue;
            var paths = ToPathsD(f.Subs);
            var rule = f.EvenOdd ? FillRule.EvenOdd : FillRule.NonZero;
            var u = f.Subs.Count > 1 ? UnionAll(paths, rule) : paths;
            if (u.Count == 0) continue;
            if (f.IsWhite) { white.Add(u); continue; }
            var key = (f.Color ?? "").Trim().ToLowerInvariant();
            if (!inkByColor.TryGetValue(key, out var list)) inkByColor[key] = list = new List<PathsD>();
            list.Add(u);
        }
        foreach (var s in strokes)
        {
            if (s.Subs.Count == 0) continue;
            double half = Math.Max(0.01, s.Width / 2.0);
            for (int i = 0; i < s.Subs.Count; i++)
            {
                var one = ToPathsD(new List<List<Pt>> { s.Subs[i] });
                PathsD shape;
                if (s.Closed[i])
                {
                    var outer = Clipper.InflatePaths(one, half, JoinType.Round, EndType.Polygon, 2.0, Prec);
                    var inner = Clipper.InflatePaths(one, -half, JoinType.Round, EndType.Polygon, 2.0, Prec);
                    shape = inner.Count == 0 ? outer
                        : Clipper.Difference(outer, inner, FillRule.NonZero, Prec);
                }
                else
                {
                    shape = Clipper.InflatePaths(one, half, JoinType.Round, EndType.Butt, 2.0, Prec);
                }
                if (shape.Count == 0) continue;
                if (s.IsWhite) { white.Add(shape); continue; }
                var key = (s.Color ?? "").Trim().ToLowerInvariant();
                if (!inkByColor.TryGetValue(key, out var list)) inkByColor[key] = list = new List<PathsD>();
                list.Add(shape);
            }
        }

        foreach (var (color, list) in inkByColor)
        {
            PathsD all = new();
            foreach (var g in list) all.AddRange(g);
            var u = UnionAll(all, FillRule.NonZero);
            if (u.Count > 0) res.Ink.Add(new KeyValuePair<string, PathsD>(color, u));
        }
        if (white.Count > 0)
        {
            PathsD wu = new();
            foreach (var g in white) wu.AddRange(g);
            res.White = UnionAll(wu, FillRule.NonZero);
        }
        return res.Ink.Count == 0 ? null : res;
    }

    /// <summary>
    /// Полоса-граница между двумя РАЗНЫМИ цветами (v1.7): точки, отстоящие не далее
    /// gap/2 от каждой из областей. Ширина полосы = gap. Пустой результат — цвета
    /// нигде не соприкасаются. Полосу нужно ВЫЧЕСТЬ из обеих областей (щёль) или
    /// нарисовать БЕЛЫМ ПОВЕРХ (визуальная граница) — см. StampLibrary.
    /// </summary>
    public static PathsD GapBand(PathsD a, PathsD b, double gap)
    {
        if (a.Count == 0 || b.Count == 0 || gap <= 0) return new PathsD();
        double half = Math.Max(1e-6, gap / 2.0);
        var oa = Clipper.InflatePaths(a, half, JoinType.Round, EndType.Polygon, 2.0, Prec);
        var ob = Clipper.InflatePaths(b, half, JoinType.Round, EndType.Polygon, 2.0, Prec);
        if (oa.Count == 0 || ob.Count == 0) return new PathsD();
        return Clipper.Intersect(oa, ob, FillRule.NonZero, Prec);
    }

    /// <summary>Контуры -> строка SVG path d (M/L/Z, 4 знака, инвариантная культура).</summary>
    public static string ToSvgPathD(PathsD paths)
    {
        var sb = new StringBuilder();
        string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
        foreach (var p in paths)
        {
            if (p.Count < 3) continue;
            sb.Append('M').Append(N(p[0].x)).Append(' ').Append(N(p[0].y));
            for (int i = 1; i < p.Count; i++)
                sb.Append('L').Append(N(p[i].x)).Append(' ').Append(N(p[i].y));
            sb.Append('Z');
        }
        return sb.ToString();
    }

    // ================= размещение на пластине =================

    /// <summary>
    /// Переводит контуры viewBox в координаты пластины (мм): viewBox натягивается
    /// ровно на бокс w×h (как 2D-схема), затем зеркало, поворот вокруг центра
    /// бокса (по часовой в осях пластины — как canvas.rotate) и сдвиг в якорь.
    /// Результат — PathsD в мм, оси пластины (y вниз, 0..W / 0..D).
    /// </summary>
    public static PathsD Place(ShapeSet set, double w, double h, double ax, double ay,
        double rotDeg, bool mirror)
    {
        var vb = set.Box;
        double sx = w / Math.Max(1e-9, vb.W);
        double sy = h / Math.Max(1e-9, vb.H);
        double a = rotDeg * Math.PI / 180.0;
        double ca = Math.Cos(a), sa = Math.Sin(a);
        double cx = w / 2.0, cy = h / 2.0;

        var res = new PathsD(set.Shapes.Count);
        foreach (var path in set.Shapes)
        {
            var np = new PathD(path.Count);
            foreach (var p in path)
            {
                // viewBox -> бокс
                double px = (p.x - vb.X) * sx;
                double py = (p.y - vb.Y) * sy;
                if (mirror) px = w - px;
                // поворот вокруг центра бокса
                double dx = px - cx, dy = py - cy;
                double rx = ca * dx - sa * dy + cx;
                double ry = sa * dx + ca * dy + cy;
                np.Add(new PointD(ax + rx, ay + ry));
            }
            if (np.Count >= 3) res.Add(np);
        }
        return res;
    }

    // ================= обход дерева =================

    private readonly record struct Pt(double X, double Y);

    private sealed class PathOut
    {
        public List<List<Pt>> Subs = new();
        public bool EvenOdd;
        public bool IsWhite;
        public string Color = "#000000";   // v1.7: исходный цвет — для группировки при импорте библиотек
    }

    private sealed class StrokeOut
    {
        public List<List<Pt>> Subs = new();
        public List<bool> Closed = new();
        public double Width = 1;
        public bool IsWhite;
        public string Color = "#000000";
    }

    private struct Style
    {
        public string? Fill;
        public string? Stroke;
        public double FillOpacity;
        public double StrokeOpacity;
        public double StrokeWidth;
        public bool EvenOdd;

        public static Style Def() => new()
        {
            Fill = null, Stroke = null,
            FillOpacity = 1, StrokeOpacity = 1, StrokeWidth = 1, EvenOdd = false
        };
    }

    private static Style Merge(Style parent, XElement el)
    {
        var s = parent;
        var fill = AttrOrStyle(el, "fill");
        if (fill != null) s.Fill = fill == "none" ? null : fill;
        var stroke = AttrOrStyle(el, "stroke");
        if (stroke != null) s.Stroke = stroke == "none" ? null : stroke;
        var fo = AttrOrStyle(el, "fill-opacity");
        if (fo != null && double.TryParse(fo, NumberStyles.Float, CultureInfo.InvariantCulture, out var fov)) s.FillOpacity = fov;
        var so = AttrOrStyle(el, "stroke-opacity");
        if (so != null && double.TryParse(so, NumberStyles.Float, CultureInfo.InvariantCulture, out var sov)) s.StrokeOpacity = sov;
        var sw = AttrOrStyle(el, "stroke-width");
        if (sw != null && double.TryParse(sw, NumberStyles.Float, CultureInfo.InvariantCulture, out var swv)) s.StrokeWidth = swv;
        var fr = AttrOrStyle(el, "fill-rule");
        if (fr != null) s.EvenOdd = fr == "evenodd";
        // opacity < 0.05 — элемент невидим
        var op = el.Attribute("opacity")?.Value;
        if (op != null && double.TryParse(op, NumberStyles.Float, CultureInfo.InvariantCulture, out var ov) && ov < 0.05)
        {
            s.FillOpacity = 0;
            s.StrokeOpacity = 0;
        }
        return s;
    }

    private static string? AttrOrStyle(XElement el, string name)
    {
        var v = el.Attribute(name)?.Value;
        if (v != null) return v;
        var style = el.Attribute("style")?.Value;
        if (style == null) return null;
        foreach (var chunk in style.Split(';'))
        {
            var idx = chunk.IndexOf(':');
            if (idx <= 0) continue;
            if (chunk[..idx].Trim() == name) return chunk[(idx + 1)..].Trim();
        }
        return null;
    }

    private static Vb ReadViewBox(XElement root)
    {
        var vbA = root.Attribute("viewBox")?.Value;
        if (!string.IsNullOrWhiteSpace(vbA))
        {
            var parts = vbA.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var vx) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var vy) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var vw) &&
                double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var vh) &&
                vw > 0 && vh > 0)
                return new Vb(vx, vy, vw, vh);
        }
        double w = ParseLen(root.Attribute("width")?.Value, 100);
        double h = ParseLen(root.Attribute("height")?.Value, 100);
        return new Vb(0, 0, w <= 0 ? 100 : w, h <= 0 ? 100 : h);
    }

    private static double ParseLen(string? raw, double def)
    {
        if (string.IsNullOrWhiteSpace(raw)) return def;
        var s = raw.Trim().TrimEnd("pxptmmcminq%").Trim();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : def;
    }

    private static void Walk(XElement el, Matrix3 ctm, Style style,
        List<PathOut> fills, List<StrokeOut> strokes, int seg)
    {
        var name = el.Name.LocalName;
        if (name is "defs" or "clipPath" or "mask" or "pattern" or "linearGradient"
            or "radialGradient" or "metadata" or "title" or "desc") return;

        var local = ParseTransform(el.Attribute("transform")?.Value, ctm);
        var st = Merge(style, el);

        switch (name)
        {
            case "svg":
            case "g":
            case "a":
            case "switch":   // v1.7: <switch> встречается в наборах — обходим как группу
                foreach (var child in el.Elements()) Walk(child, local, st, fills, strokes, seg);
                return;
            case "path":
            {
                var d = el.Attribute("d")?.Value;
                if (string.IsNullOrWhiteSpace(d)) return;
                var subs = PathData.Flatten(d, seg);
                AddPaths(subs, local, st, fills, strokes);
                return;
            }
            case "rect":
            {
                double x = Num(el, "x"), y = Num(el, "y");
                double w = Num(el, "width"), h = Num(el, "height");
                if (w <= 0 || h <= 0) return;
                var subs = new List<List<Pt>> { new()
                {
                    new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h), new(x, y)
                } };
                AddPaths(subs, local, st, fills, strokes);
                return;
            }
            case "circle":
            {
                double cx = Num(el, "cx"), cy = Num(el, "cy"), r = Num(el, "r");
                if (r <= 0) return;
                var subs = new List<List<Pt>> { EllipsePts(cx, cy, r, r, 0, seg) };
                AddPaths(subs, local, st, fills, strokes);
                return;
            }
            case "ellipse":
            {
                double cx = Num(el, "cx"), cy = Num(el, "cy");
                double rx = Num(el, "rx"), ry = Num(el, "ry");
                if (rx <= 0 || ry <= 0) return;
                var subs = new List<List<Pt>> { EllipsePts(cx, cy, rx, ry, 0, seg) };
                AddPaths(subs, local, st, fills, strokes);
                return;
            }
            case "line":
            {
                var subs = new List<List<Pt>> { new()
                {
                    new(Num(el, "x1"), Num(el, "y1")), new(Num(el, "x2"), Num(el, "y2"))
                } };
                AddPaths(subs, local, st, fills, strokes);
                return;
            }
            case "polygon":
            {
                var pts = ReadPoints(el);
                if (pts.Count < 2) return;
                var closed = new List<Pt>(pts) { pts[0] };
                AddPaths(new List<List<Pt>> { closed }, local, st, fills, strokes);
                return;
            }
            case "polyline":
            {
                var pts = ReadPoints(el);
                if (pts.Count < 2) return;
                AddPaths(new List<List<Pt>> { pts }, local, st, fills, strokes);
                return;
            }
        }
    }

    private static void AddPaths(List<List<Pt>> subs, Matrix3 m, Style st,
        List<PathOut> fills, List<StrokeOut> strokes)
    {
        bool transformed = false;
        for (int i = 0; i < subs.Count; i++)
        {
            var src = subs[i];
            var dst = new List<Pt>(src.Count);
            foreach (var p in src) dst.Add(new Pt(m.X(p.X, p.Y), m.Y(p.X, p.Y)));
            subs[i] = dst;
            transformed = true;
        }
        _ = transformed;

        bool hasFill = st.Fill != null && st.FillOpacity > 0.05;
        bool hasStroke = st.Stroke != null && st.StrokeOpacity > 0.05;
        // ширина обводки задаётся в ЛОКАЛЬНЫХ единицах — масштабируем её CTM
        // (scale(0.26458) в наборах — иначе ленты в 4 раза толще и сливаются в блоб)
        double ctmScale = Math.Sqrt(Math.Abs(m.A * m.D - m.B * m.C));
        if (ctmScale < 1e-9) ctmScale = 1;
        // как в JS (SVGLoader): заливка приоритетнее; обводка — только когда заливки нет
        if (hasFill)
        {
            fills.Add(new PathOut { Subs = subs, EvenOdd = st.EvenOdd, IsWhite = IsWhiteColor(st.Fill), Color = st.Fill ?? "#000000" });
        }
        else if (hasStroke)
        {
            strokes.Add(new StrokeOut
            {
                Subs = subs,
                Closed = subs.Select(IsClosed).ToList(),
                Width = st.StrokeWidth * ctmScale,
                IsWhite = IsWhiteColor(st.Stroke),
                Color = st.Stroke!
            });
        }
    }

    private static bool IsClosed(List<Pt> p)
        => p.Count > 2 && Math.Abs((p[0].X - p[^1].X) + (p[0].Y - p[^1].Y)) < 1e-9;

    // ---------- цвета ----------

    private static readonly Dictionary<string, double> NamedLum = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = 1, ["ghostwhite"] = 1, ["whitesmoke"] = 0.96, ["snow"] = 0.99,
        ["ivory"] = 0.99, ["beige"] = 0.96, ["honeydew"] = 0.97, ["mintcream"] = 0.98,
        ["azure"] = 0.97, ["aliceblue"] = 0.96, ["lavender"] = 0.94, ["seashell"] = 0.96,
        ["oldlace"] = 0.95, ["floralwhite"] = 1, ["linen"] = 0.95, ["cornsilk"] = 0.96,
        ["lightyellow"] = 0.98, ["lightgoldenrodyellow"] = 0.96, ["gainsboro"] = 0.86,
        ["black"] = 0, ["gray"] = 0.52, ["grey"] = 0.52, ["silver"] = 0.75,
        ["dimgray"] = 0.34, ["dimgrey"] = 0.34, ["darkgray"] = 0.55, ["darkgrey"] = 0.55,
        ["lightgray"] = 0.83, ["lightgrey"] = 0.83, ["red"] = 0.21, ["crimson"] = 0.18,
        ["darkred"] = 0.15, ["brown"] = 0.19, ["firebrick"] = 0.2, ["maroon"] = 0.12,
        ["orange"] = 0.48, ["gold"] = 0.51, ["yellow"] = 0.53, ["olive"] = 0.33,
        ["green"] = 0.25, ["lime"] = 0.44, ["darkgreen"] = 0.13, ["forestgreen"] = 0.19,
        ["blue"] = 0.07, ["navy"] = 0.04, ["darkblue"] = 0.05, ["skyblue"] = 0.49,
        ["cyan"] = 0.36, ["aqua"] = 0.36, ["teal"] = 0.17, ["purple"] = 0.12,
        ["violet"] = 0.31, ["magenta"] = 0.24, ["fuchsia"] = 0.24, ["pink"] = 0.55,
        ["orangered"] = 0.27, ["chocolate"] = 0.26, ["sienna"] = 0.18, ["indigo"] = 0.07
    };

    /// <summary>Яркость цвета 0..1; null — не удалось понять (трактуем как чернила).</summary>
    public static double? Luminance(string? css)
    {
        if (string.IsNullOrWhiteSpace(css)) return null;
        var s = css.Trim().ToLowerInvariant();
        if (s == "none" || s == "transparent" || s == "currentcolor" || s == "inherit") return null;
        if (NamedLum.TryGetValue(s, out var named)) return named;
        try
        {
            if (s.StartsWith("#"))
            {
                var hex = s[1..];
                if (hex.Length == 3) hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
                if (hex.Length == 8) hex = hex[..6];   // #AARRGGBB -> RRGGBB
                if (hex.Length != 6) return null;
                int r = Convert.ToInt32(hex[..2], 16);
                int g = Convert.ToInt32(hex[2..4], 16);
                int b = Convert.ToInt32(hex[4..6], 16);
                return (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
            }
            if (s.StartsWith("rgb"))
            {
                var parts = s.Split(new[] { '(', ',', ')' }, StringSplitOptions.RemoveEmptyEntries);
                var nums = parts.Skip(1).Select(t => double.TryParse(t.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0).Take(3).ToList();
                if (nums.Count < 3) return null;
                if (s.Contains('%')) { nums[0] /= 100; nums[1] /= 100; nums[2] /= 100; return 0.299 * nums[0] + 0.587 * nums[1] + 0.114 * nums[2]; }
                return (0.299 * nums[0] + 0.587 * nums[1] + 0.114 * nums[2]) / 255.0;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Белые и близкие к белым заливки — это «дырки» в оттиске (вырезаем), порог 0.85.</summary>
    private static bool IsWhiteColor(string? css)
        => (Luminance(css) ?? 0) >= 0.85;

    private static PathsD ToPathsD(List<List<Pt>> subs)
    {
        var res = new PathsD(subs.Count);
        foreach (var s in subs)
        {
            if (s.Count < 3) continue;
            var p = new PathD(s.Count);
            foreach (var pt in s) p.Add(new PointD(pt.X, pt.Y));
            res.Add(p);
        }
        return res;
    }

    private static double Num(XElement el, string name)
        => double.TryParse(el.Attribute(name)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static List<Pt> ReadPoints(XElement el)
    {
        var raw = el.Attribute("points")?.Value ?? "";
        var nums = new List<double>();
        foreach (var tok in Tokenize(raw))
            if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                nums.Add(v);
        var pts = new List<Pt>(nums.Count / 2);
        for (int i = 0; i + 1 < nums.Count; i += 2) pts.Add(new Pt(nums[i], nums[i + 1]));
        return pts;
    }

    private static List<Pt> EllipsePts(double cx, double cy, double rx, double ry, double rotDeg, int seg)
    {
        int n = Math.Clamp(seg * 2, 12, 64);
        var pts = new List<Pt>(n + 1);
        double rot = rotDeg * Math.PI / 180.0;
        for (int i = 0; i <= n; i++)
        {
            double t = 2.0 * Math.PI * i / n;
            double x = rx * Math.Cos(t), y = ry * Math.Sin(t);
            pts.Add(new Pt(cx + x * Math.Cos(rot) - y * Math.Sin(rot),
                           cy + x * Math.Sin(rot) + y * Math.Cos(rot)));
        }
        return pts;
    }

    // ================= трансформации =================

    private readonly struct Matrix3
    {
        public readonly double A, B, C, D, E, F;
        public Matrix3(double a, double b, double c, double d, double e, double f)
        { A = a; B = b; C = c; D = d; E = e; F = f; }

        public static Matrix3 Identity() => new(1, 0, 0, 1, 0, 0);

        public double X(double x, double y) => A * x + C * y + E;
        public double Y(double x, double y) => B * x + D * y + F;

        public Matrix3 Mul(Matrix3 m) => new(   // this * m (m применяется первым)
            A * m.A + C * m.B,
            B * m.A + D * m.B,
            A * m.C + C * m.D,
            B * m.C + D * m.D,
            A * m.E + C * m.F + E,
            B * m.E + D * m.F + F);
    }

    private static Matrix3 ParseTransform(string? raw, Matrix3 parent)
    {
        if (string.IsNullOrWhiteSpace(raw)) return parent;
        var m = Matrix3.Identity();
        int pos = 0;
        while (pos < raw.Length)
        {
            while (pos < raw.Length && raw[pos] != '(' && !char.IsLetter(raw[pos])) pos++;
            if (pos >= raw.Length) break;
            int nameStart = pos;
            while (pos < raw.Length && char.IsLetter(raw[pos])) pos++;
            var fname = raw[nameStart..pos].Trim();
            while (pos < raw.Length && raw[pos] != '(') pos++;
            if (pos >= raw.Length) break;
            pos++;   // (
            int end = raw.IndexOf(')', pos);
            if (end < 0) break;
            var argsRaw = raw[pos..end];
            pos = end + 1;
            var args = new List<double>();
            foreach (var tok in Tokenize(argsRaw))
                if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    args.Add(v);

            Matrix3 t = fname switch
            {
                "matrix" when args.Count >= 6 => new Matrix3(args[0], args[1], args[2], args[3], args[4], args[5]),
                "translate" when args.Count >= 2 => new Matrix3(1, 0, 0, 1, args[0], args[1]),
                "translate" when args.Count == 1 => new Matrix3(1, 0, 0, 1, args[0], 0),
                "scale" when args.Count >= 2 => new Matrix3(args[0], 0, 0, args[1], 0, 0),
                "scale" when args.Count == 1 => new Matrix3(args[0], 0, 0, args[0], 0, 0),
                "rotate" when args.Count >= 3 => Rot(args[0], args[1], args[2]),
                "rotate" when args.Count == 1 => Rot(args[0], 0, 0),
                "skewX" when args.Count >= 1 => new Matrix3(1, 0, Math.Tan(args[0] * Math.PI / 180.0), 1, 0, 0),
                "skewY" when args.Count >= 1 => new Matrix3(1, Math.Tan(args[0] * Math.PI / 180.0), 0, 1, 0, 0),
                _ => Matrix3.Identity()
            };
            m = m.Mul(t);
        }
        return parent.Mul(m);

        static Matrix3 Rot(double deg, double cx, double cy)
        {
            double a = deg * Math.PI / 180.0;
            double c = Math.Cos(a), s = Math.Sin(a);
            return new Matrix3(c, s, -s, c, cx - c * cx + s * cy, cy - s * cx - c * cy);
        }
    }

    private static IEnumerable<string> Tokenize(string raw)
    {
        var sb = new StringBuilder();
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch) || ch == ',')
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            }
            else sb.Append(ch);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    // ================= парсер path d =================

    /// <summary>Отладочный доступ к флэттеру path d (для тестов).</summary>
    public static List<List<(double X, double Y)>> DebugFlatten(string d, int seg)
        => PathData.Flatten(d, seg).Select(s2 => s2.Select(p => (p.X, p.Y)).ToList()).ToList();

    private static class PathData
    {
        /// <summary>Разворачивает 'd' в полилинии (по seg точек на кривую).</summary>
        public static List<List<Pt>> Flatten(string d, int seg)
        {
            var subs = new List<List<Pt>>();
            var cur = new List<Pt>();
            var start = new Pt(0, 0);
            var pos = new Pt(0, 0);
            var prevCtrl = new Pt(0, 0);
            char cmd = ' ';
            int i = 0;

            while (i < d.Length)
            {
                SkipSep(d, ref i);
                if (i >= d.Length) break;
                if (char.IsLetter(d[i]))
                {
                    cmd = d[i];
                    i++;
                    if (cmd == 'Z' || cmd == 'z')
                    {
                        if (cur.Count > 0)
                        {
                            if (Dist(cur[0], cur[^1]) > 1e-9) cur.Add(cur[0]);
                            subs.Add(cur);
                            cur = new List<Pt>();
                        }
                        pos = start;
                        continue;
                    }
                }
                else if (cmd == ' ') { cmd = 'L'; }

                switch (cmd)
                {
                    case 'M':
                        if (!TryReadPt(d, ref i, out var pm)) { i++; continue; }
                        if (cur.Count > 0) { subs.Add(cur); cur = new List<Pt>(); }
                        cur.Add(pm); pos = pm; start = pm; cmd = 'L'; prevCtrl = pos; break;
                    case 'm':
                        if (!TryReadPt(d, ref i, out var pmd)) { i++; continue; }
                        var npm = Add(pos, pmd);
                        if (cur.Count > 0) { subs.Add(cur); cur = new List<Pt>(); }
                        cur.Add(npm); pos = npm; start = npm; cmd = 'l'; prevCtrl = pos; break;
                    case 'L': while (TryReadPt(d, ref i, out var pl)) { cur.Add(ToAbs(pos, pl, false)); pos = cur[^1]; } prevCtrl = pos; break;
                    case 'l': while (TryReadPt(d, ref i, out var pll)) { cur.Add(ToAbs(pos, pll, true)); pos = cur[^1]; } prevCtrl = pos; break;
                    case 'H': while (TryReadNum(d, ref i, out var hx)) { pos = new Pt(hx, pos.Y); cur.Add(pos); } prevCtrl = pos; break;
                    case 'h': while (TryReadNum(d, ref i, out var hxx)) { pos = new Pt(pos.X + hxx, pos.Y); cur.Add(pos); } prevCtrl = pos; break;
                    case 'V': while (TryReadNum(d, ref i, out var vy)) { pos = new Pt(pos.X, vy); cur.Add(pos); } prevCtrl = pos; break;
                    case 'v': while (TryReadNum(d, ref i, out var vyy)) { pos = new Pt(pos.X, pos.Y + vyy); cur.Add(pos); } prevCtrl = pos; break;
                    case 'C': Cubic(d, ref i, ref pos, ref prevCtrl, cur, false, seg); break;
                    case 'c': Cubic(d, ref i, ref pos, ref prevCtrl, cur, true, seg); break;
                    case 'S': SmoothCubic(d, ref i, ref pos, ref prevCtrl, cur, false, seg); break;
                    case 's': SmoothCubic(d, ref i, ref pos, ref prevCtrl, cur, true, seg); break;
                    case 'Q': Quad(d, ref i, ref pos, ref prevCtrl, cur, false, seg); break;
                    case 'q': Quad(d, ref i, ref pos, ref prevCtrl, cur, true, seg); break;
                    case 'T': SmoothQuad(d, ref i, ref pos, ref prevCtrl, cur, false, seg); break;
                    case 't': SmoothQuad(d, ref i, ref pos, ref prevCtrl, cur, true, seg); break;
                    case 'A': Arc(d, ref i, ref pos, cur, start, false, seg); break;
                    case 'a': Arc(d, ref i, ref pos, cur, start, true, seg); break;
                    default:
                        // неизвестная команда — пропускаем один аргумент, чтобы не зациклиться
                        if (!TryReadNum(d, ref i, out _)) i++;
                        break;
                }
            }
            if (cur.Count > 0) subs.Add(cur);
            // выкидываем вырожденные
            subs.RemoveAll(s => s.Count < 2);
            return subs;
        }

        private static double Dist(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        private static Pt Add(Pt a, Pt b) => new(a.X + b.X, a.Y + b.Y);

        private static Pt ToAbs(Pt pos, Pt rel, bool isRel)
            => isRel ? new Pt(pos.X + rel.X, pos.Y + rel.Y) : rel;

        private static void SkipSep(string s, ref int i)
        {
            while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ',')) i++;
        }

        private static bool TryReadNum(string s, ref int i, out double v)
        {
            SkipSep(s, ref i);
            int start = i;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
            bool dot = false, exp = false;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsDigit(c)) i++;
                else if (c == '.' && !dot) { dot = true; i++; }
                else if ((c == 'e' || c == 'E') && !exp && i > start) { exp = true; i++; if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++; }
                else break;
            }
            if (i == start || (i == start + 1 && (s[start] == '+' || s[start] == '-'))) { v = 0; i = start; return false; }
            v = double.Parse(s[start..i].Replace('.', '.'), CultureInfo.InvariantCulture);
            return true;
        }

        private static bool TryReadPt(string s, ref int i, out Pt p)
        {
            p = default;
            if (!TryReadNum(s, ref i, out var x)) return false;
            if (!TryReadNum(s, ref i, out var y)) return false;
            p = new Pt(x, y);
            return true;
        }

        private static void Cubic(string s, ref int i, ref Pt pos, ref Pt prevCtrl, List<Pt> cur, bool rel, int seg)
        {
            while (TryReadPt(s, ref i, out var c1) && TryReadPt(s, ref i, out var c2) && TryReadPt(s, ref i, out var to))
            {
                var a1 = ToAbs(pos, c1, rel);
                var a2 = ToAbs(pos, c2, rel);
                var at = ToAbs(pos, to, rel);
                AddBezier(pos, a1, a2, at, seg, cur);
                prevCtrl = a2;
                pos = at;
            }
        }

        private static void SmoothCubic(string s, ref int i, ref Pt pos, ref Pt prevCtrl, List<Pt> cur, bool rel, int seg)
        {
            while (TryReadPt(s, ref i, out var c2) && TryReadPt(s, ref i, out var to))
            {
                var a1 = Reflect(prevCtrl, pos);
                var a2 = ToAbs(pos, c2, rel);
                var at = ToAbs(pos, to, rel);
                AddBezier(pos, a1, a2, at, seg, cur);
                prevCtrl = a2;
                pos = at;
            }
        }

        private static void Quad(string s, ref int i, ref Pt pos, ref Pt prevCtrl, List<Pt> cur, bool rel, int seg)
        {
            while (TryReadPt(s, ref i, out var c) && TryReadPt(s, ref i, out var to))
            {
                var ac = ToAbs(pos, c, rel);
                var at = ToAbs(pos, to, rel);
                AddQuad(pos, ac, at, seg, cur);
                prevCtrl = ac;
                pos = at;
            }
        }

        private static void SmoothQuad(string s, ref int i, ref Pt pos, ref Pt prevCtrl, List<Pt> cur, bool rel, int seg)
        {
            while (TryReadPt(s, ref i, out var to))
            {
                var ac = Reflect(prevCtrl, pos);
                var at = ToAbs(pos, to, rel);
                AddQuad(pos, ac, at, seg, cur);
                prevCtrl = ac;
                pos = at;
            }
        }

        private static Pt Reflect(Pt c, Pt around) => new(2 * around.X - c.X, 2 * around.Y - c.Y);

        private static void AddBezier(Pt p0, Pt p1, Pt p2, Pt p3, int seg, List<Pt> cur)
        {
            int n = Math.Clamp(seg, 2, 32);
            for (int k = 1; k <= n; k++)
            {
                double t = (double)k / n;
                double u = 1 - t;
                double x = u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X;
                double y = u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y;
                cur.Add(new Pt(x, y));
            }
        }

        private static void AddQuad(Pt p0, Pt p1, Pt p2, int seg, List<Pt> cur)
        {
            int n = Math.Clamp(seg, 2, 32);
            for (int k = 1; k <= n; k++)
            {
                double t = (double)k / n;
                double u = 1 - t;
                double x = u * u * p0.X + 2 * u * t * p1.X + t * t * p2.X;
                double y = u * u * p0.Y + 2 * u * t * p1.Y + t * t * p2.Y;
                cur.Add(new Pt(x, y));
            }
        }

        private static void Arc(string s, ref int i, ref Pt pos, List<Pt> cur, Pt start, bool rel, int seg)
        {
            while (TryReadNum(s, ref i, out var rx) && TryReadNum(s, ref i, out var ry) &&
                   TryReadNum(s, ref i, out var rot) && TryReadNum(s, ref i, out var laf) &&
                   TryReadNum(s, ref i, out var sf) && TryReadPt(s, ref i, out var to))
            {
                var at = ToAbs(pos, to, rel);
                AddArc(pos, rx, ry, rot, laf > 0.5, sf > 0.5, at, seg, cur);
                pos = at;
            }
        }

        private static void AddArc(Pt from, double rx, double ry, double rotDeg, bool largeArc, bool sweep, Pt to, int seg, List<Pt> cur)
        {
            if (Math.Abs(from.X - to.X) < 1e-12 && Math.Abs(from.Y - to.Y) < 1e-12) return;
            rx = Math.Abs(rx); ry = Math.Abs(ry);
            if (rx < 1e-12 || ry < 1e-12)
            {
                cur.Add(to);
                return;
            }
            double rot = rotDeg * Math.PI / 180.0;
            double cosR = Math.Cos(rot), sinR = Math.Sin(rot);
            double dx2 = (from.X - to.X) / 2, dy2 = (from.Y - to.Y) / 2;
            double x1 = cosR * dx2 + sinR * dy2;
            double y1 = -sinR * dx2 + cosR * dy2;
            double lam = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
            if (lam > 1)
            {
                double sq = Math.Sqrt(lam);
                rx *= sq; ry *= sq;
            }
            double sign = largeArc != sweep ? 1 : -1;
            double denom = rx * rx * y1 * y1 + ry * ry * x1 * x1;
            double coef = denom < 1e-12 ? 0 : sign * Math.Sqrt(Math.Max(0, (rx * rx * ry * ry) / denom - 1));
            double cxp = coef * rx * y1 / ry;
            double cyp = -coef * ry * x1 / rx;
            double cx = cosR * cxp - sinR * cyp + (from.X + to.X) / 2;
            double cy = sinR * cxp + cosR * cyp + (from.Y + to.Y) / 2;

            double Ang(double ux, double uy, double vx, double vy)
            {
                double dot = ux * vx + uy * vy;
                double len = Math.Sqrt(ux * ux + uy * uy) * Math.Sqrt(vx * vx + vy * vy);
                double a = Math.Acos(Math.Clamp(dot / (len == 0 ? 1 : len), -1, 1));
                if (ux * vy - uy * vx < 0) a = -a;
                return a;
            }
            double theta1 = Ang(1, 0, (x1 - cxp) / rx, (y1 - cyp) / ry);
            double dTheta = Ang((x1 - cxp) / rx, (y1 - cyp) / ry, (-x1 - cxp) / rx, (-y1 - cyp) / ry);
            if (!sweep && dTheta > 0) dTheta -= 2 * Math.PI;
            else if (sweep && dTheta < 0) dTheta += 2 * Math.PI;

            int n = Math.Clamp(seg * 2, 8, 48);
            for (int k = 1; k <= n; k++)
            {
                double t = theta1 + dTheta * k / n;
                double ex = rx * Math.Cos(t), ey = ry * Math.Sin(t);
                cur.Add(new Pt(cosR * ex - sinR * ey + cx, sinR * ex + cosR * ey + cy));
            }
        }
    }
}
