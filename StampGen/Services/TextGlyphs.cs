// StampGen — генерация SVG-контуров из текста (собственный парсер TrueType).
// © Guoud (iljamakeev@yandex.ru). Все права защищены. Условия: LICENSE.md.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace StampGen.Services;

/// <summary>
/// Генерация SVG-контуров из текста для текстовых оттисков.
/// Собственный минимальный парсер TrueType (glyf + loca + cmap + hmtx + head + hhea),
/// работает офлайн, поддерживает кириллицу, составные глифы и квадратичные кривые.
/// </summary>
public static class TextGlyphs
{
    public record FontDef(string Key, string Title, string Regular, string Bold);

    public static readonly List<FontDef> Fonts = new()
    {
        new("sans",   "Без засечек",  "LiberationSans-Regular.ttf",  "LiberationSans-Bold.ttf"),
        new("serif",  "С засечками",  "LiberationSerif-Regular.ttf", "LiberationSerif-Bold.ttf"),
        new("mono",   "Моноширинный", "LiberationMono-Regular.ttf",  "LiberationMono-Bold.ttf"),
        new("dejavu", "DejaVu Sans",  "DejaVuSans.ttf",              "DejaVuSans-Bold.ttf"),
    };

    private static readonly ConcurrentDictionary<string, FontFile> _fontCache = new();
    private static readonly ConcurrentDictionary<string, (string Svg, double Aspect)> _svgCache = new();
    private static string _fontsDir = "";

    public static void SetFontsDir(string dir) { _fontsDir = dir; _fontCache.Clear(); _svgCache.Clear(); }

    private static string FontPath(FontDef f, bool bold)
    {
        var name = bold ? f.Bold : f.Regular;
        var p = Path.Combine(_fontsDir, name);
        if (!File.Exists(p)) p = Path.Combine(_fontsDir, f.Regular); // запасной вариант
        return p;
    }

    /// <summary>Построить SVG (ч/б, контуры) из текста. Бросает InvalidOperationException с понятной причиной.</summary>
    public static (string Svg, double Aspect) Build(string text, string fontKey, bool bold)
    {
        text = (text ?? "").Replace("\r", "").TrimEnd('\n');
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Текст оттиска пустой — введите хотя бы один символ.");
        var key = $"{text.Length}|{text.GetHashCode(StringComparison.Ordinal)}|{fontKey}|{bold}";
        if (_svgCache.TryGetValue(key, out var cached)) return cached;

        var def = Fonts.FirstOrDefault(f => f.Key == fontKey) ?? Fonts[0];
        var path = FontPath(def, bold);
        if (!File.Exists(path))
            throw new InvalidOperationException($"Файл шрифта не найден: {def.Title}.");
        var font = _fontCache.GetOrAdd(path, p => new FontFile(p));

        var paths = new List<string>();   // v1.4: все контуры собираются в ОДИН path
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;

        var lines = text.Split('\n');
        double lineH = 1.25 * font.UnitsPerEm;
        for (int li = 0; li < lines.Length; li++)
        {
            double penX = 0;
            double baseShift = -li * lineH;   // в координатах шрифта (y вверх)
            foreach (var ch in lines[li])
            {
                if (ch == '\t') { penX += font.Advance(font.GlyphIndex(' ')) * 4; continue; }
                int gid = font.GlyphIndex(ch);
                if (gid < 0) continue;
                if (!char.IsWhiteSpace(ch) && gid == 0 && ch != ' ')
                {
                    // глифа нет — пробуем заменить похожим, иначе пропускаем
                    gid = font.GlyphIndex('?');
                    if (gid <= 0) continue;
                }
                var adv = font.Advance(gid);
                foreach (var contour in font.GlyphContours(gid))
                {
                    var pts = contour.Select(pt => (pt.X + penX, pt.Y + baseShift, pt.On)).ToList();
                    var d = ContourToPath(pts);
                    if (d.Length > 0) paths.Add(d);
                    foreach (var (px, py, _) in pts)
                    {
                        if (px < minX) minX = px;
                        if (px > maxX) maxX = px;
                        if (py < minY) minY = py;
                        if (py > maxY) maxY = py;
                    }
                    any = true;
                }
                penX += adv;
            }
        }

        if (!any)
            throw new InvalidOperationException("В тексте нет отрисовываемых символов (шрифт не содержит нужных глифов).");

        // SVG: ось Y переворачивается
        double vx0 = minX, vy0 = -maxY, vw = Math.Max(1, maxX - minX), vh = Math.Max(1, maxY - minY);
        // ================================================================
        // v1.4 ФИКС «двойного дна» у букв (буква «Д» — двойная ступенька).
        // Раньше КАЖДЫЙ контур глифа рисовался
        // отдельным залитым path — внутренние контуры-отверстия (треугольник
        // под куполом «Д», овалы «О/В/Ю/Ф»…) заливались чёрным ПОВЕРХ буквы,
        // и буква превращалась в сплошное пятно со ступеньками. Теперь все
        // контуры (наружные и отверстия) объединены в один <path> с правилом
        // заливки evenodd: отверстия корректны и в браузере (2D), и в
        // SVGLoader.createShapes (3D-выдавливание), и в PDF-экспорте.
        // ================================================================
        var svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{N(vx0)} {N(vy0)} {N(vw)} {N(vh)}\">\n<path d=\"{string.Join(" ", paths)}\" fill=\"#000000\" fill-rule=\"evenodd\"/>\n</svg>";
        var aspect = vw / vh;
        var res = (svg, aspect);
        if (_svgCache.Count > 400) _svgCache.Clear();
        _svgCache[key] = res;
        return res;
    }

