using System.Diagnostics;
using System.Security.Cryptography;
using Clipper2Lib;

// StampGen — серверная генерация 3D-меша и STL (v1.6: вместо CSG в браузере).
// © Guoud (iljamakeev@yandex.ru). Все права защищены. Условия: LICENSE.md.
//
// Подход — 2.5D-декомпозиция (без 3D-CSG):
//   карманы оттисков — плоские области (Clipper2), глубина у каждого своя.
//   Террасы: сортируем уровни глубины по убыванию, кумулятивные объединения
//   U_j (карманы с глубиной >= d_j); терраса j = U_j \ U_(j-1) — дно на H-d_j;
//   стенки — по границе террас, высота от дна террасы до уровня соседа снаружи;
//   верхняя грань = прямоугольник верха минус все карманы.
//   Пластина (стенки, нижняя фаска/кайма, дно) — лофт сечений (порт plateGeometry).
//   Шершавое дно — бугорки-призмы ниже дна кармана, внутри контура оттиска.
// Лимиты (число треугольников / время) + отмена: если ПК не справляется,
// сборка останавливается БЕЗ краха и UI показывает извинение.
// =============================================================================

namespace StampGen.Services;

/// <summary>Слишком тяжёлая сборка: превышен лимит треугольников или времени.</summary>
public class MeshTooHeavyException : Exception
{
    public MeshTooHeavyException(string msg) : base(msg) { }
}

public sealed class MeshBuildResult
{
    /// <summary>Треугольная плотка: x,y,z на вершину, по 3 вершины на треугольник (мм, пластина центрирована).</summary>
    public float[] Positions = Array.Empty<float>();
    public long Triangles;
    public double BuildMs;
    public int StampsCut;
    public int Skipped;
    // v2.0: контроль замкнутости (watertight)
    /// <summary>Открытые рёбер (щели) ДО заварки.</summary>
    public long OpenEdgesBefore;
    /// <summary>Открытые рёбра ПОСЛЕ заварки — должно быть 0 у исправной модели.</summary>
    public long OpenEdgesAfter;
    /// <summary>Разделено T-стыков при заварке.</summary>
    public long SealTSplits;
    /// <summary>Добавлено треугольников-заплат при заварке.</summary>
    public long SealFilled;
    /// <summary>Рёбра с 3+ треугольниками (печати не мешают, фиксируются для отчёта).</summary>
    public long NonManifoldEdges;
    /// <summary>Модель замкнута: каждое ребро принадлежит ровно двум треугольникам.</summary>
    public bool Watertight => OpenEdgesAfter == 0;
}

public static class MeshGen
{
    public sealed class Options
    {
        /// <summary>Точек на кривую SVG (4=черновик … 24=ультра — как QUALITY в viewer3d).</summary>
        public int CurveSegments { get; init; } = 8;
        /// <summary>null — точные глубины (STL); число — визуальное усиление рельефа.</summary>
        public double? DepthScale { get; init; } = null;
        public long MaxTriangles { get; init; } = 1_600_000;
        public int TimeoutSec { get; init; } = 240;
    }

    private const int ClipPrec = 5;             // 1e-5 мм = 10 нм (точность Clipper)
    private const double MinTriArea = 1e-12;    // мм²
    // v1.8: микроинфляция карманов. Копии оттиска, стоящие ВПРИТЫК (зазор 0),
    // дают контуры, касающиеся в общих вершинах («щипки»); Delaunay-триангулятор
    // Clipper2 на таких «щипках» строит гигантские НАЛОЖЕННЫЕ треугольники —
    // на модели это выглядело как «полигон в правом верхнем углу, закрывающий
    // половину пластины» (проверено на проекте пользователя: перекрытие 734 мм²
    // верхней грани). Инфляция на 5 мкм сливает касающиеся карманы в union,
    // после чего триангуляция сходится точно (проверено: дельта 0.00 мм²,
    // перекрытий 0). 5 мкм ≪ глубины рельефа и не видна на печати.
    private const double PocketGrow = 0.005;    // мм

