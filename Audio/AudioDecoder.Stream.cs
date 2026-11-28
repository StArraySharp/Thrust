using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NVorbis;

namespace Thrust.Audio;

public static partial class AudioDecoder
{
    /// <summary>Opens music metadata without retaining decoded PCM. Readers decode on demand.</summary>
    public static DecodedAudio? OpenStream(string path, CancellationToken cancellation = default)
    {
        if (!File.Exists(path)) return null;
        try
        {
            string fullPath = Path.GetFullPath(path);
            using var stream = File.OpenRead(fullPath);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            return extension switch
            {
                ".wav" => ReadWaveLayout(stream)?.AsTrack(fullPath),
                ".aif" or ".aiff" or ".aifc" => ReadAiffLayout(stream)?.AsTrack(fullPath),
                ".ogg" => OpenVorbisTrack(fullPath, stream),
                ".mp3" => ReadMp3Layout(fullPath, stream, cancellation),
                _ => null,
            };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[Audio] {path}: {error.Message}");
            return null;
        }
    }

    private static DecodedAudio? OpenVorbisTrack(string path, Stream stream)
    {
        using var reader = new VorbisReader(stream, closeOnDispose: false);
        long frames = reader.TotalSamples;
        return frames > 0
            ? new(reader.Channels, reader.SampleRate, frames,
                () => new VorbisFrames(new VorbisReader(path), frames))
            : null;
    }

    private sealed class VorbisFrames(VorbisReader reader, long frames) : AudioFrameReader
    {
        private readonly float[] _samples = new float[8192 * reader.Channels];
        private bool _atEnd;
        public override long Position => _atEnd ? frames : reader.SamplePosition;
        public override void Seek(long frame)
        {
            long target = Math.Clamp(frame, 0, frames);
            _atEnd = target == frames;
            if (_atEnd || target == reader.SamplePosition) return;
            // With a short PCM preroll the decoder restores the overlapping Vorbis windows after a seek.
            long start = Math.Max(0, target - 8192);
            reader.SamplePosition = start;
            while (reader.SamplePosition < target)
            {
                int count = (int)Math.Min(target - reader.SamplePosition, _samples.Length / reader.Channels);
                if (reader.ReadSamples(_samples.AsSpan(0, count * reader.Channels)) == 0) break;
            }
        }

        public override int Read(Span<short> samples)
        {
            int written = 0;
            int channels = reader.Channels;
            int available = (int)Math.Min(samples.Length / channels, Math.Max(0, frames - Position));
            samples = samples[..(available * channels)];
            while (samples.Length - written >= channels)
            {
                int count = Math.Min(_samples.Length, (samples.Length - written) / channels * channels);
                int read = reader.ReadSamples(_samples.AsSpan(0, count));
                if (read == 0) break;
                for (int i = 0; i < read; i++)
                    samples[written + i] = (short)MathF.Round(Math.Clamp(_samples[i], -1f, 1f) * short.MaxValue);
                written += read;
            }
            return written / channels;
        }

        public override void Dispose() => reader.Dispose();
    }

    private enum PcmEncoding { Unsigned8, IntegerLittle, IntegerBig, FloatLittle, FloatBig }

    private sealed record PcmLayout(long Offset, long Frames, int Channels, int Rate, int Width, PcmEncoding Encoding)
    {
        internal DecodedAudio? AsTrack(string path) => Frames > 0
            ? new(Channels, Rate, Frames, () => new PcmFrames(File.OpenRead(path), this))
            : null;
    }

    private sealed class PcmFrames(Stream stream, PcmLayout layout) : AudioFrameReader
    {
        private readonly byte[] _bytes = new byte[4096 * layout.Channels * layout.Width];
        private long _position;

        public override long Position => _position;
        public override void Seek(long frame) => _position = Math.Clamp(frame, 0, layout.Frames);

        public override int Read(Span<short> destination)
        {
            int channels = layout.Channels;
            int frameBytes = channels * layout.Width;
            int frames = (int)Math.Min(destination.Length / channels, layout.Frames - _position);
            stream.Position = layout.Offset + _position * frameBytes;
            int written = 0;
            while (written < frames)
            {
                int count = Math.Min(frames - written, _bytes.Length / frameBytes);
                var block = _bytes.AsSpan(0, count * frameBytes);
                int bytes = stream.ReadAtLeast(block, block.Length, throwOnEndOfStream: false);
                int complete = bytes / frameBytes;
                var output = destination.Slice(written * channels, complete * channels);
                if (layout is { Encoding: PcmEncoding.IntegerLittle, Width: 2 } && BitConverter.IsLittleEndian)
                    block[..(complete * frameBytes)].CopyTo(MemoryMarshal.AsBytes(output));
                else
                    ConvertPcm(block, output, layout.Width, layout.Encoding);
                written += complete;
                if (complete != count) break;
            }
            _position += written;
            return written;
        }

        public override void Dispose() => stream.Dispose();
    }

    private static void ConvertPcm(ReadOnlySpan<byte> input, Span<short> output, int width, PcmEncoding encoding)
    {
        for (int i = 0; i < output.Length; i++)
        {
            var sample = input.Slice(i * width, width);
            output[i] = encoding switch
            {
                PcmEncoding.Unsigned8 => (short)((sample[0] - 128) << 8),
                PcmEncoding.FloatLittle => FloatToPcm16(BinaryPrimitives.ReadSingleLittleEndian(sample)),
                PcmEncoding.FloatBig when width == 8 => FloatToPcm16(BinaryPrimitives.ReadDoubleBigEndian(sample)),
                PcmEncoding.FloatBig => FloatToPcm16(BinaryPrimitives.ReadSingleBigEndian(sample)),
                _ when width == 1 => (short)(sample[0] << 8),
                PcmEncoding.IntegerLittle => BinaryPrimitives.ReadInt16LittleEndian(sample[^2..]),
                _ => BinaryPrimitives.ReadInt16BigEndian(sample),
            };
        }
    }

    internal static DecodedAudio ReadAll(AudioFrameReader reader, int channels, int rate, long frames,
        CancellationToken cancellation = default)
    {
        using var output = new MemoryStream();
        var samples = new short[8192 * channels];
        while (frames > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            int requested = (int)Math.Min(frames, samples.Length / channels);
            int read = reader.Read(samples.AsSpan(0, requested * channels));
            if (read == 0) break;
            output.Write(MemoryMarshal.AsBytes(samples.AsSpan(0, read * channels)));
            frames -= read;
        }
        return new DecodedAudio { Pcm = output.ToArray(), Channels = channels, SampleRate = rate };
    }
}