    private static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    // ==================== контур -> путь SVG (TrueType: квадратичные B-сплайны) ====================

    private static string ContourToPath(List<(double X, double Y, bool On)> pts)
    {
        int n = pts.Count;
        if (n < 2) return "";
        // хвостовые дубли первой точки не нужны
        while (n > 1 && Math.Abs(pts[n - 1].X - pts[0].X) < 0.5 && Math.Abs(pts[n - 1].Y - pts[0].Y) < 0.5) n--;

        var sb = new StringBuilder();
        string P(double x, double y) => $"{N(x)} {N(-y)}";

        int start = -1;
        for (int i = 0; i < n; i++) if (pts[i].On) { start = i; break; }

        // «отложенный» off-curve контрольный узел: две подряд идущие off-точки
        // дают подразумеваемую on-точку в середине между ними
        (double X, double Y)? pend = null;
        void Emit(bool on, double x, double y)
        {
            if (on)
            {
                if (pend == null) sb.Append(" L ").Append(P(x, y));
                else
                {
                    sb.Append(" Q ").Append(P(pend.Value.X, pend.Value.Y)).Append(' ').Append(P(x, y));
                    pend = null;
                }
            }
            else
            {
                if (pend == null) pend = (x, y);
                else
                {
                    var (mx, my) = ((pend.Value.X + x) / 2, (pend.Value.Y + y) / 2);
                    sb.Append(" Q ").Append(P(pend.Value.X, pend.Value.Y)).Append(' ').Append(P(mx, my));
                    pend = (x, y);
                }
            }
        }

        if (start < 0)
        {
            // все точки off-curve: начинаем с середины между последней и первой
            var (m0x, m0y) = ((pts[n - 1].X + pts[0].X) / 2, (pts[n - 1].Y + pts[0].Y) / 2);
            sb.Append("M ").Append(P(m0x, m0y));
            for (int k = 0; k < n; k++) Emit(false, pts[k].X, pts[k].Y);
            if (pend != null)
                sb.Append(" Q ").Append(P(pend.Value.X, pend.Value.Y)).Append(' ').Append(P(m0x, m0y));
            sb.Append(" Z");
            return sb.ToString();
        }

        sb.Append("M ").Append(P(pts[start].X, pts[start].Y));
        // линейный проход по всем точкам контура; последняя — возврат к старту (on-curve)
        for (int k = 1; k <= n; k++)
        {
            var p = pts[(start + k) % n];
            Emit(p.On, p.X, p.Y);
        }
        sb.Append(" Z");
        return sb.ToString();
    }

