using System.Buffers.Binary;

namespace Thrust.Audio;

public static partial class AudioDecoder
{
    private static DecodedAudio? DecodeAiff(ReadOnlySpan<byte> file)
    {
        if (file.Length < 12 || !file[..4].SequenceEqual("FORM"u8)) return null;
        uint formSize = BinaryPrimitives.ReadUInt32BigEndian(file[4..]);
        if (formSize < 4 || formSize > file.Length - 8) return null;

        bool aifc = file[8..12].SequenceEqual("AIFC"u8);
        if (!aifc && !file[8..12].SequenceEqual("AIFF"u8)) return null;
        ReadOnlySpan<byte> chunks = file.Slice(12, (int)formSize - 4);
        ReadOnlySpan<byte> common = default;
        ReadOnlySpan<byte> sound = default;
        while (chunks.Length >= 8)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(chunks[4..]);
            if (size > chunks.Length - 8) return null;
            var contents = chunks.Slice(8, (int)size);
            if (chunks[..4].SequenceEqual("COMM"u8)) common = contents;
            if (chunks[..4].SequenceEqual("SSND"u8)) sound = contents;
            int end = 8 + (int)size;
            chunks = chunks[(end < chunks.Length ? end + ((int)size & 1) : end)..];
        }

        if (common.Length < (aifc ? 22 : 18) || sound.Length < 8) return null;
        int channels = BinaryPrimitives.ReadUInt16BigEndian(common);
        uint frames = BinaryPrimitives.ReadUInt32BigEndian(common[2..]);
        int bits = BinaryPrimitives.ReadUInt16BigEndian(common[6..]);
        int rate = ReadAiffSampleRate(common[8..18]);
        uint offset = BinaryPrimitives.ReadUInt32BigEndian(sound);
        if (channels is not (1 or 2) || frames == 0 || rate == 0 || offset > sound.Length - 8) return null;
        sound = sound[(8 + (int)offset)..];

        ReadOnlySpan<byte> format = aifc ? common[18..22] : "NONE"u8;
        bool littleEndian = format.SequenceEqual("sowt"u8);
        bool float32 = format.SequenceEqual("fl32"u8) || format.SequenceEqual("FL32"u8);
        bool float64 = format.SequenceEqual("fl64"u8) || format.SequenceEqual("FL64"u8);
        bool integer = littleEndian || format.SequenceEqual("NONE"u8) || format.SequenceEqual("twos"u8);
        if (!(integer && bits is > 0 and <= 32 || float32 && bits == 32 || float64 && bits == 64)) return null;

        int width = (bits + 7) / 8;
        long samples = (long)frames * channels;
        if (samples * width > sound.Length || samples * sizeof(short) > Array.MaxLength) return null;
        var pcm = new byte[(int)samples * sizeof(short)];
        for (int i = 0, position = 0; i < samples; i++, position += width)
        {
            var sample = sound.Slice(position, width);
            short value;
            if (float32) value = FloatToPcm16(BinaryPrimitives.ReadSingleBigEndian(sample));
            else if (float64) value = FloatToPcm16(BinaryPrimitives.ReadDoubleBigEndian(sample));
            else if (width == 1) value = (short)(sample[0] << 8);
            // AIFF integer samples are signed and left-aligned in the stored sample width.
            else if (littleEndian) value = BinaryPrimitives.ReadInt16LittleEndian(sample[^2..]);
            else value = BinaryPrimitives.ReadInt16BigEndian(sample);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * sizeof(short)), value);
        }
        return new DecodedAudio { Pcm = pcm, Channels = channels, SampleRate = rate };
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
