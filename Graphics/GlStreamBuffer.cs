using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;

namespace Thrust.Graphics;

/// <summary>
/// An array buffer for per-frame data. Orphaning storage lets queued draws finish
/// using the previous upload instead of waiting to overwrite it. Owns its GL handle.
/// </summary>
public sealed class GlStreamBuffer : IDisposable
{
    public int Handle { get; private set; } = GL.GenBuffer();
    public int CapacityBytes { get; private set; }

    /// <summary>Binds the buffer and replaces its contents; leaves ArrayBuffer bound.</summary>
    public unsafe void Upload<T>(ReadOnlySpan<T> data) where T : unmanaged
    {
        fixed (T* pointer = data)
            Upload((nint)pointer, MemoryMarshal.AsBytes(data).Length);
    }

    /// <summary>The pointer must remain valid for byteCount bytes during this call.</summary>
    public void Upload(nint data, int byteCount)
    {
        ObjectDisposedException.ThrowIf(Handle == 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        if (byteCount == 0) return;
        if (byteCount > CapacityBytes)
            CapacityBytes = (int)Math.Min(int.MaxValue, Math.Max(byteCount, Math.Max(4096L, (long)CapacityBytes * 2)));
        GL.BindBuffer(BufferTarget.ArrayBuffer, Handle);
        GL.BufferData(BufferTarget.ArrayBuffer, (nint)CapacityBytes, nint.Zero, BufferUsageHint.StreamDraw);
        GL.BufferSubData(BufferTarget.ArrayBuffer, nint.Zero, (nint)byteCount, data);
    }

    public void Dispose()
    {
        if (Handle == 0) return;
        GL.DeleteBuffer(Handle);
        Handle = 0;
        CapacityBytes = 0;
    }
}
