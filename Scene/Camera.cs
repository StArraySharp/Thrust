using OpenTK.Mathematics;
using Thrust.Geometry;
using Thrust.Graphics;

namespace Thrust.Scene;

/// <summary>
/// 投影方式 / Projection mode.
/// </summary>
public enum ProjectionMode
{
    Perspective,
    Orthographic,
}

/// <summary>
/// 摄像机，带视锥剔除 / Camera with frustum culling.
/// </summary>
public sealed class Camera
{
    private readonly List<ICullable> _visible = [];
    private Matrix4 _view;
    private Matrix4 _projection;
    private Matrix4 _viewProjection;
    private Frustum _frustum;
    private bool _dirty = true;

    public Camera()
    {
        Position = new Vector3(0f, 0f, 5f);
        Target = Vector3.Zero;
        Up = Vector3.UnitY;
        FieldOfView = MathF.PI / 3f;
        NearPlane = 0.1f;
        FarPlane = 1000f;
    }

    public Vector3 Position { get; set; }

    public Vector3 Target { get; set; }

    public Vector3 Up { get; set; }

    /// <summary>投影方式 / Projection mode.</summary>
    public ProjectionMode ProjectionMode { get; set; } = ProjectionMode.Perspective;

    /// <summary>垂直视野（弧度） / Vertical field of view in radians.</summary>
    public float FieldOfView { get; set; }

    public float NearPlane { get; set; }

    public float FarPlane { get; set; }

    /// <summary>宽高比，窗口尺寸变化时更新 / Aspect ratio, updated on resize.</summary>
    public float AspectRatio { get; set; } = 16f / 9f;

    /// <summary>正交投影高度 / Orthographic view height.</summary>
    public float OrthographicHeight { get; set; } = 10f;

    /// <summary>是否启用视锥剔除 / Whether frustum culling is enabled.</summary>
    public bool CullingEnabled { get; set; } = true;

    /// <summary>世界到视图矩阵 / World-to-view matrix.</summary>
    public Matrix4 View
    {
        get
        {
            EnsureUpdated();
            return _view;
        }
    }

    /// <summary>视图到裁剪空间矩阵 / View-to-clip matrix.</summary>
    public Matrix4 Projection
    {
        get
        {
            EnsureUpdated();
            return _projection;
        }
    }

    /// <summary>视图投影矩阵，用于把世界坐标变换到裁剪空间 / Combined view-projection matrix.</summary>
    public Matrix4 ViewProjection
    {
        get
        {
            EnsureUpdated();
            return _viewProjection;
        }
    }

    /// <summary>由视图投影矩阵提取的视锥 / Frustum extracted from the view-projection matrix.</summary>
    public Frustum Frustum
    {
        get
        {
            EnsureUpdated();
            return _frustum;
        }
    }

    /// <summary>摄像机前方向 / Forward direction.</summary>
    public Vector3 Forward => Vector3.Normalize(Target - Position);

    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Up));

    /// <summary>
    /// 标记摄像机参数已改变，下一帧重新计算矩阵与视锥
    /// Mark the camera as dirty so matrices and the frustum are rebuilt.
    /// </summary>
    public void MarkDirty() => _dirty = true;

    /// <summary>
    /// 视锥剔除，返回包围盒是否可见 / Frustum test for an axis-aligned box.
    /// </summary>
    public bool IsVisible(in Aabb box) => !CullingEnabled || Frustum.Intersects(box);

    /// <summary>
    /// 视锥剔除，返回包围球是否可见 / Frustum test for a bounding sphere.
    /// </summary>
    public bool IsVisible(in BoundingSphere sphere) => !CullingEnabled || Frustum.Intersects(sphere);

    /// <summary>
    /// 视锥剔除，返回点是否可见 / Frustum test for a point.
    /// </summary>
    public bool IsVisible(in Vector3 point) => !CullingEnabled || Frustum.Contains(point);

    /// <summary>
    /// 剔除并收集可见对象，返回可渲染列表
    /// Cull a set of objects and return the visible ones.
    /// </summary>
    public IReadOnlyList<ICullable> Cull(IEnumerable<ICullable> candidates)
    {
        _visible.Clear();
        if (!CullingEnabled)
        {
            _visible.AddRange(candidates);
            return _visible;
        }

        Frustum frustum = Frustum;
        foreach (ICullable candidate in candidates)
        {
            if (candidate.IsVisible(in frustum))
            {
                _visible.Add(candidate);
            }
        }

        return _visible;
    }

    /// <summary>
    /// 把屏幕坐标（归一化，0~1）转换为世界射线，用于拾取
    /// Build a world-space ray from normalized screen coordinates (0~1), for picking.
    /// </summary>
    public Ray ScreenPointToRay(Vector2 normalizedScreenPoint)
    {
        Vector2 ndc = new(
            normalizedScreenPoint.X * 2f - 1f,
            1f - normalizedScreenPoint.Y * 2f);

        Matrix4 inverse = Matrix4.Invert(ViewProjection);

        Vector3 nearPoint = Unproject(new Vector3(ndc.X, ndc.Y, -1f), inverse);
        Vector3 farPoint = Unproject(new Vector3(ndc.X, ndc.Y, 1f), inverse);

        return new Ray(Position, Vector3.Normalize(farPoint - nearPoint));
    }

    private static Vector3 Unproject(Vector3 ndc, in Matrix4 inverseViewProjection)
    {
        // OpenTK 使用行向量约定：clip = world * matrix
        Vector4 world = new Vector4(ndc, 1f) * inverseViewProjection;
        return new Vector3(world.X, world.Y, world.Z) / world.W;
    }

    private void EnsureUpdated()
    {
        if (!_dirty)
        {
            return;
        }

        _view = Matrix4.LookAt(Position, Target, Up);

        if (ProjectionMode == ProjectionMode.Perspective)
        {
            _projection = Matrix4.CreatePerspectiveFieldOfView(
                ClampFieldOfView(FieldOfView), MathF.Max(AspectRatio, 1e-4f), NearPlane, FarPlane);
        }
        else
        {
            float halfHeight = OrthographicHeight * 0.5f;
            float halfWidth = halfHeight * MathF.Max(AspectRatio, 1e-4f);
            _projection = Matrix4.CreateOrthographicOffCenter(-halfWidth, halfWidth, -halfHeight, halfHeight, NearPlane, FarPlane);
        }

        _viewProjection = _view * _projection;
        _frustum = Frustum.FromViewProjection(in _viewProjection);
        _dirty = false;
    }

    private static float ClampFieldOfView(float fov)
        => Math.Clamp(fov, 0.01f, MathF.PI - 0.01f);
}

