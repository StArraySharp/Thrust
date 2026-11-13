using OpenTK.Graphics.OpenGL4;
using Thrust.Geometry;
using Thrust.Scene;
using OpenTK.Mathematics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Thrust.Graphics;

/// <summary>
/// 实例化批量渲染器：把一个网格用实例矩阵画成千上万份
/// Instanced batch renderer: draws one mesh thousands of times using per-instance matrices.
/// </summary>
public sealed unsafe class InstancedRenderer : IDisposable
{
    /// <summary>每个实例占用的字节数（4x4 世界矩阵 + RGBA 颜色）/ Bytes per instance.</summary>
    public const int InstanceStrideBytes = (16 + 4) * sizeof(float);

    private readonly int _vao;
    private readonly int _instanceBuffer;
    private readonly int _shader;
    private readonly int _indexType;
    private readonly int _locationViewProjection;

    // 实例数据放在非托管内存，避免托管堆分配与 GC 压力
    // Instance data lives in unmanaged memory to avoid GC pressure
    private nint _instanceData;
    private int _instanceCapacity;
    private int _gpuCapacityBytes;
    private int _uploadedInstanceCount = -1;
    private bool _disposed;

    /// <summary>
    /// 用指定目录下的 Instanced.vert / Instanced.frag 构造。
    /// Create with Instanced.vert / Instanced.frag taken from the given directory.
    /// </summary>
    /// <param name="mesh">已上传到 GPU 的网格 / Mesh already uploaded to the GPU.</param>
    /// <param name="uploader">网格上传器 / The mesh uploader.</param>
    /// <param name="shaderDirectory">着色器所在目录 / Directory holding the shader files.</param>
    public InstancedRenderer(Mesh mesh, GlMeshUploader uploader, string shaderDirectory)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(uploader);

        Mesh = mesh;
        _indexType = mesh.IndexFormat == IndexFormat.UInt16
            ? (int)All.UnsignedShort
            : (int)All.UnsignedInt;

        if (!uploader.TryGetHandle(mesh, out GlMeshHandle meshHandle) || !meshHandle.IsValid)
        {
            throw new InvalidOperationException("网格尚未上传到 GPU / The mesh has not been uploaded");
        }

        MeshHandle = meshHandle;
        _shader = CreateShader(shaderDirectory);
        _locationViewProjection = GL.GetUniformLocation(_shader, "uViewProjection");
        _vao = meshHandle.Vao;
        _instanceBuffer = GL.GenBuffer();

