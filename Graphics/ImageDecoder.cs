using OpenTK.Graphics.OpenGL4;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Thrust.Graphics;

/// <summary>
/// 图片解码：把 PNG / JPEG 等格式解成 RGBA32 像素，并可直接上传成 OpenGL 纹理。
/// 引擎内部使用 ImageSharp，外部调用方无需依赖任何图像库。
/// Image decoding: turns PNG / JPEG and friends into RGBA32 pixels and can upload them straight
/// into an OpenGL texture. ImageSharp stays inside the engine, so callers need no image library.
/// </summary>
public static class ImageDecoder
{
    /// <summary>解码结果：RGBA32 像素 + 尺寸 / Decoded RGBA32 pixels plus their size.</summary>
    public readonly struct RgbaImage
    {
        public RgbaImage(int width, int height, byte[] pixels)
        {
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        public int Width { get; }

        public int Height { get; }

        /// <summary>RGBA32 像素，行序自上而下 / RGBA32 pixels, rows top-down.</summary>
        public byte[] Pixels { get; }
    }

    /// <summary>
    /// 解码字节流为 RGBA32。格式由内容自动识别。
    /// Decode a byte buffer into RGBA32; the format is detected from the content.
    /// </summary>
    public static RgbaImage Decode(ReadOnlySpan<byte> data)
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(data);
        byte[] pixels = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(pixels);
        return new RgbaImage(image.Width, image.Height, pixels);
    }

    /// <summary>解码文件为 RGBA32 / Decode a file into RGBA32.</summary>
    public static RgbaImage DecodeFile(string path)
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(path);
        byte[] pixels = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(pixels);
        return new RgbaImage(image.Width, image.Height, pixels);
    }

    /// <summary>
    /// 解码并上传成 OpenGL 纹理，返回纹理句柄（0 表示失败）。
    /// Decode and upload as an OpenGL texture, returning the handle (0 on failure).
    /// </summary>
    public static int LoadTexture(
        ReadOnlySpan<byte> data,
        bool generateMipmaps = true,
        bool repeat = true)
    {
        RgbaImage image = Decode(data);
        return Upload(image, generateMipmaps, repeat);
    }

    /// <summary>
    /// 把 RGBA32 像素上传成 OpenGL 纹理。行序按自下而上处理，与 OpenGL 的纹理原点一致。
    /// Upload RGBA32 pixels as an OpenGL texture. Rows are treated as bottom-up, matching the
    /// OpenGL texture origin.
    /// </summary>
    public static int Upload(in RgbaImage image, bool generateMipmaps = true, bool repeat = true)
    {
        ArgumentNullException.ThrowIfNull(image.Pixels);

        return Upload(image.Pixels, image.Width, image.Height, generateMipmaps, repeat);
    }

    /// <summary>上传 RGBA32 像素为 OpenGL 纹理 / Upload RGBA32 pixels as an OpenGL texture.</summary>
    public static unsafe int Upload(
        byte[] pixels,
        int width,
        int height,
        bool generateMipmaps = true,
        bool repeat = true)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        int texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);

        fixed (byte* data = pixels)
        {
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgba8,
                width,
                height,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                (nint)data);
        }

        TextureWrapMode wrap = repeat ? TextureWrapMode.Repeat : TextureWrapMode.ClampToEdge;

        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrap);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrap);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

        if (generateMipmaps)
        {
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        }
        else
        {
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        }

        GL.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }

    /// <summary>
    /// 把若干张图缩放到同一格并拼成 <paramref name="columns"/>×<paramref name="rows"/> 图集。
    /// 缺失的格子留透明，返回 RGBA32 像素（自上而下）。
    /// Scale each image into one cell and pack them into a columns×rows atlas. Missing cells stay
    /// transparent; the result is RGBA32 with top-down rows.
    /// </summary>
    /// <param name="sources">按槽位顺序排列的图片数据 / Image data in slot order.</param>
    /// <param name="columns">每行格数 / Cells per row.</param>
    /// <param name="rows">行数 / Number of rows.</param>
    /// <param name="cellSize">每格边长（像素） / Cell edge length in pixels.</param>
    public static byte[] BuildAtlas(
        IReadOnlyList<byte[]?> sources,
        int columns,
        int rows,
        int cellSize)
    {
        ArgumentNullException.ThrowIfNull(sources);

        if (columns <= 0 || rows <= 0 || cellSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), "图集尺寸必须为正 / Atlas dimensions must be positive");
        }

        int width = columns * cellSize;
        int height = rows * cellSize;

        using Image<Rgba32> atlas = new(width, height);

        for (int i = 0; i < sources.Count; i++)
        {
            byte[]? bytes = sources[i];

            if (bytes is null || bytes.Length == 0)
            {
                continue;
            }

            using Image<Rgba32> icon = Image.Load<Rgba32>(bytes);
            icon.Mutate(x => x.Resize(cellSize, cellSize));

            int left = i % columns * cellSize;
            int top = i / columns * cellSize;

            atlas.Mutate(x => x.DrawImage(icon, new Point(left, top), 1f));
        }

        byte[] pixels = new byte[width * height * 4];
        atlas.CopyPixelDataTo(pixels);
        return pixels;
    }

    /// <summary>
    /// 原地垂直翻转像素行（自上而下 → 自下而上）。ImageSharp 为上而下，OpenGL 原点在左下。
    /// Flip pixel rows in place (top-down to bottom-up): ImageSharp is top-down while OpenGL's
    /// texture origin sits at the bottom-left.
    /// </summary>
    public static void FlipVertically(byte[] pixels, int width, int height)
    {
        int stride = width * 4;
        byte[] row = new byte[stride];

        for (int y = 0; y < height / 2; y++)
        {
            int top = y * stride;
            int bottom = (height - 1 - y) * stride;
            System.Buffer.BlockCopy(pixels, top, row, 0, stride);
            System.Buffer.BlockCopy(pixels, bottom, pixels, top, stride);
            System.Buffer.BlockCopy(row, 0, pixels, bottom, stride);
        }
    }
}