/// <summary>
/// 可被视锥剔除的对象 / An object that can be frustum-culled.
/// </summary>
public interface ICullable
{
    /// <summary>世界空间包围盒 / World-space bounds.</summary>
    Aabb Bounds { get; }

    /// <summary>包围盒是否与视锥相交 / Whether the bounds intersect the frustum.</summary>
    bool IsVisible(in Frustum frustum);
}

/// <summary>
/// 射线 / Ray.
/// </summary>
public readonly struct Ray
{
    public readonly Vector3 Origin;
    public readonly Vector3 Direction;

    public Ray(Vector3 origin, Vector3 direction)
    {
        Origin = origin;
        Direction = direction;
    }

    public Vector3 GetPoint(float distance) => Origin + Direction * distance;

    /// <summary>
    /// 射线与包围盒求交（slab 算法），返回进入距离
    /// Ray/box intersection using the slab method; returns the entry distance.
    /// </summary>
    public bool Intersects(in Aabb box, out float distance)
    {
        float tMin = 0f;
        float tMax = float.MaxValue;

        for (int axis = 0; axis < 3; axis++)
        {
            float origin = axis switch { 0 => Origin.X, 1 => Origin.Y, _ => Origin.Z };
            float direction = axis switch { 0 => Direction.X, 1 => Direction.Y, _ => Direction.Z };
            float min = axis switch { 0 => box.Min.X, 1 => box.Min.Y, _ => box.Min.Z };
            float max = axis switch { 0 => box.Max.X, 1 => box.Max.Y, _ => box.Max.Z };

            if (MathF.Abs(direction) < 1e-8f)
            {
                if (origin < min || origin > max)
                {
                    distance = 0f;
                    return false;
                }

                continue;
            }

            float inv = 1f / direction;
            float t1 = (min - origin) * inv;
            float t2 = (max - origin) * inv;
            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }

            tMin = MathF.Max(tMin, t1);
            tMax = MathF.Min(tMax, t2);
            if (tMin > tMax)
            {
                distance = 0f;
                return false;
            }
        }

        distance = tMin;
        return true;
    }

    /// <summary>射线与包围球求交 / Ray/sphere intersection.</summary>
    public bool Intersects(in BoundingSphere sphere, out float distance)
    {
        Vector3 toCenter = sphere.Center - Origin;
        float projected = Vector3.Dot(toCenter, Direction);
        float distanceSq = toCenter.LengthSquared - projected * projected;
        float radiusSq = sphere.Radius * sphere.Radius;

        if (distanceSq > radiusSq)
        {
            distance = 0f;
            return false;
        }

        float offset = MathF.Sqrt(radiusSq - distanceSq);
        distance = projected - offset;
        if (distance < 0f)
        {
            distance = projected + offset;
        }

        return distance >= 0f;
    }
}
