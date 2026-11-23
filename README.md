# Thrust

A lightweight C# / OpenGL 4 graphics and audio engine, built as the rendering core for SharpFAI.Editor.

Thrust only covers the engine layer: mesh and geometry building, instanced batching, GPU upload, cameras and
frustum culling, textures and image decoding, streaming audio. It creates no windows, ships no UI and bundles
no assets — the window, input, shaders and textures belong to the host application.

[简体中文](README.zh-CN.md)

## Features

- **Meshes & geometry** — interleaved vertex buffers, 16/32-bit indices, sub-meshes, automatic bounds
- **Flexible vertex layout** — position, normal, tangent, 8 UV channels, 2 colour channels, bones, custom
  attributes, with Float / Half / UNorm8 / SNorm8 / UNorm16 compression
- **Procedural geometry** — planes, cubes, spheres and path-extruded tubes from `MeshBuilder`
- **Instanced batching** — thousands of instances in one draw call; instance data in unmanaged memory
- **GPU upload** — `GlMeshUploader` uploads incrementally by mesh version and binds the vertex layout
- **Cameras & culling** — perspective / orthographic cameras, frustum culling, picking rays, 2D camera,
  orbit and fly controllers
- **Scene graph** — `SceneNode` hierarchy plus `RenderQueue` (cull, group opaque by material, sort
  transparent back-to-front)
- **Textures & images** — RGBA32 textures with bilinear sampling and atlas packing; PNG / JPEG decoding
- **Text rasterization** — CPU-only RGBA32 text, system font fallback, multiline alignment and measurement
- **Streaming audio** — OpenAL device management, WAV (built-in) and OGG (NVorbis) decoding, buffer-queue
  streaming playback

## Requirements

- .NET 10 (`net10.0`) with `AllowUnsafeBlocks`
- A GPU driver with OpenGL 3.3 Core support; shaders use `#version 330 core`
- Native OpenAL for audio (the `OpenAL.Soft` package supplies `OpenAL32.dll`)

## Dependencies

