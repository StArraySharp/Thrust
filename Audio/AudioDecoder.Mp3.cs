using System.Buffers.Binary;
using MP3Sharp;

namespace Thrust.Audio;

public static partial class AudioDecoder
{
    private static DecodedAudio? DecodeMp3(Stream source)
    {
        using var input = source;
        SkipMp3Id3(input);
        var gapless = ReadMp3Gapless(input);
        using var reader = new MP3Stream(input);
        int channels = reader.ChannelCount;
        int rate = reader.Frequency;
        if (channels is not (1 or 2) || rate <= 0) return null;

        // MP3Sharp 1.0.5 always emits four-byte stereo frames. Mono occupies only the left lane.
        const int decoderFrameSize = 2 * sizeof(short);
        byte[] buffer = new byte[32 * 1024];
        using var pcm = new MemoryStream();
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (read % decoderFrameSize != 0 || reader.ChannelCount != channels || reader.Frequency != rate)
                return null;
            if (channels == 1)
            {
                for (int from = 0, to = 0; from < read; from += decoderFrameSize, to += sizeof(short))
                {
                    buffer[to] = buffer[from];
                    buffer[to + 1] = buffer[from + 1];
                }
                read /= 2;
            }
            pcm.Write(buffer, 0, read);
        }
        if (pcm.Length == 0) return null;

        int frameSize = channels * sizeof(short);
        var samples = pcm.GetBuffer().AsSpan(0, (int)pcm.Length);
        if (gapless is { } trim && pcm.Length / frameSize == trim.DecodedFrames && trim.Start < trim.DecodedFrames)
        {
            int frames = (int)Math.Min(trim.Frames, trim.DecodedFrames - trim.Start);
            samples = samples.Slice(trim.Start * frameSize, frames * frameSize);
        }
        return new DecodedAudio { Pcm = samples.ToArray(), Channels = channels, SampleRate = rate };
    }

    private static void SkipMp3Id3(Stream input)
    {
        long start = input.Position;
        Span<byte> tag = stackalloc byte[10];
        if (input.ReadAtLeast(tag, tag.Length, false) == tag.Length && tag[..3].SequenceEqual("ID3"u8) &&
            tag[3] is >= 2 and <= 4 && ((tag[6] | tag[7] | tag[8] | tag[9]) & 0x80) == 0)
        {
            int size = (tag[6] << 21) | (tag[7] << 14) | (tag[8] << 7) | tag[9];
            int footer = tag[3] == 4 && (tag[5] & 0x10) != 0 ? 10 : 0;
            long firstFrame = start + tag.Length + size + footer;
            if (firstFrame < input.Length)
            {
                // Avoid searching album art and other ID3 data for MPEG sync words.
                input.Position = firstFrame;
                return;
            }
        }
        input.Position = start;
    }

    private static (long DecodedFrames, int Start, long Frames)? ReadMp3Gapless(Stream input)
    {
        long start = input.Position;
        try
        {
            Span<byte> buffer = stackalloc byte[192];
            ReadOnlySpan<byte> frame = buffer[..input.ReadAtLeast(buffer, buffer.Length, false)];
            if (frame.Length < 4 || frame[0] != 0xff || (frame[1] & 0xe6) != 0xe2) return null;
            int version = (frame[1] >> 3) & 3;
            if (version == 1) return null;
            bool mpeg1 = version == 3;
            bool mono = (frame[3] & 0xc0) == 0xc0;
            // Xing/Info follows the MPEG header and side information in the first frame.
            int xing = 4 + (mpeg1 ? (mono ? 17 : 32) : (mono ? 9 : 17));
            if (frame.Length < xing + 12) return null;
            var marker = frame.Slice(xing, 4);
            if (!marker.SequenceEqual("Xing"u8) && !marker.SequenceEqual("Info"u8)) return null;
            uint fields = BinaryPrimitives.ReadUInt32BigEndian(frame[(xing + 4)..]);
            if ((fields & 1) == 0 || (fields & ~0xfu) != 0) return null;
            uint frameCount = BinaryPrimitives.ReadUInt32BigEndian(frame[(xing + 8)..]);
            int encoder = xing + 12;
            if ((fields & 2) != 0) encoder += 4;
            if ((fields & 4) != 0) encoder += 100;
            if ((fields & 8) != 0) encoder += 4;
            if (frame.Length < encoder + 24) return null;
            var name = frame.Slice(encoder, 4);
            if (!name.SequenceEqual("LAME"u8) && !name.SequenceEqual("Lavc"u8) && !name.SequenceEqual("Lavf"u8)) return null;

            var padding = frame[(encoder + 21)..(encoder + 24)];
            int encoderDelay = (padding[0] << 4) | (padding[1] >> 4);
            int tail = ((padding[1] & 0xf) << 8) | padding[2];
            if (encoderDelay == 0 && tail == 0) return null;
            int samplesPerFrame = mpeg1 ? 1152 : 576;
            long samples = (long)frameCount * samplesPerFrame;
            long frames = samples - encoderDelay - tail;
            if (frames <= 0) return null;
            // MP3Sharp decodes the Xing frame as well as the Layer III filter-bank priming samples.
            const int decoderDelay = 529;
            return (samples + samplesPerFrame, samplesPerFrame + encoderDelay + decoderDelay, frames);
        }
        finally
        {
            input.Position = start;
        }
    }
}