    /// <summary>Построить меш пластины с карманами. Синхронно, CPU. progress(фаза, 0..1).</summary>
    public static MeshBuildResult Build(PlateParams plate, List<LayoutSolver.Instance> instances,
        double fallbackReliefMm, Options opt, Action<string, double>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        double W = plate.Width, D = plate.Depth, H = plate.Height;
        var res = new MeshBuildResult();
        var pos = new List<float>(1 << 16);

        void ThrowIfOver()
        {
            ct.ThrowIfCancellationRequested();
            if (pos.Count / 9 >= opt.MaxTriangles)
                throw new MeshTooHeavyException($"Превышен лимит {opt.MaxTriangles:N0} треугольников.");
            if (sw.Elapsed.TotalSeconds > opt.TimeoutSec)
                throw new MeshTooHeavyException($"Превышено время построения ({opt.TimeoutSec} с).");
        }

        // ---------- 1. карманы оттисков в координатах пластины ----------
        progress?.Invoke("Разбираю SVG-контуры…", 0.02);
        var topRect = TopRectPoly(plate);
        var pockets = new List<Pocket>();

        var alive = instances.Where(i => i.SvgText != null).ToList();

        // v1.7: дедупликация ключей ДО разбора (последовательно и дёшево);
        // одинаковые оттиски разбираются один раз, копии — ссылаются на тот же Pocket.
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var uniqueIdx = new List<int>(alive.Count);
        for (int idx = 0; idx < alive.Count; idx++)
        {
            ThrowIfOver();
            var inst = alive[idx];
            double depth = Math.Max(0.001, inst.DepthMm > 0 ? inst.DepthMm : fallbackReliefMm);
            double rough = Math.Max(0, inst.RoughMm);
            if (opt.DepthScale is { } ex2)
            {
                depth *= ex2;
                rough *= ex2;
            }
            double cap0 = H / 2.0;
            if (depth > cap0) depth = cap0;
            if (depth + rough > cap0) rough = Math.Max(0, cap0 - depth);
            var key = SvgHash(inst.SvgText!) + "|" + inst.W.ToString("0.###") + "|" + inst.H.ToString("0.###") +
                      "|" + inst.RotDeg.ToString("0.##") + "|" + (inst.Mirror ? 1 : 0) +
                      "|" + inst.AnchorX.ToString("0.###") + "|" + inst.AnchorY.ToString("0.###") +
                      "|" + depth.ToString("0.####") + "|" + rough.ToString("0.####");
            if (seenKeys.Add(key)) uniqueIdx.Add(idx);
            else res.Skipped++;
        }

        // v1.7: разбор SVG/раскладка/клип — ПАРАЛЛЕЛЬНО на всех ядрах ПК
        // (раньше — строго по одному, на сотнях оттисков это были десятки минут).
        var results = new Pocket?[alive.Count];
        int done = 0;
        try
        {
            Parallel.For(0, uniqueIdx.Count, idx0 =>
            {
                ct.ThrowIfCancellationRequested();
                int idx = uniqueIdx[idx0];
                var inst = alive[idx];
                try
                {
                    double depth = Math.Max(0.001, inst.DepthMm > 0 ? inst.DepthMm : fallbackReliefMm);
                    double rough = Math.Max(0, inst.RoughMm);
                    if (opt.DepthScale is { } ex)
                    {
                        depth *= ex;
                        rough *= ex;
                    }
                    double cap = H / 2.0;                                   // как в JS: не глубже половины толщины
                    if (depth > cap) depth = cap;
                    if (depth + rough > cap) rough = Math.Max(0, cap - depth);

                    var set = SvgShapes.Parse(inst.SvgText!, opt.CurveSegments);
                    if (set == null) return;   // res.Skipped посчитаем после
                    var placed = SvgShapes.Place(set, inst.W, inst.H, inst.AnchorX, inst.AnchorY, inst.RotDeg, inst.Mirror);
                    if (placed.Count == 0) return;
                    // карман не трогает верхнюю фаску: обрезаем по прямоугольнику верхней грани
                    var clipped0 = Clipper.Intersect(placed, new PathsD { topRect }, FillRule.NonZero, ClipPrec);
                    if (clipped0.Count == 0) return;
                    // v1.8: микроинфляция + возврат в пределы верхней грани
                    // (лечит «щипки» копий впритык — см. комментарий у PocketGrow)
                    var grown = Clipper.InflatePaths(clipped0, PocketGrow, JoinType.Round, EndType.Polygon, 2, ClipPrec);
                    var clipped = Clipper.Intersect(grown, new PathsD { topRect }, FillRule.NonZero, ClipPrec);
                    if (clipped.Count == 0) return;
                    results[idx] = new Pocket(clipped, depth, rough, inst);
                }
                finally
                {
                    int d = Interlocked.Increment(ref done);
                    if (progress != null && (d % 10 == 0 || d == uniqueIdx.Count))
                        progress?.Invoke($"Строю карманы оттисков… ({d}/{uniqueIdx.Count})", 0.05 + 0.45 * d / Math.Max(1, uniqueIdx.Count));
                }
            });
        }
        catch (AggregateException agg)
        {
            // аккуратно разворачиваем: отмена — отмена, тяжесть — извинение, остальное — вниз
            foreach (var inner in agg.InnerExceptions)
            {
                if (inner is OperationCanceledException) throw inner;
                if (inner is MeshTooHeavyException) throw inner;
            }
            throw agg.InnerExceptions[0];
        }
        foreach (var pk in results)
        {
            if (pk == null) { res.Skipped++; continue; }
            pockets.Add(pk);
        }
        if (pockets.Count == 0)
        {
            // только пластина
            EmitPlateSidesAndBottom(plate, pos);
            EmitTopFace(plate, new PathsD(), pos, ThrowIfOver);
            Finish(res, pos, sw, 0);
            return res;
        }

        // ---------- 2. уровни глубины (террасы) ----------
        progress?.Invoke("Объединяю карманы…", 0.55);
        var levels = pockets.Select(p => Math.Round(p.Depth * 10000)).Distinct().OrderByDescending(x => x).ToList();
        var cum = new List<PathsD>();       // cum[i] = объединение карманов с глубиной >= levels[i]
        var terraces = new List<PathsD>();  // терраса i = cum[i] \ cum[i-1]
        PathsD prev = new();
        foreach (var lv in levels)
        {
            ThrowIfOver();
            PathsD layer = new();
            foreach (var pk in pockets)
                if (Math.Round(pk.Depth * 10000) == lv) layer.AddRange(pk.Paths);
            var u = layer.Count == 1 ? new PathsD { layer[0] } : UnionAll(layer, FillRule.NonZero);
            var cu = prev.Count == 0 ? u : UnionAll(Concat(prev, u), FillRule.NonZero);
            var terr = prev.Count == 0 ? cu : Clipper.Difference(cu, prev, FillRule.NonZero, ClipPrec);
            cum.Add(cu);
            terraces.Add(terr);
            prev = cu;
        }

        // ---------- 3. дно террас + стенки ----------
        progress?.Invoke("Нарезаю дно и стенки…", 0.7);
        double topInner = plate.TopChamfer ? PlateParams.TopChamferSize : 0;
        // v1.7: bbox-префильтр для ProbeDepth — раньше каждая стенка проверяла точку
        // по ВСЕМ путям каждого уровня (O(рёбра × пути) — на сложных оттисках это
        // давало «то 2 секунды, то 20 минут»). Теперь путь проверяется только если
        // его bbox содержит точку (4 сравнения) — ускорение в десятки раз.
        var probeCums = cum.Select(c => c.Select(p => (p, Clipper.GetBounds(p))).ToArray()).ToList();
        for (int i = 0; i < terraces.Count; i++)
        {
            ThrowIfOver();
            double dj = levels[i] / 10000.0;
            double floorY = H - dj;
            // дно кармана (видно сверху); v1.8: с контролем триангуляции
            int rangeStart = pos.Count;
            var tris = TriangulateChecked(terraces[i], ThrowIfOver);
            foreach (var t in tris)
                EmitTriY(pos, t[0].x, t[0].y, t[1].x, t[1].y, t[2].x, t[2].y, floorY, W, D, +1);
            // v2.0: стенки по РЁБРАМ ТРИАНГУЛЯЦИИ дна — раньше они строились по исходным
            // вершинам путей, а триангулятор Delaunay смещает/сливает вершины (682 -> 591,
            // общих 10) — контур кармана превращался в кольцо микрощелей (2546 открытых
            // рёбер на одном черепе: «белые пропуски» вокруг глаз в CHITUBOX). Теперь
            // низ стенок берётся из фактических треугольников дна — совпадение точное.
            bool singleLevel = terraces.Count == 1;
            EmitWallsFromFloor(pos, rangeStart, H, dj, floorY,
                singleLevel ? null : probeCums, singleLevel ? null : levels, W, D, topInner, plate, ThrowIfOver);
            progress?.Invoke("Нарезаю дно и стенки…", 0.7 + 0.15 * (i + 1) / terraces.Count);
        }

        // ---------- 4. шершавое дно ----------
        progress?.Invoke("Шершавое дно…", 0.86);
        foreach (var pk in pockets)
        {
            if (pk.Rough <= 0) continue;
            ThrowIfOver();
            EmitRoughBumps(pos, pk, H, W, D);
        }

        // ---------- 5. пластина ----------
        progress?.Invoke("Собираю пластину…", 0.92);
        EmitPlateSidesAndBottom(plate, pos);
        EmitTopFace(plate, prev, pos, ThrowIfOver);

        // ---------- 6. v2.0: заварка щелей + проверка замкнутости ----------
        progress?.Invoke("Проверяю замкнутость граней…", 0.97);
        var seal = MeshSealer.Seal(pos);
        res.OpenEdgesBefore = seal.OpenBefore;
        res.OpenEdgesAfter = seal.OpenAfter;
        res.SealTSplits = seal.TSplits;
        res.SealFilled = seal.FilledTriangles;
        res.NonManifoldEdges = seal.NonManifold;

        progress?.Invoke("Готово", 1);
        Finish(res, pos, sw, pockets.Count);
        return res;
    }

