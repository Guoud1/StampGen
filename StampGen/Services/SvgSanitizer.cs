using System.Xml.Linq;

// DERECHOS DE AUTOR: StampGen fue escrito por Guoud. La distribución y el uso
// comercial del programa sin permiso del titular de los derechos está PROHIBIDO.
// Contacto para licencias: iljamakeev@yandex.ru
namespace StampGen.Services;

/// <summary>Приведение пользовательского SVG к ч/б: цветные заливки/обводки -> чёрные,
/// белые/светлые -> «none» (прорези), градиенты/маски/картинки/скрипты удаляются.</summary>
public static class SvgSanitizer
{
    public record SanitizeResult(bool Ok, string? Svg, double Aspect, string? Error, List<string> Warnings);

    private static readonly string[] RemovedTags =
        { "script", "foreignObject", "image", "defs", "linearGradient", "radialGradient",
          "pattern", "mask", "filter", "clipPath", "text", "tspan", "style" };

    public static SanitizeResult Sanitize(string content, string fileName)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(content))
            return new SanitizeResult(false, null, 1, "Файл пустой.", warnings);
        try
        {
            var doc = XDocument.Parse(content);
            var root = doc.Root;
            if (root == null || !root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
                return new SanitizeResult(false, null, 1, "Корневой элемент не <svg> — это не SVG-файл.", warnings);

            foreach (var el in root.Descendants().Where(e => RemovedTags.Contains(e.Name.LocalName)).ToList())
            {
                if (el.Name.LocalName is "text" or "tspan") warnings.Add("Текстовые элементы удалены (не годятся для гравировки) — конвертируйте текст в кривые.");
                if (el.Name.LocalName == "image") warnings.Add("Встроенные растровые картинки удалены.");
                if (el.Name.LocalName == "clipPath") warnings.Add("Найден clipPath — он удалён; обрезка контуров не применялась.");
                el.Remove();
            }

            foreach (var el in root.Descendants().ToList())
            {
                // атрибуты цвета/непрозрачности
                foreach (var attr in el.Attributes().ToList())
                {
                    var n = attr.Name.LocalName.ToLowerInvariant();
                    if (n is "opacity" or "fill-opacity" or "stroke-opacity" or "filter" or "mask" or "clip-path" or "style-class")
                        attr.Remove();
                    else if (n == "fill" || n == "stroke")
                        attr.Value = ConvertColor(attr.Value);
                    else if (n == "style")
                        attr.Value = ConvertStyle(attr.Value);
                }
            }

            double aspect = GetAspect(root, warnings);
            if (aspect <= 0 || double.IsNaN(aspect) || double.IsInfinity(aspect)) aspect = 1;

            var svg = doc.ToString(SaveOptions.DisableFormatting);
            if (!root.Descendants().Any(e => e.Name.LocalName is "path" or "rect" or "circle" or "ellipse" or "polygon" or "polyline" or "line"))
                return new SanitizeResult(false, null, aspect, "В SVG не найдено ни одной фигуры (path/rect/circle/polygon...) — гравировать нечего.", warnings);
            return new SanitizeResult(true, svg, aspect, null, warnings);
        }
        catch (Exception ex)
        {
            return new SanitizeResult(false, null, 1, $"Не удалось разобрать SVG: {ex.Message}", warnings);
        }
    }

    private static string ConvertStyle(string style)
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
            if (name is "fill" or "stroke") val = ConvertColor(val);
            outp.Add($"{name}:{val}");
        }
        return string.Join("; ", outp);
    }

    /// <summary>Цвет -> чёрный; белый/светлый и «none» -> none; url(#...) -> чёрный.</summary>
    public static string ConvertColor(string value)
    {
        var v = value.Trim();
        if (v.Length == 0) return "none";
        if (v.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return "none";
        if (v.StartsWith("url(")) return "#000000";
        double lum = Luminance(v);
        if (lum < 0) return "#000000";        // незнакомый формат
        return lum > 0.85 ? "none" : "#000000";
    }

    public static double Luminance(string v)
    {
        v = v.Trim().ToLowerInvariant();
        if (v.StartsWith('#'))
        {
            var hex = v[1..];
            if (hex.Length == 3) hex = new string(hex.Select(c => c).ToArray().SelectMany(c => new[] { c, c }).ToArray());
            if (hex.Length < 6) return -1;
            try
            {
                int r = Convert.ToInt32(hex[..2], 16), g = Convert.ToInt32(hex.Substring(2, 2), 16), b = Convert.ToInt32(hex.Substring(4, 2), 16);
                return (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
            }
            catch { return -1; }
        }
        if (v.StartsWith("rgb"))
        {
            var digits = new string(v.Where(char.IsDigit).ToArray());
            var nums = digits.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (nums.Length >= 3 &&
                int.TryParse(nums[0], out var r) && int.TryParse(nums[1], out var g) && int.TryParse(nums[2], out var b))
                return (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
            return -1;
        }
        return v switch
        {
            "white" => 1.0,
            "black" => 0.0,
            "gray" or "grey" => 0.5,
            "silver" => 0.75,
            "red" => 0.3, "blue" => 0.3, "green" => 0.4,
            "yellow" => 0.9, "orange" => 0.6, "purple" => 0.35, "magenta" => 0.5, "cyan" => 0.7,
            _ => -1
        };
    }

    private static double GetAspect(XElement root, List<string> warnings)
    {
        var vb = root.Attribute("viewBox")?.Value;
        if (!string.IsNullOrWhiteSpace(vb))
        {
            var p = vb.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 4 &&
                double.TryParse(p[2], System.Globalization.CultureInfo.InvariantCulture, out var w) &&
                double.TryParse(p[3], System.Globalization.CultureInfo.InvariantCulture, out var h) &&
                w > 0 && h > 0) return w / h;
        }
        var wA = root.Attribute("width")?.Value; var hA = root.Attribute("height")?.Value;
        if (wA != null && hA != null)
        {
            double? Parse(string? s)
            {
                if (s == null) return null;
                var num = new string(s.TakeWhile(c => char.IsDigit(c) || c == '.' || c == ',').ToArray()).Replace(',', '.');
                return double.TryParse(num, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
            }
            var w = Parse(wA); var h = Parse(hA);
            if (w is > 0 && h is > 0) return w.Value / h.Value;
        }
        warnings.Add("Не найден viewBox — принято 1:1. Проверьте пропорции.");
        return 1;
    }

    /// <summary>Быстрое чтение аспекта без полной санации (для открытых проектов).</summary>
    public static double AspectOnly(string content)
    {
        try
        {
            var doc = XDocument.Parse(content);
            return doc.Root == null ? 1 : GetAspect(doc.Root, new List<string>());
        }
        catch { return 1; }
    }
}