        // 网格属性占用 location 0..4，实例数据从 5 开始
        // Mesh attributes occupy locations 0..4, so instance data starts at 5
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _instanceBuffer);

        for (int i = 0; i < 4; i++)
        {
            int location = 5 + i;
            GL.EnableVertexAttribArray(location);
            GL.VertexAttribPointer(
                location,
                4,
                VertexAttribPointerType.Float,
                false,
                InstanceStrideBytes,
                i * 4 * sizeof(float));
            GL.VertexAttribDivisor(location, 1);
        }

        GL.EnableVertexAttribArray(9);
        GL.VertexAttribPointer(9, 4, VertexAttribPointerType.Float, false, InstanceStrideBytes, 16 * sizeof(float));
        GL.VertexAttribDivisor(9, 1);

        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

    public Mesh Mesh { get; }

    public GlMeshHandle MeshHandle { get; }

    /// <summary>已提交的实例数量 / Number of submitted instances.</summary>
    public int InstanceCount { get; private set; }

    /// <summary>上一帧的绘制调用数（实例化后恒为 1）/ Draw calls last frame, always 1 when instanced.</summary>
    public int DrawCalls { get; private set; }

    /// <summary>实例数据占用的 GPU 字节数 / GPU bytes used by the instance buffer.</summary>
    public long InstanceBytes => (long)InstanceCount * InstanceStrideBytes;

    /// <summary>上一帧是否把实例数据上传到了 GPU / Whether instance data was uploaded last frame.</summary>
    public bool UploadedThisFrame { get; private set; }

    /// <summary>GPU 上的实例数据是否仍然有效 / Whether the GPU buffer still holds valid instance data.</summary>
    public bool HasUploadedData => _uploadedInstanceCount == InstanceCount && InstanceCount > 0;

    /// <summary>
    /// 准备实例数据 / Prepare instance data.
    /// </summary>
    public void Begin(int instanceCount)
    {
        if (instanceCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(instanceCount));
        }

        EnsureCapacity(instanceCount);
        InstanceCount = instanceCount;
    }

    /// <summary>写入一个实例 / Write one instance.</summary>
    public void SetInstance(int index, in Matrix4 world, in Vector4 color)
    {
        if ((uint)index >= (uint)InstanceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        float* slot = (float*)_instanceData + (nint)index * (InstanceStrideBytes / sizeof(float));

        slot[0] = world.M11; slot[1] = world.M12; slot[2] = world.M13; slot[3] = world.M14;
        slot[4] = world.M21; slot[5] = world.M22; slot[6] = world.M23; slot[7] = world.M24;
        slot[8] = world.M31; slot[9] = world.M32; slot[10] = world.M33; slot[11] = world.M34;
        slot[12] = world.M41; slot[13] = world.M42; slot[14] = world.M43; slot[15] = world.M44;

        slot[16] = color.X;
        slot[17] = color.Y;
        slot[18] = color.Z;
        slot[19] = color.W;

        _uploadedInstanceCount = -1;
    }

    /// <summary>
    /// 标记实例数据未变化，跳过本帧上传（静态场景用）
    /// Mark the instance data as unchanged so this frame skips the upload.
    /// </summary>
    public void SkipUploadIfUnchanged()
    {
        if (_uploadedInstanceCount != InstanceCount)
        {
            _uploadedInstanceCount = InstanceCount;
        }
    }

    /// <summary>
    /// 提交并绘制，viewProjection 为摄像机矩阵
    /// Upload and draw; viewProjection is the camera matrix.
    /// </summary>
    public void Draw(in Matrix4 viewProjection)
    {
        if (InstanceCount == 0)
        {
            DrawCalls = 0;
            UploadedThisFrame = false;
            return;
        }

        int bytes = InstanceCount * InstanceStrideBytes;
        GL.BindBuffer(BufferTarget.ArrayBuffer, _instanceBuffer);

        if (bytes > _gpuCapacityBytes)
        {
            GL.BufferData(BufferTarget.ArrayBuffer, (nint)bytes, 0, BufferUsageHint.StreamDraw);
            _gpuCapacityBytes = bytes;
            _uploadedInstanceCount = -1;
        }

        // 只在数据变化时上传 / Upload only when the data changed
        UploadedThisFrame = _uploadedInstanceCount != InstanceCount;
        if (UploadedThisFrame)
        {
            GL.BufferSubData(BufferTarget.ArrayBuffer, 0, (nint)bytes, _instanceData);
            _uploadedInstanceCount = InstanceCount;
        }

        GL.UseProgram(_shader);

        if (_locationViewProjection >= 0)
        {
            Matrix4 vp = viewProjection;

            // OpenTK 是行主序 + 行向量（v * M），GLSL 是列主序。
            // 传 transpose=false 时 GL 按列主序解释行主序内存，等价于 GLSL 侧得到 M^T，
            // 从而 GLSL 的 `M * v` 与 C# 的 `v * M` 完全一致。
            // OpenTK uses row vectors (v * M) while GLSL uses column vectors.
            // With transpose=false the GLSL matrix ends up as M^T, so `M * v` in GLSL matches `v * M` in C#.
            GL.UniformMatrix4(_locationViewProjection, false, ref vp);
        }

        GL.BindVertexArray(_vao);
        GL.DrawElementsInstanced(
            PrimitiveType.Triangles,
            MeshHandle.IndexCount,
            (DrawElementsType)_indexType,
            0,
            InstanceCount);

        GL.BindVertexArray(0);
        GL.UseProgram(0);

        DrawCalls = 1;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _instanceCapacity)
        {
            return;
        }

        int capacity = _instanceCapacity == 0 ? Math.Max(required, 1024) : _instanceCapacity;
        while (capacity < required)
        {
            capacity = capacity > int.MaxValue / 2 ? required : capacity * 2;
        }

        nint newBuffer = (nint)NativeMemory.AllocZeroed((nuint)capacity * InstanceStrideBytes);
        if (_instanceData != 0)
        {
            System.Buffer.MemoryCopy(
                (void*)_instanceData,
                (void*)newBuffer,
                (long)capacity * InstanceStrideBytes,
                (long)InstanceCount * InstanceStrideBytes);

            NativeMemory.Free((void*)_instanceData);
        }

        _instanceData = newBuffer;
        _instanceCapacity = capacity;
    }

    private static int CreateShader(string shaderDirectory)
    {
        Shader loaded = Shader.FromFiles(shaderDirectory, "Instanced.vert", "Instanced.frag");
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

        // length 必须是 UTF-8 字节数，否则含非 ASCII 注释时会被截断
        // The length must be the UTF-8 byte count; otherwise non-ASCII comments truncate the source
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

        // 实例缓冲属于本渲染器；VAO/VBO/EBO 仍归上传器所有
        // The instance buffer belongs to this renderer; the VAO/VBO/EBO stay with the uploader
        GL.DeleteBuffer(_instanceBuffer);
        GL.DeleteProgram(_shader);

        if (_instanceData != 0)
        {
            NativeMemory.Free((void*)_instanceData);
            _instanceData = 0;
        }

        _instanceCapacity = 0;
        _gpuCapacityBytes = 0;
        _disposed = true;
    }
}
