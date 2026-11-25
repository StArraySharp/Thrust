using OpenTK.Graphics.OpenGL4;
using Thrust.Geometry;
using OpenTK.Mathematics;

namespace Thrust.Graphics;

/// <summary>
/// OpenGL 网格上传器：把交错顶点缓冲与索引缓冲上传到 VBO/EBO 并绑定顶点布局
/// OpenGL mesh uploader: uploads the interleaved vertex and index buffers into a VBO/EBO and binds the layout.
/// </summary>
public sealed unsafe class GlMeshUploader : IMeshGpuUploader, IDisposable
{
    private readonly Dictionary<Mesh, GlMeshHandle> _handles = [];

    /// <summary>已上传的网格数量 / Number of uploaded meshes.</summary>
    public int Count => _handles.Count;

    /// <summary>显存占用估算（字节） / Estimated GPU memory usage in bytes.</summary>
    public long GpuBytes
    {
        get
        {
            long total = 0;
            foreach (GlMeshHandle handle in _handles.Values)
            {
                total += handle.VertexBytes + handle.IndexBytes;
            }

            return total;
        }
    }

    /// <summary>上传或刷新网格 / Upload or refresh a mesh.</summary>
    public void Upload(Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        if (!_handles.TryGetValue(mesh, out GlMeshHandle? handle))
        {
            handle = new GlMeshHandle();
            _handles[mesh] = handle;
        }

        // 版本未变时跳过 / Skip when the mesh has not changed
        if (handle.IsValid && handle.Version == mesh.Version && handle.VertexCount == mesh.VertexCount && handle.IndexCount == mesh.IndexCount)
        {
            return;
        }

        bool configureLayout = !handle.IsValid;
        if (configureLayout)
        {
            handle.Vao = GL.GenVertexArray();
            handle.Vbo = GL.GenBuffer();
            handle.Ebo = GL.GenBuffer();
        }

        GL.BindVertexArray(handle.Vao);

        // 顶点缓冲 / Vertex buffer
        GL.BindBuffer(BufferTarget.ArrayBuffer, handle.Vbo);
        GL.BufferData(
            BufferTarget.ArrayBuffer,
            (nint)(mesh.VertexBytes),
            (nint)mesh.VertexPointer,
            BufferUsageHint.StaticDraw);

        // 索引缓冲 / Index buffer
        if (configureLayout)
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, handle.Ebo);
        GL.BufferData(
            BufferTarget.ElementArrayBuffer,
            (nint)(mesh.IndexBytes),
            (nint)mesh.IndexPointer,
            BufferUsageHint.StaticDraw);

        // The declaration and buffer names are fixed for the lifetime of the VAO.
        if (configureLayout)
        {
            GlVertexAttribute[] attributes = VertexLayoutExport.ForOpenGL(mesh.Declaration);
            foreach (GlVertexAttribute attribute in attributes)
            {
                GL.EnableVertexAttribArray(attribute.Location);
                GL.VertexAttribPointer(
                    attribute.Location,
                    attribute.Components,
                    (VertexAttribPointerType)attribute.OpenGlType,
                    attribute.Normalized,
                    attribute.Stride,
                    attribute.Offset);
            }
            handle.EnabledAttributes = attributes.Length;
        }

        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

        handle.Version = mesh.Version;
        handle.VertexCount = mesh.VertexCount;
        handle.IndexCount = mesh.IndexCount;
        handle.VertexBytes = mesh.VertexBytes;
        handle.IndexBytes = mesh.IndexBytes;
        handle.IndexFormat = mesh.IndexFormat;
    }

    /// <summary>取已上传的句柄 / Get the uploaded handle.</summary>
    public bool TryGetHandle(Mesh mesh, out GlMeshHandle handle)
    {
        bool found = _handles.TryGetValue(mesh, out GlMeshHandle? value);
        handle = value ?? new GlMeshHandle();
        return found;
    }

    /// <summary>释放单个网格的 GPU 资源 / Release one mesh's GPU resources.</summary>
    public void Release(Mesh mesh)
    {
        if (!_handles.Remove(mesh, out GlMeshHandle? handle))
        {
            return;
        }

        handle.Dispose();
    }

    public void Dispose()
    {
        foreach (GlMeshHandle handle in _handles.Values)
        {
            handle.Dispose();
        }

        _handles.Clear();
    }
}

/// <summary>
/// 一个网格在 GPU 上的句柄 / GPU-side handle of one mesh.
/// </summary>
public sealed class GlMeshHandle : IDisposable
{
    public int Vao;
    public int Vbo;
    public int Ebo;
    public int VertexCount;
    public int IndexCount;
    public int VertexBytes;
    public int IndexBytes;
    public uint Version;
    public int EnabledAttributes;
    public IndexFormat IndexFormat = IndexFormat.UInt32;

    public bool IsValid => Vao != 0;

    public void Dispose()
    {
        if (Vao != 0)
        {
            GL.DeleteVertexArray(Vao);
            Vao = 0;
        }

        if (Vbo != 0)
        {
            GL.DeleteBuffer(Vbo);
            Vbo = 0;
        }

        if (Ebo != 0)
        {
            GL.DeleteBuffer(Ebo);
            Ebo = 0;
        }
    }
}
