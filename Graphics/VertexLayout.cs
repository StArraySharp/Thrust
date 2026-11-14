using System.Runtime.CompilerServices;
using Thrust.Geometry;

namespace Thrust.Graphics;

/// <summary>
/// 顶点属性槽位 / Vertex attribute slots.
/// </summary>
public enum VertexAttribute
{
    Position = 0,
    Normal,
    Tangent,
    Bitangent,

    TexCoord0,
    TexCoord1,
    TexCoord2,
    TexCoord3,
    TexCoord4,
    TexCoord5,
    TexCoord6,
    TexCoord7,

    Color0,
    Color1,

    BoneIndices,
    BoneWeights,

    Custom0,
    Custom1,
    Custom2,
    Custom3,
    Custom4,
}

/// <summary>
/// 单个分量的存储类型 / Storage type of one component.
/// </summary>
public enum VertexValueType
{
    /// <summary>32 位浮点 / 32-bit float.</summary>
    Float,

    /// <summary>16 位半精度浮点 / 16-bit half float.</summary>
    Half,

    /// <summary>8 位有符号整数 / 8-bit signed integer.</summary>
    SByte,

    /// <summary>8 位无符号整数 / 8-bit unsigned integer.</summary>
    Byte,

    /// <summary>16 位有符号整数 / 16-bit signed integer.</summary>
    Short,

    /// <summary>16 位无符号整数 / 16-bit unsigned integer.</summary>
    UShort,
}

/// <summary>
/// 顶点分量格式：类型 + 维度 + 是否归一化
/// Vertex component format: value type, dimension and normalization.
/// </summary>
public readonly struct VertexFormat : IEquatable<VertexFormat>
{
    public readonly VertexValueType Type;
    public readonly byte Dimension;
    public readonly bool Normalized;

    public VertexFormat(VertexValueType type, int dimension, bool normalized = false)
    {
        if (dimension is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(dimension), dimension, "分量维度只能是 1-4 / Component count must be 1-4");
        }

        Type = type;
        Dimension = (byte)dimension;
        Normalized = normalized;
    }

    /// <summary>单个分量的字节数 / Bytes per component.</summary>
    public int ComponentSize => Type switch
    {
        VertexValueType.SByte or VertexValueType.Byte => 1,
        VertexValueType.Half or VertexValueType.Short or VertexValueType.UShort => 2,
        _ => 4,
    };

    /// <summary>整个格式的字节数 / Total bytes of this format.</summary>
    public int SizeInBytes => ComponentSize * Dimension;

    public static VertexFormat Float1 => new(VertexValueType.Float, 1);
    public static VertexFormat Float2 => new(VertexValueType.Float, 2);
    public static VertexFormat Float3 => new(VertexValueType.Float, 3);
    public static VertexFormat Float4 => new(VertexValueType.Float, 4);

    public static VertexFormat Half1 => new(VertexValueType.Half, 1);
    public static VertexFormat Half2 => new(VertexValueType.Half, 2);
    public static VertexFormat Half3 => new(VertexValueType.Half, 3);
    public static VertexFormat Half4 => new(VertexValueType.Half, 4);

    /// <summary>8 位归一化无符号（颜色/权重常用） / Normalized 8-bit unsigned, common for colors.</summary>
    public static VertexFormat UNorm8x4 => new(VertexValueType.Byte, 4, true);

    /// <summary>8 位归一化有符号（法线压缩常用） / Normalized 8-bit signed, common for compressed normals.</summary>
    public static VertexFormat SNorm8x4 => new(VertexValueType.SByte, 4, true);

    /// <summary>8 位归一化无符号双分量 / Normalized 8-bit unsigned, two components.</summary>
    public static VertexFormat UNorm8x2 => new(VertexValueType.Byte, 2, true);

    /// <summary>16 位归一化无符号 / Normalized 16-bit unsigned.</summary>
    public static VertexFormat UNorm16x2 => new(VertexValueType.UShort, 2, true);

    /// <summary>16 位归一化无符号四分量 / Normalized 16-bit unsigned, four components.</summary>
    public static VertexFormat UNorm16x4 => new(VertexValueType.UShort, 4, true);

    /// <summary>原始无符号字节四分量（骨骼索引常用） / Raw unsigned bytes, common for bone indices.</summary>
    public static VertexFormat UByte4 => new(VertexValueType.Byte, 4);

    public bool Equals(VertexFormat other)
        => Type == other.Type && Dimension == other.Dimension && Normalized == other.Normalized;

    public override bool Equals(object? obj) => obj is VertexFormat other && Equals(other);

    public override int GetHashCode() => HashCode.Combine((int)Type, Dimension, Normalized);

    public static bool operator ==(VertexFormat left, VertexFormat right) => left.Equals(right);

    public static bool operator !=(VertexFormat left, VertexFormat right) => !left.Equals(right);

    public override string ToString()
        => $"{Type}x{Dimension}{(Normalized ? "(norm)" : string.Empty)}";
}