    // ================= STL =================

    /// <summary>Бинарный STL из плотки позиций (мм).</summary>
    public static byte[] ToBinaryStl(float[] positions)
    {
        long triCount = positions.Length / 9;
        using var ms = new MemoryStream(84 + (int)triCount * 50);
        using var w = new BinaryWriter(ms);
        var header = new byte[80];
        var hdr = System.Text.Encoding.ASCII.GetBytes("StampGen binary STL (mm) (c) Guoud");
        Array.Copy(hdr, header, Math.Min(80, hdr.Length));
        w.Write(header);
        w.Write((uint)triCount);
        var buf = new byte[50];
        for (long t = 0; t < triCount; t++)
        {
            int o = (int)t * 9;
            float ax = positions[o], ay = positions[o + 1], az = positions[o + 2];
            float bx = positions[o + 3], by = positions[o + 4], bz = positions[o + 5];
            float cx = positions[o + 6], cy = positions[o + 7], cz = positions[o + 8];
            double ux = bx - ax, uy = by - ay, uz = bz - az;
            double vx = cx - ax, vy = cy - ay, vz = cz - az;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-12) { nx = 0; ny = 1; nz = 0; len = 1; }
            w.Write((float)(nx / len)); w.Write((float)(ny / len)); w.Write((float)(nz / len));
            w.Write(ax); w.Write(ay); w.Write(az);
            w.Write(bx); w.Write(by); w.Write(bz);
            w.Write(cx); w.Write(cy); w.Write(cz);
            w.Write((ushort)0);
        }
        w.Flush();
        return ms.ToArray();
    }

    // ================= внутреннее =================

    private sealed record Pocket(PathsD Paths, double Depth, double Rough, LayoutSolver.Instance Inst);

    private static void Finish(MeshBuildResult res, List<float> pos, Stopwatch sw, int stamps)
    {
        res.Positions = pos.ToArray();
        res.Triangles = pos.Count / 9;
        res.BuildMs = sw.Elapsed.TotalMilliseconds;
        res.StampsCut = stamps;
    }

    private static PathsD Concat(PathsD a, PathsD b) { var r = new PathsD(a.Count + b.Count); r.AddRange(a); r.AddRange(b); return r; }

    /// <summary>Union с повышенной точностью (2-аргументный Union жёстко использует precision=2).</summary>
    private static PathsD UnionAll(PathsD subject, FillRule rule)
    {
        var cd = new ClipperD(ClipPrec);
        cd.AddSubject(subject);
        var sol = new PathsD();
        cd.Execute(ClipType.Union, rule, sol);
        return sol;
    }

    private static PathD TopRectPoly(PlateParams p)
    {
        double inset = p.TopChamfer ? PlateParams.TopChamferSize : 0;
        return Clipper.MakePath(new double[]
        {
            inset, inset, p.Width - inset, inset, p.Width - inset, p.Depth - inset, inset, p.Depth - inset
        });
    }

    private static PathsD Triangulate(PathsD paths)
    {
        if (paths.Count == 0) return new PathsD();
        // v2.0: предсварка путей — у входов бывают почти совпадающие вершины
        // (расстояние ~1e-5…3e-5 мм), на которых триангулятор молча смещает/сливает
        // вершины. Сварка убирает причину и заодно уменьшает число вершин.
        var welded = WeldPaths(paths);
        var r = Clipper.Triangulate(welded, 4, out PathsD tris);
        if (r == TriangulateResult.success) return tris;
        // неудача — пробуем ещё раз через явный union (на всякий случай)
        var u = UnionAll(welded, FillRule.EvenOdd);
        r = Clipper.Triangulate(WeldPaths(u), 4, out tris);
        return r == TriangulateResult.success ? tris : new PathsD();
    }

    /// <summary>v2.0: убрать подряд идущие вершины ближе WeldGrid (0.1 мкм) —
    /// стабилизирует триангуляцию и уменьшает размер меша.</summary>
    internal const double WeldGrid = 1e-4;

    internal static PathsD WeldPaths(PathsD paths)
    {
        var res = new PathsD(paths.Count);
        foreach (var p in paths)
        {
            if (p.Count < 3) continue;
            var np = new PathD(p.Count);
            foreach (var pt in p)
            {
                if (np.Count > 0)
                {
                    var last = np[np.Count - 1];
                    double dx = pt.x - last.x, dy = pt.y - last.y;
                    if (dx * dx + dy * dy < WeldGrid * WeldGrid) continue;
                }
                np.Add(pt);
            }
            // замыкание: последняя != первая
            while (np.Count > 1)
            {
                var last = np[np.Count - 1];
                var first = np[0];
                double dx = last.x - first.x, dy = last.y - first.y;
                if (dx * dx + dy * dy < WeldGrid * WeldGrid) np.RemoveAt(np.Count - 1); else break;
            }
            if (np.Count >= 3) res.Add(np);
        }
        return res.Count > 0 ? res : paths;
    }

    /// <summary>Площадь области с учётом ориентации (внешние +, дырки −) — как выдаёт булева операция.</summary>
    private static double RegionArea(PathsD ps) => ps.Sum(p => Clipper.Area(p));

    /// <summary>
    /// v1.8: триангуляция С КОНТРОЛЕМ. Delaunay Clipper2 на вырожденных входах
    /// («щипки», касания контуров) молча выдаёт перекрывающиеся треугольники —
    /// раньше это давало «полигон» на модели. Теперь сумму площадей треугольников
    /// сверяем с площадью области; при расхождении — режем область сеткой
    /// (ячейки триангулируются по отдельности, крупные делятся рекурсивно);
    /// если и это не помогло — честная ошибка «слишком тяжело» (UI покажет извинение).
    /// </summary>
    private static PathsD TriangulateChecked(PathsD paths, Action? throwIfOver)
    {
        if (paths.Count == 0) return new PathsD();
        double expected = RegionArea(paths);
        if (expected <= 1e-9) return new PathsD();

        var tris = Triangulate(paths);
        if (TriOk(tris, expected)) return tris;

        // фолбэк: сетка 10 мм -> 2.5 мм -> 0.625 мм (рекурсия по неудачным ячейкам)
        var b = Clipper.GetBounds(paths);
        var result = new PathsD();
        GridTri(paths, b.left, b.top, b.right, b.bottom, 0, result, throwIfOver);
        return result;
    }

    private static bool TriOk(PathsD tris, double expected)
    {
        double got = 0;
        foreach (var t in tris) got += Math.Abs(Clipper.Area(t));
        double tol = Math.Max(0.001 * Math.Abs(expected), 0.05);
        return Math.Abs(got - Math.Abs(expected)) <= tol;
    }

    private static void GridTri(PathsD paths, double x0, double y0, double x1, double y1,
        int depth, PathsD result, Action? throwIfOver)
    {
        throwIfOver?.Invoke();
        if (x1 - x0 < 1e-9 || y1 - y0 < 1e-9) return;
        var rect = new RectD(x0, y0, x1, y1);
        var piece = Clipper.RectClip(rect, paths, ClipPrec);
        if (piece.Count == 0) return;
        double expected = RegionArea(piece);
        if (expected <= 1e-9) return;
        var tris = Triangulate(piece);
        if (TriOk(tris, expected) || depth >= 3)
        {
            if (!TriOk(tris, expected))
                throw new MeshTooHeavyException("Не удалось корректно триангулировать область рельефа (вырожденная геометрия).");
            foreach (var t in tris) result.Add(t);
            return;
        }
        double xm = (x0 + x1) / 2, ym = (y0 + y1) / 2;
        GridTri(paths, x0, y0, xm, ym, depth + 1, result, throwIfOver);
        GridTri(paths, xm, y0, x1, ym, depth + 1, result, throwIfOver);
        GridTri(paths, x0, ym, xm, y1, depth + 1, result, throwIfOver);
        GridTri(paths, xm, ym, x1, y1, depth + 1, result, throwIfOver);
    }

    /// <summary>Треугольник на высоте y (мм, координаты пластины -> центрированные). Нормаль ±Y.</summary>
    private static void EmitTriY(List<float> pos, double ax, double ay, double bx, double by,
        double cx, double cy, double y, double W, double D, double wantNy)
    {
        // n.y = dx1*dz2 - dz1*dx2 с обратным знаком (z = y_пластины), поэтому: знак area2 == wantNy -> разворот
        double area2 = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
        if (Math.Abs(area2) < MinTriArea) return;
        if (Math.Sign(area2) == Math.Sign(wantNy)) (bx, by, cx, cy) = (cx, cy, bx, by);
        Push(pos, ax - W / 2, y, ay - D / 2, bx - W / 2, y, by - D / 2, cx - W / 2, y, cy - D / 2);
    }

    /// <summary>Стенки кармана по РЁБРАМ ТРИАНГУЛЯЦИИ дна (v2.0).
    /// Треугольники дна (диапазон [startIdx..pos.Count)) обходим, берём граничные рёбра
    /// (принадлежащие одному треугольнику) и для каждого строим стенку от дна до уровня
    /// соседа снаружи. Низ стенки совпадает с дном ТОЧНО (те же float-вершины).
    /// Треугольники дна при EmitTriY всегда обходятся по часовой в плоскости XZ
    /// (нормаль +Y), поэтому карман (воздух) — СПРАВА от направленного ребра.</summary>
    private static void EmitWallsFromFloor(List<float> pos, int startIdx, double H, double dj, double floorY,
        List<(PathD p, Clipper2Lib.RectD b)[]>? cums, List<double>? levels,
        double W, double D, double topInner, PlateParams plate, Action? throwIfOver = null)
    {
        // локальные вершины диапазона по точным float-координатам
        var vmap = new Dictionary<(int, int, int), int>(1 << 10);
        var vx = new List<double>(); var vy = new List<double>(); var vz = new List<double>();
        int Vert(float x, float y, float z)
        {
            var key = (BitConverter.SingleToInt32Bits(x), BitConverter.SingleToInt32Bits(y), BitConverter.SingleToInt32Bits(z));
            if (!vmap.TryGetValue(key, out int id))
            {
                id = vx.Count; vmap[key] = id;
                vx.Add(x); vy.Add(y); vz.Add(z);
            }
            return id;
        }

        var counts = new Dictionary<long, int>();
        var owner = new Dictionary<long, (int a, int b)>();
        for (int o = startIdx; o < pos.Count; o += 9)
        {
            int a = Vert(pos[o], pos[o + 1], pos[o + 2]);
            int b = Vert(pos[o + 3], pos[o + 4], pos[o + 5]);
            int c = Vert(pos[o + 6], pos[o + 7], pos[o + 8]);
            foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
            {
                long key = p < q ? (long)p << 32 | (uint)q : (long)q << 32 | (uint)p;
                counts[key] = counts.GetValueOrDefault(key) + 1;
                owner.TryAdd(key, (p, q));   // направленное ребро владельца (обход треугольника)
            }
        }

        int sinceCheck = 0;
        foreach (var (key, cnt) in counts)
        {
            if (cnt != 1 || !owner.TryGetValue(key, out var dir)) continue;
            if (++sinceCheck >= 4096) { sinceCheck = 0; throwIfOver?.Invoke(); }
            double ax = vx[dir.a], az = vz[dir.a];
            double bx = vx[dir.b], bz = vz[dir.b];
            double dx = bx - ax, dz = bz - az;
            double len = Math.Sqrt(dx * dx + dz * dz);
            if (len < 2 * WeldGrid) continue;   // микроребро — после сварки вершин закроется само

            // воздух (карман) справа => нормаль стенки (в карман) = (dz, -dx)/len,
            // наружу (к соседу-материалу) = (-dz, dx)/len
            double nx = dz / len, nz = -dx / len;
            double ox = -nx, oz = -nz;

            double dOut;
            if (cums == null)
            {
                dOut = 0;   // один уровень глубины: снаружи всегда верх пластины
            }
            else
            {
                // v2.0: зонд на ДВУХ расстояниях, берём минимум (мелководье) —
                // раньше перелёт зонда через тонкую перемычку давал слишком НИЗКУЮ
                // стенку (щель). Минимум может дать стенку чуть выше соседа — это
                // безопасно (уходит в материал), щелей не оставляет.
                double eps1 = Math.Min(0.02, Math.Max(4 * WeldGrid, len * 0.05));
                double d1 = ProbeDepthAt((ax + bx) / 2 + ox * eps1, (az + bz) / 2 + oz * eps1, cums, levels!, topInner, plate);
                double d2 = ProbeDepthAt((ax + bx) / 2 + ox * eps1 * 0.35, (az + bz) / 2 + oz * eps1 * 0.35, cums, levels!, topInner, plate);
                dOut = Math.Min(d1, d2);
            }

            if (dOut >= dj - 1e-9) continue;   // сосед глубже или равен — стенку рисует он
            double yBot = floorY;              // ниже (дно этого кармана)
            double yTop = H - dOut;            // выше
            if (yTop - yBot < 1e-9) continue;

            // vx/vz хранят УЖЕ центрированные координаты (как в pos) —
            // EmitWallQuad ожидает координаты пластины и сам вычитает W/2, D/2
            EmitWallQuad(pos, ax + W / 2, az + D / 2, bx + W / 2, bz + D / 2, yBot, yTop, nx, nz, W, D);
        }
    }

    /// <summary>Глубина в точке по уровням с учётом выхода за верхнюю грань (0 — материал/верх).</summary>
    private static double ProbeDepthAt(double mx, double my,
        List<(PathD p, Clipper2Lib.RectD b)[]> cums, List<double> levels, double topInner, PlateParams plate)
    {
        // у края верхней грани снаружи — фаска/верх: считаем как верх (0)
        if (mx < topInner - 1e-6 || my < topInner - 1e-6 ||
            mx > plate.Width - topInner + 1e-6 || my > plate.Depth - topInner + 1e-6)
            return 0;
        return ProbeDepth(mx, my, cums, levels);
    }

    private static void EmitWallQuad(List<float> pos, double x1, double z1, double x2, double z2,
        double yBot, double yTop, double wantNx, double wantNz, double W, double D)
    {
        // порядок (B1,B2,T2),(B1,T2,T1) даёт нормаль ~ (-dz,0,dx)
        // проверяем знак и при необходимости разворачиваем
        double n1x = -(z2 - z1), n1z = (x2 - x1);
        bool flip = n1x * wantNx + n1z * wantNz < 0;
        if (flip) (x1, z1, x2, z2) = (x2, z2, x1, z1);   // разворот ребра
        Push(
            pos,
            x1 - W / 2, yBot, z1 - D / 2,
            x2 - W / 2, yBot, z2 - D / 2,
            x2 - W / 2, yTop, z2 - D / 2);
        Push(
            pos,
            x1 - W / 2, yBot, z1 - D / 2,
            x2 - W / 2, yTop, z2 - D / 2,
            x1 - W / 2, yTop, z1 - D / 2);
    }

    /// <summary>Глубина в точке по уровням (v1.7: пути фильтруются по bbox — 4 сравнения,
    /// PointInPolygon только для кандидатов; чётность по-прежнему определяет дырки).</summary>
    private static double ProbeDepth(double x, double y, List<(PathD p, Clipper2Lib.RectD b)[]> cums, List<double> levels)
    {
        var pt = new PointD(x, y);
        for (int i = 0; i < cums.Count; i++)
        {
            var arr = cums[i];
            int cnt = 0;
            for (int k = 0; k < arr.Length; k++)
            {
                var (p, b) = arr[k];
                if (x < b.left || x > b.right || y < b.top || y > b.bottom) continue;
                if (Clipper.PointInPolygon(pt, p) != PointInPolygonResult.IsOutside) cnt++;
            }
            if ((cnt & 1) == 1) return levels[i] / 10000.0;
        }
        return 0;
    }

    /// <summary>Точка внутри области (объединение Clipper: внешние и отверстия — чёт/нечет).</summary>
    private static bool InsideParity(PathsD paths, PointD pt)
    {
        int cnt = 0;
        foreach (var path in paths)
            if (Clipper.PointInPolygon(pt, path) != PointInPolygonResult.IsOutside) cnt++;
        return (cnt & 1) == 1;
    }

    /// <summary>Бугорки шершавого дна: призмы ниже дна кармана (порт roughBumpGeoms из JS).</summary>
    private static void EmitRoughBumps(List<float> pos, Pocket pk, double H, double W, double D)
    {
        var inst = pk.Inst;
        // тот же ГПСЧ, что в JS: seed из id оттиска и глубины
        uint seed = 12345;
        if (!string.IsNullOrEmpty(inst.StampId.ToString()))
            foreach (var ch in inst.StampId.ToString()) seed = (uint)(seed * 31 + ch) | 1u;
        seed ^= (uint)Math.Round(pk.Depth * 100000);
        var rng = new Rng(seed);

        double area = inst.W * inst.H;
        int count = Math.Clamp((int)Math.Round(area / 0.14), 6, 320);
        double floorY = H - pk.Depth;
        var paths = pk.Paths;
        for (int k = 0; k < count; k++)
        {
            double mx = 0, my = 0;
            bool ok = false;
            for (int t = 0; t < 8 && !ok; t++)
            {
                // случайная точка в боксе оттиска
                double bx = inst.AnchorX + rng.Next() * inst.W;
                double by = inst.AnchorY + rng.Next() * inst.H;
                // небольшой джиттер вокруг точки — ищем точку внутри контура
                mx = bx; my = by;
                ok = InsideParity(paths, new PointD(mx, my));
            }
            if (!ok) continue;
            double h = pk.Rough * (0.35 + 0.65 * rng.Next());
            if (h <= 0.0005) continue;
            double bw = 0.10 + 0.22 * rng.Next();
            double bh = bw * (0.6 + 0.8 * rng.Next());
            EmitBox(pos, mx, my, bw, bh, floorY - h, floorY, W, D);
        }
    }

    /// <summary>Коробка (бугорок) ЗАКРЫТАЯ: 4 стенки + дно + верх (v2.0 — раньше верх был
    /// открыт, «крышей» служили треугольники дна, не совпадавшие рёбрами — щель).
    /// Коробка — самостоятельное замкнутое тело, утопленное в пластину ниже дна кармана.</summary>
    private static void EmitBox(List<float> pos, double cx, double cz, double w, double d,
        double yBot, double yTop, double W, double D)
    {
        double x0 = cx - w / 2, x1 = cx + w / 2, z0 = cz - d / 2, z1 = cz + d / 2;
        // стенки
        EmitWallQuad(pos, x0, z0, x1, z0, yBot, yTop, 0, -1, W, D);
        EmitWallQuad(pos, x1, z0, x1, z1, yBot, yTop, 1, 0, W, D);
        EmitWallQuad(pos, x1, z1, x0, z1, yBot, yTop, 0, 1, W, D);
        EmitWallQuad(pos, x0, z1, x0, z0, yBot, yTop, -1, 0, W, D);
        // дно (нормаль вниз)
        EmitTriY(pos, x0, z0, x1, z0, x1, z1, yBot, W, D, -1);
        EmitTriY(pos, x0, z0, x1, z1, x0, z1, yBot, W, D, -1);
        // верх (нормаль вверх) — v2.0
        EmitTriY(pos, x0, z0, x1, z0, x1, z1, yTop, W, D, +1);
        EmitTriY(pos, x0, z0, x1, z1, x0, z1, yTop, W, D, +1);
    }

    // ---------- пластина: лофт сечений (порт plateGeometry из viewer3d.js) ----------

    private static void EmitPlateSidesAndBottom(PlateParams p, List<float> pos)
    {
        double W = p.Width, D = p.Depth, H = p.Height;
        double zMaxCh = Math.Min(p.BottomChamferHeight, H - (p.TopChamfer ? 1 : 0));
        var zs = new SortedSet<double> { 0, H };
        if (p.BottomOffsetMm > 0 && Math.Abs(p.BottomChamferAngleDeg) >= 0.01) zs.Add(zMaxCh);
        if (p.TopChamfer && H - 1 > 0) zs.Add(H - 1);
        var zArr = zs.ToList();
        double omax = p.BottomOffsetMm;

        double Off(double z)
        {
            double o = 0;
            if (omax > 0 && Math.Abs(p.BottomChamferAngleDeg) >= 0.01)
            {
                double hb = zMaxCh;
                if (p.BottomChamferAngleDeg > 0) { if (z < hb) o = omax * (1 - z / hb); }
                else { o = (z <= hb) ? omax * (z / hb) : omax; }
            }
            if (p.TopChamfer && z > H - 1) o += (z - (H - 1));
            double lim = Math.Min(W, D) / 2 - 0.2;
            return Math.Min(o, lim);
        }

        var secs = zArr.Select(z => (z, o: Off(z))).ToList();

        (double x, double z) Corner((double z, double o) s, int i)
        {
            double hx = W / 2 - s.o, hz = D / 2 - s.o;
            return i switch
            {
                0 => (-hx, -hz),
                1 => (hx, -hz),
                2 => (hx, hz),
                _ => (-hx, hz)
            };
        }

        for (int k = 0; k < secs.Count - 1; k++)
        {
            var s0 = secs[k];
            var s1 = secs[k + 1];
            foreach (var (pi, qi) in new[] { (0, 1), (1, 2), (2, 3), (3, 0) })
            {
                var P0 = Corner(s0, pi);
                var Q0 = Corner(s0, qi);
                var P1 = Corner(s1, pi);
                var Q1 = Corner(s1, qi);
                // желаемая нормаль — наружу от центра (знак нужен только для выбора направления намотки)
                double mx = (P0.x + Q0.x + P1.x + Q1.x) / 4;
                double mz = (P0.z + Q0.z + P1.z + Q1.z) / 4;
                // лофт построен в центрированных координатах -> переводим в координаты пластины
                QuadN(pos, Q0.x + W / 2, s0.z, Q0.z + D / 2, P0.x + W / 2, s0.z, P0.z + D / 2,
                      P1.x + W / 2, s1.z, P1.z + D / 2, Q1.x + W / 2, s1.z, Q1.z + D / 2, mx, 0, mz, W, D);
            }
        }
        // нижняя крышка (нормаль -Y) — порядок как в JS
        var A = Corner(secs[0], 0);
        var B = Corner(secs[0], 1);
        var C = Corner(secs[0], 2);
        var Dd = Corner(secs[0], 3);
        EmitTriY(pos, A.x + W / 2, A.z + D / 2, B.x + W / 2, B.z + D / 2, C.x + W / 2, C.z + D / 2, secs[0].z, W, D, -1);
        EmitTriY(pos, A.x + W / 2, A.z + D / 2, C.x + W / 2, C.z + D / 2, Dd.x + W / 2, Dd.z + D / 2, secs[0].z, W, D, -1);
    }

    /// <summary>Верхняя грань: прямоугольник верха минус карманы, триангуляция (нормаль +Y).</summary>
    private static void EmitTopFace(PlateParams plate, PathsD pocketsUnion, List<float> pos, Action throwIfOver)
    {
        double inset = plate.TopChamfer ? PlateParams.TopChamferSize : 0;
        var top = new PathsD { TopRectPoly(plate) };
        var face = pocketsUnion.Count == 0 ? top : Clipper.Difference(top, pocketsUnion, FillRule.NonZero, ClipPrec);
        // v1.8: триангуляция с контролем площади (лечит «полигон в углу»)
        var tris = TriangulateChecked(face, throwIfOver);
        foreach (var t in tris)
        {
            throwIfOver();
            EmitTriY(pos, t[0].x, t[0].y, t[1].x, t[1].y, t[2].x, t[2].y, plate.Height, plate.Width, plate.Depth, +1);
        }
    }

    /// <summary>Четырёхугольник с автоподбором порядка под заданную нормаль (в координатах пластины).</summary>
    private static void QuadN(List<float> pos,
        double ax, double ay, double az, double bx, double by, double bz,
        double cx, double cy, double cz, double dx, double dy, double dz,
        double wnx, double wny, double wnz, double W, double D)
    {
        double n1x = (by - ay) * (cz - az) - (bz - az) * (cy - ay);
        double n1y = (bz - az) * (cx - ax) - (bx - ax) * (cz - az);
        double n1z = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        bool flip = n1x * wnx + n1y * wny + n1z * wnz < 0;
        if (flip) (bx, by, bz, dx, dy, dz) = (dx, dy, dz, bx, by, bz);   // разворот: b <-> d
        Push(pos, ax - W / 2, ay, az - D / 2, bx - W / 2, by, bz - D / 2, cx - W / 2, cy, cz - D / 2);
        Push(pos, ax - W / 2, ay, az - D / 2, cx - W / 2, cy, cz - D / 2, dx - W / 2, dy, dz - D / 2);
    }

    private static void Push(List<float> pos, double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz)
    {
        pos.Add((float)ax); pos.Add((float)ay); pos.Add((float)az);
        pos.Add((float)bx); pos.Add((float)by); pos.Add((float)bz);
        pos.Add((float)cx); pos.Add((float)cy); pos.Add((float)cz);
    }

    /// <summary>Детерминированный ГПСЧ (как makeRng в JS).</summary>
    private sealed class Rng
    {
        private uint _s;
        public Rng(uint seed) { _s = seed == 0 ? 1 : seed; }
        public double Next()
        {
            _s = _s * 1664525u + 1013904223u;
            return _s / 4294967296.0;
        }
    }

    /// <summary>Дешёвый хэш строки SVG (как svgHash в JS).</summary>
    private static string SvgHash(string text)
    {
        uint h = 5381;
        int step = Math.Max(1, text.Length / 2048);
        for (int i = 0; i < text.Length; i += step) h = (h * 33) ^ text[i];
        return h.ToString("x8");
    }
}
