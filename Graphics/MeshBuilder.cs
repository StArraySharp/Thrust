using OpenTK.Mathematics;
using Thrust.Geometry;

namespace Thrust.Graphics;

/// <summary>
/// 程序化几何构建器，支持多 UV 通道、多颜色与压缩格式
/// Procedural geometry builder with multiple UV channels, multiple colors and compressed formats.
/// </summary>
public sealed class MeshBuilder : IDisposable
{
    /// <summary>常用布局：位置 + 法线 + 切线 + UV0 / Common layout: position, normal, tangent, UV0.</summary>
    public static readonly VertexDeclaration DefaultDeclaration = new(
        (VertexAttribute.Position, VertexFormat.Float3),
        (VertexAttribute.Normal, VertexFormat.Float3),
        (VertexAttribute.Tangent, VertexFormat.Float4),
        (VertexAttribute.TexCoord0, VertexFormat.Float2));

    /// <summary>紧凑布局：位置 Float3、法线 SNorm8x4、UV0 Half2，步长仅 20 字节 / Compact 20-byte layout.</summary>
    public static readonly VertexDeclaration CompactDeclaration = new(
        (VertexAttribute.Position, VertexFormat.Float3),
        (VertexAttribute.Normal, VertexFormat.SNorm8x4),
        (VertexAttribute.TexCoord0, VertexFormat.Half2));

    private readonly List<(int Start, int MaterialSlot)> _subMeshStarts = [];
    private Mesh? _mesh;

    public MeshBuilder(VertexDeclaration? declaration = null, IndexFormat indexFormat = IndexFormat.UInt32)
    {
        Declaration = declaration ?? DefaultDeclaration;
        IndexFormat = indexFormat;
    }

    public IndexFormat IndexFormat { get; }

    public VertexDeclaration Declaration { get; }

    public int VertexCount => _mesh?.VertexCount ?? 0;

    public int IndexCount => _mesh?.IndexCount ?? 0;

    /// <summary>清空内容以复用构建器 / Reset the builder for reuse.</summary>
    public void Clear()
    {
        _mesh ??= new Mesh(Declaration, 256, IndexFormat, 512);
        _mesh.Clear();
        _subMeshStarts.Clear();
    }

    /// <summary>追加顶点并返回其索引 / Append a vertex and return its index.</summary>
    public int AddVertex(
        Vector3 position,
        Vector3 normal = default,
        Vector2 uv0 = default,
        Vector4 tangent = default)
    {
        Mesh mesh = GetMesh();
        VertexWriter writer = mesh.AppendVertex();
        int index = mesh.VertexCount - 1;

        writer.SetPosition(position);
        writer.SetNormal(normal == default ? Vector3.UnitY : normal);
        writer.SetTangent(tangent == default ? new Vector4(1f, 0f, 0f, 1f) : tangent);
        writer.SetTexCoord(0, uv0);
        return index;
    }

    /// <summary>
    /// 追加带完整属性集的顶点：多 UV 通道与多颜色通道
    /// Append a vertex with a rich attribute set: several UV and color channels.
    /// </summary>
    public int AddVertex(
        Vector3 position,
        Vector3 normal,
        Vector4 tangent,
        ReadOnlySpan<Vector2> texCoords,
        ReadOnlySpan<Vector4> colors = default)
    {
        Mesh mesh = GetMesh();
        VertexWriter writer = mesh.AppendVertex();
        int index = mesh.VertexCount - 1;

        writer.SetPosition(position);
        writer.SetNormal(normal);
        writer.SetTangent(tangent);

        for (int i = 0; i < texCoords.Length && i < 8; i++)
        {
            writer.SetTexCoord(i, texCoords[i]);
        }

        for (int i = 0; i < colors.Length && i < 2; i++)
        {
            writer.SetColor(i, colors[i]);
        }

        return index;
    }

    /// <summary>追加一个三角形 / Append one triangle.</summary>
    public void AddTriangle(int a, int b, int c) => GetMesh().AppendTriangle((uint)a, (uint)b, (uint)c);

    /// <summary>追加一个四边形（拆为两个三角形） / Append a quad as two triangles.</summary>
    public void AddQuad(int a, int b, int c, int d) => GetMesh().AppendQuad((uint)a, (uint)b, (uint)c, (uint)d);

    /// <summary>在当前索引位置开启一个子网格 / Open a sub-mesh at the current index position.</summary>
    public void BeginSubMesh(int materialSlot = 0) => _subMeshStarts.Add((GetMesh().IndexCount, materialSlot));