/// <summary>
/// 一个顶点属性在顶点缓冲中的位置描述 / Describes one attribute inside a vertex buffer.
/// </summary>
public readonly struct VertexElement
{
    public readonly VertexAttribute Attribute;
    public readonly VertexFormat Format;

    /// <summary>相对顶点起始地址的字节偏移 / Byte offset from the start of the vertex.</summary>
    public readonly int Offset;

    public VertexElement(VertexAttribute attribute, VertexFormat format, int offset)
    {
        Attribute = attribute;
        Format = format;
        Offset = offset;
    }

    public override string ToString() => $"{Attribute} {Format} @{Offset}";
}

/// <summary>
/// 顶点声明：描述顶点缓冲的交错布局 / Vertex declaration describing the interleaved layout.
/// </summary>
public sealed class VertexDeclaration
{
    /// <summary>枚举值的数量，用于按枚举值直接索引 / Number of enum values, used for direct indexing.</summary>
    private const int AttributeCount = (int)VertexAttribute.Custom4 + 1;

    private readonly VertexElement[] _elements;
    private readonly Dictionary<VertexAttribute, int> _indexByAttribute;

    // 按属性枚举值直接查元素，避免逐顶点写入时走字典哈希。
    // 千万级顶点 × 9 个属性会产生上亿次 TryGetValue，那是烘焙耗时的主要来源。
    // Direct lookup by enum value instead of a dictionary hash on every attribute write. Tens of
    // millions of vertices times nine attributes means hundreds of millions of TryGetValue calls,
    // which dominated bake time.
    private readonly VertexElement[] _elementByAttribute = new VertexElement[AttributeCount];
    private readonly bool[] _hasAttribute = new bool[AttributeCount];

    /// <summary>
    /// 按给定顺序构建声明，偏移自动累加
    /// Build a declaration in the given order, computing offsets automatically.
    /// </summary>
    public VertexDeclaration(params (VertexAttribute Attribute, VertexFormat Format)[] layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (layout.Length == 0)
        {
            throw new ArgumentException("顶点声明至少需要一个属性 / A declaration needs at least one attribute", nameof(layout));
        }

        _elements = new VertexElement[layout.Length];
        _indexByAttribute = new Dictionary<VertexAttribute, int>(layout.Length);

        int offset = 0;
        for (int i = 0; i < layout.Length; i++)
        {
            (VertexAttribute attribute, VertexFormat format) = layout[i];

            if (_indexByAttribute.ContainsKey(attribute))
            {
                throw new ArgumentException($"属性重复定义: {attribute} / Duplicate attribute", nameof(layout));
            }

            _elements[i] = new VertexElement(attribute, format, offset);
            _indexByAttribute[attribute] = i;

            int slot = (int)attribute;
            if ((uint)slot < AttributeCount)
            {
                _elementByAttribute[slot] = _elements[i];
                _hasAttribute[slot] = true;
            }

            offset += format.SizeInBytes;
        }

        Stride = offset;
    }

