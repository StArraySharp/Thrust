using OpenTK.Mathematics;
using Thrust.Geometry;
using Thrust.Scene;

namespace Thrust.Graphics;

/// <summary>
/// 混合模式 / Blend mode.
/// </summary>
public enum BlendMode
{
    Opaque,
    Alpha,
    Additive,
    Multiply,
}

/// <summary>
/// 深度测试模式 / Depth test function.
/// </summary>
public enum DepthTest
{
    Disabled,
    Less,
    LessOrEqual,
    Greater,
    Always,
}

/// <summary>
/// 剔除模式 / Face culling.
/// </summary>
public enum CullMode
{
    None,
    Back,
    Front,
}

/// <summary>
/// 材质：一个网格如何被绘制 / Material: how a mesh should be drawn.
/// </summary>
public sealed class Material : IDisposable
{
    public Material(string? name = null, Shader? shader = null)
    {
        Name = name ?? "Material";
        Shader = shader;
    }

    public string Name { get; set; }

    /// <summary>使用的着色器 / Shader used for this material.</summary>
    public Shader? Shader { get; set; }

    /// <summary>基础颜色 / Base (albedo) color.</summary>
    public Vector4 BaseColor { get; set; } = Vector4.One;

    /// <summary>基础颜色贴图 / Albedo texture.</summary>
    public Texture? BaseColorTexture { get; set; }

    /// <summary>法线贴图（配合多 UV 使用时指定通道） / Normal map.</summary>
    public Texture? NormalTexture { get; set; }

    /// <summary>金属度 / Metalness in 0~1.</summary>
    public float Metalness { get; set; }

    /// <summary>粗糙度 / Roughness in 0~1.</summary>
    public float Roughness { get; set; } = 0.8f;

    /// <summary>自发光颜色 / Emissive color.</summary>
    public Vector3 Emissive { get; set; } = Vector3.Zero;

    /// <summary>UV 平铺 / UV tiling.</summary>
    public Vector2 UvTiling { get; set; } = Vector2.One;

    /// <summary>UV 偏移 / UV offset.</summary>
    public Vector2 UvOffset { get; set; } = Vector2.Zero;

    /// <summary>使用哪个 UV 通道采样（0-3） / Which UV channel to sample (0-3).</summary>
    public int UvChannel { get; set; }

    /// <summary>混合模式 / Blend mode.</summary>
    public BlendMode BlendMode { get; set; } = BlendMode.Opaque;

    /// <summary>深度测试 / Depth test function.</summary>
    public DepthTest DepthTest { get; set; } = DepthTest.LessOrEqual;

    /// <summary>是否写入深度缓冲 / Whether depth writes are enabled.</summary>
    public bool DepthWrite { get; set; } = true;

    /// <summary>面剔除 / Face culling.</summary>
    public CullMode CullMode { get; set; } = CullMode.Back;

    /// <summary>是否启用双面渲染 / Whether both faces are rendered.</summary>
    public bool DoubleSided => CullMode == CullMode.None;

    /// <summary>渲染队列，数值小的先绘制 / Render queue; lower values draw first.</summary>
    public int RenderQueue => BlendMode == BlendMode.Opaque ? 2000 : 3000;

    /// <summary>此材质是否为半透明 / Whether this material is transparent.</summary>
    public bool IsTransparent => BlendMode != BlendMode.Opaque;

    /// <summary>
    /// 材质版本号，属性变更时手动递增以触发重新绑定
    /// Manual version counter; bump it when properties change so the backend rebinds.
    /// </summary>
    public int Version { get; private set; }

    public void MarkDirty() => Version++;

    /// <summary>所有被引用的贴图 / All textures referenced by this material.</summary>
    public IEnumerable<Texture> Textures()
    {
        if (BaseColorTexture != null)
        {
            yield return BaseColorTexture;
        }

        if (NormalTexture != null)
        {
            yield return NormalTexture;
        }
    }

    public void Dispose()
    {
        Shader?.Dispose();
        Shader = null;
        BaseColorTexture?.Dispose();
        BaseColorTexture = null;
        NormalTexture?.Dispose();
        NormalTexture = null;
    }
}

/// <summary>
/// 光照类型 / Light type.
/// </summary>
public enum LightType
{
    Directional,
    Point,
    Spot,
}

/// <summary>
/// 场景光源 / Scene light.
/// </summary>
public sealed class Light
{
    public LightType Type { get; set; } = LightType.Directional;

    /// <summary>颜色（线性空间，0~1） / Color in linear space.</summary>
    public Vector3 Color { get; set; } = Vector3.One;

    /// <summary>强度 / Intensity.</summary>
    public float Intensity { get; set; } = 1f;

    /// <summary>方向光/聚光灯的朝向 / Direction for directional and spot lights.</summary>
    public Vector3 Direction { get; set; } = -Vector3.UnitY;

    /// <summary>点光源/聚光灯的位置 / Position for point and spot lights.</summary>
    public Vector3 Position { get; set; } = Vector3.Zero;

    /// <summary>衰减半径 / Attenuation range.</summary>
    public float Range { get; set; } = 10f;

    /// <summary>聚光灯内锥角（弧度） / Spot inner cone angle in radians.</summary>
    public float InnerConeAngle { get; set; } = MathF.PI / 12f;

    /// <summary>聚光灯外锥角（弧度） / Spot outer cone angle in radians.</summary>
    public float OuterConeAngle { get; set; } = MathF.PI / 8f;

    /// <summary>灯光是否投射阴影 / Whether shadows are enabled.</summary>
    public bool CastShadows { get; set; }

    /// <summary>创建平行光 / Convenience: a directional light.</summary>
    public static Light CreateDirectional(Vector3 direction, Vector3? color = null, float intensity = 1f) => new()
    {
        Type = LightType.Directional,
        Direction = Vector3.Normalize(direction),
        Color = color ?? Vector3.One,
        Intensity = intensity,
    };

    /// <summary>创建点光源 / Convenience: a point light.</summary>
    public static Light CreatePoint(Vector3 position, Vector3? color = null, float intensity = 1f, float range = 10f) => new()
    {
        Type = LightType.Point,
        Position = position,
        Color = color ?? Vector3.One,
        Intensity = intensity,
        Range = range,
    };
}

/// <summary>
/// 环境光 / Ambient lighting settings.
/// </summary>
public sealed class AmbientLight
{
    public Vector3 Color { get; set; } = new(0.15f, 0.16f, 0.2f);

    public float Intensity { get; set; } = 1f;

    public Vector3 ToDirectionalColor() => Color * Intensity;
}
