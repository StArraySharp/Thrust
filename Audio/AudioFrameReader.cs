using System.Runtime.InteropServices;

namespace Thrust.Audio;

/// <summary>A seekable cursor over interleaved signed 16-bit PCM. Each consumer owns its cursor.</summary>
public abstract class AudioFrameReader : IDisposable
{
    public abstract long Position { get; }
    public abstract void Seek(long frame);
    /// <summary>Reads complete frames and returns the number of frames written.</summary>
    public abstract int Read(Span<short> samples);
    public abstract void Dispose();

    internal sealed class MemoryReader(byte[] pcm, int channels) : AudioFrameReader
    {
        private long _position;
        public override long Position => _position;
        public override void Seek(long frame) => _position = Math.Clamp(frame, 0, pcm.Length / (channels * sizeof(short)));
        public override int Read(Span<short> samples)
        {
            var left = MemoryMarshal.Cast<byte, short>(pcm.AsSpan())[(int)(_position * channels)..];
            int frames = Math.Min(left.Length, samples.Length) / channels;
            left[..(frames * channels)].CopyTo(samples);
            _position += frames;
            return frames;
        }
        public override void Dispose() { }
    }
}

/// <summary>Bounded read-ahead for consumers that revisit nearby samples between updates.</summary>
public sealed class AudioSampleWindow(DecodedAudio audio, int blockFrames = 8192) : IDisposable
{
    private readonly short[] _samples = new short[Math.Max(2, blockFrames) * audio.Channels];
    private AudioFrameReader? _reader;
    private long _first;
    private int _count;

    public ReadOnlySpan<short> Read(long first, int frames)
    {
        first = Math.Clamp(first, 0, audio.FrameCount);
        frames = (int)Math.Clamp(frames, 0, audio.FrameCount - first);
        if (frames == 0) return [];
        if (frames > _samples.Length / audio.Channels)
            throw new ArgumentOutOfRangeException(nameof(frames));
        if (first < _first || first + frames > _first + _count)
            Fill(first);
        int count = (int)Math.Clamp(_first + _count - first, 0, frames);
        return count == 0 ? [] : _samples.AsSpan((int)(first - _first) * audio.Channels, count * audio.Channels);
    }

    private void Fill(long first)
    {
        var reader = _reader ??= audio.OpenReader();
        // Preserve the overlap during forward playback to avoid repeatedly seeking compressed streams.
        int overlap = (int)Math.Clamp(_first + _count - first, 0, _count);
        if (first < _first || reader.Position != _first + _count) overlap = 0;
        if (overlap > 0)
            _samples.AsSpan((int)(first - _first) * audio.Channels, overlap * audio.Channels).CopyTo(_samples);
        _first = first;
        _count = 0;
        if (reader.Position != first + overlap) reader.Seek(first + overlap);
        _count = overlap + reader.Read(_samples.AsSpan(overlap * audio.Channels));
    }

    public void Dispose() => _reader?.Dispose();
}