    /// <summary>每个顶点占用的字节数 / Bytes per vertex.</summary>
    public int Stride { get; }

    public IReadOnlyList<VertexElement> Elements => _elements;

    public int ElementCount => _elements.Length;

    public bool Has(VertexAttribute attribute) => _indexByAttribute.ContainsKey(attribute);

    /// <summary>取属性在声明中的元素 / Get the element of an attribute.</summary>
    public bool TryGetElement(VertexAttribute attribute, out VertexElement element)
    {
        // 快路径：枚举值即下标，逐顶点写入时不用算哈希
        // Fast path: the enum value is the index, so per-vertex writes skip hashing entirely
        int slot = (int)attribute;
        if ((uint)slot < AttributeCount && _hasAttribute[slot])
        {
            element = _elementByAttribute[slot];
            return true;
        }

        if (_indexByAttribute.TryGetValue(attribute, out int index))
        {
            element = _elements[index];
            return true;
        }

        element = default;
        return false;
    }

    public VertexElement this[int index] => _elements[index];

    /// <summary>某个属性的字节偏移，不存在时返回 -1 / Byte offset of an attribute, or -1 when absent.</summary>
    public int GetOffset(VertexAttribute attribute)
        => _indexByAttribute.TryGetValue(attribute, out int index) ? _elements[index].Offset : -1;

    /// <summary>某个属性的格式，不存在时返回 0 维格式 / Format of an attribute, or a zero format when absent.</summary>
    public VertexFormat GetFormat(VertexAttribute attribute)
        => _indexByAttribute.TryGetValue(attribute, out int index) ? _elements[index].Format : default;

    /// <summary>取第 N 个 UV 通道对应的属性 / Resolve a UV channel index to its attribute.</summary>
    public static VertexAttribute TexCoord(int channel)
    {
        if (channel is < 0 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "UV 通道只能是 0-7 / UV channel must be 0-7");
        }

        return VertexAttribute.TexCoord0 + channel;
    }

    /// <summary>取第 N 个颜色通道对应的属性 / Resolve a color channel index to its attribute.</summary>
    public static VertexAttribute Color(int channel)
    {
        if (channel is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "颜色通道只能是 0-1 / Color channel must be 0-1");
        }

        return VertexAttribute.Color0 + channel;
    }

    public override string ToString()
        => $"Stride={Stride} [{string.Join(", ", _elements)}]";
}

/// <summary>
/// 供图形 API 使用的分量类型 / Component type understood by graphics APIs.
/// </summary>
public enum GpuComponentType
{
    Float,
    HalfFloat,
    SignedByte,
    UnsignedByte,
    SignedShort,
    UnsignedShort,
    Int,
    UnsignedInt,
}

/// <summary>
/// OpenGL 顶点属性指针参数，可直接喂给 glVertexAttribPointer
/// Parameters for glVertexAttribPointer, ready to pass through.
/// </summary>
public readonly struct GlVertexAttribute
{
    /// <summary>着色器中的 layout(location) / Shader attribute location.</summary>
    public readonly int Location;

    public readonly int Components;

    public readonly GpuComponentType Type;

    public readonly bool Normalized;

    /// <summary>字节偏移 / Byte offset inside the vertex.</summary>
    public readonly int Offset;

    /// <summary>顶点步长 / Vertex stride in bytes.</summary>
    public readonly int Stride;

    public GlVertexAttribute(int location, int components, GpuComponentType type, bool normalized, int offset, int stride)
    {
        Location = location;
        Components = components;
        Type = type;
        Normalized = normalized;
        Offset = offset;
        Stride = stride;
    }

    /// <summary>OpenGL 的 type 常量值 / The OpenGL type constant.</summary>
    public uint OpenGlType => Type switch
    {
        GpuComponentType.Float => 0x1406,          // GL_FLOAT
        GpuComponentType.HalfFloat => 0x140B,      // GL_HALF_FLOAT
        GpuComponentType.SignedByte => 0x1400,     // GL_BYTE
        GpuComponentType.UnsignedByte => 0x1401,   // GL_UNSIGNED_BYTE
        GpuComponentType.SignedShort => 0x1402,    // GL_SHORT
        GpuComponentType.UnsignedShort => 0x1403,  // GL_UNSIGNED_SHORT
        GpuComponentType.Int => 0x1404,            // GL_INT
        _ => 0x1405,                               // GL_UNSIGNED_INT
    };

    public override string ToString()
        => $"loc={Location} {Type}x{Components}{(Normalized ? "(norm)" : string.Empty)} off={Offset} stride={Stride}";
}

