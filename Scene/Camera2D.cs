using OpenTK.Mathematics;
using Thrust.Geometry;
using Thrust.Graphics;

namespace Thrust.Scene;

/// <summary>
/// 纯 2D 正交相机：位置直接以世界单位表示，缩放以像素为单位
/// Pure 2D orthographic camera; the position is in world units and the zoom in pixels.
/// </summary>
public sealed class Camera2D
{
    private Matrix4 _view = Matrix4.Identity;
    private Matrix4 _projection = Matrix4.Identity;
    private Matrix4 _viewProjection = Matrix4.Identity;
    private float _viewportWidth = 1280f;
    private float _viewportHeight = 800f;
    private bool _dirty = true;

    /// <summary>相机中心的世界坐标 / Camera center in world space.</summary>
    public Vector2 Position { get; set; }

    /// <summary>每世界单位对应的像素数 / Pixels per world unit.</summary>
    public float Zoom { get; set; } = 1f;

    /// <summary>旋转（度） / Rotation in degrees.</summary>
    public float Rotation { get; set; }

    /// <summary>视口宽度（像素） / Viewport width in pixels.</summary>
    public float ViewportWidth
    {
        get => _viewportWidth;
        set
        {
            _viewportWidth = MathF.Max(value, 1f);
            _dirty = true;
        }
    }

    /// <summary>视口高度（像素） / Viewport height in pixels.</summary>
    public float ViewportHeight
    {
        get => _viewportHeight;
        set
        {
            _viewportHeight = MathF.Max(value, 1f);
            _dirty = true;
        }
    }

    public float NearPlane { get; set; } = -100f;

    public float FarPlane { get; set; } = 100f;

    /// <summary>最小/最大缩放（像素每世界单位） / Zoom limits in pixels per world unit.</summary>
    public float MinZoom { get; set; } = 0.0001f;

    public float MaxZoom { get; set; } = 100f;

    /// <summary>世界到视图矩阵 / World-to-view matrix.</summary>
    public Matrix4 View
    {
        get
        {
            Update();
            return _view;
        }
    }

    /// <summary>视图到裁剪空间矩阵 / View-to-clip matrix.</summary>
    public Matrix4 Projection
    {
        get
        {
            Update();
            return _projection;
        }
    }

    /// <summary>视图投影矩阵 / Combined view-projection matrix.</summary>
    public Matrix4 ViewProjection
    {
        get
        {
            Update();
            return _viewProjection;
        }
    }

    /// <summary>标记矩阵需要重建 / Mark the matrices as dirty.</summary>
    public void MarkDirty() => _dirty = true;

    /// <summary>当前可见的世界区域宽度 / Visible world width.</summary>
    public float WorldViewWidth => ViewportWidth / Zoom;

    /// <summary>当前可见的世界区域高度 / Visible world height.</summary>
    public float WorldViewHeight => ViewportHeight / Zoom;

    /// <summary>平移（屏幕像素增量） / Pan by a screen-space pixel delta.</summary>
    public void PanPixels(Vector2 screenDelta)
    {
        float worldPerPixel = 1f / Zoom;
        Position -= new Vector2(screenDelta.X, -screenDelta.Y) * worldPerPixel;
        MarkDirty();
    }

    /// <summary>
    /// 以给定屏幕点为锚点缩放；factor 大于 1 表示放大
    /// Zoom around a screen point; a factor above one zooms in.
    /// </summary>
    public void ZoomAt(Vector2 screenPoint, float factor)
    {
        if (factor <= 0f || !float.IsFinite(factor))
        {
            return;
        }

        Vector2 anchorWorld = ScreenToWorld(screenPoint);
        float next = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        if (MathF.Abs(next - Zoom) < float.Epsilon)
        {
            return;
        }

        Zoom = next;

        // 让锚点保持在原来的屏幕位置 / Keep the anchor under the same screen point
        Vector2 anchorScreen = WorldToScreen(anchorWorld);
        Vector2 correction = screenPoint - anchorScreen;
        Position -= new Vector2(correction.X, -correction.Y) / Zoom;

        MarkDirty();
    }

