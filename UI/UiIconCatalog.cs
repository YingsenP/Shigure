using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using Svg;

namespace Shigure;

/// <summary>
/// 将嵌入的单色 SVG（Bootstrap Icons，fill=currentColor）按指定尺寸和颜色光栅化。
/// </summary>
internal static class UiIconCatalog
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, string> SvgMarkup = new(StringComparer.Ordinal);
    private static readonly Dictionary<(string Name, int Size, int Argb), Bitmap> Cache = new();

    static UiIconCatalog()
    {
        var prefix = $"{typeof(UiIconCatalog).Namespace}.Assets.UiIcons.";
        foreach (var resourceName in typeof(UiIconCatalog).Assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix, StringComparison.Ordinal)
                || !resourceName.EndsWith(".svg", StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = typeof(UiIconCatalog).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            SvgMarkup[resourceName[prefix.Length..^4]] = reader.ReadToEnd();
        }
    }

    public static void Draw(Graphics graphics, SettingsNavIcon icon, Rectangle bounds, Color color)
        => Draw(graphics, icon.ToString(), bounds, color);

    public static void Draw(Graphics graphics, string name, Rectangle bounds, Color color)
    {
        var size = Math.Max(1, Math.Min(bounds.Width, bounds.Height));
        var image = Get(name, size, color);
        if (image is null)
        {
            return;
        }

        var destination = new Rectangle(
            bounds.Left + (bounds.Width - size) / 2,
            bounds.Top + (bounds.Height - size) / 2,
            size,
            size);
        var previous = graphics.InterpolationMode;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(image, destination);
        graphics.InterpolationMode = previous;
    }

    private static Image? Get(string name, int size, Color color)
    {
        lock (SyncRoot)
        {
            var key = (name, size, color.ToArgb());
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (!SvgMarkup.TryGetValue(name, out var markup))
            {
                return null;
            }

            try
            {
                var filled = markup.Replace("currentColor", ToCssColor(color), StringComparison.Ordinal);
                var document = SvgDocument.FromSvg<SvgDocument>(filled);
                document.Width = new SvgUnit(SvgUnitType.Pixel, size);
                document.Height = new SvgUnit(SvgUnitType.Pixel, size);
                var bitmap = document.Draw(size, size);
                Cache[key] = bitmap;
                return bitmap;
            }
            catch (Exception ex) when (ex is SvgException or ArgumentException or IOException)
            {
                return null;
            }
        }
    }

    private static string ToCssColor(Color color)
        => color.A >= 255
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"rgba({color.R},{color.G},{color.B},{color.A / 255f:0.###})");
}
