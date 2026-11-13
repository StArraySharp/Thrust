using OpenTK.Graphics.OpenGL4;
using Thrust.Geometry;
using OpenTK.Mathematics;

namespace Thrust.Graphics;

/// <summary>
/// 动态圆形渲染器：以实例化的方式绘制会移动的实心圆。
/// Dynamic circle renderer that draws moving solid discs.
/// </summary>
public sealed unsafe class CircleRenderer : IDisposable
{
    private const int Segments = 48;
    private static readonly int VertexCount = Segments + 2; // 中心 + 环上顶点
    private static readonly int IndexCount = Segments * 3;

    private readonly int _vao;
    private readonly int _vbo;
    private readonly int _ebo;
    private readonly int _shader;
    private readonly int _locationViewProjection;
    private readonly int _locationCenter;
    private readonly int _locationRadius;
    private readonly int _locationColor;
    private bool _disposed;

    /// <summary>
    /// 用指定目录下的 Circle.vert / Circle.frag 构造。
    /// Create with Circle.vert / Circle.frag taken from the given directory.
    /// </summary>
    /// <param name="shaderDirectory">着色器所在目录 / Directory holding the shader files.</param>
    public CircleRenderer(string shaderDirectory)
    {
        _shader = CreateShader(shaderDirectory);
        _locationViewProjection = GL.GetUniformLocation(_shader, "uViewProjection");
        _locationCenter = GL.GetUniformLocation(_shader, "uCenter");
        _locationRadius = GL.GetUniformLocation(_shader, "uRadius");
        _locationColor = GL.GetUniformLocation(_shader, "uColor");

        BuildUnitCircle(out float[] vertices, out uint[] indices);

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        _ebo = GL.GenBuffer();

        GL.BindVertexArray(_vao);

        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        fixed (float* data = vertices)
        {
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), (nint)data, BufferUsageHint.StaticDraw);
        }

        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
        fixed (uint* indicesPtr = indices)
        {
            GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), (nint)indicesPtr, BufferUsageHint.StaticDraw);
        }

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);

        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
    }

    /// <summary>绘制一个圆 / Draw one circle.</summary>
    public void Draw(in Matrix4 viewProjection, Vector2 center, float radius, Vector4 color)
    {
        GL.UseProgram(_shader);

        if (_locationViewProjection >= 0)
        {
            Matrix4 vp = viewProjection;
            GL.UniformMatrix4(_locationViewProjection, false, ref vp);
        }

        if (_locationCenter >= 0)
        {
            GL.Uniform2(_locationCenter, center.X, center.Y);
        }

        if (_locationRadius >= 0)
        {
            GL.Uniform1(_locationRadius, radius);
        }

        if (_locationColor >= 0)
        {
            GL.Uniform4(_locationColor, color.X, color.Y, color.Z, color.W);
        }

        GL.BindVertexArray(_vao);
        GL.DrawElements(PrimitiveType.Triangles, IndexCount, DrawElementsType.UnsignedInt, 0);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    private static void BuildUnitCircle(out float[] vertices, out uint[] indices)
    {
        vertices = new float[VertexCount * 2];
        indices = new uint[IndexCount];

        // 中心点 / Center vertex
        vertices[0] = 0f;
        vertices[1] = 0f;

        for (int i = 0; i < Segments; i++)
        {
            float angle = MathF.Tau * i / Segments;
            int offset = (i + 1) * 2;
            vertices[offset] = MathF.Cos(angle);
            vertices[offset + 1] = MathF.Sin(angle);
        }

        // 最后一个顶点与第一个重合，闭合圆环 / Close the ring
        int last = (Segments + 1) * 2;
        vertices[last] = vertices[2];
        vertices[last + 1] = vertices[3];

        for (int i = 0; i < Segments; i++)
        {
            indices[i * 3] = 0;
            indices[i * 3 + 1] = (uint)(i + 1);
            indices[i * 3 + 2] = (uint)(i + 2);
        }
    }

    private static int CreateShader(string shaderDirectory)
    {
        Shader loaded = Shader.FromFiles(shaderDirectory, "Circle.vert", "Circle.frag");
        string vertexSource = loaded.VertexSource;
        string fragmentSource = loaded.FragmentSource;
        loaded.Dispose();

        int vertex = CompileShader(ShaderType.VertexShader, vertexSource);
        int fragment = CompileShader(ShaderType.FragmentShader, fragmentSource);

        int program = GL.CreateProgram();
        GL.AttachShader(program, vertex);
        GL.AttachShader(program, fragment);
        GL.LinkProgram(program);

        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int status);
        if (status == 0)
        {
            string log = GL.GetProgramInfoLog(program);
            GL.DeleteProgram(program);
            throw new InvalidOperationException($"着色器链接失败 / Shader link failed: {log}");
        }

        GL.DetachShader(program, vertex);
        GL.DetachShader(program, fragment);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);

        return program;
    }

    private static int CompileShader(ShaderType type, string source)
    {
        int shader = GL.CreateShader(type);
        string[] sources = [source];
        int[] lengths = [System.Text.Encoding.UTF8.GetByteCount(source)];
        GL.ShaderSource(shader, 1, sources, lengths);
        GL.CompileShader(shader);

        GL.GetShader(shader, ShaderParameter.CompileStatus, out int status);
        if (status == 0)
        {
            string log = GL.GetShaderInfoLog(shader);
            GL.DeleteShader(shader);
            throw new InvalidOperationException($"{type} 编译失败 / Compilation failed: {log}");
        }

        return shader;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
        GL.DeleteBuffer(_ebo);
        GL.DeleteProgram(_shader);
        _disposed = true;
    }
}
