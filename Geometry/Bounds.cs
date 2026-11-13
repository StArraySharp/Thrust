using OpenTK.Mathematics;

namespace Thrust.Geometry;

/// <summary>
/// 轴对齐包围盒 / Axis-aligned bounding box.
/// </summary>
public struct Aabb
{
    public Vector3 Min;
    public Vector3 Max;

    public Aabb(Vector3 min, Vector3 max)
    {
        Min = min;
        Max = max;
    }

    /// <summary>从中心与半长构建 / Build from center and half-extents.</summary>
    public static Aabb FromCenterExtents(Vector3 center, Vector3 halfExtents)
        => new(center - halfExtents, center + halfExtents);

    public Vector3 Center => (Min + Max) * 0.5f;

    public Vector3 Extents => (Max - Min) * 0.5f;

    public Vector3 Size => Max - Min;

    /// <summary>构建能包住所有点的包围盒 / Build a box enclosing all points.</summary>
    public static Aabb FromPoints(ReadOnlySpan<Vector3> points)
    {
        if (points.IsEmpty)
        {
            return new Aabb(Vector3.Zero, Vector3.Zero);
        }

        Vector3 min = points[0];
        Vector3 max = points[0];
        for (int i = 1; i < points.Length; i++)
        {
            min = Vector3.ComponentMin(min, points[i]);
            max = Vector3.ComponentMax(max, points[i]);
        }

        return new Aabb(min, max);
    }

    /// <summary>合并两个包围盒 / Union of two boxes.</summary>
    public Aabb Union(in Aabb other) => new(Vector3.ComponentMin(Min, other.Min), Vector3.ComponentMax(Max, other.Max));

    /// <summary>变换后重新构建包围盒（结果保守） / Transform, producing a conservative box.</summary>
    public Aabb Transform(in Matrix4 matrix)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        int i = 0;
        for (int x = 0; x < 2; x++)
        {
            for (int y = 0; y < 2; y++)
            {
                for (int z = 0; z < 2; z++)
                {
                    corners[i++] = Vector3.TransformPosition(
                        new Vector3(x == 0 ? Min.X : Max.X, y == 0 ? Min.Y : Max.Y, z == 0 ? Min.Z : Max.Z),
                        matrix);
                }
            }
        }

        return FromPoints(corners);
    }

    /// <summary>与视锥相交判定 / Intersect with a frustum.</summary>
    public bool Intersects(in Frustum frustum) => frustum.Intersects(this);

    /// <summary>包含判定 / Containment test.</summary>
    public bool Contains(in Vector3 point)
        => point.X >= Min.X && point.X <= Max.X
        && point.Y >= Min.Y && point.Y <= Max.Y
        && point.Z >= Min.Z && point.Z <= Max.Z;
}

/// <summary>
/// 包围球 / Bounding sphere.
/// </summary>
public struct BoundingSphere
{
    public Vector3 Center;
    public float Radius;

    public BoundingSphere(Vector3 center, float radius)
    {
        Center = center;
        Radius = radius;
    }

    /// <summary>从点集构建（中心取包围盒中心） / Build from points (center = box center).</summary>
    public static BoundingSphere FromPoints(ReadOnlySpan<Vector3> points)
    {
        if (points.IsEmpty)
        {
            return new BoundingSphere(Vector3.Zero, 0f);
        }

        Aabb box = Aabb.FromPoints(points);
        Vector3 center = box.Center;
        float radiusSq = 0f;
        foreach (Vector3 p in points)
        {
            radiusSq = MathF.Max(radiusSq, (p - center).LengthSquared);
        }

        return new BoundingSphere(center, MathF.Sqrt(radiusSq));
    }

    /// <summary>变换后重新构建包围球 / Transform, keeping it conservative.</summary>
    public BoundingSphere Transform(in Matrix4 matrix)
    {
        Vector3 center = Vector3.TransformPosition(Center, matrix);
        // 取三个基向量的最大缩放，保证变换后仍能包住原球
        float scaleX = Vector3.TransformNormal(Vector3.UnitX, matrix).Length;
        float scaleY = Vector3.TransformNormal(Vector3.UnitY, matrix).Length;
        float scaleZ = Vector3.TransformNormal(Vector3.UnitZ, matrix).Length;
        return new BoundingSphere(center, Radius * MathF.Max(scaleX, MathF.Max(scaleY, scaleZ)));
    }

    public bool Intersects(in Frustum frustum) => frustum.Intersects(this);

    public bool Contains(in Vector3 point) => (point - Center).LengthSquared <= Radius * Radius;
}

/// <summary>
/// 平面（法线 + 距离）/ Plane (normal + distance), where dot(n, p) + D == 0.
/// </summary>
public struct Plane
{
    public Vector3 Normal;
    public float D;

    public Plane(Vector3 normal, float d)
    {
        Normal = normal;
        D = d;
    }

    /// <summary>用过三点的平面构建 / Build a plane through three points.</summary>
    public static Plane FromPoints(Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        return new Plane(normal, -Vector3.Dot(normal, a));
    }

    /// <summary>归一化，使法线长度为 1 / Normalize so the normal has unit length.</summary>
    public Plane Normalized()
    {
        float length = Normal.Length;
        return length > 0f ? new Plane(Normal / length, D / length) : this;
    }