| Package | Version | Purpose | License |
| --- | --- | --- | --- |
| [OpenTK](https://github.com/opentk/opentk) | 4.9.4 | OpenGL / OpenAL bindings, math types | MIT |
| [NVorbis](https://github.com/NVorbis/NVorbis) | 1.0.0-rc.2 | OGG Vorbis decoding | MIT |
| [OpenAL.Soft](https://github.com/kcat/openal-soft) | 1.23.1 | Native OpenAL runtime | LGPL-2.1-or-later |
| [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp) | 4.1.3 | Image decoding and scaling | Six Labors Split License 1.0 |
| [SixLabors.ImageSharp.Drawing](https://github.com/SixLabors/ImageSharp.Drawing) | 3.1.3 | Text drawing (includes SixLabors.Fonts) | Six Labors Split License 1.0 |

## Project Structure

```
Thrust/
├─ Audio/      AudioEngine, AudioClip, AudioDecoder (WAV / OGG)
├─ Geometry/   Aabb, BoundingSphere, Plane, Frustum, Ray, Transform
├─ Graphics/   Mesh, MeshBuilder, GlMeshUploader, InstancedRenderer, CircleRenderer,
│              Shader, Material, Light, Texture, ImageDecoder, TextRasterizer, VertexLayout
└─ Scene/      Camera, Camera2D, OrbitCameraController, FlyCameraController,
               SceneNode, RenderQueue
```

## Quick Start

### Mesh upload

```csharp
using OpenTK.Mathematics;
using Thrust.Graphics;

using GlMeshUploader uploader = new();
using Mesh cube = MeshBuilder.CreateCube(Vector3.One);
uploader.Upload(cube);
```

Custom layout and sub-meshes:

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

Also available: `AddPlane`, `AddTube` (extrude a tube along a polyline, handy for tracks), `Mesh.SetData`.

### Camera and culling

```csharp
using OpenTK.Mathematics;
using Thrust.Scene;

Camera camera = new()
{
    Position = new Vector3(0f, 2f, 6f),
    Target = Vector3.Zero,
    AspectRatio = width / height,
};
camera.MarkDirty();                        // plain setters: rebuild matrices manually

Matrix4 viewProjection = camera.ViewProjection;
bool visible = cube.IsVisible(camera.Frustum);
Ray ray = camera.ScreenPointToRay(new Vector2(0.5f, 0.5f)); // normalized screen coords
```

2D camera and pointer controllers:

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
fly.Move(keyboardState, deltaTime);        // OpenTK KeyboardState
fly.ApplyTo(camera);
```

### Instanced batching

Needs `Instanced.vert` / `Instanced.frag` on disk and a mesh already uploaded to the GPU.

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

`SkipUploadIfUnchanged()` skips redundant uploads for static scenes.

### Dynamic discs

```csharp
using CircleRenderer circles = new(shaderDirectory: "Resources/Shaders");
circles.Draw(camera.ViewProjection, Vector2.Zero, radius: 2f, Vector4.One);
```

### Scene graph

```csharp
using SceneNode root = new("Root");
root.AddChild(new SceneNode("Cube", cube, new Material("Default")));
root.UpdateWorldMatrix();

RenderQueue queue = new();
queue.Build(root, camera);

foreach (RenderItem item in queue.Opaque)  // then Transparent
{
    Matrix4 mvp = item.GetModelViewProjection(camera.ViewProjection);
}
```

`Build` culls against the frustum (`CulledCount` reports the number dropped), groups opaque items by render
queue and material name, and sorts transparent items back-to-front.

### Textures and images

```csharp
using OpenTK.Mathematics;
using Thrust.Graphics;

using Texture texture = Texture.Load("tile.png");
Color32 color = texture.Sample(new Vector2(0.5f, 0.5f));

int decoded = ImageDecoder.LoadTexture(File.ReadAllBytes("tile.png"), generateMipmaps: true);

ImageDecoder.RgbaImage image = ImageDecoder.DecodeFile("planet.png");
ImageDecoder.FlipVertically(image.Pixels, image.Width, image.Height); // top-down → bottom-up
int uploaded = ImageDecoder.Upload(image.Pixels, image.Width, image.Height);

byte[] atlas = ImageDecoder.BuildAtlas(sources, columns: 2, rows: 2, cellSize: 512);
```

GPU texture handles are owned by the caller — release them with `GL.DeleteTexture`.

### Text rasterization

```csharp
var options = new TextRasterizationOptions
{
    FontFamily = "Arial", FontSize = 48, Padding = 4,
    FallbackFamilies = ["Microsoft YaHei", "Noto Sans CJK SC"],
    Alignment = TextAlignment.Center, LineSpacing = 1f,
};
var size = TextRasterizer.Measure("Hello\n世界", options); // no pixel allocation
ImageDecoder.RgbaImage text = TextRasterizer.Rasterize("Hello\n世界", options);
// Flip vertically before uploading to OpenGL, as with decoded images.
ImageDecoder.FlipVertically(text.Pixels, text.Width, text.Height);
```

Text uses pixel units and explicit line breaks; it does not wrap automatically. `MaxWidth` / `MaxHeight`
clip the canvas without scaling glyphs. Empty text returns a transparent 1×1 image. Install fonts on
the host system (including on Linux/macOS); missing faces use the configured fallbacks, then installed
system fonts. Output can differ across systems with different fonts. Resolved faces are cached with a
fixed entry limit; text pixels remain owned by each call. Drawing writes directly into the RGBA array.

### Audio

```csharp
using Thrust.Audio;

AudioEngine.Initialize();                  // once per process

using AudioClip music = new() { Volume = 0.8f };
music.Load("song.ogg");                    // .wav / .ogg
music.Play();
music.Update();                            // call every frame
music.Seek(12.0);

AudioEngine.Shutdown();
```

The WAV decoder supports integer PCM and mono/stereo 32-bit IEEE float, including their `WAVE_FORMAT_EXTENSIBLE` variants. Float samples are converted to signed 16-bit PCM at the original sample rate for OpenAL.

`AudioDecoder.Decode` caches per path; `AudioDecoder.ClearCache()` drops the cache. Synthesised tracks can be
wrapped in `DecodedAudio` and played through `LoadDecoded`.

## Shader Contract

The engine ships no shaders, but the built-in renderers read disk files with a fixed contract:

| Renderer | Files | Uniforms |
| --- | --- | --- |
| `CircleRenderer` | `Circle.vert`, `Circle.frag` | `uViewProjection`, `uCenter`, `uRadius`, `uColor` |
| `InstancedRenderer` | `Instanced.vert`, `Instanced.frag` | `uViewProjection` |

Vertex attribute locations expected by `Instanced.vert`:

| location | Meaning |
| --- | --- |
| 0 | position (`vec3`) |
| 1 | normal (`vec3`) |
| 2 | UV0 (`vec2`) |
| 3 | tangent (`vec4`) |
| 4 | vertex colour (`vec4`) |
| 5–8 | per-instance world matrix (4×4) |
| 9 | per-instance colour (`vec4`) |

Meshes fed to `InstancedRenderer` must therefore declare their first five attributes as
`Position, Normal, TexCoord0, Tangent, Color0`, because `VertexLayoutExport.ForOpenGL` assigns locations in
declaration order.

Matrix convention: OpenTK uses row vectors (`v * M`) and the engine uploads `Matrix4` with
`transpose: false`, so `M * v` in GLSL matches `v * M` in C#.

## License

GNU Lesser General Public License v3.0 or later (LGPL-3.0-or-later). See [LICENSE](LICENSE); LGPL v3
incorporates GPL v3 by reference, provided here as [LICENSE.GPL-3.0](LICENSE.GPL-3.0).

Third-party components keep their own licenses — see [Dependencies](#dependencies).

## Author

StArray