    /// <summary>屏幕坐标（像素，左上原点）转世界坐标 / Screen pixels (top-left origin) to world.</summary>
    public Vector2 ScreenToWorld(Vector2 screenPoint)
    {
        Vector2 centered = new(
            screenPoint.X - ViewportWidth * 0.5f,
            ViewportHeight * 0.5f - screenPoint.Y);

        if (MathF.Abs(Rotation) > 1e-4f)
        {
            float radians = Rotation * MathF.PI / 180f;
            float cos = MathF.Cos(radians);
            float sin = MathF.Sin(radians);
            centered = new Vector2(centered.X * cos + centered.Y * sin, -centered.X * sin + centered.Y * cos);
        }

        return Position + centered / Zoom;
    }

    /// <summary>世界坐标转屏幕坐标（像素，左上原点） / World to screen pixels (top-left origin).</summary>
    public Vector2 WorldToScreen(Vector2 worldPoint)
    {
        Vector2 local = (worldPoint - Position) * Zoom;

        if (MathF.Abs(Rotation) > 1e-4f)
        {
            float radians = Rotation * MathF.PI / 180f;
            float cos = MathF.Cos(radians);
            float sin = MathF.Sin(radians);
            local = new Vector2(local.X * cos - local.Y * sin, local.X * sin + local.Y * cos);
        }

        return new Vector2(
            ViewportWidth * 0.5f + local.X,
            ViewportHeight * 0.5f - local.Y);
    }

    /// <summary>世界点是否在可见区域内（带边距） / Whether a world point is inside the view, with a margin.</summary>
    public bool IsWorldPointVisible(Vector2 worldPoint, float margin = 0f)
    {
        Vector2 screen = WorldToScreen(worldPoint);
        return screen.X >= -margin
            && screen.X <= ViewportWidth + margin
            && screen.Y >= -margin
            && screen.Y <= ViewportHeight + margin;
    }

    /// <summary>世界包围盒是否与可见区域相交 / Whether a world AABB intersects the view.</summary>
    public bool IsWorldBoxVisible(in Aabb worldBox, float margin = 0f)
    {
        Vector2 min = WorldToScreen(new Vector2(worldBox.Min.X, worldBox.Max.Y));
        Vector2 max = WorldToScreen(new Vector2(worldBox.Max.X, worldBox.Min.Y));

        float left = MathF.Min(min.X, max.X);
        float right = MathF.Max(min.X, max.X);
        float top = MathF.Min(min.Y, max.Y);
        float bottom = MathF.Max(min.Y, max.Y);

        return right >= -margin
            && left <= ViewportWidth + margin
            && bottom >= -margin
            && top <= ViewportHeight + margin;
    }

    /// <summary>把视野调整到能完整看到给定世界矩形 / Fit the view around a world rectangle.</summary>
    public void FrameWorldRect(Vector2 min, Vector2 max, float padding = 1.1f)
    {
        Vector2 size = max - min;
        Vector2 center = (min + max) * 0.5f;

        float safeWidth = MathF.Max(size.X, 1e-3f);
        float safeHeight = MathF.Max(size.Y, 1e-3f);

        float zoomX = ViewportWidth / (safeWidth * padding);
        float zoomY = ViewportHeight / (safeHeight * padding);

        Position = center;
        Zoom = Math.Clamp(MathF.Min(zoomX, zoomY), MinZoom, MaxZoom);
        MarkDirty();
    }

    private void Update()
    {
        if (!_dirty)
        {
            return;
        }

        // 平移 + 旋转 + 缩放（与参考实现一致：较大的 Zoom 表示放大）
        // Translate, rotate, then scale; a larger Zoom means zoomed in
        Matrix4 translation = Matrix4.CreateTranslation(-Position.X, -Position.Y, 0f);
        Matrix4 rotation = Matrix4.CreateRotationZ(-Rotation * MathF.PI / 180f);
        Matrix4 scale = Matrix4.CreateScale(Zoom, Zoom, 1f);

        _view = translation * rotation * scale;

        float halfWidth = ViewportWidth * 0.5f;
        float halfHeight = ViewportHeight * 0.5f;
        _projection = Matrix4.CreateOrthographicOffCenter(
            -halfWidth,
            halfWidth,
            -halfHeight,
            halfHeight,
            NearPlane,
            FarPlane);

        _viewProjection = _view * _projection;
        _dirty = false;
    }
}