    /// <summary>点到平面的有符号距离 / Signed distance from a point.</summary>
    public float DistanceTo(in Vector3 point) => Vector3.Dot(Normal, point) + D;

    /// <summary>法线方向翻转 / Flip the plane.</summary>
    public Plane Flipped() => new(-Normal, -D);
}

/// <summary>
/// 视锥体，由 6 个平面组成 / View frustum, made of six planes.
/// </summary>
public struct Frustum
{
    public Plane Left;
    public Plane Right;
    public Plane Bottom;
    public Plane Top;
    public Plane Near;
    public Plane Far;

    /// <summary>
    /// 从视图投影矩阵提取视锥平面（Gribb–Hartmann 方法）
    /// Extract the six planes from a combined view-projection matrix (Gribb–Hartmann).
    /// </summary>
    /// <param name="viewProjection">视图 × 投影矩阵 / View * projection matrix.</param>
    public static Frustum FromViewProjection(in Matrix4 viewProjection)
    {
        // OpenTK 的 Matrix4 以行向量为行存储，等价于 DirectX 风格的行主序矩阵
        Matrix4 m = viewProjection;

        static Plane Normalize(Vector4 p) => new Plane(new Vector3(p.X, p.Y, p.Z), p.W).Normalized();

        return new Frustum
        {
            Left = Normalize(new Vector4(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41)),
            Right = Normalize(new Vector4(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41)),
            Bottom = Normalize(new Vector4(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42)),
            Top = Normalize(new Vector4(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42)),
            Near = Normalize(new Vector4(m.M13, m.M23, m.M33, m.M43)),
            Far = Normalize(new Vector4(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43)),
        };
    }

    /// <summary>视锥剔除：包围盒完全在视锥外时返回 false / Frustum test for a box.</summary>
    public readonly bool Intersects(in Aabb box)
    {
        return !IsOutside(box, Left)
            && !IsOutside(box, Right)
            && !IsOutside(box, Bottom)
            && !IsOutside(box, Top)
            && !IsOutside(box, Near)
            && !IsOutside(box, Far);
    }

    private static bool IsOutside(in Aabb box, in Plane plane)
    {
        // 取包围盒在平面法线方向上最远的顶点（正顶点）
        Vector3 positive = new(
            plane.Normal.X >= 0f ? box.Max.X : box.Min.X,
            plane.Normal.Y >= 0f ? box.Max.Y : box.Min.Y,
            plane.Normal.Z >= 0f ? box.Max.Z : box.Min.Z);

        return plane.DistanceTo(positive) < 0f;
    }

    /// <summary>视锥剔除：包围球 / Frustum test for a sphere.</summary>
    public readonly bool Intersects(in BoundingSphere sphere)
    {
        return !IsOutside(sphere, Left)
            && !IsOutside(sphere, Right)
            && !IsOutside(sphere, Bottom)
            && !IsOutside(sphere, Top)
            && !IsOutside(sphere, Near)
            && !IsOutside(sphere, Far);
    }

    private static bool IsOutside(in BoundingSphere sphere, in Plane plane)
        => plane.DistanceTo(sphere.Center) < -sphere.Radius;

    /// <summary>点是否在视锥内 / Whether a point lies inside the frustum.</summary>
    public readonly bool Contains(in Vector3 point)
        => Left.DistanceTo(point) >= 0f
        && Right.DistanceTo(point) >= 0f
        && Bottom.DistanceTo(point) >= 0f
        && Top.DistanceTo(point) >= 0f
        && Near.DistanceTo(point) >= 0f
        && Far.DistanceTo(point) >= 0f;

    /// <summary>视锥的 8 个角点 / The eight corners of the frustum.</summary>
    public readonly Vector3[] GetCorners()
    {
        Plane[] planes = [Left, Right, Bottom, Top, Near, Far];
        Vector3[] corners = new Vector3[8];

        // 每个角点由三个平面求交得到 / Each corner is the intersection of three planes
        (int A, int B, int C)[] combinations =
        [
            (4, 2, 0), (4, 3, 0), (4, 2, 1), (4, 3, 1),
            (5, 2, 0), (5, 3, 0), (5, 2, 1), (5, 3, 1),
        ];

        for (int i = 0; i < combinations.Length; i++)
        {
            var (a, b, c) = combinations[i];
            corners[i] = IntersectThreePlanes(planes[a], planes[b], planes[c]);
        }

        return corners;
    }

    private static Vector3 IntersectThreePlanes(in Plane p1, in Plane p2, in Plane p3)
    {
        Vector3 n1 = p1.Normal, n2 = p2.Normal, n3 = p3.Normal;
        Vector3 cross23 = Vector3.Cross(n2, n3);

        float denominator = Vector3.Dot(n1, cross23);
        if (MathF.Abs(denominator) < 1e-6f)
        {
            return Vector3.Zero;
        }

        // 解线性方程组 / Solve the three-plane system
        return (-p1.D * cross23
                - p2.D * Vector3.Cross(n3, n1)
                - p3.D * Vector3.Cross(n1, n2)) / denominator;
    }
}
