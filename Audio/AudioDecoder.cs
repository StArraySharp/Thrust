using NVorbis;
using OpenTK.Audio.OpenAL;
using System.Buffers.Binary;

namespace Thrust.Audio;

/// <summary>
/// 解码后的 PCM 音频数据（16 位交错，保留源采样率）
/// Decoded PCM audio: 16-bit interleaved, keeping the source sample rate.
/// </summary>
public sealed class DecodedAudio
{
    /// <summary>
    /// 缺省/兜底采样率 / Default and fallback sample rate.
    /// </summary>
    /// <remarks>
    /// 各音轨保留自己的采样率，<see cref="AudioClip"/> 原样交给 <c>AL.BufferData</c>，
    /// OpenAL 按该率播放，无需重采样。
    /// Clips keep their own rate; AudioClip hands it to AL.BufferData, so OpenAL plays it correctly
    /// without resampling.
    /// </remarks>
    public const int TargetSampleRate = 48000;

    public byte[] Pcm = [];
    public int Channels = 2;
    public int SampleRate = TargetSampleRate;

    public double Duration => SampleRate > 0 && Channels > 0
        ? Pcm.Length / (double)(SampleRate * Channels * sizeof(short))
        : 0.0;

    public ALFormat Format => Channels == 1 ? ALFormat.Mono16 : ALFormat.Stereo16;
}

/// <summary>
/// 跨平台音频解码器：WAV（内置解析）+ OGG Vorbis（NVorbis，纯托管）
/// Cross-platform audio decoder: WAV (built-in parser) plus OGG Vorbis through the pure managed NVorbis.
/// </summary>
public static class AudioDecoder
{
    private const int TargetSampleRate = 44100;

    /// <summary>支持的文件扩展名 / Supported file extensions.</summary>
    public static readonly string[] SupportedExtensions = [".wav", ".ogg"];

    // 按文件路径缓存解码结果，避免重复解码同一文件
    // Cache decoded audio per path so the same file is not decoded twice
    private static readonly Dictionary<string, DecodedAudio> Cache = [];
    private static readonly Lock CacheLock = new();

    /// <summary>清空解码缓存 / Clear the decode cache.</summary>
    public static void ClearCache()
    {
        lock (CacheLock)
        {
            Cache.Clear();
        }
    }

    /// <summary>当前缓存的音频条目数 / Number of cached audio entries.</summary>
    public static int CacheCount
    {
        get
        {
            lock (CacheLock)
            {
                return Cache.Count;
            }
        }
    }

