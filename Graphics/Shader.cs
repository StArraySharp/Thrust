namespace Thrust.Graphics;

/// <summary>
/// 着色器程序：顶点/片元源码与后端句柄。
/// A shader program: the vertex/fragment sources plus the backend handle.
/// </summary>
public sealed class Shader : IDisposable
{
    /// <summary>
    /// Optional source resolver. When set, renderers ask it for shader text before touching the disk,
    /// which lets a self-contained executable serve shaders from its own embedded resources.
    /// </summary>
    public static Func<string, string?>? SourceResolver { get; set; }

    public Shader(string vertexSource, string fragmentSource, string? name = null)
    {
        VertexSource = vertexSource;
        FragmentSource = fragmentSource;
        Name = name ?? "Shader";
    }

    public string Name { get; set; }

    public string VertexSource { get; }

    public string FragmentSource { get; }

    /// <summary>后端创建的 GPU 句柄 / Backend program handle (opaque to this layer).</summary>
    public int Handle { get; internal set; }

    /// <summary>是否已编译并链接 / Whether the program has been linked.</summary>
    public bool IsCompiled => Handle != 0;

    public bool IsDisposed { get; private set; }

    /// <summary>
    /// 从磁盘目录读取两个阶段并构造，找不到文件时抛 <see cref="FileNotFoundException"/>。
    /// Load both stages from a disk directory, throwing <see cref="FileNotFoundException"/> when a
    /// file is missing.
    /// </summary>
    /// <param name="directory">着色器所在目录 / Directory holding the shader files.</param>
    /// <param name="vertexName">顶点着色器文件名 / Vertex shader file name.</param>
    /// <param name="fragmentName">片元着色器文件名 / Fragment shader file name.</param>
    public static Shader FromFiles(string directory, string vertexName, string fragmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(vertexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fragmentName);

        string vertexSource = ReadSource(directory, vertexName);
        string fragmentSource = ReadSource(directory, fragmentName);
        return new Shader(vertexSource, fragmentSource, Path.GetFileNameWithoutExtension(vertexName));
    }

    /// <summary>读取一个着色器文件 / Read one shader file.</summary>
    public static string ReadSource(string directory, string name)
    {
        // A resolver lets a single-file bundle supply the source without a loose file on disk.
        if (SourceResolver?.Invoke(name) is { } resolved)
        {
            return resolved;
        }

        string path = Path.Combine(directory, name);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到着色器文件 / Shader file not found: {path}", path);
        }

        return File.ReadAllText(path, System.Text.Encoding.UTF8);
    }

    public void Dispose()
    {
        Handle = 0;
        IsDisposed = true;
    }
}
