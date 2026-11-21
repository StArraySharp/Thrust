using OpenTK.Graphics.OpenGL4;

namespace Thrust.Graphics;

/// <summary>
/// Saves the GL state touched by fullscreen and sprite effects without allocating.
/// Use in a using statement on the current context; do not copy an active scope.
/// Does not save texture contents/parameters, read framebuffer or blend equations.
/// </summary>
public unsafe struct GlRenderStateScope : IDisposable
{
    public const int TextureUnits = 3;
    private fixed int _viewport[4];
    private fixed int _textures[TextureUnits];
    private fixed int _samplers[TextureUnits];
    private readonly int _framebuffer, _program, _vao, _renderbuffer, _active, _arrayBuffer;
    private readonly int _srcRgb, _dstRgb, _srcAlpha, _dstAlpha;
    private readonly bool _blend, _depth, _cull, _scissor;
    private readonly bool _red, _green, _blue, _alpha;

    public int Framebuffer => _framebuffer;
    public int X => _viewport[0];
    public int Y => _viewport[1];
    public int Width => _viewport[2];
    public int Height => _viewport[3];

    public GlRenderStateScope()
    {
        this = default;
        _framebuffer = GL.GetInteger(GetPName.DrawFramebufferBinding);
        fixed (int* viewport = _viewport)
            GL.GetInteger(GetPName.Viewport, viewport);
        bool* mask = stackalloc bool[4];
        GL.GetBoolean(GetPName.ColorWritemask, mask);
        _red = mask[0]; _green = mask[1]; _blue = mask[2]; _alpha = mask[3];
        _blend = GL.IsEnabled(EnableCap.Blend);
        _depth = GL.IsEnabled(EnableCap.DepthTest);
        _cull = GL.IsEnabled(EnableCap.CullFace);
        _scissor = GL.IsEnabled(EnableCap.ScissorTest);
        _program = GL.GetInteger(GetPName.CurrentProgram);
        _vao = GL.GetInteger(GetPName.VertexArrayBinding);
        _renderbuffer = GL.GetInteger(GetPName.RenderbufferBinding);
        _active = GL.GetInteger(GetPName.ActiveTexture);
        _arrayBuffer = GL.GetInteger(GetPName.ArrayBufferBinding);
        _srcRgb = GL.GetInteger(GetPName.BlendSrcRgb);
        _dstRgb = GL.GetInteger(GetPName.BlendDstRgb);
        _srcAlpha = GL.GetInteger(GetPName.BlendSrcAlpha);
        _dstAlpha = GL.GetInteger(GetPName.BlendDstAlpha);
        for (int unit = 0; unit < TextureUnits; unit++)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + unit);
            _textures[unit] = GL.GetInteger(GetPName.TextureBinding2D);
            _samplers[unit] = GL.GetInteger(GetPName.SamplerBinding);
        }
        GL.ActiveTexture(TextureUnit.Texture0);
    }

    public void Dispose()
    {
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _framebuffer);
        GL.Viewport(X, Y, Width, Height);
        GL.UseProgram(_program);
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _arrayBuffer);
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _renderbuffer);
        GL.BlendFuncSeparate((BlendingFactorSrc)_srcRgb, (BlendingFactorDest)_dstRgb,
            (BlendingFactorSrc)_srcAlpha, (BlendingFactorDest)_dstAlpha);
        GL.ColorMask(_red, _green, _blue, _alpha);
        Restore(EnableCap.Blend, _blend);
        Restore(EnableCap.DepthTest, _depth);
        Restore(EnableCap.CullFace, _cull);
        Restore(EnableCap.ScissorTest, _scissor);
        for (int unit = 0; unit < TextureUnits; unit++)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + unit);
            GL.BindTexture(TextureTarget.Texture2D, _textures[unit]);
            GL.BindSampler(unit, _samplers[unit]);
        }
        GL.ActiveTexture((TextureUnit)_active);
    }

    private static void Restore(EnableCap capability, bool enabled)
    {
        if (enabled) GL.Enable(capability);
        else GL.Disable(capability);
    }
}
