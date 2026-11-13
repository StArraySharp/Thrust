using OpenTK.Audio.OpenAL;

namespace Thrust.Audio;

/// <summary>
/// OpenAL 音频上下文：整个进程共用一个设备和一个上下文
/// OpenAL audio context; the whole process shares one device and one context.
/// </summary>
public static class AudioEngine
{
    private static ALDevice _device;
    private static ALContext _context;
    private static bool _initialized;

    /// <summary>初始化 OpenAL（进程内只需一次） / Initialize OpenAL once per process.</summary>
    public static bool Initialize()
    {
        if (_initialized)
        {
            return true;
        }

        try
        {
            _device = ALC.OpenDevice(null);
            if (_device.Handle == IntPtr.Zero)
            {
                Console.Error.WriteLine("[Audio] 无法打开 OpenAL 设备");
                return false;
            }

            _context = ALC.CreateContext(_device, (int[]?)null);
            if (_context.Handle == IntPtr.Zero)
            {
                ALC.CloseDevice(_device);
                Console.Error.WriteLine("[Audio] 无法创建 OpenAL 上下文");
                return false;
            }

            if (!ALC.MakeContextCurrent(_context))
            {
                ALC.DestroyContext(_context);
                ALC.CloseDevice(_device);
                Console.Error.WriteLine("[Audio] 无法激活 OpenAL 上下文");
                return false;
            }

            // 2D 播放：不做距离衰减 / 2D playback: no distance attenuation
            AL.DistanceModel(ALDistanceModel.None);

            // 音频线程独立刷新，减少主线程卡顿的影响
            // Let the mixer keep a steady stream so main-thread hitches matter less
            _initialized = true;
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Audio] OpenAL 初始化失败: {ex.Message}");
            return false;
        }
    }

    public static bool IsInitialized => _initialized;

    /// <summary>关闭 OpenAL / Shut OpenAL down.</summary>
    public static void Shutdown()
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            ALC.MakeContextCurrent(ALContext.Null);
            ALC.DestroyContext(_context);
            ALC.CloseDevice(_device);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Audio] 关闭失败: {ex.Message}");
        }

        _initialized = false;
    }
}

/// <summary>
/// 流式音频播放器：用多个小缓冲循环填充（OpenAL 标准流式做法）
/// Streaming audio player: keeps several small buffers queued, the standard OpenAL streaming approach.
/// </summary>
/// <remarks>
/// 为什么这样能根治卡顿爆音：
/// 1. 不再每帧 Seek（Seek 会打断音频流，产生爆音）
/// 2. 预排队若干缓冲，主线程卡顿时 GPU/音频线程仍能继续消费
/// 3. 每个缓冲只保留几十毫秒数据，内存占用极小
/// Why this fixes the crackling:
/// 1. No per-frame seeking, which cuts the stream and produces clicks
/// 2. Several buffers stay queued so a hiccup on the main thread does not starve the mixer
/// 3. Each buffer holds only tens of milliseconds, so memory use stays tiny
/// </remarks>
public sealed unsafe class AudioClip : IDisposable
{
    /// <summary>缓冲数量：八个共 2 秒，足以覆盖明显的主线程卡顿 / Eight buffers hold 2 s of audio.</summary>
    private const int BufferCount = 8;

    /// <summary>每个缓冲的时长（秒）/ Duration of each queued buffer in seconds.</summary>
    private const double BufferDuration = 0.25;

    private readonly int[] _buffers = new int[BufferCount];
    private readonly bool[] _inUse = new bool[BufferCount];
    private readonly int _source;

    private DecodedAudio? _data;
    private bool _disposed;

    // 音频流内部读取位置（以帧为单位）/ Internal read cursor in frames
    private int _readFrame;

    // 已排队但尚未播放的帧数，用于推算当前位置
    // Frames queued but not yet played, used to derive the current position
    private int _queuedFrames;

    // 用户意图：应该在播放还是暂停 / User intent: should we be playing
    private bool _wantsPlay;
    private float _volume = 1f;

    // 音频时钟起点 / Clock anchor
    private double _clockBase;

    public string Name { get; set; } = "Clip";

    public AudioClip()
    {
        if (!AudioEngine.Initialize())
        {
            _source = 0;
            return;
        }

        _source = AL.GenSource();

        for (int i = 0; i < BufferCount; i++)
        {
            _buffers[i] = AL.GenBuffer();
        }

        AL.Source(_source, ALSourceb.Looping, false);
        ApplyGain();
    }

