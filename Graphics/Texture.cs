using OpenTK.Mathematics;
using Thrust.Geometry;

namespace Thrust.Graphics;

/// <summary>
/// 纹理采样与环绕模式 / Texture sampling and wrapping.
/// </summary>
public enum TextureWrap
{
    Repeat,
    ClampToEdge,
    MirroredRepeat,
}

/// <summary>
/// 纹理过滤模式 / Texture filtering.
/// </summary>
public enum TextureFilter
{
    Linear,
    Nearest,
}

/// <summary>
/// CPU 纹理数据，RGBA32 排列。引擎本身不解码图片格式，像素由宿主提供，
/// 这样引擎不绑定任何图像解码库。
/// CPU texture data in RGBA32 order. The engine does not decode image formats itself; the host
/// supplies the pixels, so the engine stays free of any image-decoding dependency.
/// </summary>
public sealed class Texture : IDisposable
{
    private byte[] _pixels;

    public Texture(int width, int height, byte[] rgba)
    {
        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException("像素数据长度必须等于 width * height * 4 / Pixel buffer must be width*height*4 bytes", nameof(rgba));
        }

        Width = width;
        Height = height;
        _pixels = rgba;
    }

    public int Width { get; }

    public int Height { get; }

    public string Name { get; private set; } = "Texture";

    public TextureWrap WrapU { get; set; } = TextureWrap.Repeat;

    public TextureWrap WrapV { get; set; } = TextureWrap.Repeat;

    public TextureFilter MinFilter { get; set; } = TextureFilter.Linear;

    public TextureFilter MagFilter { get; set; } = TextureFilter.Linear;

    /// <summary>生成 mipmap 标记，具体上传逻辑留给渲染后端 / Ask the backend to generate mipmaps.</summary>
    public bool GenerateMipmaps { get; set; } = true;

    /// <summary>只读 RGBA32 像素 / Read-only RGBA32 pixels.</summary>
    public ReadOnlyMemory<byte> Pixels => _pixels;

    /// <summary>数据版本，更新时自增 / Incremented whenever the pixels change.</summary>
    public int Version { get; private set; }

    /// <summary>是否已释放 / Whether the texture has been disposed.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>整体替换像素 / Replace all pixels.</summary>
    public void SetPixels(byte[] rgba, int width, int height)
    {
        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException("像素数据长度必须等于 width * height * 4 / Pixel buffer must be width*height*4 bytes", nameof(rgba));
        }

        _pixels = rgba;
        Version++;
    }

    /// <summary>读取某个像素（RGBA32） / Read one pixel as RGBA32.</summary>
    public Color32 GetPixel(int x, int y)
    {
        int index = (y * Width + x) * 4;
        return new Color32(_pixels[index], _pixels[index + 1], _pixels[index + 2], _pixels[index + 3]);
    }

    /// <summary>
    /// 双线性采样，UV 可超出 [0,1] 由环绕模式处理
    /// Bilinear sampling; UVs outside [0,1] are handled by the wrap mode.
    /// </summary>
    public Color32 Sample(Vector2 uv)
    {
        float u = ApplyWrap(uv.X, WrapU) * (Width - 1);
        float v = ApplyWrap(uv.Y, WrapV) * (Height - 1);

        int x0 = (int)MathF.Floor(u);
        int y0 = (int)MathF.Floor(v);
        int x1 = Math.Min(x0 + 1, Width - 1);
        int y1 = Math.Min(y0 + 1, Height - 1);

        float fx = u - x0;
        float fy = v - y0;

        Color32 c00 = GetPixel(x0, y0);
        Color32 c10 = GetPixel(x1, y0);
        Color32 c01 = GetPixel(x0, y1);
        Color32 c11 = GetPixel(x1, y1);

        return Color32.Lerp(Color32.Lerp(c00, c10, fx), Color32.Lerp(c01, c11, fx), fy);
    }

    private static float ApplyWrap(float value, TextureWrap wrap) => wrap switch
    {
        TextureWrap.ClampToEdge => Math.Clamp(value, 0f, 1f),
        TextureWrap.MirroredRepeat => ApplyMirroredRepeat(value),
        _ => value - MathF.Floor(value),
    };

    private static float ApplyMirroredRepeat(float value)
    {
        float t = MathF.Abs(value) % 2f;
        return t > 1f ? 2f - t : t;
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        _pixels = [];
        IsDisposed = true;
    }
}

/// <summary>
/// 8 位 RGBA 颜色 / 8-bit RGBA color.
/// </summary>
public readonly struct Color32
{
    public readonly byte R;
    public readonly byte G;
    public readonly byte B;
    public readonly byte A;

    public Color32(byte r, byte g, byte b, byte a = 255)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public static Color32 White => new(255, 255, 255);
    public static Color32 Black => new(0, 0, 0);
    public static Color32 Transparent => new(0, 0, 0, 0);

    /// <summary>转换为浮点颜色（0~1） / Convert to a float color in 0~1.</summary>
    public Vector4 ToVector4() => new(R / 255f, G / 255f, B / 255f, A / 255f);

    /// <summary>转换为打包的 0xRRGGBBAA / Packed 0xRRGGBBAA.</summary>
    public uint ToPacked() => (uint)(R << 24 | G << 16 | B << 8 | A);

    public static Color32 Lerp(in Color32 a, in Color32 b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new Color32(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t),
            (byte)(a.A + (b.A - a.A) * t));
    }

    public override string ToString() => $"rgba({R}, {G}, {B}, {A})";
}
