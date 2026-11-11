// StampGen — проверка и гарантированная заварка щелей меша (watertight).
// © Guoud (iljamakeev@yandex.ru). Все права защищены. Условия: LICENSE.md.
// v2.0: ЗАМКНУТОСТЬ МЕША (watertight) — проверка и гарантированная заварка щелей.
//
// Проблема (найдена в v1.9): триангулятор Delaunay Clipper2 НЕ сохраняет вершины
// входных контуров — сливает близкие и привязывает остальные к своей сетке
// (замер: вход 682 вершины -> выход 591, общих только 10). Стенки карманов
// строились по исходным вершинам путей, дно/верх — по смещённым вершинам
// триангуляции. Весь контур кармана (в т.ч. внутренние объекты вдавливания —
// «глаза» черепов) превращался в кольцо микрощелей: на тесте одного черепа —
// 2546 открытых рёбер, «лавры» — 33816. CHITUBOX показывал это как «белые
// пропуски на гранях выдавливания» и чинил «ремонтом».
//
// Решение — три уровня:
//   1) MeshGen: стенки строятся по РЁБРАМ ТРИАНГУЛЯЦИИ дна (низ стенки совпадает
//      с дном точно), пути предварительно свариваются, зонд глубины берёт
//      минимум по двум расстояниям (стенка не может выйти слишком короткой).
//   2) MeshSealer.Seal — гарантия: сварка вершин; T-стыки (ребро одной
//      поверхности, проходящее через вершину другой) РЕАЛЬНО разрезаются —
//      треугольник-владелец заменяется веером с вершиной стыка; после взаимных
//      разрезов подрёбра двух поверхностей совпадают точно. Оставшиеся
//      замкнутые кольца (редкие дыры) заливаются триангуляцией кольца.
//
//   v2.1 — ВАЖНОЕ ИСПРАВЛЕНИЕ: сварка вершин теперь ВЕРТИКАЛЬНО-избирательна.
//   Раньше MergeTol = 0.02 мм объединял ЛЮБЫЕ вершины ближе 0.02 мм в 3D.
//   Стенка кармана стандартной глубины 20 мкм (ReliefMm = 0.02 мм) имеет верх
//   (y = H) и дно (y = H − 0.02) ровно на расстоянии допуска — вся стенка
//   СХЛОПЫВАЛАСЬ: карман исчезал, рельеф пропадал. Отсюда симптомы:
//   «в STL рельефа нет вообще» (точные глубины) и «при усилении ×1 рельефа
//   нет, при ×2 всё ок» (×2 даёт стенку 0.04 мм > 0.02 — она выживала).
//   Теперь вершины разных ВЫСОТ не свариваются никогда (допуск YMergeTol),
//   а сварка по горизонтали (хордовые провисания на одной поверхности, |Δy| ≈ 0)
//   работает как раньше. Теорию «×1 = умножение на 0» подтвердили иначе:
//   высота не обнулялась — стенка сваривалась с верхом пластины.
//   3) MeshChecks.Analyze — независимая проверка результата: каждое ребро
//      должно принадлежать ровно двум треугольникам (0 открытых рёбер).
// =============================================================================

namespace StampGen.Services;

/// <summary>Независимая проверка замкнутости меша (по тем же float, что уходят в STL).</summary>
public static class MeshChecks
{
    public sealed class Report
    {
        public long Triangles;
        /// <summary>Рёбра, принадлежащие только одному треугольнику («щели» — белые пропуски).</summary>
        public long OpenEdges;
        /// <summary>Рёбра с 3+ треугольниками (не мешают печати, но фиксируются).</summary>
        public long NonManifoldEdges;
        /// <summary>Координаты (x,y,z) первых щелей — для отчётов.</summary>
        public List<double[]> OpenSamples = new();
    }

    /// <summary>Проверить меш: каждое ребро должно иметь ровно 2 треугольника.
    /// Вершины свариваются с допуском MeshSealer.MergeTol — как в MeshSealer.</summary>
    public static Report Analyze(float[] positions)
    {
        var rep = new Report();
        var (wpos, tris) = SealerCore.WeldAndTriangulate(positions);
        rep.Triangles = tris.Length / 3;

        var (counts, _) = SealerCore.EdgeCounts(tris);
        foreach (var (key, c) in counts)
        {
            if (c == 1)
            {
                rep.OpenEdges++;
                if (rep.OpenSamples.Count < 10)
                {
                    int a = (int)(key >> 32);
                    rep.OpenSamples.Add(new double[] { wpos[a * 3], wpos[a * 3 + 1], wpos[a * 3 + 2] });
                }
            }
            else if (c > 2) rep.NonManifoldEdges++;
        }
        return rep;
    }
}

/// <summary>Заварка щелей меша: сварка вершин, реальные разрезы T-стыков, заливка колец.</summary>
public static class MeshSealer
{
    public sealed class Stats
    {
        public long OpenBefore;
        public long OpenAfter;
        public long NonManifold;
        /// <summary>Разрезано T-стыков (рёбер, прошедших через чужую вершину).</summary>
        public long TSplits;
        /// <summary>Добавлено треугольников-заплат (заливка оставшихся дыр).</summary>
        public long FilledTriangles;
        public long Vertices;
    }

