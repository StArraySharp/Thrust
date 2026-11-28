using System.Buffers.Binary;

namespace Thrust.Audio;

public static partial class AudioDecoder
{
    private static DecodedAudio? DecodeAiff(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        if (ReadAiffLayout(stream) is not { } layout) return null;
        using var reader = new PcmFrames(stream, layout);
        return ReadAll(reader, layout.Channels, layout.Rate, layout.Frames);
    }

    private static PcmLayout? ReadAiffLayout(Stream stream)
    {
        Span<byte> header = stackalloc byte[12];
        if (stream.ReadAtLeast(header, header.Length, false) != header.Length || !header[..4].SequenceEqual("FORM"u8))
            return null;
        uint formSize = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        if (formSize < 4 || formSize > stream.Length - 8) return null;
        long formEnd = 8L + formSize;
        bool compressed = header[8..].SequenceEqual("AIFC"u8);
        if (!compressed && !header[8..].SequenceEqual("AIFF"u8)) return null;

        Span<byte> common = stackalloc byte[22];
        long audioOffset = 0, audioSize = 0;
        int commonSize = 0;
        while (stream.Position + 8 <= formEnd)
        {
            stream.ReadExactly(header[..8]);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            long start = stream.Position;
            long end = start + size;
            if (end > formEnd) return null;
            if (header[..4].SequenceEqual("COMM"u8))
            {
                commonSize = (int)Math.Min(size, (uint)common.Length);
                stream.ReadExactly(common[..commonSize]);
            }
            else if (header[..4].SequenceEqual("SSND"u8) && size >= 8)
            {
                stream.ReadExactly(header[..8]);
                uint offset = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (offset > size - 8) return null;
                audioOffset = start + 8 + offset;
                audioSize = size - 8 - offset;
            }
            stream.Position = end + (end < formEnd ? size & 1 : 0);
        }

        if (commonSize < (compressed ? 22 : 18) || audioSize == 0) return null;
        int channels = BinaryPrimitives.ReadUInt16BigEndian(common);
        uint frames = BinaryPrimitives.ReadUInt32BigEndian(common[2..]);
        int bits = BinaryPrimitives.ReadUInt16BigEndian(common[6..]);
        int rate = ReadAiffSampleRate(common[8..18]);
        if (channels is not (1 or 2) || frames == 0 || rate == 0) return null;

        ReadOnlySpan<byte> format = compressed ? common[18..22] : "NONE"u8;
        bool little = format.SequenceEqual("sowt"u8);
        bool integer = little || format.SequenceEqual("NONE"u8) || format.SequenceEqual("twos"u8);
        bool floating = bits == 32 && (format.SequenceEqual("fl32"u8) || format.SequenceEqual("FL32"u8)) ||
            bits == 64 && (format.SequenceEqual("fl64"u8) || format.SequenceEqual("FL64"u8));
        if (!(integer && bits is > 0 and <= 32 || floating)) return null;
        int width = (bits + 7) / 8;
        if ((long)frames * channels * width > audioSize) return null;

        return new(audioOffset, frames, channels, rate, width, floating ? PcmEncoding.FloatBig :
            little ? PcmEncoding.IntegerLittle : PcmEncoding.IntegerBig);
    }


    private static int ReadAiffSampleRate(ReadOnlySpan<byte> data)
    {
        int exponent = BinaryPrimitives.ReadUInt16BigEndian(data);
        ulong significand = BinaryPrimitives.ReadUInt64BigEndian(data[2..]);
        if (exponent is <= 0 or >= 0x7fff || (significand & (1ul << 63)) == 0) return 0;
        // The 80-bit extended format stores the integer significand bit explicitly.
        double rate = Math.Round(Math.ScaleB((double)significand, exponent - 16383 - 63));
        return rate is >= 1d and <= int.MaxValue ? (int)rate : 0;
    }
}
