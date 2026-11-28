using MP3Sharp;

namespace Thrust.Audio;

public static partial class AudioDecoder
{
    private const int Mp3IndexStride = 32;
    private const int Mp3PrerollFrames = 12;
    private static readonly int[] Mpeg1Layer1 = [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448];
    private static readonly int[] Mpeg1Layer2 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
    private static readonly int[] Mpeg1Layer3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mpeg2Layer1 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256];
    private static readonly int[] Mpeg2Other = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    private static readonly int[] Mpeg1Rates = [44100, 48000, 32000];

    private readonly record struct MpegHeader(int Rate, int Channels, int Samples, int Bytes);

    private static DecodedAudio? ReadMp3Layout(string path, Stream stream, CancellationToken cancellation)
    {
        SkipMp3Id3(stream);
        long searchEnd = Math.Min(stream.Length - 4, stream.Position + 64 * 1024);
        Span<byte> bytes = stackalloc byte[4];
        MpegHeader first = default;
        long start = stream.Position;
        for (; start <= searchEnd; start++)
        {
            if ((start & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            stream.Position = start;
            stream.ReadExactly(bytes);
            if (ReadMpegHeader(bytes) is not { } found || start + found.Bytes > stream.Length) continue;
            // A second valid header avoids locking on a sync-like byte sequence before the actual audio.
            if (start + found.Bytes + bytes.Length <= stream.Length)
            {
                stream.Position = start + found.Bytes;
                stream.ReadExactly(bytes);
                if (ReadMpegHeader(bytes) is not { } next || next.Rate != found.Rate)
                    continue;
            }
            first = found;
            break;
        }
        if (first.Rate == 0) return null;

        stream.Position = start;
        var gapless = ReadMp3Gapless(stream);
        var checkpoints = new List<long>();
        long frames = 0;
        for (long offset = start; offset <= stream.Length - bytes.Length;)
        {
            if ((frames % Mp3IndexStride) == 0) cancellation.ThrowIfCancellationRequested();
            stream.Position = offset;
            stream.ReadExactly(bytes);
            if (ReadMpegHeader(bytes) is not { } frame || offset + frame.Bytes > stream.Length) break;
            if (frame.Channels != first.Channels || frame.Rate != first.Rate || frame.Samples != first.Samples)
                return null;
            if ((frames % Mp3IndexStride) == 0) checkpoints.Add(offset);
            frames++;
            offset += frame.Bytes;
        }

        long decoded = frames * first.Samples;
        if (decoded == 0) return null;
        long length = decoded;
        int trimStart = 0;
        if (gapless is { } trim && trim.DecodedFrames == decoded && trim.Start < decoded)
        {
            trimStart = trim.Start;
            length = Math.Min(trim.Frames, decoded - trimStart);
        }

        long[] index = checkpoints.ToArray();
        return new(first.Channels, first.Rate, length,
            () => new Mp3Frames(path, first, index, trimStart, length));
    }

    private static MpegHeader? ReadMpegHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes[0] != 0xff || (bytes[1] & 0xe0) != 0xe0) return null;
        int version = (bytes[1] >> 3) & 3;
        int layer = (bytes[1] >> 1) & 3;
        int bitRateIndex = bytes[2] >> 4;
        int rateIndex = (bytes[2] >> 2) & 3;
        if (version == 1 || layer == 0 || bitRateIndex is 0 or 15 || rateIndex == 3) return null;
        int[] bitRates = (version, layer) switch
        {
            (3, 3) => Mpeg1Layer1, (3, 2) => Mpeg1Layer2, (3, 1) => Mpeg1Layer3,
            (_, 3) => Mpeg2Layer1, _ => Mpeg2Other,
        };
        int sampleRate = Mpeg1Rates[rateIndex] >> (version == 3 ? 0 : version == 2 ? 1 : 2);
        int samples = layer == 3 ? 384 : layer == 1 && version != 3 ? 576 : 1152;
        int padding = (bytes[2] >> 1) & 1;
        int slot = layer == 3 ? 4 : 1;
        int frameSize = (samples / (8 * slot) * bitRates[bitRateIndex] * 1000 / sampleRate + padding) * slot;
        return new(sampleRate, (bytes[3] & 0xc0) == 0xc0 ? 1 : 2, samples, frameSize);
    }

    private static int MissingPrerollSamples(Stream stream, long offset)
    {
        Span<byte> bits = stackalloc byte[4];
        int reservoir = 0, missing = 0;
        while (offset <= stream.Length - bits.Length)
        {
            stream.Position = offset;
            stream.ReadExactly(bits);
            if (((bits[1] >> 1) & 3) != 1 || ReadMpegHeader(bits) is not { } frame) break;
            bool mpeg1 = ((bits[1] >> 3) & 3) == 3;
            int sideBytes = mpeg1 ? (frame.Channels == 1 ? 17 : 32) : (frame.Channels == 1 ? 9 : 17);
            int overhead = 4 + ((bits[1] & 1) == 0 ? 2 : 0);
            if (frame.Bytes < overhead + sideBytes || offset + frame.Bytes > stream.Length) break;
            stream.Position = offset + overhead;
            stream.ReadExactly(bits[..2]);
            int required = mpeg1 ? (bits[0] << 1) | (bits[1] >> 7) : bits[0];
            if (required <= reservoir) break;
            // MP3Sharp consumes these MPEG frames without emitting PCM while the reservoir fills.
            missing += frame.Samples;
            reservoir += frame.Bytes - overhead - sideBytes;
            offset += frame.Bytes;
        }
        return missing;
    }

    private sealed class Mp3Frames(string path, MpegHeader header, long[] index, int trim, long length)
        : AudioFrameReader
    {
        // MP3Sharp outputs four bytes per frame, even for mono.
        private const int FrameBytes = 4;
        private readonly byte[] _buffer = new byte[8192 * FrameBytes];
        private MP3Stream? _decoder;
        private long _position;
        private long _rawPosition;
        public override long Position => _position;

        public override void Seek(long frame)
        {
            _position = Math.Clamp(frame, 0, length);
            if (_position == length) return;
            long target = _position + trim;
            long current = _rawPosition;
            // Decode a short preroll for the bit reservoir and synthesis filters after random seeks.
            long checkpoint = Math.Max(0, target / header.Samples - Mp3PrerollFrames) / Mp3IndexStride;
            if (_decoder is null || target < current || target - current > 2L * header.Rate)
            {
                _decoder?.Dispose();
                _decoder = null;
                var stream = File.OpenRead(path);
                try
                {
                    int missing = 0;
                    while (checkpoint > 0)
                    {
                        missing = MissingPrerollSamples(stream, index[(int)checkpoint]);
                        if (checkpoint * Mp3IndexStride * header.Samples + missing + 3L * header.Samples <= target)
                            break;
                        checkpoint--;
                    }
                    if (checkpoint == 0) missing = 0;
                    stream.Position = index[(int)checkpoint];
                    _decoder = new MP3Stream(stream);
                    _rawPosition = checkpoint * Mp3IndexStride * header.Samples + missing;
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            while (_rawPosition < target)
            {
                int count = (int)Math.Min(target - _rawPosition, _buffer.Length / FrameBytes);
                if (ReadRaw(count) == 0) break;
            }
        }

        public override int Read(Span<short> samples)
        {
            int frames = (int)Math.Min(samples.Length / header.Channels, length - _position);
            if (frames == 0) return 0;
            if (_decoder is null) Seek(_position);
            int written = 0;
            while (written < frames)
            {
                int read = ReadRaw(Math.Min(frames - written, _buffer.Length / FrameBytes));
                if (read == 0) break;
                var block = _buffer.AsSpan(0, read * FrameBytes);
                var output = samples.Slice(written * header.Channels, read * header.Channels);
                for (int i = 0; i < read; i++)
                {
                    output[i * header.Channels] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(block[(i * FrameBytes)..]);
                    if (header.Channels == 2)
                        output[i * 2 + 1] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(block[(i * FrameBytes + 2)..]);
                }
                written += read;
            }
            _position += written;
            return written;
        }

        private int ReadRaw(int frames)
        {
            int read = _decoder!.Read(_buffer, 0, frames * FrameBytes);
            if (read % FrameBytes != 0 || _decoder.ChannelCount != header.Channels || _decoder.Frequency != header.Rate)
                throw new InvalidDataException("MP3 audio format changed inside the stream.");
            int decoded = read / FrameBytes;
            _rawPosition += decoded;
            return decoded;
        }

        public override void Dispose() => _decoder?.Dispose();
    }
}