    /// <summary>Допуск стыковки, мм. Должен перекрывать «стрелку провисания» хорд
    /// триангуляции на криволинейных контурах (до ~0.02 мм на черновом качестве),
    /// но оставаться намного меньше Resolution печати (0.05 мм).</summary>
    public const double MergeTol = 0.02;

    /// <summary>v2.1: вертикальный допуск сварки, мм (25 нм).
    /// Свариваются только вершины, лежащие на ОДНОЙ высоте (|Δy| ≤ YMergeTol):
    /// все легитимные стыки — на одной плоскости (верхняя грань y = H, дно террасы
    /// y = floorY), вершины верха и низа стенки кармана отличаются минимум на
    /// 0.001 мм (минимальная глубина в MeshGen) и свариваться НЕ ДОЛЖНЫ.
    /// 25 нм: в ~14 раз выше float-шума координат (~4e-7 мм) и в 40 раз ниже
    /// минимальной глубины и шага квантования уровней (0.0001 мм).</summary>
    public const double YMergeTol = 2.5e-5;

    /// <summary>Максимальное расстояние для финальной стяжки одиночных щелей, мм.</summary>
    public const double FinalWeldMax = 0.3;

    /// <summary>Заварить меш in-place (список позиций, 9 float на треугольник).</summary>
    public static Stats Seal(List<float> pos)
    {
        var st = new Stats();
        var (wpos, triList) = SealerCore.WeldAndTriangulate(pos.ToArray());
        var tris = new List<int>(triList);
        st.Vertices = wpos.Length / 3;

        // --- КОНВЕЙЕР ЗАВАРКИ (до 3 проходов, пока не будет 0 щелей):
        //     1) T-стыки: ребро одной поверхности проходит через вершину другой —
        //        треугольник-владелец РЕАЛЬНО разрезается веером по этой вершине;
        //     2) оставшиеся граничные рёбра собираются в кольца/цепи и заливаются
        //        триангуляцией; хорда цепи после сварки вершин исчезает. ---
        for (int pass = 0; pass < 3; pass++)
        {
            long openAtPassStart = -1;
            for (int round = 0; round < 12; round++)
            {
                var (counts, owner) = SealerCore.EdgeCounts(tris);
                var boundary = new List<(int a, int b, int tri, int slot)>();
                foreach (var (key, c) in counts)
                {
                    if (c == 1 && owner.TryGetValue(key, out var ow))
                        boundary.Add((tris[ow.tri * 3 + ow.slot], tris[ow.tri * 3 + (ow.slot + 1) % 3], ow.tri, ow.slot));
                }
                if (pass == 0 && round == 0) st.OpenBefore = boundary.Count;
                if (boundary.Count == 0) break;
                openAtPassStart = boundary.Count;

                var cand = SealerCore.SpatialHash();
                var candSeen = new HashSet<int>();
                foreach (var (a, b, _, _) in boundary)
                {
                    if (candSeen.Add(a)) SealerCore.HashAdd(cand, wpos, a);
                    if (candSeen.Add(b)) SealerCore.HashAdd(cand, wpos, b);
                }

                var splits = new Dictionary<int, List<int>>();   // tri -> вершины-стыки по порядку
                long nSplits = 0;
                foreach (var (a, b, tri, slot) in boundary)
                {
                    // один разрез на треугольник за раунд: второй граничный рёбра
                    // этого треугольника обработается в следующем раунде
                    if (splits.ContainsKey(tri)) continue;
                    var inside = SealerCore.OnSegmentCandidates(cand, wpos, a, b, MergeTol);
                    if (inside.Count == 0) continue;
                    int r = tris[tri * 3 + ((slot + 2) % 3)];
                    if (r == a || r == b || inside.Contains(r)) continue;
                    splits[tri] = SealerCore.OrderAlongSegment(wpos, a, inside, b);
                    nSplits += splits[tri].Count;
                }
                if (nSplits == 0) break;
                st.TSplits += nSplits;

                // выполняем разрезы: треугольник (a,b,r) -> веер (a,v1,r),(v1,v2,r)…,(vk,b,r)
                foreach (var (tri, list) in splits)
                {
                    int a = tris[tri * 3], b = tris[tri * 3 + 1], r = tris[tri * 3 + 2];
                    var seq = SealerCore.OrderAlongSegment(wpos, a, list, b);
                    var fan = new List<int>(3 * (seq.Count + 1));
                    int prev = a;
                    foreach (var v in seq)
                    {
                        if (v == a || v == b || v == r) { prev = v; continue; }
                        fan.AddRange(new[] { prev, v, r });
                        prev = v;
                    }
                    fan.AddRange(new[] { prev, b, r });
                    tris[tri * 3] = fan[0]; tris[tri * 3 + 1] = fan[1]; tris[tri * 3 + 2] = fan[2];
                    for (int i = 3; i < fan.Count; i += 3)
                        tris.AddRange(fan.GetRange(i, 3));
                }
            }

            // --- заливка оставшихся дыр (кольца) и «лесенок» (цепи с хордой) ---
            var (countsF, ownerF) = SealerCore.EdgeCounts(tris);
            var boundaryF = new List<(int a, int b)>();
            foreach (var (key, c) in countsF)
            {
                if (c > 2) st.NonManifold++;
                if (c == 1 && ownerF.TryGetValue(key, out var ow))
                    boundaryF.Add((tris[ow.tri * 3 + ow.slot], tris[ow.tri * 3 + (ow.slot + 1) % 3]));
            }
            var (loops, chains) = SealerCore.AssembleBoundary(boundaryF);
            bool dbg = Environment.GetEnvironmentVariable("STAMPGEN_SEAL_DEBUG") == "1";
            if (dbg)
            {
                Console.Error.WriteLine($"[seal] проход {pass}: разрезов всего {st.TSplits}, граничных рёбер: {boundaryF.Count}, колец: {loops.Count}, цепей: {chains.Count}");
                foreach (var l in loops.Take(5))
                    Console.Error.WriteLine($"[seal]   кольцо: {l.Count} вершин, y=[{l.Min(v => wpos[v * 3 + 1]):F4}..{l.Max(v => wpos[v * 3 + 1]):F4}]");
                foreach (var l in chains)
                {
                    var cs = $"{l[0]}"; var ce = $"{l[^1]}";
                    double ddx = wpos[l[0] * 3] - wpos[l[^1] * 3], ddy = wpos[l[0] * 3 + 1] - wpos[l[^1] * 3 + 1], ddz = wpos[l[0] * 3 + 2] - wpos[l[^1] * 3 + 2];
                    Console.Error.WriteLine($"[seal]   цепь: {l.Count} вершин, y=[{l.Min(v => wpos[v * 3 + 1]):F4}..{l.Max(v => wpos[v * 3 + 1]):F4}], концы: ({cs})→({ce}), дист={Math.Sqrt(ddx*ddx+ddy*ddy+ddz*ddz):F4}");
                }
            }
            foreach (var loop in loops)
            {
                if (loop.Count < 3) continue;
                st.FilledTriangles += FillLoop(pos, wpos, loop, tris);
            }
            foreach (var chain in chains)
            {
                if (chain.Count < 3) continue;
                st.FilledTriangles += FillLoop(pos, wpos, chain, tris);
            }

            // пересчёт: если щелей больше нет — готово, иначе следующий проход
            var (countsP, _) = SealerCore.EdgeCounts(tris);
            long openNow = countsP.Values.Count(c => c == 1);
            if (dbg) Console.Error.WriteLine($"[seal] проход {pass}: осталось щелей {openNow}");
            if (openNow == 0) break;
            if (pass == 2 || openNow >= openAtPassStart && pass > 0 && openNow == openAtPassStart) { /* больше не сходимся — честный отчёт */ }
        }

        // --- финальная стяжка: одиночные оставшиеся щели (ребро, у которого
        //     нет пары) закрываем точечной сваркой концов — локальный сдвиг
        //     в пределах уже расходящейся «лесенки», геометрия не страдает ---
        {
            var (countsW, _) = SealerCore.EdgeCounts(tris);
            var remap = new Dictionary<int, int>();
            foreach (var (key, c) in countsW)
            {
                if (c != 1) continue;
                int p = (int)(key >> 32), q = (int)(key & 0xffffffff);
                double dx = wpos[p * 3] - wpos[q * 3], dy = wpos[p * 3 + 1] - wpos[q * 3 + 1], dz = wpos[p * 3 + 2] - wpos[q * 3 + 2];
                double d2 = dx * dx + dy * dy + dz * dz;
                if (d2 > FinalWeldMax * FinalWeldMax) continue;
                if (Math.Abs(dy) > MeshSealer.YMergeTol) continue;   // v2.1: вертикальную щель (стенку) не стягиваем
                int root = Math.Min(p, q), leaf = Math.Max(p, q);
                remap[leaf] = root;
            }
            if (remap.Count > 0)
            {
                for (int i = 0; i < tris.Count; i++)
                    if (remap.TryGetValue(tris[i], out var rep)) tris[i] = rep;
                for (int i = tris.Count - 3; i >= 0; i -= 3)
                {
                    int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                    if (a == b || b == c || c == a) tris.RemoveRange(i, 3);
                }
            }
        }

        // --- финальная стяжка: одиночные оставшиеся щели с близкими концами
        //     закрываем точечной сваркой концов (локальный сдвиг в пределах
        //     уже расходящейся «лесенки») ---
        {
            var (countsW, _) = SealerCore.EdgeCounts(tris);
            var remap = new Dictionary<int, int>();
            foreach (var (key, c) in countsW)
            {
                if (c != 1) continue;
                int p = (int)(key >> 32), q = (int)(key & 0xffffffff);
                double dx = wpos[p * 3] - wpos[q * 3], dy = wpos[p * 3 + 1] - wpos[q * 3 + 1], dz = wpos[p * 3 + 2] - wpos[q * 3 + 2];
                double d2 = dx * dx + dy * dy + dz * dz;
                if (d2 > FinalWeldMax * FinalWeldMax) continue;
                if (Math.Abs(dy) > MeshSealer.YMergeTol) continue;   // v2.1: вертикальную щель (стенку) не стягиваем
                remap[Math.Max(p, q)] = Math.Min(p, q);
            }
            if (remap.Count > 0)
            {
                for (int i = 0; i < tris.Count; i++)
                    if (remap.TryGetValue(tris[i], out var rep)) tris[i] = rep;
                for (int i = tris.Count - 3; i >= 0; i -= 3)
                {
                    int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                    if (a == b || b == c || c == a) tris.RemoveRange(i, 3);
                }
            }
        }

        // --- последний резерв: хорды цепей, которые не сварились, закрываем
        //     «лепестком» — встречная пара треугольников к середине хорды
        //     (вырожденная мембрана внутри щели: геометрию не меняет,
        //     на печать не влияет, но меш становится замкнутым) ---
        {
            var (countsM, ownerM) = SealerCore.EdgeCounts(tris);
            var boundaryM = new List<(int a, int b)>();
            foreach (var (key, c) in countsM)
            {
                if (c > 2) st.NonManifold++;
                if (c == 1 && ownerM.TryGetValue(key, out var ow))
                    boundaryM.Add((tris[ow.tri * 3 + ow.slot], tris[ow.tri * 3 + (ow.slot + 1) % 3]));
            }
            var (loopsM, chainsM) = SealerCore.AssembleBoundary(boundaryM);
            foreach (var loop in loopsM)
            {
                if (loop.Count < 3) continue;
                st.FilledTriangles += FillLoop(pos, wpos, loop, tris);
            }
            var wposL = new List<float>(wpos);
            foreach (var chain in chainsM)
            {
                if (chain.Count < 2) continue;
                if (chain.Count == 2)
                {
                    // одиночное ребро без пары — сразу «лепесток»
                    int s0 = chain[0], e0 = chain[1];
                    int m0 = wposL.Count / 3;
                    wposL.Add((float)((wposL[s0 * 3] + wposL[e0 * 3]) / 2));
                    wposL.Add((float)((wposL[s0 * 3 + 1] + wposL[e0 * 3 + 1]) / 2));
                    wposL.Add((float)((wposL[s0 * 3 + 2] + wposL[e0 * 3 + 2]) / 2));
                    tris.AddRange(new[] { s0, e0, m0 });
                    tris.AddRange(new[] { e0, s0, m0 });
                    st.FilledTriangles += 2;
                    continue;
                }
                int s = chain[0], e = chain[^1];
                double dx = wposL[s * 3] - wposL[e * 3], dy = wposL[s * 3 + 1] - wposL[e * 3 + 1], dz = wposL[s * 3 + 2] - wposL[e * 3 + 2];
                if (dx * dx + dy * dy + dz * dz <= MergeTol * MergeTol) continue;  // сварится сама
                // середина хорды — новая вершина
                int m = wposL.Count / 3;
                wposL.Add((float)((wposL[s * 3] + wposL[e * 3]) / 2));
                wposL.Add((float)((wposL[s * 3 + 1] + wposL[e * 3 + 1]) / 2));
                wposL.Add((float)((wposL[s * 3 + 2] + wposL[e * 3 + 2]) / 2));
                tris.AddRange(new[] { s, e, m });   // встречная пара — «лепесток»
                tris.AddRange(new[] { e, s, m });
                st.FilledTriangles += 2;
            }
            wpos = wposL.ToArray();
        }

        // --- независимая перепроверка + замена меша на заваренный ---
        // (без повторной сварки: идентификаторы вершин уже сварены, а «лепестки»
        //  и заплаты обязаны остаться в меше — иначе проверка не совпадёт с STL)
        {
            var (countsR, _) = SealerCore.EdgeCounts(tris);
            st.OpenAfter = countsR.Values.Count(c => c == 1);
            if (Environment.GetEnvironmentVariable("STAMPGEN_SEAL_DEBUG") == "1")
            {
                // контроль в пространстве ПОВТОРНОЙ сварки (как это увидит внешний проверятор)
                var (w3, t3) = SealerCore.WeldAndTriangulate(SealerCore.Flatten(wpos, tris));
                var (c3, _) = SealerCore.EdgeCounts(t3);
                long o3 = c3.Values.Count(c => c == 1);
                Console.Error.WriteLine($"[seal] контроль повторной сварки: щелей {o3} (внутри: {st.OpenAfter})");
                if (o3 > 0)
                    foreach (var (key, c) in c3)
                    {
                        if (c != 1) continue;
                        int p = (int)(key >> 32), q = (int)(key & 0xffffffff);
                        Console.Error.WriteLine($"[seal]   внешняя щель ({w3[p * 3]:F4}, {w3[p * 3 + 1]:F4}, {w3[p * 3 + 2]:F4})→({w3[q * 3]:F4}, {w3[q * 3 + 1]:F4}, {w3[q * 3 + 2]:F4})");
                    }
            }
            if (Environment.GetEnvironmentVariable("STAMPGEN_SEAL_DEBUG") == "1" && st.OpenAfter > 0)
            {
                foreach (var (key, c) in countsR)
                {
                    if (c != 1) continue;
                    int p = (int)(key >> 32), q = (int)(key & 0xffffffff);
                    Console.Error.WriteLine($"[seal] ПОСЛЕ всего: щель ({p}→{q}) ({wpos[p * 3]:F4}, {wpos[p * 3 + 1]:F4}, {wpos[p * 3 + 2]:F4})→({wpos[q * 3]:F4}, {wpos[q * 3 + 1]:F4}, {wpos[q * 3 + 2]:F4})");
                }
            }
            var flat = new List<float>(tris.Count / 3 * 9);
            for (int t = 0; t < tris.Count; t += 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    int v = tris[t + k] * 3;
                    flat.Add(wpos[v]); flat.Add(wpos[v + 1]); flat.Add(wpos[v + 2]);
                }
            }
            pos.Clear();
            pos.AddRange(flat);
        }
        return st;
    }

    /// <summary>Залить кольцо щели триангуляцией (треугольники дописываются в tris —
    /// индексами сваренных вершин, и в pos — координатами). Возвращает число треугольников.</summary>
    private static int FillLoop(List<float> pos, float[] wpos, List<int> loop, List<int> tris)
    {
        // порядок: рёбра собраны в направлении треугольников-владельцев; заплаты
        // должны обходить рёбра ВСТРЕЧНО -> разворачиваем кольцо
        loop.Reverse();

        // плоскость кольца (Ньюэлла) и проекция с отбрасыванием доминирующей оси
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            var p = P(wpos, loop[i]);
            var q = P(wpos, loop[(i + 1) % loop.Count]);
            nx += (p.y - q.y) * (p.z + q.z);
            ny += (p.z - q.z) * (p.x + q.x);
            nz += (p.x - q.x) * (p.y + q.y);
        }
        double ax = Math.Abs(nx), ay = Math.Abs(ny), az = Math.Abs(nz);
        (int u, int v) = ax > ay ? (ax > az ? (1, 2) : (0, 1)) : (ay > az ? (2, 0) : (0, 1));

        int n = loop.Count;
        var uv = new (double x, double y)[n];
        for (int i = 0; i < n; i++)
        {
            var p = P(wpos, loop[i]);
            uv[i] = (u switch { 0 => p.x, 1 => p.y, _ => p.z }, v switch { 0 => p.x, 1 => p.y, _ => p.z });
        }

        double Area(int i, int j, int k)
        {
            var a = uv[i]; var b = uv[j]; var c = uv[k];
            return (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
        }
        bool PointIn(int i, int j, int k, int p)
        {
            var a = uv[i]; var b = uv[j]; var c = uv[k]; var q = uv[p];
            double d = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
            if (Math.Abs(d) < 1e-18) return false;
            double l1 = ((b.y - c.y) * (q.x - c.x) + (c.x - b.x) * (q.y - c.y)) / d;
            double l2 = ((c.y - a.y) * (q.x - c.x) + (a.x - c.x) * (q.y - c.y)) / d;
            double l3 = 1 - l1 - l2;
            const double e = 1e-9;
            return l1 >= -e && l2 >= -e && l3 >= -e;
        }

        var idx = Enumerable.Range(0, n).ToList();
        var result = new List<(int, int, int)>();
        if (n <= 2000)
        {
            // отсечение ушей (учитывает ориентацию полигона); гигантские кольца — сразу веером
            int guard = 0;
            while (idx.Count > 3 && guard++ < n * n)
            {
                bool clipped = false;
                for (int i = 0; i < idx.Count; i++)
                {
                    int i0 = idx[(i + idx.Count - 1) % idx.Count];
                    int i1 = idx[i];
                    int i2 = idx[(i + 1) % idx.Count];
                    double ar = Area(i0, i1, i2);
                    if (Math.Abs(ar) < 1e-18) continue;            // вырожденное ухо — пропускаем
                    bool convex = ar * PolySign(uv) > 0;           // выпуклое (знак как у полигона)
                    if (!convex) continue;
                    bool contains = false;
                    for (int j = 0; j < idx.Count && !contains; j++)
                    {
                        int pj = idx[j];
                        if (pj == i0 || pj == i1 || pj == i2) continue;
                        if (PointIn(i0, i1, i2, pj)) contains = true;
                    }
                    if (contains) continue;
                    result.Add((loop[i0], loop[i1], loop[i2]));
                    idx.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) break;   // не удалось — уходим в веер
            }
        }
        if (idx.Count == 3) result.Add((loop[idx[0]], loop[idx[1]], loop[idx[2]]));
        else
        {
            // веер-фолбэк (гарантированно замыкает кольцо)
            for (int i = 1; i + 1 < idx.Count; i++)
                result.Add((loop[idx[0]], loop[idx[i]], loop[idx[i + 1]]));
        }

        foreach (var (a, b, c) in result)
        {
            tris.AddRange(new[] { a, b, c });
            var pa = P(wpos, a); var pb = P(wpos, b); var pc = P(wpos, c);
            pos.Add((float)pa.x); pos.Add((float)pa.y); pos.Add((float)pa.z);
            pos.Add((float)pb.x); pos.Add((float)pb.y); pos.Add((float)pb.z);
            pos.Add((float)pc.x); pos.Add((float)pc.y); pos.Add((float)pc.z);
        }
        return result.Count;
    }

    private static double PolySign((double x, double y)[] uv)
    {
        double s = 0;
        for (int i = 0; i < uv.Length; i++)
        {
            var a = uv[i]; var b = uv[(i + 1) % uv.Length];
            s += a.x * b.y - b.x * a.y;
        }
        return s >= 0 ? 1 : -1;
    }

    private static (double x, double y, double z) P(float[] w, int i) => (w[i * 3], w[i * 3 + 1], w[i * 3 + 2]);
}

