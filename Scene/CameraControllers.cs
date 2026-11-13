using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Thrust.Geometry;
using Thrust.Graphics;

namespace Thrust.Scene;

/// <summary>
/// 轨道摄像机控制器：绕目标旋转、平移、缩放
/// Orbit camera controller: rotate around a pivot, pan and zoom.
/// </summary>
public sealed class OrbitCameraController
{
    public Vector3 Pivot { get; set; } = Vector3.Zero;

    /// <summary>与目标的距离 / Distance to the pivot.</summary>
    public float Distance { get; set; } = 6f;

    /// <summary>水平旋转（弧度） / Yaw in radians.</summary>
    public float Yaw { get; set; }

    /// <summary>俯仰（弧度），限制在 ±89° / Pitch in radians, clamped near the poles.</summary>
    public float Pitch { get; set; } = 0.35f;

    /// <summary>旋转灵敏度（弧度/像素） / Rotation sensitivity in radians per pixel.</summary>
    public float RotationSpeed { get; set; } = 0.005f;

    /// <summary>平移灵敏度 / Pan sensitivity.</summary>
    public float PanSpeed { get; set; } = 0.0025f;

    /// <summary>缩放灵敏度 / Zoom sensitivity.</summary>
    public float ZoomSpeed { get; set; } = 0.15f;

    public float MinDistance { get; set; } = 0.5f;

    public float MaxDistance { get; set; } = 500f;

    /// <summary>按住鼠标拖动旋转 / Drag to rotate.</summary>
    public void Rotate(Vector2 delta)
    {
        Yaw -= delta.X * RotationSpeed;
        Pitch = Math.Clamp(Pitch - delta.Y * RotationSpeed, -MathF.PI / 2f + 0.01f, MathF.PI / 2f - 0.01f);
    }

    /// <summary>按住鼠标拖动平移 / Drag to pan.</summary>
    public void Pan(Vector2 delta, in Vector3 cameraRight, in Vector3 cameraUp)
    {
        float scale = Distance * PanSpeed;
        Pivot -= cameraRight * delta.X * scale;
        Pivot += cameraUp * delta.Y * scale;
    }

    /// <summary>滚轮缩放 / Zoom with the mouse wheel.</summary>
    public void Zoom(float scroll)
    {
        Distance = Math.Clamp(Distance * MathF.Exp(-scroll * ZoomSpeed), MinDistance, MaxDistance);
    }

    /// <summary>把控制器状态写入摄像机 / Apply the controller state to a camera.</summary>
    public void ApplyTo(Camera camera)
    {
        float cosPitch = MathF.Cos(Pitch);
        Vector3 offset = new(
            MathF.Sin(Yaw) * cosPitch,
            MathF.Sin(Pitch),
            MathF.Cos(Yaw) * cosPitch);

        camera.Position = Pivot + offset * Distance;
        camera.Target = Pivot;
        camera.Up = Vector3.UnitY;
        camera.MarkDirty();
    }

    /// <summary>旋转到指定角度看向目标 / Snap the view so it looks at the pivot.</summary>
    public void Focus(Vector3 pivot, float? distance = null)
    {
        Pivot = pivot;
        if (distance.HasValue)
        {
            Distance = Math.Clamp(distance.Value, MinDistance, MaxDistance);
        }
    }
}

/// <summary>
/// 自由飞行摄像机控制器：WASD 移动、鼠标环视
/// Free-fly camera controller: WASD movement with mouse look.
/// </summary>
public sealed class FlyCameraController
{
    public Vector3 Position { get; set; } = new(0f, 0f, 5f);

    /// <summary>水平旋转（弧度） / Yaw in radians.</summary>
    public float Yaw { get; set; } = MathF.PI;

    /// <summary>俯仰（弧度） / Pitch in radians.</summary>
    public float Pitch { get; set; }

    public float MoveSpeed { get; set; } = 5f;

    public float SprintMultiplier { get; set; } = 3f;

    public float LookSpeed { get; set; } = 0.0025f;

    /// <summary>鼠标环视 / Mouse look.</summary>
    public void Look(Vector2 delta)
    {
        Yaw -= delta.X * LookSpeed;
        Pitch = Math.Clamp(Pitch - delta.Y * LookSpeed, -MathF.PI / 2f + 0.01f, MathF.PI / 2f - 0.01f);
    }

    /// <summary>根据按键与时间步长移动 / Move according to held keys and delta time.</summary>
    public void Move(KeyboardState keyboard, float deltaTime, bool sprint = false)
    {
        Vector3 forward = GetForward();
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        Vector3 up = Vector3.UnitY;

        Vector3 direction = Vector3.Zero;
        if (keyboard.IsKeyDown(Keys.W)) direction += forward;
        if (keyboard.IsKeyDown(Keys.S)) direction -= forward;
        if (keyboard.IsKeyDown(Keys.D)) direction += right;
        if (keyboard.IsKeyDown(Keys.A)) direction -= right;
        if (keyboard.IsKeyDown(Keys.E)) direction += up;
        if (keyboard.IsKeyDown(Keys.Q)) direction -= up;

        if (direction.LengthSquared < 1e-8f)
        {
            return;
        }

        float speed = MoveSpeed * (sprint ? SprintMultiplier : 1f);
        Position += Vector3.Normalize(direction) * speed * deltaTime;
    }

    public Vector3 GetForward()
    {
        float cosPitch = MathF.Cos(Pitch);
        return Vector3.Normalize(new Vector3(
            -MathF.Sin(Yaw) * cosPitch,
            MathF.Sin(Pitch),
            -MathF.Cos(Yaw) * cosPitch));
    }

    /// <summary>把控制器状态写入摄像机 / Apply the controller state to a camera.</summary>
    public void ApplyTo(Camera camera)
    {
        camera.Position = Position;
        camera.Target = Position + GetForward();
        camera.Up = Vector3.UnitY;
        camera.MarkDirty();
    }
}
