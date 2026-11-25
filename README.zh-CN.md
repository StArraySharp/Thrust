# Thrust

轻量的 C# / OpenGL 4 图形与音频引擎，作为 SharpFAI.Editor 的渲染核心。

Thrust 只负责引擎那一层：网格与几何构建、实例化批绘、GPU 上传、相机与视锥剔除、纹理与图片解码、流式音频。
它不创建窗口、不提供 UI、也不携带资源文件——窗口、输入、着色器与贴图都由宿主程序负责。

[English](README.md)

## 功能特性

- **网格与几何** — 交错顶点缓冲、16/32 位索引、子网格、包围盒与包围球自动计算
- **灵活顶点布局** — 位置、法线、切线、8 个 UV 通道、2 个颜色通道、骨骼、自定义属性，支持
  Float / Half / UNorm8 / SNorm8 / UNorm16 压缩格式
- **程序化几何** — `MeshBuilder` 直接生成平面、立方体、球体与沿路径挤出的圆管
- **实例化批绘** — 一次 draw call 画上千个实例，实例数据存放在非托管内存
- **GPU 上传** — `GlMeshUploader` 按网格版本号增量上传，并自动绑定顶点布局
- **相机与剔除** — 透视 / 正交相机、视锥剔除、拾取射线、2D 相机、轨道与飞行控制器
- **场景图** — `SceneNode` 层级与 `RenderQueue`（剔除后按材质聚合，半透明由远到近排序）
- **纹理与图片** — RGBA32 纹理、双线性采样、图集拼接；PNG / JPEG 解码
- **文字栅格化** — 纯 CPU 的 RGBA32 文字输出、系统字体回退、多行对齐与测量
- **流式音频** — OpenAL 设备管理、WAV、Ogg Vorbis、MP3 和 AIFF 解码、按缓冲队列播放

## 系统要求

- .NET 10（`net10.0`）并开启 `AllowUnsafeBlocks`
- 支持 OpenGL 3.3 Core 的显卡驱动，着色器使用 `#version 330 core`
- 音频需要原生 OpenAL（`OpenAL.Soft` 包提供 `OpenAL32.dll`）

## 依赖

