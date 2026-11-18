using System.Runtime.InteropServices;
using Thrust.Geometry;
using Thrust.Scene;
using OpenTK.Mathematics;

namespace Thrust.Graphics;

/// <summary>
/// 索引缓冲的位宽 / Index buffer width.
/// </summary>
public enum IndexFormat
{
    /// <summary>16 位索引，最多 65536 顶点 / 16-bit indices, up to 65536 vertices.</summary>
    UInt16,

    /// <summary>32 位索引 / 32-bit indices.</summary>
    UInt32,
}

/// <summary>
/// 子网格：一段索引范围对应一个材质 / A sub-mesh: an index range drawn with one material.
/// </summary>
public sealed class SubMesh
{
    public SubMesh(int indexOffset, int indexCount, int materialSlot = 0)
    {
        if (indexOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(indexOffset));
        }

        if (indexCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(indexCount));
        }

        IndexOffset = indexOffset;
        IndexCount = indexCount;
        MaterialSlot = materialSlot;
    }

    /// <summary>首个索引在索引缓冲中的位置 / First index inside the index buffer.</summary>
    public int IndexOffset { get; }

    /// <summary>索引数量（必须是 3 的倍数才能构成完整三角形） / Index count, a multiple of three for whole triangles.</summary>
    public int IndexCount { get; }

    /// <summary>材质槽位，渲染时映射到具体材质 / Material slot resolved at render time.</summary>
    public int MaterialSlot { get; set; }

    /// <summary>索引对应的三角形数量 / Number of triangles.</summary>
    public int TriangleCount => IndexCount / 3;

    public override string ToString() => $"SubMesh(offset={IndexOffset}, count={IndexCount}, slot={MaterialSlot})";
}

/// <summary>
/// 引擎级网格：交错顶点缓冲（非托管）、16/32 位索引、子网格与包围体
/// Engine-grade mesh: interleaved unmanaged vertex buffer, 16/32-bit indices, sub-meshes and bounds.
/// </summary>
public sealed unsafe class Mesh : ICullable, IDisposable
{
    private nint _vertexData;
    private nint _indexData;
    private int _vertexCapacity;
    private int _indexCapacity;
    private Aabb _localBounds;
    private BoundingSphere _localSphere;
    private bool _boundsDirty = true;
    private bool _disposed;
    private uint _version;
    private readonly List<SubMesh> _subMeshes = [];

    public Mesh(VertexDeclaration declaration, int vertexCapacity = 0, IndexFormat indexFormat = IndexFormat.UInt32, int indexCapacity = 0)
    {
        Declaration = declaration ?? throw new ArgumentNullException(nameof(declaration));
        IndexFormat = indexFormat;

        EnsureVertexCapacity(vertexCapacity);
        EnsureIndexCapacity(indexCapacity);
    }

    /// <summary>顶点布局声明 / Vertex layout declaration.</summary>
    public VertexDeclaration Declaration { get; }

    /// <summary>索引位宽 / Index width.</summary>
    public IndexFormat IndexFormat { get; }

    /// <summary>调试名称 / Debug name.</summary>
    public string Name { get; set; } = "Mesh";

    /// <summary>顶点数量 / Number of vertices.</summary>
    public int VertexCount { get; private set; }

    /// <summary>索引数量 / Number of indices.</summary>
    public int IndexCount { get; private set; }

    /// <summary>三角形数量 / Number of triangles.</summary>
    public int TriangleCount => IndexCount / 3;

    /// <summary>顶点步长（字节） / Vertex stride in bytes.</summary>
    public int Stride => Declaration.Stride;

    /// <summary>顶点缓冲字节数 / Size of the vertex buffer in bytes.</summary>
    public int VertexBytes => VertexCount * Stride;

    /// <summary>索引缓冲字节数 / Size of the index buffer in bytes.</summary>
    public int IndexBytes => IndexCount * (IndexFormat == IndexFormat.UInt16 ? 2 : 4);

    /// <summary>顶点缓冲起始指针（不拥有所有权） / Pointer to the vertex buffer, not owned by the caller.</summary>
    public void* VertexPointer => (void*)_vertexData;

    /// <summary>索引缓冲起始指针（不拥有所有权） / Pointer to the index buffer, not owned by the caller.</summary>
    public void* IndexPointer => (void*)_indexData;