    /// <summary>判断文件是否可解码 / Whether the file can be decoded.</summary>
    public static bool IsSupported(string path)
    {
        string extension = Path.GetExtension(path);
        return SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>解码音频文件（带缓存）/ Decode an audio file, with caching.</summary>
    public static DecodedAudio? Decode(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string key = Path.GetFullPath(path);

        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out DecodedAudio? cached))
            {
                return cached;
            }
        }

        DecodedAudio? decoded = DecodeUncached(path, fileExtension: Path.GetExtension(path));
        if (decoded is null)
        {
            return null;
        }

        lock (CacheLock)
        {
            Cache[key] = decoded;
        }

        return decoded;
    }

    /// <summary>
    /// 从内存字节解码（用于嵌入式资源）。会按 key 缓存。
    /// Decode from an in-memory buffer, used for embedded resources; results are cached by key.
    /// </summary>
    public static DecodedAudio? Decode(byte[] data, string key, string extension)
    {
        if (data.Length == 0)
        {
            return null;
        }

        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out DecodedAudio? cached))
            {
                return cached;
            }
        }

        DecodedAudio? decoded = DecodeUncached(data, extension);
        if (decoded is null)
        {
            return null;
        }

        lock (CacheLock)
        {
            Cache[key] = decoded;
        }

        return decoded;
    }

    private static DecodedAudio? DecodeUncached(string path, string fileExtension)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            return DecodeUncached(bytes, fileExtension);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Audio] 解码失败 {path}: {ex.Message}");
            return null;
        }
    }

    private static DecodedAudio? DecodeUncached(byte[] bytes, string fileExtension)
    {
        try
        {
            return fileExtension.ToLowerInvariant() switch
            {
                ".ogg" => DecodeOgg(bytes),
                ".wav" => DecodeWav(bytes),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Audio] 解码失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>用 NVorbis 解码 OGG Vorbis / Decode OGG Vorbis with NVorbis.</summary>
    private static unsafe DecodedAudio DecodeOgg(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        using VorbisReader reader = new(stream, closeOnDispose: false);

        int channels = reader.Channels;
        int sampleRate = reader.SampleRate;

        // 预估容量后一次性分配，解出的浮点直接写进最终 16 位缓冲，省掉中间数组
        // Size the buffer up front and write the decoded floats straight into the final 16-bit
        // buffer, so no intermediate array is needed
        long estimatedFrames = (long)(reader.TotalTime.TotalSeconds * sampleRate);
        int initialSamples = estimatedFrames is > 0 and < int.MaxValue / 8
            ? (int)estimatedFrames * channels
            : sampleRate * 60 * channels;

        byte[] pcm = new byte[initialSamples * sizeof(short)];
        float[] buffer = new float[sampleRate * channels];
        int samplesWritten = 0;

        int read;
        while ((read = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
        {
            if ((long)(samplesWritten + read) * sizeof(short) > pcm.Length)
            {
                // 预估偏小（VBR 常见），按需翻倍
                // The estimate was low (common with VBR), so grow geometrically as needed
                long needed = (long)(samplesWritten + read) * sizeof(short);
                long capacity = pcm.Length;
                while (capacity < needed)
                {
                    capacity *= 2;
                }

                Array.Resize(ref pcm, (int)Math.Min(capacity, int.MaxValue / 2L));
            }

            // 每批只 pin 一次，而不是逐样本走 Span / Pin once per batch instead of per sample
            fixed (byte* destination = pcm)
            {
                short* output = (short*)destination + samplesWritten;
                for (int i = 0; i < read; i++)
                {
                    float value = buffer[i];
                    if (value > 1f)
                    {
                        value = 1f;
                    }
                    else if (value < -1f)
                    {
                        value = -1f;
                    }

                    output[i] = (short)MathF.Round(value * short.MaxValue);
                }
            }

            samplesWritten += read;
        }

        if (samplesWritten == 0)
        {
            return new DecodedAudio
            {
                Pcm = [],
                Channels = channels,
                SampleRate = sampleRate,
            };
        }

        int usedBytes = samplesWritten * sizeof(short);
        if (usedBytes < pcm.Length)
        {
            Array.Resize(ref pcm, usedBytes);
        }

        // 不重采样：OpenAL 按源采样率播放即可，省掉一次整轨逐样本遍历
        // No resampling: OpenAL plays at the source rate, saving a per-sample pass over the whole track
        return new DecodedAudio
        {
            Pcm = pcm,
            Channels = channels,
            SampleRate = sampleRate,
        };
    }

    private const ushort WavePcm = 1;
    private const ushort WaveIeeeFloat = 3;
    private const ushort WaveExtensible = 0xFFFE;
    private static readonly Guid WavePcmSubtype = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid WaveFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>解析整数 PCM 或 32 位浮点 WAV / Parse integer PCM or 32-bit float WAV.</summary>
    private static DecodedAudio? DecodeWav(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        using BinaryReader reader = new(stream);

        if (stream.Length < 44 || new string(reader.ReadChars(4)) != "RIFF")
        {
            return null;
        }

        reader.ReadInt32(); // riff size

        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            return null;
        }

        int channels = 0;
        int sampleRate = 0;
        int blockAlign = 0;
        int bitsPerSample = 0;
        ushort formatTag = 0;
        ReadOnlySpan<byte> raw = default;

        while (stream.Position + 8 <= stream.Length)
        {
            string chunkId = new(reader.ReadChars(4));
            int chunkSize = reader.ReadInt32();

            if (chunkSize < 0 || stream.Position + chunkSize > stream.Length)
            {
                break;
            }

            long chunkStart = stream.Position;
            if (chunkId == "fmt ")
            {
                if (chunkSize < 16)
                    return null;

                formatTag = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32(); // byte rate
                blockAlign = reader.ReadUInt16();
                bitsPerSample = reader.ReadUInt16();
                if (formatTag == WaveExtensible)
                {
                    if (chunkSize < 40)
                        return null;
                    int extensionSize = reader.ReadUInt16();
                    if (extensionSize < 22 || extensionSize > chunkSize - 18)
                        return null;

                    Guid subtype = new(bytes.AsSpan((int)chunkStart + 24, 16));
                    formatTag = subtype == WavePcmSubtype ? WavePcm :
                        subtype == WaveFloatSubtype ? WaveIeeeFloat : WaveExtensible;
                }
                if (formatTag is not (WavePcm or WaveIeeeFloat))
                {
                    Console.Error.WriteLine($"[Audio] 不支持的 WAV 编码 {formatTag}");
                    return null;
                }
            }
            else if (chunkId == "data")
            {
                raw = bytes.AsSpan((int)chunkStart, chunkSize);
            }

            stream.Position = chunkStart + chunkSize + (chunkSize & 1);
        }

        if (raw.IsEmpty || formatTag == 0 || channels == 0 || sampleRate <= 0)
        {
            return null;
        }

        // 统一转成 16 位 / Normalize to 16-bit
        byte[] pcm16;
        if (formatTag == WaveIeeeFloat)
        {
            if (bitsPerSample != 32 || channels > 2 || blockAlign != channels * sizeof(float) ||
                raw.Length % blockAlign != 0)
                return null;

            int sampleCount = raw.Length / sizeof(float);
            pcm16 = new byte[sampleCount * sizeof(short)];
            for (int i = 0; i < sampleCount; i++)
            {
                float sample = BinaryPrimitives.ReadSingleLittleEndian(raw[(i * sizeof(float))..]);
                short value = float.IsNaN(sample) ? (short)0 : (short)Math.Clamp(
                    MathF.Round(Math.Clamp(sample, -1f, 1f) * 32768f), short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(i * sizeof(short)), value);
            }
        }
        else if (bitsPerSample == 16)
        {
            pcm16 = raw.ToArray();
        }
        else if (bitsPerSample == 8)
        {
            // 8 位 WAV 是无符号，需居中到 0 / 8-bit WAV is unsigned and needs recentering
            pcm16 = new byte[raw.Length * 2];
            for (int i = 0; i < raw.Length; i++)
            {
                short value = (short)((raw[i] - 128) << 8);
                BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(i * 2), value);
            }
        }
        else if (bitsPerSample == 24)
        {
            int sampleCount = raw.Length / 3;
            pcm16 = new byte[sampleCount * 2];
            for (int i = 0; i < sampleCount; i++)
            {
                // 取高 16 位 / Keep the high 16 bits
                short value = (short)(raw[i * 3 + 1] | (raw[i * 3 + 2] << 8));
                BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(i * 2), value);
            }
        }
        else if (bitsPerSample == 32)
        {
            int sampleCount = raw.Length / 4;
            pcm16 = new byte[sampleCount * 2];
            for (int i = 0; i < sampleCount; i++)
            {
                int value = BinaryPrimitives.ReadInt32LittleEndian(raw[(i * 4)..]);
                BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(i * 2), (short)(value >> 16));
            }
        }
        else
        {
            Console.Error.WriteLine($"[Audio] 不支持的 WAV 位深 {bitsPerSample}");
            return null;
        }

        // 统一重采样到目标采样率 / Resample to the target rate
        // 不需要重采样，播放端按源采样率直接播放 / No resampling; the player uses the source rate

        return new DecodedAudio
        {
            Pcm = pcm16,
            Channels = channels,
            SampleRate = sampleRate,
        };
    }
}
