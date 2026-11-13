using OpenTK.Mathematics;

namespace Thrust.Geometry;

/// <summary>
/// 位移 / 旋转 / 缩放变换，可计算世界矩阵 / Translation-rotation-scale transform.
/// </summary>
public struct Transform
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Scale;

    public static readonly Transform Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    public Transform(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        Position = position;
        Rotation = rotation;
        Scale = scale;
    }

    public Transform(Vector3 position)
        : this(position, Quaternion.Identity, Vector3.One)
    {
    }

    /// <summary>欧拉角（弧度）设置旋转 / Set rotation from Euler angles in radians.</summary>
    public void SetEulerAngles(Vector3 radians)
        => Rotation = Quaternion.FromEulerAngles(radians.X, radians.Y, radians.Z);

    /// <summary>欧拉角（角度）设置旋转 / Set rotation from Euler angles in degrees.</summary>
    public void SetEulerAnglesDegrees(Vector3 degrees)
        => SetEulerAngles(degrees * (MathF.PI / 180f));

    public Vector3 Forward => Vector3.Transform(Vector3.UnitZ, Rotation);

    public Vector3 Right => Vector3.Transform(Vector3.UnitX, Rotation);

    public Vector3 Up => Vector3.Transform(Vector3.UnitY, Rotation);

    /// <summary>本地到世界矩阵 / Local-to-world matrix.</summary>
    public readonly Matrix4 ToMatrix()
        => Matrix4.CreateScale(Scale) * Matrix4.CreateFromQuaternion(Rotation) * Matrix4.CreateTranslation(Position);

    /// <summary>世界到本地矩阵 / World-to-local matrix.</summary>
    public readonly Matrix4 ToInverseMatrix() => Matrix4.Invert(ToMatrix());

    public Vector3 TransformPoint(in Vector3 point) => Vector3.TransformPosition(point, ToMatrix());

    public Vector3 TransformDirection(in Vector3 direction) => Vector3.TransformNormal(direction, ToMatrix());

    /// <summary>朝向目标点（使用默认的上方向） / Look toward a target using the default up vector.</summary>
    public static Quaternion LookAt(in Vector3 eye, in Vector3 target, in Vector3 up)
    {
        Vector3 forward = target - eye;
        if (forward.LengthSquared < 1e-12f)
        {
            return Quaternion.Identity;
        }

        forward = Vector3.Normalize(forward);

        Vector3 right = Vector3.Cross(up, forward);
        if (right.LengthSquared < 1e-12f)
        {
            // 视线与上方向平行，换一个参考轴 / Degenerate case: pick another axis
            Vector3 fallback = MathF.Abs(forward.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
            right = Vector3.Cross(fallback, forward);
        }

        right = Vector3.Normalize(right);
        Vector3 trueUp = Vector3.Cross(forward, right);

        // 旋转矩阵三列分别是本地 X/Y/Z 轴的世界方向 / Rotation matrix columns are the local axes
        Matrix3F m = new(right, trueUp, forward);
        return m.ToQuaternion();
    }
}

/// <summary>
/// 3x3 旋转矩阵到四元数的转换辅助 / Helper converting a 3x3 rotation basis to a quaternion.
/// </summary>
internal readonly struct Matrix3F
{
    public readonly Vector3 ColumnX;
    public readonly Vector3 ColumnY;
    public readonly Vector3 ColumnZ;

    public Matrix3F(Vector3 columnX, Vector3 columnY, Vector3 columnZ)
    {
        ColumnX = columnX;
        ColumnY = columnY;
        ColumnZ = columnZ;
    }

    /// <summary>按列主序的旋转矩阵转换为四元数 / Convert a column-major rotation basis to a quaternion.</summary>
    public Quaternion ToQuaternion()
    {
        float m00 = ColumnX.X, m10 = ColumnX.Y, m20 = ColumnX.Z;
        float m01 = ColumnY.X, m11 = ColumnY.Y, m21 = ColumnY.Z;
        float m02 = ColumnZ.X, m12 = ColumnZ.Y, m22 = ColumnZ.Z;

        float trace = m00 + m11 + m22;
        Quaternion q;

        if (trace > 0f)
        {
            float s = MathF.Sqrt(trace + 1f) * 2f;
            q = new Quaternion(
                (m21 - m12) / s,
                (m02 - m20) / s,
                (m10 - m01) / s,
                0.25f * s);
        }
        else if (m00 > m11 && m00 > m22)
        {
            float s = MathF.Sqrt(1f + m00 - m11 - m22) * 2f;
            q = new Quaternion(
                0.25f * s,
                (m01 + m10) / s,
                (m02 + m20) / s,
                (m21 - m12) / s);
        }
        else if (m11 > m22)
        {
            float s = MathF.Sqrt(1f + m11 - m00 - m22) * 2f;
            q = new Quaternion(
                (m01 + m10) / s,
                0.25f * s,
                (m12 + m21) / s,
                (m02 - m20) / s);
        }
        else
        {
            float s = MathF.Sqrt(1f + m22 - m00 - m11) * 2f;
            q = new Quaternion(
                (m02 + m20) / s,
                (m12 + m21) / s,
                0.25f * s,
                (m10 - m01) / s);
        }

        return Quaternion.Normalize(q);
    }
}
