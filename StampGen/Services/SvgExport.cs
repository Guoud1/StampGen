using System.Globalization;
using System.Xml.Linq;
using Clipper2Lib;

// StampGen — экспорт SVG: только оттиски, без рамки пластины и служебных линий.
// © Guoud (iljamakeev@yandex.ru). Все права защищены. Условия: LICENSE.md.
namespace StampGen.Services;

/// <summary>Векторный SVG-экспорт: ТОЛЬКО оттиски, без рамки пластины, служебных
/// линий и подписи сверху (v1.6) — содержимое совпадает с PDF-экспортом один в один.</summary>
public static class SvgExport
{
    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static async Task<string> BuildAsync(StampState state, List<LayoutSolver.Instance> instances, StampLibrary lib)
    {
        var p = state.Plate;
        var sb = new System.Text.StringBuilder();

        // v1.6: ровно габарит пластины; никаких рамок, dashed-линий и заголовка
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(p.Width)}mm\" height=\"{N(p.Depth)}mm\" viewBox=\"{N(0)} {N(0)} {N(p.Width)} {N(p.Depth)}\">");

        // оттиски
        sb.AppendLine("<g id=\"stamps\">");
        foreach (var i in instances)
        {
            string? svgText = await GetStampSvg(state, i, lib);
            if (svgText == null) continue;
            var mirror = i.Mirror ? $"translate({N(i.W)},0) scale(-1,1)" : "";
            // порядок «зеркало → поворот» согласован с 3D-моделью и PDF-экспортом
            sb.AppendLine($"<g transform=\"translate({N(i.AnchorX)},{N(i.AnchorY)}) rotate({N(i.RotDeg)} {N(i.W / 2)} {N(i.H / 2)}) {mirror}\">");
            sb.AppendLine(WrapInner(svgText, i.W, i.H));
            sb.AppendLine("</g>");
        }
        sb.AppendLine("</g></svg>");
        return sb.ToString();
    }

    internal static async Task<string?> GetStampSvg(StampState state, LayoutSolver.Instance i, StampLibrary lib)
    {
        var st = state.Stamps.FirstOrDefault(s => s.Id == i.StampId);
        if (st == null) return null;
        // v1.4 ФИКС: текстовые оттиски берём из TextSvgs (по Id оттиска) — раньше они
        // ошибочно искались в CustomSvgs и в SVG-экспорт вообще не попадали.
        if (st.Kind == StampKind.Text)
            return state.TextSvgs.TryGetValue(st.Id.ToString(), out var tsvg) ? tsvg : null;
        if (st.Source == StampSource.Base)
            return await lib.GetSvgTextAsync(st.SvgPath);   // v1.7: штамп из библиотеки (папка stamp/)
        return state.CustomSvgs.TryGetValue(st.CustomSvgId, out var svg) ? svg : null;
    }

    /// <summary>Заворачивает содержимое исходного SVG в вложенный <svg> с целевым размером.</summary>
    private static string WrapInner(string svgText, double w, double h)
    {
        try
        {
            var doc = XDocument.Parse(svgText);
            var root = doc.Root!;
            var vb = root.Attribute("viewBox")?.Value;
            if (string.IsNullOrWhiteSpace(vb))
            {
                var wA = root.Attribute("width")?.Value; var hA = root.Attribute("height")?.Value;
                vb = $"0 0 {wA} {hA}";
            }
            var inner = string.Concat(root.Nodes().Select(n => n.ToString()));
            return $"<svg x=\"0\" y=\"0\" width=\"{N(w)}\" height=\"{N(h)}\" viewBox=\"{Escape(vb)}\" preserveAspectRatio=\"none\">{inner}</svg>";
        }
        catch
        {
            return "";
        }
    }

    private static string Escape(string s) => s
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}

/// <summary>
/// v1.6: PDF-экспорт на ТОМ ЖЕ парсере, что и SVG (SvgShapes): контуры оттисков
/// переводятся в полигоны и записываются в PDF как векторные пути. Содержимое
/// SVG и PDF теперь совпадает математически.
/// Раньше был отдельный хрупкий конвертер SVG-элементов — часть значков
/// пропадала, тексты вставали не на место. Никаких рамок и подписей —
/// только сами оттиски на листе размером с пластину.
/// </summary>
public static class PdfExport
{
    private const double K = 72.0 / 25.4;   // мм -> pt
    private const int Segments = 24;        // сгущение кривых (векторное качество)

    public static async Task<string> BuildAsync(StampState state, List<LayoutSolver.Instance> instances, StampLibrary lib)
    {
        var p = state.Plate;
        double W = p.Width * K, D = p.Depth * K;

        var c = new List<string>
        {
            "0 G 0 g [] 0 d 1 w",
            $"{F(p.Width * K)} 0 m {F(p.Width * K)} {F(p.Depth * K)} l f",   // (безопасный первый оператор — не виден)
        };
        c.RemoveAt(1);

        foreach (var i in instances)
        {
            string? svgText = await SvgExport.GetStampSvg(state, i, lib);
            if (string.IsNullOrWhiteSpace(svgText)) continue;
            var set = SvgShapes.Parse(svgText, Segments);
            if (set == null || set.Shapes.Count == 0) continue;
            var placed = SvgShapes.Place(set, i.W, i.H, i.AnchorX, i.AnchorY, i.RotDeg, i.Mirror);
            if (placed.Count == 0) continue;

            c.Add("q");
            var sb = new System.Text.StringBuilder();
            foreach (var path in placed)
            {
                if (path.Count < 3) continue;
                for (int k = 0; k < path.Count; k++)
                {
                    var pt = path[k];
                    double px = pt.x * K;
                    double py = (p.Depth - pt.y) * K;   // y пластины (вниз) -> система PDF (вверх)
                    sb.Append(k == 0 ? $"{F(px)} {F(py)} m " : $"{F(px)} {F(py)} l ");
                }
                sb.Append("h ");
            }
            // заливка nonzero: внешние контуры и отверстия после Union намотаны в разные стороны
            sb.Append("f");
            c.Add(sb.ToString());
            c.Add("Q");
        }

        string content = string.Join("\n", c);
        var objs = new List<string>
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 {F(W)} {F(D)}]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>",
            null!, // placeholder for content stream
            "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>"
        };
        objs[3] = $"<</Length {content.Length}>>\nstream\n{content}\nendstream";

        var pdfDoc = new System.Text.StringBuilder();
        pdfDoc.Append("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objs.Count; i++)
        {
            offsets.Add(pdfDoc.Length);
            pdfDoc.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n");
        }
        int xref = pdfDoc.Length;
        pdfDoc.Append($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets)
            pdfDoc.Append(off.ToString("D10") + " 00000 n \n");
        pdfDoc.Append($"trailer\n<</Size {objs.Count + 1}/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF");
        return pdfDoc.ToString();
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