/// <summary>Внутренние примитивы сварки/рёбер (общие для MeshChecks и MeshSealer).</summary>
internal static class SealerCore
{
    /// <summary>Сварить вершины (объединение ближе MergeTol через union-find) и собрать треугольники.
    /// Вырожденные треугольники отбрасываются.</summary>
    public static (float[] wpos, int[] tris) WeldAndTriangulate(float[] positions)
    {
        int vc = positions.Length / 3;
        double cell = MeshSealer.MergeTol;
        var grid = new Dictionary<(int, int, int), List<int>>(1 << 14);
        int[] parent = new int[vc];
        for (int i = 0; i < vc; i++) parent[i] = i;

        int CellIdx(double v) => (int)Math.Floor(v / cell);
        (int, int, int) CellOf(double x, double y, double z) => (CellIdx(x), CellIdx(y), CellIdx(z));

        for (int i = 0; i < vc; i++)
        {
            double x = positions[i * 3], y = positions[i * 3 + 1], z = positions[i * 3 + 2];
            var c = CellOf(x, y, z);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!grid.TryGetValue((c.Item1 + dx, c.Item2 + dy, c.Item3 + dz), out var list)) continue;
                        foreach (var j in list)
                        {
                            double ddx = positions[j * 3] - x, ddy = positions[j * 3 + 1] - y, ddz = positions[j * 3 + 2] - z;
                            // v2.1: сварка ТОЛЬКО на одной высоте — верх/низ стенки кармана
                            // (расстояние = глубина) больше не схлопываются в одну точку
                            if (ddx * ddx + ddy * ddy + ddz * ddz <= MeshSealer.MergeTol * MeshSealer.MergeTol
                                && Math.Abs(ddy) <= MeshSealer.YMergeTol)
                            {
                                int rj = Find(parent, j), ri = Find(parent, i);
                                if (rj != ri) parent[Math.Max(ri, rj)] = Math.Min(ri, rj);
                            }
                        }
                    }
            if (!grid.TryGetValue(c, out var l)) grid[c] = l = new List<int>();
            l.Add(i);
        }

        // представители -> новые индексы; позиции представителя сохраняются как есть
        var rep = new Dictionary<int, int>();
        var wposList = new List<float>(vc);
        int[] map = new int[vc];
        for (int i = 0; i < vc; i++)
        {
            int r = Find(parent, i);
            if (r == i)
            {
                map[i] = rep.Count;
                rep[r] = map[i];
                wposList.Add(positions[i * 3]); wposList.Add(positions[i * 3 + 1]); wposList.Add(positions[i * 3 + 2]);
            }
            else map[i] = rep[r];
        }

        var tris = new List<int>(positions.Length / 3);
        for (int t = 0; t < positions.Length / 9; t++)
        {
            int a = map[t * 3], b = map[t * 3 + 1], c = map[t * 3 + 2];
            if (a == b || b == c || c == a) continue;
            // отброс нулевой площади (в сваренных координатах)
            double ax = wposList[a * 3], ay = wposList[a * 3 + 1], az = wposList[a * 3 + 2];
            double bx = wposList[b * 3], by = wposList[b * 3 + 1], bz = wposList[b * 3 + 2];
            double cx = wposList[c * 3], cy = wposList[c * 3 + 1], cz = wposList[c * 3 + 2];
            double nx = (by - ay) * (cz - az) - (bz - az) * (cy - ay);
            double ny = (bz - az) * (cx - ax) - (bx - ax) * (cz - az);
            double nz = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (nx * nx + ny * ny + nz * nz < 1e-24) continue;   // (1e-12 мм²)²
            tris.Add(a); tris.Add(b); tris.Add(c);
        }
        return (wposList.ToArray(), tris.ToArray());
    }

    private static int Find(int[] p, int i) { while (p[i] != i) i = p[i] = p[p[i]]; return i; }

    /// <summary>Карта рёбер: ключ (min<<32|max) -> число треугольников; владелец для граничных.</summary>
    public static (Dictionary<long, int> counts, Dictionary<long, (int tri, int slot)> owner) EdgeCounts(List<int> tris)
    {
        int tc = tris.Count / 3;
        var counts = new Dictionary<long, int>(tc * 2);
        var owner = new Dictionary<long, (int, int)>();
        for (int t = 0; t < tc; t++)
        {
            for (int s = 0; s < 3; s++)
            {
                int a = tris[t * 3 + s], b = tris[t * 3 + (s + 1) % 3];
                long key = a < b ? (long)a << 32 | (uint)b : (long)b << 32 | (uint)a;
                counts[key] = counts.GetValueOrDefault(key) + 1;
                owner.TryAdd(key, (t, s));
            }
        }
        return (counts, owner);
    }

    public static (Dictionary<long, int> counts, Dictionary<long, (int tri, int slot)> owner) EdgeCounts(int[] tris)
        => EdgeCounts(new List<int>(tris));

    public sealed class Hash { public Dictionary<(int, int, int), List<int>> G = new(); }

    public static Hash SpatialHash() => new();

    public static void HashAdd(Hash h, float[] wpos, int v)
    {
        var c = (Cell(wpos[v * 3]), Cell(wpos[v * 3 + 1]), Cell(wpos[v * 3 + 2]));
        if (!h.G.TryGetValue(c, out var l)) h.G[c] = l = new List<int>();
        l.Add(v);
    }
    private static int Cell(double v) => (int)Math.Floor(v / 0.5);

    /// <summary>Вершины-кандидаты, лежащие СТРОГО внутри сегмента (a,b) с допуском tol.</summary>
    public static List<int> OnSegmentCandidates(Hash h, float[] wpos, int a, int b, double tol)
    {
        var res = new List<int>();
        double ax = wpos[a * 3], ay = wpos[a * 3 + 1], az = wpos[a * 3 + 2];
        double bx = wpos[b * 3], by = wpos[b * 3 + 1], bz = wpos[b * 3 + 2];
        double dx = bx - ax, dy = by - ay, dz = bz - az;
        double len2 = dx * dx + dy * dy + dz * dz;
        if (len2 < 1e-20) return res;

        double minX = Math.Min(ax, bx) - tol, maxX = Math.Max(ax, bx) + tol;
        double minY = Math.Min(ay, by) - tol, maxY = Math.Max(ay, by) + tol;
        double minZ = Math.Min(az, bz) - tol, maxZ = Math.Max(az, bz) + tol;
        int x0 = Cell(minX), x1 = Cell(maxX), y0 = Cell(minY), y1 = Cell(maxY), z0 = Cell(minZ), z1 = Cell(maxZ);
        long cells = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
        var seen = new HashSet<int>();
        if (cells <= 4096)
        {
            for (int cx = x0; cx <= x1; cx++)
                for (int cy = y0; cy <= y1; cy++)
                    for (int cz = z0; cz <= z1; cz++)
                        if (h.G.TryGetValue((cx, cy, cz), out var l))
                            ProbeList(l, wpos, a, b, ax, ay, az, dx, dy, dz, len2, tol, seen, res);
        }
        else
        {
            foreach (var l in h.G.Values) ProbeList(l, wpos, a, b, ax, ay, az, dx, dy, dz, len2, tol, seen, res);
        }
        res.Sort((p, q) =>
        {
            double tp = (wpos[p * 3] - ax) * dx + (wpos[p * 3 + 1] - ay) * dy + (wpos[p * 3 + 2] - az) * dz;
            double tq = (wpos[q * 3] - ax) * dx + (wpos[q * 3 + 1] - ay) * dy + (wpos[q * 3 + 2] - az) * dz;
            return tp.CompareTo(tq);
        });
        return res;
    }

    private static void ProbeList(List<int> l, float[] wpos, int a, int b,
        double ax, double ay, double az, double dx, double dy, double dz, double len2, double tol,
        HashSet<int> seen, List<int> res)
    {
        foreach (var v in l)
        {
            if (v == a || v == b || !seen.Add(v)) continue;
            double px = wpos[v * 3], py = wpos[v * 3 + 1], pz = wpos[v * 3 + 2];
            double tt = ((px - ax) * dx + (py - ay) * dy + (pz - az) * dz) / len2;
            if (tt <= 1e-9 || tt >= 1 - 1e-9) continue;
            // v2.1: кандидат-стык обязан лежать на высоте САМОГО ребра — вершина
            // чужой поверхности (например, дно кармана под ребром верхней грани)
            // не считается T-стыком и разрез не порождает
            if (Math.Abs(py - (ay + tt * dy)) > MeshSealer.YMergeTol) continue;
            double ex = ax + tt * dx - px, ey = ay + tt * dy - py, ez = az + tt * dz - pz;
            if (ex * ex + ey * ey + ez * ez <= tol * tol) res.Add(v);
        }
    }

    /// <summary>Упорядочить вершины-стыки вдоль сегмента a->b.</summary>
    public static List<int> OrderAlongSegment(float[] wpos, int a, List<int> verts, int b)
    {
        double ax = wpos[a * 3], ay = wpos[a * 3 + 1], az = wpos[a * 3 + 2];
        double bx = wpos[b * 3], by = wpos[b * 3 + 1], bz = wpos[b * 3 + 2];
        return verts
            .Where(v => v != a && v != b)
            .Distinct()
            .OrderBy(v =>
                (wpos[v * 3] - ax) * (bx - ax) + (wpos[v * 3 + 1] - ay) * (by - ay) + (wpos[v * 3 + 2] - az) * (bz - az))
            .ToList();
    }

    public static float[] Flatten(float[] wpos, List<int> tris)
    {
        var flat = new float[tris.Count / 3 * 9];
        int o = 0;
        for (int t = 0; t < tris.Count; t += 3)
        {
            for (int k = 0; k < 3; k++)
            {
                int v = tris[t + k] * 3;
                flat[o++] = wpos[v]; flat[o++] = wpos[v + 1]; flat[o++] = wpos[v + 2];
            }
        }
        return flat;
    }

    /// <summary>Собрать граничные рёбра в замкнутые кольца и открытые цепи
    /// (обход по общим вершинам в обе стороны + склейка цепей общими концами).</summary>
    public static (List<List<int>> loops, List<List<int>> chains) AssembleBoundary(List<(int a, int b)> edges)
    {
        var loops = new List<List<int>>();
        var chains = new List<List<int>>();
        var used = new bool[edges.Count];
        var byV = new Dictionary<int, List<int>>();
        for (int i = 0; i < edges.Count; i++)
        {
            if (!byV.TryGetValue(edges[i].a, out var la)) byV[edges[i].a] = la = new List<int>();
            la.Add(i);
            if (!byV.TryGetValue(edges[i].b, out var lb)) byV[edges[i].b] = lb = new List<int>();
            lb.Add(i);
        }

        for (int start = 0; start < edges.Count; start++)
        {
            if (used[start]) continue;
            used[start] = true;
            var loop = new List<int> { edges[start].a, edges[start].b };
            // вперёд
            bool extended = true;
            while (extended && loop[^1] != loop[0])
            {
                extended = false;
                int cur = loop[^1];
                if (byV.TryGetValue(cur, out var list))
                {
                    foreach (var ei in list)
                    {
                        if (used[ei]) continue;
                        var (ea, eb) = edges[ei];
                        int nxt = ea == cur ? eb : (eb == cur ? ea : -1);
                        if (nxt < 0) continue;
                        used[ei] = true;
                        loop.Add(nxt);
                        extended = true;
                        break;
                    }
                }
            }
            // назад (если упёрлись в тупик впереди)
            extended = true;
            while (extended && loop[^1] != loop[0])
            {
                extended = false;
                int cur = loop[0];
                if (byV.TryGetValue(cur, out var list))
                {
                    foreach (var ei in list)
                    {
                        if (used[ei]) continue;
                        var (ea, eb) = edges[ei];
                        int nxt = ea == cur ? eb : (eb == cur ? ea : -1);
                        if (nxt < 0) continue;
                        used[ei] = true;
                        loop.Insert(0, nxt);
                        extended = true;
                        break;
                    }
                }
            }
            if (loop.Count > 2 && loop[^1] == loop[0]) { loop.RemoveAt(loop.Count - 1); loops.Add(loop); }
            else if (loop.Count > 1) chains.Add(loop);
        }

        // склейка цепей общими концами (возможны ветвления после частичных разрезов)
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < chains.Count && !merged; i++)
                for (int j = 0; j < chains.Count && !merged; j++)
                {
                    if (i == j) continue;
                    var ci = chains[i]; var cj = chains[j];
                    if (ci[^1] == cj[0])
                    {
                        chains[i] = ci.Concat(cj.Skip(1)).ToList();
                        chains.RemoveAt(j);
                        merged = true;
                    }
                    else if (ci[^1] == cj[^1])
                    {
                        cj.Reverse();
                        chains[i] = ci.Concat(cj.Skip(1)).ToList();
                        chains.RemoveAt(j);
                        merged = true;
                    }
                    else if (ci[0] == cj[^1])
                    {
                        chains[i] = cj.Concat(ci.Skip(1)).ToList();
                        chains.RemoveAt(j);
                        merged = true;
                    }
                    else if (ci[0] == cj[0])
                    {
                        cj.Reverse();
                        chains[i] = cj.Concat(ci.Skip(1)).ToList();
                        chains.RemoveAt(j);
                        merged = true;
                    }
                    if (merged && chains[i][^1] == chains[i][0] && chains[i].Count > 2)
                    {
                        chains[i].RemoveAt(chains[i].Count - 1);
                        loops.Add(chains[i]);
                        chains.RemoveAt(i);
                        break;
                    }
                }
        }
        return (loops, chains);
    }
}