/// <summary>
/// Vulkan 顶点输入属性描述 / Vulkan vertex input attribute description.
/// </summary>
public readonly struct VulkanVertexAttribute
{
    public readonly uint Location;

    public readonly uint Binding;

    /// <summary>VK_FORMAT_* 枚举名，交由调用方转换为对应数值 / The VK_FORMAT_* name for the caller to resolve.</summary>
    public readonly string FormatName;

    public readonly uint Offset;

    public VulkanVertexAttribute(uint location, uint binding, string formatName, uint offset)
    {
        Location = location;
        Binding = binding;
        FormatName = formatName;
        Offset = offset;
    }

    public override string ToString() => $"loc={Location} bind={Binding} {FormatName} off={Offset}";
}

/// <summary>
/// 由顶点声明导出图形 API 的布局参数 / Export graphics-API layouts from a declaration.
/// </summary>
public static class VertexLayoutExport
{
    /// <summary>
    /// 生成 OpenGL 的 glVertexAttribPointer 参数，location 按声明顺序依次分配
    /// Build glVertexAttribPointer parameters; locations follow the declaration order.
    /// </summary>
    public static GlVertexAttribute[] ForOpenGL(VertexDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        GlVertexAttribute[] result = new GlVertexAttribute[declaration.ElementCount];
        for (int i = 0; i < declaration.ElementCount; i++)
        {
            VertexElement element = declaration[i];
            result[i] = new GlVertexAttribute(
                i,
                element.Format.Dimension,
                ToGpuType(element.Format.Type),
                element.Format.Normalized,
                element.Offset,
                declaration.Stride);
        }

        return result;
    }

    /// <summary>
    /// 生成 Vulkan 的 VkVertexInputAttributeDescription 参数
    /// Build VkVertexInputAttributeDescription parameters.
    /// </summary>
    /// <param name="declaration">顶点声明 / The declaration.</param>
    /// <param name="binding">顶点缓冲绑定索引 / Vertex buffer binding index.</param>
    public static VulkanVertexAttribute[] ForVulkan(VertexDeclaration declaration, uint binding = 0)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        VulkanVertexAttribute[] result = new VulkanVertexAttribute[declaration.ElementCount];
        for (int i = 0; i < declaration.ElementCount; i++)
        {
            VertexElement element = declaration[i];
            result[i] = new VulkanVertexAttribute(
                (uint)i,
                binding,
                ToVulkanFormatName(element.Format),
                (uint)element.Offset);
        }