    /// <summary>音量（0~1，可超过 1 做增益） / Volume in 0..1.</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 4f);
            ApplyGain();
        }
    }

    public bool IsLoaded => _data is not null && _source != 0;

    public double Duration => _data?.Duration ?? 0.0;

    /// <summary>是否正在播放（用户意图） / Whether playback is requested.</summary>
    public bool IsPlaying => _wantsPlay;

    /// <summary>
    /// 当前播放位置：以时钟为准，并从音频流状态做缓慢校正
    /// Playback position taken from the clock, slowly reconciled with the stream state.
    /// </summary>
    public double Position
    {
        get
        {
            if (_data is null)
            {
                return _clockBase;
            }

            if (!_wantsPlay)
            {
                return _clockBase;
            }

            // 已播放帧数 = 已入队的绝对游标 - 队列中尚未播放的帧数
            // _readFrame 是相对整段音频的绝对游标（Seek 时会重设），若改用 _queuedFrames
            // 会得到"滚动窗口"计数，永远不超过队列总长，位置就会卡在两秒以内。
            // Frames played = absolute read cursor - frames still pending in the queue.
            // _readFrame is absolute within the clip and reset by Seek; using _queuedFrames here
            // would give a rolling-window count that never exceeds the queue length, pinning the
            // reported position a couple of seconds in.
            int pending = 0;
            if (_source != 0)
            {
                AL.GetSource(_source, ALGetSourcei.BuffersQueued, out pending);
            }

            int bufferFrames = FramesPerBuffer();
            int stillQueued = Math.Max(0, pending) * bufferFrames;

            // 当前缓冲内已经播掉的偏移要补回来
            // Add back the offset already played inside the current buffer
            int consumed = 0;
            AL.GetSource(_source, ALGetSourcei.SampleOffset, out consumed);

            int playedFrames = _readFrame - stillQueued + consumed;
            double seconds = playedFrames / (double)SampleRate();

            return Math.Clamp(seconds, 0.0, Duration);
        }
    }

    /// <summary>加载音频文件（WAV / OGG），不立即播放 / Load a file without starting playback.</summary>
    public bool Load(string path)
    {
        DecodedAudio? data = AudioDecoder.Decode(path);
        if (data is null || data.Pcm.Length == 0)
        {
            return false;
        }

        return LoadDecoded(data);
    }

    /// <summary>直接加载已解码的 PCM（用于运行时合成的音轨）/ Load already decoded PCM, e.g. a synthesized track.</summary>
    public bool LoadDecoded(DecodedAudio data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (_source == 0)
        {
            return false;
        }

        Stop();
        _data = data;
        _readFrame = 0;
        _queuedFrames = 0;
        _clockBase = 0.0;
        return true;
    }

    /// <summary>开始（或继续）播放 / Start or resume playback.</summary>
    public void Play()
    {
        if (_data is null || _source == 0)
        {
            return;
        }

        if (_wantsPlay)
        {
            return;
        }

        _wantsPlay = true;
        _clockBase = GetStreamPosition();
        Refill();
    }

    /// <summary>暂停播放，位置保持不变 / Pause while keeping the position.</summary>
    public void Pause()
    {
        if (!_wantsPlay)
        {
            return;
        }

        _clockBase = Position;
        _wantsPlay = false;

        if (_source != 0)
        {
            AL.SourcePause(_source);
        }
    }

    /// <summary>停止并回到开头 / Stop and rewind to the start.</summary>
    public void Stop()
    {
        _wantsPlay = false;
        _clockBase = 0.0;
        _readFrame = 0;
        _queuedFrames = 0;

        if (_source == 0)
        {
            return;
        }

        AL.SourceStop(_source);
        UnqueueAll();
        AL.Source(_source, ALSourcei.SampleOffset, 0);
    }

    /// <summary>跳转到指定时间 / Seek to a time in seconds.</summary>
    public void Seek(double seconds)
    {
        if (_data is null)
        {
            return;
        }

        double clamped = Math.Clamp(seconds, 0.0, Duration);
        bool wasPlaying = _wantsPlay;

        _clockBase = clamped;
        _readFrame = (int)(clamped * SampleRate());

        if (_source == 0)
        {
            return;
        }

        // 清空队列后从新位置重新填充，这是唯一会产生间隙的地方（用户主动 seek 时可接受）
        // Clear the queue and refill from the new position; this is the only place that can gap,
        // and only for explicit seeks.
        AL.SourceStop(_source);
        UnqueueAll();
        _queuedFrames = 0;
        _wantsPlay = wasPlaying;

        if (wasPlaying)
        {
            Refill();
        }
    }

    /// <summary>
    /// 每帧调用：补足队列并处理播放结束。
    /// 一次只补一个缓冲，并保证补完后队列里始终有数据，避免 underrun 造成的爆音。
    /// Call every frame to top up the queue and detect the end of the stream. Refill one
    /// buffer at a time and always keep the queue non-empty so underruns cannot click.
    /// </summary>
    public void Update()
    {
        if (_data is null || _source == 0 || !_wantsPlay)
        {
            return;
        }

        // 回收到期的缓冲 / Reclaim finished buffers
        AL.GetSource(_source, ALGetSourcei.BuffersProcessed, out int processed);
        while (processed-- > 0)
        {
            int buffer = AL.SourceUnqueueBuffer(_source);
            if (buffer == 0)
            {
                break;
            }

            MarkInUse(buffer, false);
            _queuedFrames = Math.Max(0, _queuedFrames - FramesPerBuffer());

            // 能补就立刻补回去，避免队列变短 / Refill right away so the queue never shrinks
            QueueBuffer(buffer);
        }

        // 循环补齐空闲缓冲 / Fill any remaining free buffers
        while (CountQueued() < BufferCount && HasFreeBuffer(out int free))
        {
            if (!QueueBuffer(free))
            {
                break;
            }
        }

        // 队列被排空过就确保源在播放 / Make sure the source keeps running
        AL.GetSource(_source, ALGetSourcei.SourceState, out int state);
        bool sourceStopped = (ALSourceState)state != ALSourceState.Playing;

        if (sourceStopped && HasQueuedBuffers())
        {
            AL.SourcePlay(_source);
        }

        // 只有「数据全读完 + 队列已空 + 源确实停下来了」才算播放结束
        // Only treat the stream as finished when the data is exhausted, the queue is empty
        // and the source really stopped
        if (_readFrame >= TotalFrames() && !HasQueuedBuffers() && sourceStopped)
        {
            _wantsPlay = false;
            _clockBase = Duration;
        }
    }

    /// <summary>把剩余数据一次性排入队列 / Push remaining buffers into the queue.</summary>
    private void Refill()
    {
        // 一直填到队列满或数据读完 / Fill until the queue is full or the data runs out
        while (CountQueued() < BufferCount)
        {
            if (!HasFreeBuffer(out int buffer))
            {
                break;
            }

            if (!QueueBuffer(buffer))
            {
                break;
            }
        }

        if (HasQueuedBuffers())
        {
            AL.SourcePlay(_source);
        }
    }

    private bool HasFreeBuffer(out int buffer)
    {
        buffer = 0;
        for (int i = 0; i < BufferCount; i++)
        {
            if (!_inUse[i])
            {
                buffer = _buffers[i];
                return true;
            }
        }

        return false;
    }

    /// <summary>把下一段 PCM 填入缓冲并排队 / Upload the next chunk and queue it.</summary>
    private bool QueueBuffer(int buffer)
    {
        if (_data is null)
        {
            return false;
        }

        int sampleRate = SampleRate();
        int channels = _data.Channels;
        int frameBytes = channels * sizeof(short);
        int framesPerBuffer = FramesPerBuffer();

        int remaining = TotalFrames() - _readFrame;
        if (remaining <= 0)
        {
            return false;
        }

        int frames = Math.Min(framesPerBuffer, remaining);
        int byteOffset = _readFrame * frameBytes;
        int byteCount = frames * frameBytes;

        fixed (byte* pcm = _data.Pcm)
        {
            AL.BufferData(buffer, _data.Format, (nint)(pcm + byteOffset), byteCount, sampleRate);
        }

        AL.SourceQueueBuffer(_source, buffer);
        MarkInUse(buffer, true);

        _readFrame += frames;
        _queuedFrames += frames;
        return true;
    }

    private void MarkInUse(int buffer, bool inUse)
    {
        for (int i = 0; i < BufferCount; i++)
        {
            if (_buffers[i] == buffer)
            {
                _inUse[i] = inUse;
                return;
            }
        }
    }

    private void UnqueueAll()
    {
        if (_source == 0)
        {
            return;
        }

        AL.GetSource(_source, ALGetSourcei.BuffersQueued, out int queued);
        while (queued-- > 0)
        {
            int buffer = AL.SourceUnqueueBuffer(_source);
            if (buffer == 0)
            {
                break;
            }

            MarkInUse(buffer, false);
        }
    }

    private bool HasQueuedBuffers() => CountQueued() > 0;

    private int CountQueued()
    {
        if (_source == 0)
        {
            return 0;
        }

        AL.GetSource(_source, ALGetSourcei.BuffersQueued, out int queued);
        return Math.Max(0, queued);
    }

    private int SampleRate() => _data?.SampleRate ?? DecodedAudio.TargetSampleRate;

    private int FramesPerBuffer() => Math.Max(1, (int)(SampleRate() * BufferDuration));

    private int TotalFrames()
    {
        if (_data is null)
        {
            return 0;
        }

        return _data.Pcm.Length / (_data.Channels * sizeof(short));
    }

    private double GetStreamPosition() => _clockBase;

    private void ApplyGain()
    {
        if (_source != 0)
        {
            AL.Source(_source, ALSourcef.Gain, _volume);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_source != 0)
        {
            AL.SourceStop(_source);
            UnqueueAll();
            AL.DeleteSource(_source);
        }

        for (int i = 0; i < BufferCount; i++)
        {
            if (_buffers[i] != 0)
            {
                AL.DeleteBuffer(_buffers[i]);
                _buffers[i] = 0;
            }
        }

        _data = null;
        _disposed = true;
    }
}