| 包 | 版本 | 用途 | 许可证 |
| --- | --- | --- | --- |
| [OpenTK](https://github.com/opentk/opentk) | 4.9.4 | OpenGL / OpenAL 绑定与数学类型 | MIT |
| [NVorbis](https://github.com/NVorbis/NVorbis) | 1.0.0-rc.2 | OGG Vorbis 解码 | MIT |
| [MP3Sharp](https://www.nuget.org/packages/MP3Sharp/1.0.5) | 1.0.5 | MP3 解码 | LGPL-3.0 |
| [OpenAL.Soft](https://github.com/kcat/openal-soft) | 1.23.1 | 原生 OpenAL 运行时 | LGPL-2.1-or-later |
| [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp) | 4.1.3 | 图片解码与缩放 | Six Labors Split License 1.0 |
| [SixLabors.ImageSharp.Drawing](https://github.com/SixLabors/ImageSharp.Drawing) | 3.1.3 | 文字绘制（含 SixLabors.Fonts） | Six Labors Split License 1.0 |

## 项目结构

```
Thrust/
├─ Audio/      AudioEngine、AudioClip、AudioDecoder（WAV / OGG / MP3 / AIFF）
├─ Geometry/   Aabb、BoundingSphere、Plane、Frustum、Ray、Transform
├─ Graphics/   Mesh、MeshBuilder、GlMeshUploader、InstancedRenderer、CircleRenderer、
│              Shader、Material、Light、Texture、ImageDecoder、TextRasterizer、VertexLayout
└─ Scene/      Camera、Camera2D、OrbitCameraController、FlyCameraController、
               SceneNode、RenderQueue
```

## 快速上手

### 上传网格

```csharp
using OpenTK.Mathematics;
using Thrust.Graphics;

using GlMeshUploader uploader = new();
using Mesh cube = MeshBuilder.CreateCube(Vector3.One);
uploader.Upload(cube);
```

自定义布局与子网格：

```csharp
VertexDeclaration declaration = new(
    (VertexAttribute.Position, VertexFormat.Float3),
    (VertexAttribute.Normal, VertexFormat.Float3),
    (VertexAttribute.TexCoord0, VertexFormat.Float2),
    (VertexAttribute.Tangent, VertexFormat.Float4),
    (VertexAttribute.Color0, VertexFormat.Float4));

using MeshBuilder builder = new(declaration);
builder.BeginSubMesh(materialSlot: 0);
builder.AddCube(Vector3.One);
builder.EndSubMesh();
using Mesh mesh = builder.Build("Demo");
```

还有 `AddPlane`、`AddTube`（沿折线挤出圆管，适合轨道）以及 `Mesh.SetData`。

### 相机与剔除

```csharp
using OpenTK.Mathematics;
using Thrust.Scene;

Camera camera = new()
{
    Position = new Vector3(0f, 2f, 6f),
    Target = Vector3.Zero,
    AspectRatio = width / height,
};
camera.MarkDirty();                        // 属性是普通 setter，改完需手动重建矩阵

Matrix4 viewProjection = camera.ViewProjection;
bool visible = cube.IsVisible(camera.Frustum);
Ray ray = camera.ScreenPointToRay(new Vector2(0.5f, 0.5f)); // 归一化屏幕坐标
```

2D 相机与鼠标控制器：

```csharp
Camera2D camera2D = new()
{
    Position = Vector2.Zero,
    Zoom = 2f,
    ViewportWidth = 1600f,
    ViewportHeight = 900f,
};
camera2D.MarkDirty();
Vector2 world = camera2D.ScreenToWorld(mousePosition);

OrbitCameraController orbit = new();
orbit.Rotate(mouseDelta);
orbit.Zoom(scroll);
orbit.ApplyTo(camera);

FlyCameraController fly = new();
fly.Look(mouseDelta);
fly.Move(keyboardState, deltaTime);        // OpenTK 的 KeyboardState
fly.ApplyTo(camera);
```

### 实例化批绘

需要磁盘上的 `Instanced.vert` / `Instanced.frag`，且网格已上传到 GPU。

```csharp
using InstancedRenderer batches = new(cube, uploader, shaderDirectory: "Resources/Shaders");

batches.Begin(instanceCount: 1000);
for (int i = 0; i < 1000; i++)
{
    Matrix4 world = Matrix4.CreateTranslation(i * 1.5f, 0f, 0f);
    batches.SetInstance(i, in world, Vector4.One);
}

batches.Draw(camera.ViewProjection);
```

静态场景可调用 `SkipUploadIfUnchanged()` 跳过重复上传。

### 动态圆形

```csharp
using CircleRenderer circles = new(shaderDirectory: "Resources/Shaders");
circles.Draw(camera.ViewProjection, Vector2.Zero, radius: 2f, Vector4.One);
```

### 场景图

```csharp
using SceneNode root = new("Root");
root.AddChild(new SceneNode("Cube", cube, new Material("Default")));
root.UpdateWorldMatrix();

RenderQueue queue = new();
queue.Build(root, camera);

foreach (RenderItem item in queue.Opaque)  // 再画 Transparent
{
    Matrix4 mvp = item.GetModelViewProjection(camera.ViewProjection);
}
```

`Build` 会做视锥剔除（`CulledCount` 报告被剔除的数量），不透明项按渲染队列与材质名聚合，
半透明项按距离由远到近排序。

### 纹理与图片

```csharp
using OpenTK.Mathematics;
using Thrust.Graphics;

using Texture texture = Texture.Load("tile.png");
Color32 color = texture.Sample(new Vector2(0.5f, 0.5f));

int decoded = ImageDecoder.LoadTexture(File.ReadAllBytes("tile.png"), generateMipmaps: true);

ImageDecoder.RgbaImage image = ImageDecoder.DecodeFile("planet.png");
ImageDecoder.FlipVertically(image.Pixels, image.Width, image.Height); // 自上而下 → 自下而上
int uploaded = ImageDecoder.Upload(image.Pixels, image.Width, image.Height);

byte[] atlas = ImageDecoder.BuildAtlas(sources, columns: 2, rows: 2, cellSize: 512);
```

GPU 纹理句柄由调用方持有，请用 `GL.DeleteTexture` 释放。

### 文字栅格化

```csharp
var options = new TextRasterizationOptions
{
    FontFamily = "Arial", FontSize = 48, Padding = 4,
    FallbackFamilies = ["Microsoft YaHei", "Noto Sans CJK SC"],
    Alignment = TextAlignment.Center, LineSpacing = 1f,
};
var size = TextRasterizer.Measure("Hello\n世界", options); // 不分配像素缓冲
ImageDecoder.RgbaImage text = TextRasterizer.Rasterize("Hello\n世界", options);
// 与图片解码结果相同，上传 OpenGL 前先垂直翻转。
ImageDecoder.FlipVertically(text.Pixels, text.Width, text.Height);
```

字号按像素计算，保留显式换行，不自动折行；`MaxWidth` / `MaxHeight` 限制画布并裁切溢出，
不会缩放字形。空文本返回透明的 1×1 图片。宿主系统（包括 Linux/macOS）需要安装字体；
缺失字体依次使用指定回退字体和系统字体，因此不同系统的字体差异会影响输出。字体解析结果
有数量上限的缓存，每次调用独立持有像素，绘制直接写入返回的 RGBA 数组。

### 音频

```csharp
using Thrust.Audio;

AudioEngine.Initialize();                  // 进程内一次即可

using AudioClip music = new() { Volume = 0.8f };
music.Load("song.ogg");                    // .wav / .ogg / .mp3 / .aif / .aiff / .aifc
music.Play();
music.Update();                            // 每帧调用
music.Seek(12.0);

AudioEngine.Shutdown();
```

WAV 解码支持整数 PCM 和单声道或双声道的 32 位 IEEE 浮点 PCM，也支持其 `WAVE_FORMAT_EXTENSIBLE` 变体。浮点采样转换为 OpenAL 使用的 16 位有符号 PCM，保留原采样率。

MP3 和 AIFF 保留原采样率及单声道或双声道布局。MP3 带有效的 Xing/Info 编码器标记时会裁掉编码延迟与尾部填充。AIFF 支持有符号整数 PCM，AIFF-C 支持大端或小端整数 PCM 和 IEEE 浮点 PCM。

`AudioDecoder.Decode` 按路径缓存解码结果，`AudioDecoder.ClearCache()` 可清理缓存；
合成的音轨可包装成 `DecodedAudio`，通过 `LoadDecoded` 播放。

## 着色器约定

引擎本身不携带着色器，但内置渲染器按固定约定读取磁盘文件：

| 渲染器 | 需要的文件 | Uniforms |
| --- | --- | --- |
| `CircleRenderer` | `Circle.vert`、`Circle.frag` | `uViewProjection`、`uCenter`、`uRadius`、`uColor` |
| `InstancedRenderer` | `Instanced.vert`、`Instanced.frag` | `uViewProjection` |

`Instanced.vert` 期望的顶点属性位置：

| location | 含义 |
| --- | --- |
| 0 | 位置（`vec3`） |
| 1 | 法线（`vec3`） |
| 2 | UV0（`vec2`） |
| 3 | 切线（`vec4`） |
| 4 | 顶点色（`vec4`） |
| 5–8 | 实例世界矩阵（4×4） |
| 9 | 实例颜色（`vec4`） |

因此用于 `InstancedRenderer` 的网格，其 `VertexDeclaration` 前五项必须是
`Position, Normal, TexCoord0, Tangent, Color0`，因为 `VertexLayoutExport.ForOpenGL` 按声明顺序分配 location。

矩阵约定：OpenTK 使用行向量（`v * M`），引擎以 `transpose: false` 上传 `Matrix4`，
因此 GLSL 侧的 `M * v` 与 C# 侧的 `v * M` 完全一致。

## 许可证

GNU Lesser General Public License v3.0 or later（LGPL-3.0-or-later）。全文见 [LICENSE](LICENSE)；
LGPL v3 通过引用并入 GPL v3，其副本见 [LICENSE.GPL-3.0](LICENSE.GPL-3.0)。

第三方组件遵循各自的许可证，见上方「依赖」。

## 作者

StArray