    /// <summary>数据版本号，用于判断 GPU 是否需要重新上传 / Version counter for GPU upload decisions.</summary>
    public uint Version => _version;

    /// <summary>子网格列表 / Sub-mesh list.</summary>
    public IReadOnlyList<SubMesh> SubMeshes => _subMeshes;

    /// <summary>世界矩阵，设置后包围盒随之变换 / World matrix applied to the world-space bounds.</summary>
    public Matrix4 WorldMatrix { get; set; } = Matrix4.Identity;

    /// <summary>本地空间包围盒 / Local-space bounds.</summary>
    public Aabb LocalBounds
    {
        get
        {
            RecalculateBounds();
            return _localBounds;
        }
    }

    public BoundingSphere LocalBoundingSphere
    {
        get
        {
            RecalculateBounds();
            return _localSphere;
        }
    }

    /// <summary>世界空间包围盒 / World-space bounds.</summary>
    public Aabb Bounds
    {
        get
        {
            Aabb local = LocalBounds;
            if (WorldMatrix == Matrix4.Identity)
            {
                return local;
            }

            Matrix4 world = WorldMatrix;
            return local.Transform(in world);
        }
    }

    public bool IsVisible(in Frustum frustum) => frustum.Intersects(Bounds);

    public bool IsVisible(Camera camera) => camera.IsVisible(Bounds);

    /// <summary>
    /// 重置顶点与索引计数，保留已分配的容量
    /// Reset the counts while keeping the allocated capacity.
    /// </summary>
    public void Clear()
    {
        VertexCount = 0;
        IndexCount = 0;
        _subMeshes.Clear();
        Invalidate();
    }

    /// <summary>
    /// 追加顶点，返回新顶点的写入器
    /// Append a vertex and return a writer for it.
    /// </summary>
    public VertexWriter AppendVertex()
    {
        ThrowIfDisposed();
        EnsureVertexCapacity(VertexCount + 1);

        byte* vertex = (byte*)_vertexData + (nint)VertexCount * Stride;
        VertexCount++;
        _boundsDirty = true;
        _version++;
        return new VertexWriter(vertex, Declaration);
    }

    /// <summary>批量追加顶点，返回起始下标 / Append many vertices and return the first index.</summary>
    public int AppendVertices(int count)
    {
        ThrowIfDisposed();
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        int first = VertexCount;
        EnsureVertexCapacity(VertexCount + count);
        VertexCount += count;
        _boundsDirty = true;
        _version++;
        return first;
    }

    /// <summary>追加一个索引 / Append one index.</summary>
    public void AppendIndex(uint index)
    {
        ThrowIfDisposed();
        ValidateIndex(index);
        EnsureIndexCapacity(IndexCount + 1);

        if (IndexFormat == IndexFormat.UInt16)
        {
            ((ushort*)_indexData)[IndexCount] = (ushort)index;
        }
        else
        {
            ((uint*)_indexData)[IndexCount] = index;
        }

        IndexCount++;
        _version++;
    }

    /// <summary>Appends a block of indices relative to the supplied base vertex.</summary>
    public void AppendIndices(ReadOnlySpan<int> indices, int baseVertex)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(baseVertex);
        if (indices.IsEmpty)
        {
            return;
        }

        int end = checked(IndexCount + indices.Length);
        EnsureIndexCapacity(end);
        if (IndexFormat == IndexFormat.UInt16)
        {
            Span<ushort> target = new((ushort*)_indexData + IndexCount, indices.Length);
            for (int i = 0; i < indices.Length; i++)
            {
                uint index = checked((uint)((long)baseVertex + indices[i]));
                ValidateIndex(index);
                target[i] = (ushort)index;
            }
        }
        else
        {
            Span<uint> target = new((uint*)_indexData + IndexCount, indices.Length);
            for (int i = 0; i < indices.Length; i++)
            {
                uint index = checked((uint)((long)baseVertex + indices[i]));
                ValidateIndex(index);
                target[i] = index;
            }
        }