    private static (double X, double Y) Mid((double X, double Y, bool On) a, (double X, double Y, bool On) b)
        => ((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    // ==================== минимальный TrueType-парсер ====================

    private class FontFile
    {
        private readonly byte[] _d;
        private readonly uint _glyf, _loca, _hmtx;
        private readonly ushort _numHMetrics;
        private readonly int _locaLong;
        public readonly ushort UnitsPerEm;
        public readonly ushort NumGlyphs;
        private readonly CmapSubtable _cmap;

        public FontFile(string path)
        {
            _d = File.ReadAllBytes(path);
            if (_d.Length < 12) throw new InvalidOperationException("Файл шрифта повреждён (слишком короткий).");
            uint sfnt = U32(0);
            if (sfnt != 0x00010000 && sfnt != 0x74727565 && sfnt != 0x4F54544F)
                throw new InvalidOperationException("Не похоже на TTF-шрифт (только TrueType-контуры поддерживаются).");

            ushort numTables = U16(4);
            uint head = 0, hhea = 0, cmap = 0, maxp = 0;
            for (int i = 0; i < numTables; i++)
            {
                int rec = 12 + i * 16;
                if (rec + 16 > _d.Length) break;
                uint tag = U32(rec);
                uint off = U32(rec + 8), len = U32(rec + 12);
                if (off + len > (uint)_d.Length) continue;
                switch (tag)
                {
                    case 0x68656164: head = off; break;   // head
                    case 0x68686561: hhea = off; break;   // hhea
                    case 0x636D6170: cmap = off; break;   // cmap
                    case 0x6D617870: maxp = off; break;   // maxp
                    case 0x676C7966: _glyf = off; break;  // glyf
                    case 0x6C6F6361: _loca = off; break;  // loca
                    case 0x686D7478: _hmtx = off; break;  // hmtx
                }
            }
            if (head == 0 || cmap == 0 || maxp == 0 || _loca == 0 || _glyf == 0 || _hmtx == 0 || hhea == 0)
                throw new InvalidOperationException("В шрифте нет нужных таблиц (glyf/cmap) — файл не подходит.");

            UnitsPerEm = U16((int)head + 18);
            if (UnitsPerEm == 0) UnitsPerEm = 1000;
            _locaLong = S16((int)head + 50);
            NumGlyphs = U16((int)maxp + 4);
            _numHMetrics = U16((int)hhea + 34);
            _cmap = ParseCmap((int)cmap);
        }

        private CmapSubtable ParseCmap(int cmapOff)
        {
            ushort n = U16(cmapOff + 2);
            int best4 = -1, best12 = -1, best0 = -1;
            for (int i = 0; i < n; i++)
            {
                int rec = cmapOff + 4 + i * 8;
                ushort pid = U16(rec), eid = U16(rec + 2);
                uint off = U32(rec + 4);
                int sub = cmapOff + (int)off;
                ushort fmt = U16(sub);
                bool unicode = pid == 0 || (pid == 3 && (eid == 1 || eid == 10 || eid == 0));
                if (!unicode) continue;
                if (fmt == 4 && best4 < 0) best4 = sub;
                if (fmt == 12 && best12 < 0) best12 = sub;
                if ((fmt == 0 || fmt == 6) && best0 < 0) best0 = sub;
            }
            if (best12 >= 0) return new Cmap12(_d, best12);
            if (best4 >= 0) return new Cmap4(_d, best4);
            if (best0 >= 0) return new Cmap0(_d, best0);
            throw new InvalidOperationException("В шрифте нет Unicode-таблицы символов (cmap).");
        }

        public int GlyphIndex(uint codepoint) => _cmap.Get(codepoint);

        public double Advance(int gid)
        {
            if (gid < 0 || _numHMetrics == 0) return UnitsPerEm * 0.5;
            int i = Math.Min(gid, _numHMetrics - 1);
            return U16((int)_hmtx + i * 4);
        }

        public List<List<(double X, double Y, bool On)>> GlyphContours(int gid)
        {
            var res = new List<List<(double, double, bool)>>();
            if (gid < 0 || gid >= NumGlyphs) return res;
            long start, end;
            if (_locaLong == 0)
            {
                start = U16((int)_loca + gid * 2) * 2L;
                end = U16((int)_loca + gid * 2 + 2) * 2L;
            }
            else
            {
                start = U32((int)_loca + gid * 4);
                end = U32((int)_loca + gid * 4 + 4);
            }
            if (end <= start) return res; // пустой глиф (пробел)
            CollectContours((int)(_glyf + start), 1, 0, 0, 1, 0, 1, 0, res);
            return res;
        }

        /// <summary>Разбор простого/составного глифа с матрицей трансформации (a b c d e f).</summary>
        private void CollectContours(long gOff, double a, double b, double c, double dd, double e, double f,
            int depth, List<List<(double, double, bool)>> res)
        {
            if (depth > 8 || gOff + 10 > _d.Length) return;
            short numContours = S16((int)gOff);
            if (numContours >= 0)
            {
                int p = (int)gOff + 10;
                ushort nPts = 0;
                var ends = new ushort[numContours];
                for (int i = 0; i < numContours; i++) { ends[i] = U16(p); p += 2; }
                nPts = numContours > 0 ? (ushort)(ends[numContours - 1] + 1) : (ushort)0;
                ushort instrLen = U16(p); p += 2 + instrLen;

                var flags = new byte[nPts];
                for (int i = 0; i < nPts; )
                {
                    byte fl = _d[p++];
                    flags[i++] = fl;
                    if ((fl & 0x08) != 0) { int rep = _d[p++]; for (int k = 0; k < rep && i < nPts; k++) flags[i++] = fl; }
                }

                var xs = new double[nPts];
                int v = 0;
                for (int i = 0; i < nPts; i++)
                {
                    byte fl = flags[i];
                    if ((fl & 0x02) != 0)
                    {
                        int d8 = _d[p++];                          // короткая дельта (1 байт)
                        if ((fl & 0x10) == 0) d8 = -d8;            // знак задаёт бит X_SAME_OR_POSITIVE
                        v += d8;
                    }
                    else if ((fl & 0x10) == 0) { v += S16(p); p += 2; }          // длинная со знаком
                    xs[i] = v;
                }
                var ys = new double[nPts];
                v = 0;
                for (int i = 0; i < nPts; i++)
                {
                    byte fl = flags[i];
                    if ((fl & 0x04) != 0)
                    {
                        int d8 = _d[p++];
                        if ((fl & 0x20) == 0) d8 = -d8;
                        v += d8;
                    }
                    else if ((fl & 0x20) == 0) { v += S16(p); p += 2; }
                    ys[i] = v;
                }

                int idx = 0;
                for (int ci = 0; ci < numContours; ci++)
                {
                    int cnt = ends[ci] - idx + 1;
                    var list = new List<(double, double, bool)>(cnt);
                    for (int k = 0; k < cnt && idx < nPts; k++, idx++)
                    {
                        double x = a * xs[idx] + c * ys[idx] + e;
                        double y = b * xs[idx] + dd * ys[idx] + f;
                        list.Add((x, y, (flags[idx] & 0x01) != 0));
                    }
                    if (list.Count >= 2) res.Add(list);
                }
            }
            else
            {
                // составной глиф
                int p = (int)gOff + 10;
                while (true)
                {
                    if (p + 4 > _d.Length) break;
                    ushort cfl = U16(p); p += 2;
                    ushort cgi = U16(p); p += 2;
                    double dx, dy;
                    if ((cfl & 0x0002) != 0) // ARGS_ARE_XY_VALUES
                    {
                        if ((cfl & 0x0001) != 0) { dx = S16(p); dy = S16(p + 2); p += 4; }
                        else { dx = (sbyte)_d[p]; dy = (sbyte)_d[p + 1]; p += 2; }
                    }
                    else { dx = 0; dy = 0; p += (cfl & 0x0001) != 0 ? 4 : 2; } // привязка к точкам — не поддерживаем

                    double ca = 1, cb = 0, cc = 0, cd = 1;
                    if ((cfl & 0x0008) != 0) { double sc = S16(p) / 65536.0; p += 2; ca = cd = sc; }                                  // scale
                    else if ((cfl & 0x0040) != 0) { ca = S16(p) / 65536.0; p += 2; cd = S16(p) / 65536.0; p += 2; }                  // xscale, yscale
                    else if ((cfl & 0x0080) != 0)                                                                                     // 2x2: a b c d
                    {
                        ca = S16(p) / 65536.0; p += 2;
                        cb = S16(p) / 65536.0; p += 2;
                        cc = S16(p) / 65536.0; p += 2;
                        cd = S16(p) / 65536.0; p += 2;
                    }

                    double na = ca * a + cb * c, nb = ca * b + cb * dd;
                    double nc = cc * a + cd * c, nd = cc * b + cd * dd;
                    double ne = e + a * dx + c * dy, nf = f + b * dx + dd * dy;

                    long s, en;
                    if (_locaLong == 0)
                    {
                        s = U16((int)_loca + cgi * 2) * 2L;
                        en = U16((int)_loca + cgi * 2 + 2) * 2L;
                    }
                    else
                    {
                        s = U32((int)_loca + cgi * 4);
                        en = U32((int)_loca + cgi * 4 + 4);
                    }
                    if (en > s)
                        CollectContours(_glyf + s, na, nb, nc, nd, ne, nf, depth + 1, res);
                    if ((cfl & 0x0020) == 0) break; // MORE_COMPONENTS
                }
            }
        }

        private ushort U16(int o) => (ushort)((_d[o] << 8) | _d[o + 1]);
        private short S16(int o) => (short)U16(o);
        private uint U32(int o) => ((uint)_d[o] << 24) | ((uint)_d[o + 1] << 16) | ((uint)_d[o + 2] << 8) | _d[o + 3];

        private abstract class CmapSubtable { public abstract int Get(uint cp); }

        private class Cmap4 : CmapSubtable
        {
            private readonly byte[] _d; private readonly int _sub;
            public readonly ushort SegCount;
            public readonly int EndOff, StartOff, DeltaOff, RangeOff, RangeBase;
            public Cmap4(byte[] d, int sub)
            {
                _d = d; _sub = sub;
                SegCount = (ushort)(U16(sub + 6) / 2);
                EndOff = sub + 14;
                StartOff = EndOff + SegCount * 2 + 2;
                DeltaOff = StartOff + SegCount * 2;
                RangeOff = DeltaOff + SegCount * 2;
                RangeBase = RangeOff; // адрес массива idRangeOffset
            }
            public override int Get(uint cp)
            {
                if (cp > 0xFFFF) return 0;
                for (int i = 0; i < SegCount; i++)
                {
                    ushort endC = U16(EndOff + i * 2), startC = U16(StartOff + i * 2);
                    if (cp < startC || cp > endC) continue;
                    ushort ro = U16(RangeOff + i * 2);
                    short delta = S16(DeltaOff + i * 2);
                    if (ro == 0) return (int)((cp + (uint)delta) & 0xFFFF);
                    // адрес: позиция idRangeOffset[i] + ro + 2*(cp - startC)
                    int addr = RangeOff + i * 2 + ro + (int)(cp - startC) * 2;
                    if (addr + 1 >= _d.Length) return 0;
                    ushort g = (ushort)((_d[addr] << 8) | _d[addr + 1]);
                    return g == 0 ? 0 : (g + delta) & 0xFFFF;
                }
                return 0;
            }
            private ushort U16(int o) => (ushort)((_d[o] << 8) | _d[o + 1]);
            private short S16(int o) => (short)U16(o);
        }

        private class Cmap12 : CmapSubtable
        {
            private readonly byte[] _d; private readonly int _sub; private readonly uint _n;
            public Cmap12(byte[] d, int sub) { _d = d; _sub = sub; _n = U32(sub + 12); }
            public override int Get(uint cp)
            {
                for (uint i = 0; i < _n; i++)
                {
                    int g = _sub + 16 + (int)i * 12;
                    uint s = U32(g), e = U32(g + 4), gi = U32(g + 8);
                    if (cp >= s && cp <= e) return (int)(gi + (cp - s));
                    if (cp < s) break;
                }
                return 0;
            }
            private uint U32(int o) => ((uint)_d[o] << 24) | ((uint)_d[o + 1] << 16) | ((uint)_d[o + 2] << 8) | _d[o + 3];
        }

        private class Cmap0 : CmapSubtable
        {
            private readonly byte[] _d; private readonly int _sub;
            public Cmap0(byte[] d, int sub) { _d = d; _sub = sub; }
            public override int Get(uint cp) => cp < 256 ? _d[_sub + 6 + cp] : 0;
        }
    }
}