    /// <summary>结束最近开启的子网格 / Close the most recently opened sub-mesh.</summary>
    public void EndSubMesh()
    {
        if (_subMeshStarts.Count == 0)
        {
            return;
        }

        (int start, int materialSlot) = _subMeshStarts[^1];
        _subMeshStarts.RemoveAt(_subMeshStarts.Count - 1);

        Mesh mesh = GetMesh();
        int count = mesh.IndexCount - start;
        if (count > 0)
        {
            mesh.AddSubMesh(start, count, materialSlot);
        }
    }

    /// <summary>添加平面网格 / Append a flat grid plane.</summary>
    public void AddPlane(float width, float depth, int subdivisionsX = 1, int subdivisionsZ = 1)
    {
        subdivisionsX = Math.Max(1, subdivisionsX);
        subdivisionsZ = Math.Max(1, subdivisionsZ);

        int baseIndex = VertexCount;

        for (int z = 0; z <= subdivisionsZ; z++)
        {
            float v = (float)z / subdivisionsZ;
            for (int x = 0; x <= subdivisionsX; x++)
            {
                float u = (float)x / subdivisionsX;
                AddVertex(new Vector3((u - 0.5f) * width, 0f, (v - 0.5f) * depth), Vector3.UnitY, new Vector2(u, 1f - v));
            }
        }

        int stride = subdivisionsX + 1;
        for (int z = 0; z < subdivisionsZ; z++)
        {
            for (int x = 0; x < subdivisionsX; x++)
            {
                int i0 = baseIndex + z * stride + x;
                AddQuad(i0, i0 + stride, i0 + stride + 1, i0 + 1);
            }
        }
    }

    /// <summary>添加立方体，每个面独立 UV / Append a cube with per-face UVs.</summary>
    public void AddCube(Vector3 size, Vector3 center = default)
    {
        Vector3 h = size * 0.5f;

        (Vector3 Normal, Vector3 A, Vector3 B, Vector3 C, Vector3 D)[] faces =
        [
            (Vector3.UnitZ, new(-h.X, -h.Y, h.Z), new(h.X, -h.Y, h.Z), new(h.X, h.Y, h.Z), new(-h.X, h.Y, h.Z)),
            (new Vector3(0f, 0f, -1f), new(h.X, -h.Y, -h.Z), new(-h.X, -h.Y, -h.Z), new(-h.X, h.Y, -h.Z), new(h.X, h.Y, -h.Z)),
            (Vector3.UnitX, new(h.X, -h.Y, h.Z), new(h.X, -h.Y, -h.Z), new(h.X, h.Y, -h.Z), new(h.X, h.Y, h.Z)),
            (new Vector3(-1f, 0f, 0f), new(-h.X, -h.Y, -h.Z), new(-h.X, -h.Y, h.Z), new(-h.X, h.Y, h.Z), new(-h.X, h.Y, -h.Z)),
            (Vector3.UnitY, new(-h.X, h.Y, h.Z), new(h.X, h.Y, h.Z), new(h.X, h.Y, -h.Z), new(-h.X, h.Y, -h.Z)),
            (new Vector3(0f, -1f, 0f), new(-h.X, -h.Y, -h.Z), new(h.X, -h.Y, -h.Z), new(h.X, -h.Y, h.Z), new(-h.X, -h.Y, h.Z)),
        ];

        Vector2[] uvs = [new(0f, 1f), new(1f, 1f), new(1f, 0f), new(0f, 0f)];

        foreach ((Vector3 normal, Vector3 a, Vector3 b, Vector3 c, Vector3 d) in faces)
        {
            int baseIndex = VertexCount;
            Span<Vector3> corners = [a, b, c, d];
            Vector4 tangent = new(Vector3.Normalize(Vector3.Cross(Vector3.UnitY, normal)), 1f);

            for (int i = 0; i < 4; i++)
            {
                AddVertex(center + corners[i], normal, uvs[i], tangent);
            }

            AddQuad(baseIndex, baseIndex + 1, baseIndex + 2, baseIndex + 3);
        }
    }

    /// <summary>添加 UV 球 / Append a UV sphere.</summary>
    public void AddSphere(float radius, int segments = 32, int rings = 16)
    {
        segments = Math.Max(3, segments);
        rings = Math.Max(2, rings);

        int baseIndex = VertexCount;

        for (int ring = 0; ring <= rings; ring++)
        {
            float v = (float)ring / rings;
            float phi = v * MathF.PI;
            float sinPhi = MathF.Sin(phi);
            float cosPhi = MathF.Cos(phi);

            for (int segment = 0; segment <= segments; segment++)
            {
                float u = (float)segment / segments;
                float theta = u * MathF.PI * 2f;
                Vector3 normal = new(sinPhi * MathF.Cos(theta), cosPhi, sinPhi * MathF.Sin(theta));

                AddVertex(normal * radius, normal, new Vector2(u, v), new Vector4(-MathF.Sin(theta), 0f, MathF.Cos(theta), 1f));
            }
        }

        int rowStride = segments + 1;
        for (int ring = 0; ring < rings; ring++)
        {
            for (int segment = 0; segment < segments; segment++)
            {
                int i0 = baseIndex + ring * rowStride + segment;
                AddQuad(i0, i0 + rowStride, i0 + rowStride + 1, i0 + 1);
            }
        }
    }

