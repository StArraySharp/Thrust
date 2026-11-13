using OpenTK.Mathematics;
using Thrust.Geometry;
using Thrust.Graphics;

namespace Thrust.Scene;

/// <summary>
/// 场景对象：网格 + 材质 + 变换 / A scene object: mesh, material and transform.
/// </summary>
public sealed class SceneNode : ICullable, IDisposable
{
    private Matrix4 _worldMatrix = Matrix4.Identity;

    public SceneNode(string? name = null, Mesh? mesh = null, Material? material = null)
    {
        Name = name ?? "Node";
        Mesh = mesh;
        Material = material;
    }

    public string Name { get; set; }

    public Mesh? Mesh { get; set; }

    public Material? Material { get; set; }

    public Transform Transform { get; set; } = Transform.Identity;

    /// <summary>按局部变换与父节点计算出的世界矩阵 / World matrix computed from local transform and parent.</summary>
    public Matrix4 WorldMatrix
    {
        get => _worldMatrix;
        set => _worldMatrix = value;
    }

    public SceneNode? Parent { get; private set; }

    public List<SceneNode> Children { get; } = [];

    public bool Visible { get; set; } = true;

    public void AddChild(SceneNode child)
    {
        if (child == this)
        {
            throw new ArgumentException("节点不能以自身为父节点 / A node cannot parent itself", nameof(child));
        }

        child.Parent?.Children.Remove(child);
        child.Parent = this;
        Children.Add(child);
    }

    public bool RemoveChild(SceneNode child)
    {
        if (!Children.Remove(child))
        {
            return false;
        }

        child.Parent = null;
        return true;
    }

    /// <summary>
    /// 递归刷新世界矩阵，父节点变化后调用
    /// Refresh world matrices recursively; call after transforms change.
    /// </summary>
    public void UpdateWorldMatrix(in Matrix4 parentMatrix)
    {
        _worldMatrix = Transform.ToMatrix() * parentMatrix;

        if (Mesh != null)
        {
            Mesh.WorldMatrix = _worldMatrix;
        }

        foreach (SceneNode child in Children)
        {
            child.UpdateWorldMatrix(_worldMatrix);
        }
    }

    public void UpdateWorldMatrix() => UpdateWorldMatrix(Matrix4.Identity);

    public Aabb Bounds => Mesh?.Bounds ?? Aabb.FromCenterExtents(_worldMatrix.ExtractTranslation(), Vector3.Zero);

    public bool IsVisible(in Frustum frustum) => Visible && (Mesh?.IsVisible(in frustum) ?? false);

    public void Dispose()
    {
        Mesh?.Dispose();
        Mesh = null;
        Material?.Dispose();
        Material = null;
    }
}

/// <summary>
/// 一条绘制指令 / A single draw call.
/// </summary>
public readonly struct RenderItem
{
    public readonly Mesh Mesh;
    public readonly Material? Material;
    public readonly Matrix4 WorldMatrix;
    public readonly float DistanceToCamera;

    public RenderItem(Mesh mesh, Material? material, in Matrix4 worldMatrix, float distanceToCamera)
    {
        Mesh = mesh;
        Material = material;
        WorldMatrix = worldMatrix;
        DistanceToCamera = distanceToCamera;
    }

    /// <summary>模型视图投影矩阵 / Model-view-projection matrix.</summary>
    public Matrix4 GetModelViewProjection(in Matrix4 viewProjection) => WorldMatrix * viewProjection;

    /// <summary>法线矩阵，用于非等比缩放 / Normal matrix, valid under non-uniform scale.</summary>
    public Matrix4 GetNormalMatrix()
    {
        Matrix4 world = WorldMatrix;
        Matrix4 inverse = Matrix4.Invert(world);
        return Matrix4.Transpose(inverse);
    }
}

/// <summary>
/// 渲染队列：按剔除、排序后产生绘制列表 / Render queue: cull, sort, and produce a draw list.
/// </summary>
public sealed class RenderQueue
{
    private readonly List<RenderItem> _opaque = [];
    private readonly List<RenderItem> _transparent = [];
    private readonly List<SceneNode> _flattened = [];

    public int CulledCount { get; private set; }

    public IReadOnlyList<RenderItem> Opaque => _opaque;

    public IReadOnlyList<RenderItem> Transparent => _transparent;

    public int TotalCount => _opaque.Count + _transparent.Count;

    public void Clear()
    {
        _opaque.Clear();
        _transparent.Clear();
        _flattened.Clear();
        CulledCount = 0;
    }

    /// <summary>
    /// 收集场景中的可绘制对象，执行视锥剔除并按材质排序
    /// Gather drawable objects, cull them, and sort by material.
    /// </summary>
    public void Build(SceneNode root, Camera camera)
    {
        Clear();
        Flatten(root);

        Vector3 cameraPosition = camera.Position;
        Frustum frustum = camera.Frustum;

        foreach (SceneNode node in _flattened)
        {
            if (!node.Visible || node.Mesh == null || node.Mesh.VertexCount == 0)
            {
                continue;
            }

            if (camera.CullingEnabled && !node.Mesh.IsVisible(in frustum))
            {
                CulledCount++;
                continue;
            }

            float distance = Vector3.DistanceSquared(cameraPosition, node.WorldMatrix.ExtractTranslation());
            RenderItem item = new(node.Mesh, node.Material, node.WorldMatrix, distance);

            if (node.Material?.IsTransparent ?? false)
            {
                _transparent.Add(item);
            }
            else
            {
                _opaque.Add(item);
            }
        }

        // 不透明：按材质（渲染队列 + 材质名）聚合以减少状态切换
        // Opaque: group by material to reduce state changes
        _opaque.Sort(static (a, b) =>
        {
            int queue = (a.Material?.RenderQueue ?? 2000).CompareTo(b.Material?.RenderQueue ?? 2000);
            if (queue != 0)
            {
                return queue;
            }

            return string.CompareOrdinal(a.Material?.Name, b.Material?.Name);
        });

        // 半透明：由远到近，保证混合正确
        // Transparent: back to front so blending is correct
        _transparent.Sort(static (a, b) => b.DistanceToCamera.CompareTo(a.DistanceToCamera));
    }

    private void Flatten(SceneNode node)
    {
        _flattened.Add(node);
        foreach (SceneNode child in node.Children)
        {
            Flatten(child);
        }
    }

    /// <summary>遍历全部绘制指令，先不透明后半透明 / Enumerate draws, opaque first.</summary>
    public IEnumerable<RenderItem> All()
    {
        foreach (RenderItem item in _opaque)
        {
            yield return item;
        }

        foreach (RenderItem item in _transparent)
        {
            yield return item;
        }
    }
}