        IndexCount = end;
        _version++;
    }

    /// <summary>追加一个三角形 / Append one triangle.</summary>
    public void AppendTriangle(uint a, uint b, uint c)
    {
        AppendIndex(a);
        AppendIndex(b);
        AppendIndex(c);
    }

    /// <summary>追加一个四边形（拆成两个三角形） / Append a quad as two triangles.</summary>
    public void AppendQuad(uint a, uint b, uint c, uint d)
    {
        AppendTriangle(a, b, c);
        AppendTriangle(a, c, d);
    }

    /// <summary>读取某个索引 / Read one index.</summary>
    public uint GetIndex(int position)
    {
        if ((uint)position >= (uint)IndexCount)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        return IndexFormat == IndexFormat.UInt16
            ? ((ushort*)_indexData)[position]
            : ((uint*)_indexData)[position];
    }

    /// <summary>取某个顶点的写入器（用于原地修改） / Writer for an existing vertex, for in-place edits.</summary>
    public VertexWriter GetVertexWriter(int index)
    {
        if ((uint)index >= (uint)VertexCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return new VertexWriter((byte*)_vertexData + (nint)index * Stride, Declaration);
    }

    /// <summary>添加子网格 / Add a sub-mesh.</summary>
    public SubMesh AddSubMesh(int indexOffset, int indexCount, int materialSlot = 0)
    {
        SubMesh subMesh = new(indexOffset, indexCount, materialSlot);
        _subMeshes.Add(subMesh);
        return subMesh;
    }

    /// <summary>
    /// 用当前索引范围自动生成一个覆盖全部索引的子网格
    /// Add a sub-mesh covering every index currently in the buffer.
    /// </summary>
    public SubMesh AddSubMeshForAll(int materialSlot = 0)
        => AddSubMesh(0, IndexCount, materialSlot);

    /// <summary>清空子网格 / Remove all sub-meshes.</summary>
    public void ClearSubMeshes() => _subMeshes.Clear();

    /// <summary>
    /// 设置完整数据：复制调用方的交错顶点与索引缓冲区
    /// Replace the whole mesh from caller-provided interleaved vertex and index buffers.
    /// </summary>
    public void SetData(ReadOnlySpan<byte> vertexBytes, ReadOnlySpan<uint> indices)
    {
        ThrowIfDisposed();

        if (vertexBytes.Length % Stride != 0)
        {
            throw new ArgumentException(
                $"顶点字节数必须是步长 {Stride} 的整数倍 / Vertex byte length must be a multiple of the stride",
                nameof(vertexBytes));
        }

        int vertexCount = vertexBytes.Length / Stride;
        if (IndexFormat == IndexFormat.UInt16 && vertexCount > ushort.MaxValue + 1)
        {
            throw new InvalidOperationException(
                $"顶点数 {vertexCount} 超出 16 位索引范围 / Vertex count exceeds the 16-bit index range");
        }

        EnsureVertexCapacity(vertexCount);
        EnsureIndexCapacity(indices.Length);

        if (!vertexBytes.IsEmpty)
        {
            vertexBytes.CopyTo(new Span<byte>((void*)_vertexData, vertexCount * Stride));
        }

        // 先提交顶点数，索引校验才能通过 / Commit the vertex count first so index validation passes
        VertexCount = vertexCount;

        for (int i = 0; i < indices.Length; i++)
        {
            ValidateIndex(indices[i]);

            if (IndexFormat == IndexFormat.UInt16)
            {
                ((ushort*)_indexData)[i] = (ushort)indices[i];
            }
            else
            {
                ((uint*)_indexData)[i] = indices[i];
            }
        }

        IndexCount = indices.Length;
        Invalidate();
    }

    /// <summary>
    /// 导出为独立的顶点与索引字节数组，便于交给图形 API 或序列化
    /// Copy the buffers into standalone byte arrays for graphics APIs or serialization.
    /// </summary>
    public (byte[] Vertices, byte[] Indices) ToArrays()
    {
        byte[] vertices = new byte[VertexBytes];
        byte[] indices = new byte[IndexBytes];

        if (VertexBytes > 0)
        {
            new ReadOnlySpan<byte>((void*)_vertexData, VertexBytes).CopyTo(vertices);
        }

        if (IndexBytes > 0)
        {
            new ReadOnlySpan<byte>((void*)_indexData, IndexBytes).CopyTo(indices);
        }

        return (vertices, indices);
    }

    /// <summary>由顶点位置重新计算包围体 / Recompute bounds from the stored positions.</summary>
    public void RecalculateBounds()
    {
        if (!_boundsDirty)
        {
            return;
        }

        if (VertexCount == 0 || !Declaration.TryGetElement(VertexAttribute.Position, out VertexElement positionElement))
        {
            _localBounds = new Aabb(Vector3.Zero, Vector3.Zero);
            _localSphere = new BoundingSphere(Vector3.Zero, 0f);
            _boundsDirty = false;
            return;
        }

        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);

        byte* basePointer = (byte*)_vertexData;
        for (int i = 0; i < VertexCount; i++)
        {
            Vector3 position = VertexReader.ReadPosition(basePointer + (nint)i * Stride, positionElement);
            min = Vector3.ComponentMin(min, position);
            max = Vector3.ComponentMax(max, position);
        }

        _localBounds = new Aabb(min, max);

        Vector3 center = _localBounds.Center;
        float radiusSquared = 0f;
        for (int i = 0; i < VertexCount; i++)
        {
            Vector3 position = VertexReader.ReadPosition(basePointer + (nint)i * Stride, positionElement);
            radiusSquared = MathF.Max(radiusSquared, (position - center).LengthSquared);
        }

        _localSphere = new BoundingSphere(center, MathF.Sqrt(radiusSquared));
        _boundsDirty = false;
    }

    /// <summary>
    /// 上传到 GPU 的抽象接口，由具体渲染后端实现
    /// Abstract GPU upload hook, implemented by the rendering backend.
    /// </summary>
    public void UploadToGpu(IMeshGpuUploader uploader)
    {
        ArgumentNullException.ThrowIfNull(uploader);
        ThrowIfDisposed();

        uploader.Upload(this);
    }

    private void ValidateIndex(uint index)
    {
        if ((long)index >= VertexCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                $"索引 {index} 超出顶点数 {VertexCount} / Index out of range");
        }
    }

    private void EnsureVertexCapacity(int required)
    {
        if (required <= _vertexCapacity)
        {
            return;
        }

        ThrowIfDisposed();

        int newCapacity = _vertexCapacity == 0 ? Math.Max(required, 16) : _vertexCapacity;
        while (newCapacity < required)
        {
            newCapacity = newCapacity > int.MaxValue / 2 ? required : newCapacity * 2;
        }

        nint newBuffer = (nint)NativeMemory.AllocZeroed((nuint)newCapacity * (nuint)Stride);
        if (_vertexData != 0)
        {
            long copyBytes = (long)VertexCount * Stride;
            Buffer.MemoryCopy((void*)_vertexData, (void*)newBuffer, (long)newCapacity * Stride, copyBytes);
            NativeMemory.Free((void*)_vertexData);
        }

        _vertexData = newBuffer;
        _vertexCapacity = newCapacity;
    }

    private void EnsureIndexCapacity(int required)
    {
        if (required <= _indexCapacity)
        {
            return;
        }

        ThrowIfDisposed();

        int newCapacity = _indexCapacity == 0 ? Math.Max(required, 16) : _indexCapacity;
        while (newCapacity < required)
        {
            newCapacity = newCapacity > int.MaxValue / 2 ? required : newCapacity * 2;
        }

        int elementSize = IndexFormat == IndexFormat.UInt16 ? 2 : 4;
        nint newBuffer = (nint)NativeMemory.AllocZeroed((nuint)newCapacity * (nuint)elementSize);
        if (_indexData != 0)
        {
            long copyBytes = (long)IndexCount * elementSize;
            Buffer.MemoryCopy((void*)_indexData, (void*)newBuffer, (long)newCapacity * elementSize, copyBytes);
            NativeMemory.Free((void*)_indexData);
        }

        _indexData = newBuffer;
        _indexCapacity = newCapacity;
    }

    private void Invalidate()
    {
        _boundsDirty = true;
        _version++;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>释放非托管缓冲 / Release the unmanaged buffers.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_vertexData != 0)
        {
            NativeMemory.Free((void*)_vertexData);
            _vertexData = 0;
        }

        if (_indexData != 0)
        {
            NativeMemory.Free((void*)_indexData);
            _indexData = 0;
        }

        _vertexCapacity = 0;
        _indexCapacity = 0;
        VertexCount = 0;
        IndexCount = 0;
        _subMeshes.Clear();
        _disposed = true;
        _version++;
    }

    public bool IsDisposed => _disposed;
}

/// <summary>
/// 网格上传后端接口 / Backend contract for uploading a mesh to the GPU.
/// </summary>
public interface IMeshGpuUploader
{
    /// <summary>上传顶点、索引缓冲并绑定顶点布局 / Upload the buffers and bind the vertex layout.</summary>
    void Upload(Mesh mesh);
}