    /// <summary>沿路径挤出圆管，例如轨道 / Extrude a tube along a polyline, useful for tracks.</summary>
    public void AddTube(ReadOnlySpan<Vector3> path, float radius, int radialSegments = 8, bool closed = false)
    {
        if (path.Length < 2)
        {
            return;
        }

        radialSegments = Math.Max(3, radialSegments);
        int baseIndex = VertexCount;

        float totalLength = 0f;
        for (int i = 1; i < path.Length; i++)
        {
            totalLength += Vector3.Distance(path[i - 1], path[i]);
        }

        float accumulated = 0f;

        for (int i = 0; i < path.Length; i++)
        {
            Vector3 current = path[i];
            Vector3 next = path[(i + 1) % path.Length];
            Vector3 previous = path[(i - 1 + path.Length) % path.Length];

            Vector3 tangent = Vector3.Normalize(next - previous);
            if (tangent.LengthSquared < 1e-12f)
            {
                tangent = Vector3.UnitY;
            }

            Vector3 reference = MathF.Abs(Vector3.Dot(tangent, Vector3.UnitY)) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
            Vector3 binormal = Vector3.Normalize(Vector3.Cross(tangent, reference));
            Vector3 normal = Vector3.Normalize(Vector3.Cross(binormal, tangent));

            if (i > 0)
            {
                accumulated += Vector3.Distance(path[i - 1], current);
            }

            float v = totalLength > 0f ? accumulated / totalLength : 0f;

            for (int s = 0; s <= radialSegments; s++)
            {
                float u = (float)s / radialSegments;
                float angle = u * MathF.PI * 2f;
                Vector3 offset = normal * MathF.Cos(angle) + binormal * MathF.Sin(angle);

                AddVertex(current + offset * radius, offset, new Vector2(u, v), new Vector4(tangent, 1f));
            }
        }

        int rowStride = radialSegments + 1;
        int ringCount = closed ? path.Length : path.Length - 1;

        for (int i = 0; i < ringCount; i++)
        {
            int ringA = baseIndex + i * rowStride;
            int ringB = baseIndex + ((i + 1) % path.Length) * rowStride;

            for (int s = 0; s < radialSegments; s++)
            {
                AddQuad(ringA + s, ringB + s, ringB + s + 1, ringA + s + 1);
            }
        }
    }

    /// <summary>
    /// 完成构建并交出网格所有权，构建器不再持有它
    /// Finish building and hand over the mesh; the builder stops owning it.
    /// </summary>
    public Mesh Build(string name = "Mesh")
    {
        Mesh mesh = GetMesh();

        // 未显式划分时自动生成一个覆盖全部索引的子网格
        if (mesh.SubMeshes.Count == 0 && mesh.IndexCount > 0)
        {
            mesh.AddSubMeshForAll();
        }

        mesh.Name = name;
        _mesh = null;
        _subMeshStarts.Clear();
        return mesh;
    }

    private Mesh GetMesh() => _mesh ??= new Mesh(Declaration, 256, IndexFormat, 512);

    /// <summary>释放尚未交接的内部网格 / Dispose the internal mesh when it was never handed over.</summary>
    public void Dispose()
    {
        _mesh?.Dispose();
        _mesh = null;
    }

    /// <summary>快速构建立方体 / Convenience: build a cube.</summary>
    public static Mesh CreateCube(Vector3 size, Vector3 center = default)
    {
        using MeshBuilder builder = new();
        builder.AddCube(size, center);
        return builder.Build("Cube");
    }

    /// <summary>快速构建球体 / Convenience: build a sphere.</summary>
    public static Mesh CreateSphere(float radius, int segments = 32, int rings = 16)
    {
        using MeshBuilder builder = new();
        builder.AddSphere(radius, segments, rings);
        return builder.Build("Sphere");
    }

    /// <summary>快速构建平面 / Convenience: build a plane.</summary>
    public static Mesh CreatePlane(float width, float depth, int subdivisionsX = 1, int subdivisionsZ = 1)
    {
        using MeshBuilder builder = new();
        builder.AddPlane(width, depth, subdivisionsX, subdivisionsZ);
        return builder.Build("Plane");
    }
}
