using NVorbis;
using OpenTK.Audio.OpenAL;

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

    private byte[] _pcm = [];
    private Func<AudioFrameReader>? _open;
    private readonly long _frames;

    public DecodedAudio() { }

    internal DecodedAudio(int channels, int sampleRate, long frames, Func<AudioFrameReader> open)
    {
        Channels = channels;
        SampleRate = sampleRate;
        _frames = frames;
        _open = open;
    }

    /// <summary>Obtains whole-track PCM for callers that explicitly require it.</summary>
    public byte[] Pcm
    {
        get => _open is null ? _pcm : ReadPcm();
        set
        {
            _pcm = value;
            _open = null;
        }
    }

    public bool IsStreamed => _open is not null;
    public long FrameCount => _open is not null ? _frames : Channels > 0 ? _pcm.Length / (Channels * sizeof(short)) : 0;
    public AudioFrameReader OpenReader() => _open?.Invoke() ?? new AudioFrameReader.MemoryReader(_pcm, Channels);

    public byte[] ReadPcm(CancellationToken cancellation = default)
    {
        if (_open is null) return _pcm;
        using var reader = OpenReader();
        return AudioDecoder.ReadAll(reader, Channels, SampleRate, FrameCount, cancellation).Pcm;
    }
    public int Channels = 2;
    public int SampleRate = TargetSampleRate;

    public double Duration => SampleRate > 0 ? FrameCount / (double)SampleRate : 0.0;

    public ALFormat Format => Channels == 1 ? ALFormat.Mono16 : ALFormat.Stereo16;
}

/// <summary>
/// Cross-platform PCM decoder for WAV, Ogg Vorbis, MP3, and AIFF.
/// </summary>
public static partial class AudioDecoder
{


    /// <summary>支持的文件扩展名 / Supported file extensions.</summary>
    public static readonly string[] SupportedExtensions = [".wav", ".ogg", ".mp3", ".aif", ".aiff", ".aifc"];

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

        DecodedAudio? decoded = DecodeFile(path);
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

    private static DecodedAudio? DecodeFile(string path)
    {
        try
        {
            if (Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
                return DecodeMp3(File.OpenRead(path));

            var audio = OpenStream(path);
            return audio is null ? null : new()
            {
                Channels = audio.Channels,
                SampleRate = audio.SampleRate,
                Pcm = audio.ReadPcm(),
            };
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[Audio] {path}: {error.Message}");
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
                ".mp3" => DecodeMp3(new MemoryStream(bytes, writable: false)),
                ".aif" or ".aiff" or ".aifc" => DecodeAiff(bytes),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Audio] 解码失败: {ex.Message}");
            return null;
        }
    }

    private static DecodedAudio DecodeOgg(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var vorbis = new VorbisReader(stream, closeOnDispose: false);
        long frames = vorbis.TotalSamples;
        using var reader = new VorbisFrames(vorbis, frames);
        return ReadAll(reader, vorbis.Channels, vorbis.SampleRate, frames);
    }

    private const ushort WavePcm = 1;
    private const ushort WaveIeeeFloat = 3;
    private const ushort WaveExtensible = 0xFFFE;
    private static readonly Guid WavePcmSubtype = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid WaveFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    private static DecodedAudio? DecodeWav(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        if (ReadWaveLayout(stream) is not { } layout) return null;
        using var reader = new PcmFrames(stream, layout);
        return ReadAll(reader, layout.Channels, layout.Rate, layout.Frames);
    }

    private static PcmLayout? ReadWaveLayout(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        if (stream.Length < 44 || reader.ReadUInt32() != 0x46464952) return null; // RIFF
        reader.ReadUInt32();
        if (reader.ReadUInt32() != 0x45564157) return null; // WAVE

        int channels = 0, rate = 0, align = 0, bits = 0;
        ushort format = 0;
        long offset = 0, length = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            uint chunk = reader.ReadUInt32();
            uint size = reader.ReadUInt32();
            long start = stream.Position;
            if (size > stream.Length - start) break;
            if (chunk == 0x20746D66) // fmt
            {
                if (size < 16) return null;
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                rate = reader.ReadInt32();
                reader.ReadUInt32();
                align = reader.ReadUInt16();
                bits = reader.ReadUInt16();
                if (format == WaveExtensible)
                {
                    if (size < 40) return null;
                    int extension = reader.ReadUInt16();
                    if (extension < 22 || extension > size - 18) return null;
                    stream.Position = start + 24;
                    var subtype = new Guid(reader.ReadBytes(16));
                    format = subtype == WavePcmSubtype ? WavePcm :
                        subtype == WaveFloatSubtype ? WaveIeeeFloat : WaveExtensible;
                }
                if (format is not (WavePcm or WaveIeeeFloat)) return null;
            }
            else if (chunk == 0x61746164) // data
            {
                offset = start;
                length = size;
            }
            stream.Position = start + size + (size & 1);
        }

        int width = bits / 8;
        if (channels <= 0 || rate <= 0 || length == 0 || align != channels * width || align == 0 ||
            length % align != 0)
            return null;
        var encoding = format switch
        {
            WaveIeeeFloat when bits == 32 && channels <= 2 => PcmEncoding.FloatLittle,
            WavePcm when bits == 8 => PcmEncoding.Unsigned8,
            WavePcm when bits is 16 or 24 or 32 => PcmEncoding.IntegerLittle,
            _ => (PcmEncoding?)null,
        };
        return encoding is { } value ? new(offset, length / align, channels, rate, width, value) : null;
    }


    private static short FloatToPcm16(double value) => double.IsNaN(value) ? (short)0 : (short)Math.Clamp(
        Math.Round(Math.Clamp(value, -1d, 1d) * 32768d), short.MinValue, short.MaxValue);
}