        return result;
    }

    private static GpuComponentType ToGpuType(VertexValueType type) => type switch
    {
        VertexValueType.Float => GpuComponentType.Float,
        VertexValueType.Half => GpuComponentType.HalfFloat,
        VertexValueType.SByte => GpuComponentType.SignedByte,
        VertexValueType.Byte => GpuComponentType.UnsignedByte,
        VertexValueType.Short => GpuComponentType.SignedShort,
        _ => GpuComponentType.UnsignedShort,
    };

    private static string ToVulkanFormatName(VertexFormat format)
    {
        string components = format.Dimension switch
        {
            1 => "R",
            2 => "R" + format.Dimension * 0 + "G",
            3 => "RGB",
            _ => "RGBA",
        };

        string type = format.Type switch
        {
            VertexValueType.Float => "SFLOAT",
            VertexValueType.Half => "SFLOAT",
            VertexValueType.SByte => format.Normalized ? "SNORM" : "SINT",
            VertexValueType.Byte => format.Normalized ? "UNORM" : "UINT",
            VertexValueType.Short => format.Normalized ? "SNORM" : "SINT",
            _ => format.Normalized ? "UNORM" : "UINT",
        };

        // 半精度需要使用 16 位格式名 / Half floats use the 16-bit format names
        if (format.Type == VertexValueType.Half)
        {
            string bits = format.Dimension switch
            {
                1 => "R16_SFLOAT",
                2 => "R16G16_SFLOAT",
                3 => "R16G16B16_SFLOAT",
                _ => "R16G16B16A16_SFLOAT",
            };

            return "VK_FORMAT_" + bits;
        }

        string elementBits = format.Type switch
        {
            VertexValueType.Float => "32",
            VertexValueType.SByte or VertexValueType.Byte => "8",
            _ => "16",
        };

        return $"VK_FORMAT_{components}{elementBits}_{type}";
    }
}

/// <summary>
/// 按声明读写顶点缓冲的写入器，负责格式压缩
/// Writes attributes into an interleaved vertex buffer, applying format compression.
/// </summary>
public readonly unsafe ref struct VertexWriter
{
    private readonly byte* _vertexBase;
    private readonly VertexDeclaration _declaration;

    public VertexWriter(byte* vertexBase, VertexDeclaration declaration)
    {
        _vertexBase = vertexBase;
        _declaration = declaration;
    }

    public void SetPosition(in OpenTK.Mathematics.Vector3 value) => Write(VertexAttribute.Position, value);

    public void SetNormal(in OpenTK.Mathematics.Vector3 value) => Write(VertexAttribute.Normal, value);

    public void SetTangent(in OpenTK.Mathematics.Vector4 value) => Write(VertexAttribute.Tangent, value);

    /// <summary>写入某个 UV 通道 / Write one UV channel.</summary>
    public void SetTexCoord(int channel, in OpenTK.Mathematics.Vector2 value)
        => Write(VertexDeclaration.TexCoord(channel), value);

    /// <summary>写入某个颜色通道 / Write one color channel.</summary>
    public void SetColor(int channel, in OpenTK.Mathematics.Vector4 value)
        => Write(VertexDeclaration.Color(channel), value);

    public void SetBoneIndices(in OpenTK.Mathematics.Vector4 value) => Write(VertexAttribute.BoneIndices, value);

    public void SetBoneWeights(in OpenTK.Mathematics.Vector4 value) => Write(VertexAttribute.BoneWeights, value);

    public void SetFloat(VertexAttribute attribute, float value) => Write(attribute, new OpenTK.Mathematics.Vector4(value, 0f, 0f, 0f));

    public void SetVector2(VertexAttribute attribute, in OpenTK.Mathematics.Vector2 value) => Write(attribute, value);

    public void SetVector3(VertexAttribute attribute, in OpenTK.Mathematics.Vector3 value) => Write(attribute, value);

    public void SetVector4(VertexAttribute attribute, in OpenTK.Mathematics.Vector4 value) => Write(attribute, value);

    private void Write(VertexAttribute attribute, in OpenTK.Mathematics.Vector2 value)
        => WriteComponents(attribute, value.X, value.Y, 0f, 0f, 2);

    private void Write(VertexAttribute attribute, in OpenTK.Mathematics.Vector3 value)
        => WriteComponents(attribute, value.X, value.Y, value.Z, 0f, 3);

    private void Write(VertexAttribute attribute, in OpenTK.Mathematics.Vector4 value)
        => WriteComponents(attribute, value.X, value.Y, value.Z, value.W, 4);

    private void WriteComponents(VertexAttribute attribute, float x, float y, float z, float w, int sourceCount)
    {
        if (!_declaration.TryGetElement(attribute, out VertexElement element))
        {
            // 声明中没有该属性时直接忽略 / Silently skip attributes absent from the declaration
            return;
        }

        int components = Math.Min(element.Format.Dimension, sourceCount);
        byte* destination = _vertexBase + element.Offset;

        for (int i = 0; i < element.Format.Dimension; i++)
        {
            float value = i switch
            {
                0 => x,
                1 => y,
                2 => z,
                _ => w,
            };

            // 数据不足时补齐默认值 / Pad missing components with sensible defaults
            if (i >= components)
            {
                value = element.Format.Normalized && element.Format.Type is VertexValueType.Byte ? 1f : 0f;
            }

            WriteComponent(destination + i * element.Format.ComponentSize, element.Format, value);
        }
    }

    private static void WriteComponent(byte* destination, in VertexFormat format, float value)
    {
        switch (format.Type)
        {
            case VertexValueType.Float:
                *(float*)destination = value;
                break;

            case VertexValueType.Half:
                *(Half*)destination = (Half)value;
                break;

            case VertexValueType.Byte:
                *(byte*)destination = format.Normalized
                    ? (byte)Math.Clamp(MathF.Round(value * 255f), 0f, 255f)
                    : (byte)Math.Clamp(MathF.Round(value), 0f, 255f);
                break;

            case VertexValueType.SByte:
                *(sbyte*)destination = format.Normalized
                    ? (sbyte)Math.Clamp(MathF.Round(value * 127f), -128f, 127f)
                    : (sbyte)Math.Clamp(MathF.Round(value), -128f, 127f);
                break;

            case VertexValueType.UShort:
                *(ushort*)destination = format.Normalized
                    ? (ushort)Math.Clamp(MathF.Round(value * 65535f), 0f, 65535f)
                    : (ushort)Math.Clamp(MathF.Round(value), 0f, 65535f);
                break;

            case VertexValueType.Short:
                *(short*)destination = format.Normalized
                    ? (short)Math.Clamp(MathF.Round(value * 32767f), -32768f, 32767f)
                    : (short)Math.Clamp(MathF.Round(value), -32768f, 32767f);
                break;
        }
    }
}

