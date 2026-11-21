using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Thrust.Graphics;

/// <summary>Alignment of each line within the text block.</summary>
public enum TextAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>Pixel-based text layout settings. Font and image library types stay inside Thrust.</summary>
public sealed record TextRasterizationOptions
{
    public string FontFamily { get; init; } = "Arial";
    public float FontSize { get; init; } = 16f;
    public IReadOnlyList<string>? FallbackFamilies { get; init; }
    public float LineSpacing { get; init; } = 1f;
    public TextAlignment Alignment { get; init; } = TextAlignment.Left;
    public int Padding { get; init; }

    /// <summary>Maximum canvas dimensions. Overflow is clipped without wrapping or scaling.</summary>
    public int MaxWidth { get; init; } = 8192;
    public int MaxHeight { get; init; } = 4096;
}

/// <summary>
/// Measures and rasterizes text into top-down RGBA32 pixels using the installed system fonts.
/// No window or OpenGL context is required.
/// </summary>
public static class TextRasterizer
{
    private const int FontCacheLimit = 128;
    private static readonly TextRasterizationOptions DefaultOptions = new();
    private static readonly Dictionary<string, ResolvedFonts> FontCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object FontCacheLock = new();

    private sealed record ResolvedFonts(FontFamily Primary, FontFamily[] Fallbacks);

    /// <summary>Returns the same padded, clipped canvas size as Rasterize, without allocating pixels.</summary>
    public static (int Width, int Height) Measure(string text, TextRasterizationOptions? options = null)
    {
        var layout = CreateLayout(text, options ?? DefaultOptions);
        return (layout.Width, layout.Height);
    }

    /// <summary>
    /// Rasterizes text without automatic wrapping. The returned image has transparent background
    /// and white, antialiased glyphs in top-down RGBA32 pixel rows. Each call owns its pixel buffer.
    /// </summary>
    public static ImageDecoder.RgbaImage Rasterize(
        string text, TextRasterizationOptions? options = null)
    {
        var layout = CreateLayout(text, options ?? DefaultOptions);
        byte[] pixels = new byte[checked(layout.Width * layout.Height * 4)];
        if (layout.Options is not null)
        {
            // Render directly into the returned array: no second full-size image buffer, PNG
            // round-trip or pixel copy is needed. Disposing the view leaves our array intact.
            using Image<Rgba32> image = Image.WrapMemory<Rgba32>(pixels.AsMemory(), layout.Width, layout.Height);
            image.Mutate(context => context.Paint(canvas =>
                canvas.DrawText(layout.Options, text.AsSpan(), Brushes.Solid(Color.White), null)));
        }
        return new ImageDecoder.RgbaImage(layout.Width, layout.Height, pixels);
    }

    private static (int Width, int Height, RichTextOptions? Options) CreateLayout(
        string text, TextRasterizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FontFamily);
        if (!float.IsFinite(options.FontSize) || options.FontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.FontSize));
        if (!float.IsFinite(options.LineSpacing) || options.LineSpacing <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.LineSpacing));
        if (!Enum.IsDefined(options.Alignment))
            throw new ArgumentOutOfRangeException(nameof(options.Alignment));
        ArgumentOutOfRangeException.ThrowIfNegative(options.Padding);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxHeight);
        if (options.FallbackFamilies is { } fallbacks)
            foreach (string fallback in fallbacks)
                ArgumentException.ThrowIfNullOrWhiteSpace(fallback, nameof(options.FallbackFamilies));

        if (text.Length == 0)
            return (1, 1, null);

        ResolvedFonts fonts = GetFonts(options.FontFamily, options.FallbackFamilies);
        Font font = fonts.Primary.CreateFont(options.FontSize, FontStyle.Regular);
        var drawOptions = new RichTextOptions(font)
        {
            Dpi = 72,
            WrappingLength = -1,
            FallbackFontFamilies = fonts.Fallbacks,
            FontFallbackResolver = SystemFonts.FallbackResolver,
            LineSpacing = options.LineSpacing,
            TextAlignment = options.Alignment switch
            {
                TextAlignment.Center => SixLabors.Fonts.TextAlignment.Center,
                TextAlignment.Right => SixLabors.Fonts.TextAlignment.End,
                _ => SixLabors.Fonts.TextAlignment.Start,
            },
        };

        FontRectangle advance = TextMeasurer.MeasureAdvance(text, drawOptions);
        FontRectangle ink = TextMeasurer.MeasureBounds(text, drawOptions);
        float emLineHeight = options.FontSize * options.LineSpacing;
        float fontLineHeight = font.FontMetrics.HorizontalMetrics.LineHeight *
            (options.FontSize / font.FontMetrics.UnitsPerEm);
        float outset = MathF.Max(0, (fontLineHeight - emLineHeight) * .5f);

        // Keep the font's line box rather than cropping to this string's glyphs. Otherwise
        // changing "hello" to "gyp" moves/resizes a centred decoration. Include ink overhangs
        // and explicit blank lines as well, so accents, fallback glyphs and newlines survive.
        int lineCount = 1 + text.AsSpan().Count('\n');
        float left = MathF.Floor(MathF.Min(0, ink.Left));
        float top = MathF.Floor(MathF.Min(-outset, ink.Top));
        float right = MathF.Ceiling(MathF.Max(advance.Right, ink.Right));
        float bottom = MathF.Ceiling(MathF.Max(
            MathF.Max(advance.Height, lineCount * emLineHeight) + outset, ink.Bottom));
        int width = ClampDimension((double)right - left + 2d * options.Padding, options.MaxWidth);
        int height = ClampDimension((double)bottom - top + 2d * options.Padding, options.MaxHeight);
        drawOptions.Origin = new PointF(options.Padding - left, options.Padding - top);
        return (width, height, drawOptions);
    }

    private static int ClampDimension(double value, int maximum)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), "The text layout exceeds the supported size.");
        return (int)Math.Clamp(value, 1d, maximum);
    }

    private static ResolvedFonts GetFonts(string family, IReadOnlyList<string>? fallbacks)
    {
        string key = fallbacks is not { Count: > 0 } ? family : family + "\0" + string.Join('\0', fallbacks);
        lock (FontCacheLock)
        {
            if (FontCache.TryGetValue(key, out var cached))
                return cached;

            ResolvedFonts resolved = ResolveFonts(key);
            // Cache faces, not every requested font size or text. Keep arbitrary authored
            // family names from growing a process-wide cache indefinitely.
            if (FontCache.Count < FontCacheLimit)
                FontCache.Add(key, resolved);
            return resolved;
        }
    }

    private static ResolvedFonts ResolveFonts(string key)
    {
        var resolved = new List<FontFamily>();
        foreach (string name in key.Split('\0'))
        {
            if (SystemFonts.TryGet(name, out FontFamily family) &&
                !resolved.Any(existing => existing.Name.Equals(family.Name, StringComparison.OrdinalIgnoreCase)))
                resolved.Add(family);
        }

        if (resolved.Count > 0)
            return new ResolvedFonts(resolved[0], [.. resolved.Skip(1)]);

        FontFamily firstInstalled = SystemFonts.Families.FirstOrDefault();
        if (string.IsNullOrEmpty(firstInstalled.Name))
            throw new InvalidOperationException("No system fonts are available for text rasterization.");
        return new ResolvedFonts(firstInstalled, []);
    }
}