/// <summary>
/// 从顶点缓冲读取属性的辅助方法 / Helpers that read attributes back from a vertex buffer.
/// </summary>
public static unsafe class VertexReader
{
    /// <summary>读取一个顶点上的某个属性（统一返回四分量的浮点） / Read an attribute as a four-component float vector.</summary>
    public static OpenTK.Mathematics.Vector4 Read(byte* vertexBase, in VertexElement element)
    {
        int components = element.Format.Dimension;
        Span<float> values = stackalloc float[4];

        for (int i = 0; i < components; i++)
        {
            byte* source = vertexBase + element.Offset + i * element.Format.ComponentSize;
            values[i] = ReadComponent(source, element.Format);
        }

        return new OpenTK.Mathematics.Vector4(values[0], values[1], values[2], values[3]);
    }

    /// <summary>读取位置属性 / Read the position attribute.</summary>
    public static OpenTK.Mathematics.Vector3 ReadPosition(byte* vertexBase, in VertexElement element)
    {
        OpenTK.Mathematics.Vector4 value = Read(vertexBase, element);
        return new OpenTK.Mathematics.Vector3(value.X, value.Y, value.Z);
    }

    private static float ReadComponent(byte* source, in VertexFormat format)
    {
        switch (format.Type)
        {
            case VertexValueType.Float:
                return *(float*)source;

            case VertexValueType.Half:
                return (float)*(Half*)source;

            case VertexValueType.Byte:
                return format.Normalized ? *(byte*)source / 255f : *(byte*)source;

            case VertexValueType.SByte:
                return format.Normalized ? MathF.Max(*(sbyte*)source / 127f, -1f) : *(sbyte*)source;

            case VertexValueType.UShort:
                return format.Normalized ? *(ushort*)source / 65535f : *(ushort*)source;

            case VertexValueType.Short:
                return format.Normalized ? MathF.Max(*(short*)source / 32767f, -1f) : *(short*)source;

            default:
                return 0f;
        }
    }
}
