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

/// <summary>Streams PCM blocks through a bounded queue of OpenAL buffers.</summary>
public sealed unsafe class AudioClip : IDisposable
{
    private const int BufferCount = 8;
    private const double BufferDuration = 0.25;
    private readonly Queue<(int Buffer, int Frames)> _queued = new(BufferCount);
    private readonly Queue<int> _available = new(BufferCount);
    private readonly int[] _buffers = new int[BufferCount];
    private readonly int _source;
    private DecodedAudio? _data;
    private AudioPrefetch? _prefetch;
    private bool _disposed, _failed, _wantsPlay, _endOfData;
    private long _nextFrame;
    private int _queuedFrames;
    private int _sampleRate = DecodedAudio.TargetSampleRate;
    private float _volume = 1f;
    private double _clockBase;

    public string Name { get; set; } = "Clip";

    public AudioClip()
    {
        if (!AudioEngine.Initialize()) return;
        _source = AL.GenSource();
        if (_source == 0) return;
        for (int i = 0; i < BufferCount; i++)
        {
            int buffer = AL.GenBuffer();
            _buffers[i] = buffer;
            if (buffer != 0) _available.Enqueue(buffer);
        }
        AL.Source(_source, ALSourceb.Looping, false);
        ApplyGain();
    }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 4f);
            ApplyGain();
        }
    }

    public bool IsLoaded => !_disposed && !_failed && _data is not null && _source != 0 && _buffers[0] != 0;
    public double Duration => (_data?.FrameCount ?? 0) / (double)_sampleRate;
    public bool IsPlaying => _wantsPlay;
    public bool IsOutputPlaying => _wantsPlay && SourceState() == ALSourceState.Playing;
    public bool IsAtEnd => _data is { } data && _nextFrame >= data.FrameCount && _queued.Count == 0;

    public double Position
    {
        get
        {
            if (!_wantsPlay || _queued.Count == 0) return _clockBase;
            // OpenAL reports the offset from the first buffer still in the queue, including processed buffers.
            AL.GetSource(_source, ALGetSourcei.SampleOffset, out int offset);
            AL.GetSource(_source, ALGetSourcei.SourceState, out int state);
            long frames = (ALSourceState)state == ALSourceState.Stopped
                ? _nextFrame : _nextFrame - _queuedFrames + Math.Clamp(offset, 0, _queuedFrames);
            return Math.Clamp(frames / (double)_sampleRate, 0, Duration);
        }
    }

    public bool Load(string path) => AudioDecoder.OpenStream(path) is { } audio && LoadDecoded(audio);

    public bool LoadDecoded(DecodedAudio data) => LoadDecoded(data, 1.0);

    public bool LoadDecoded(DecodedAudio data, double playbackRate)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (_disposed || _source == 0 || data.Channels is not (1 or 2) || data.SampleRate <= 0 ||
            data.FrameCount == 0 || !double.IsFinite(playbackRate) || playbackRate <= 0)
            return false;
        Stop();
        _data = data;
        _failed = false;
        _sampleRate = (int)Math.Clamp(Math.Round(data.SampleRate * playbackRate), 1, int.MaxValue);
        return true;
    }

    public void Play()
    {
        if (!IsLoaded || _wantsPlay) return;
        _wantsPlay = true;
        Update();
    }

    public void Pause()
    {
        if (!_wantsPlay) return;
        AL.SourcePause(_source);
        _clockBase = Position;
        _wantsPlay = false;
    }

    public void Stop() => ResetQueue(0, false);

    public void Seek(double seconds)
    {
        if (!IsLoaded) return;
        long frame = (long)Math.Clamp(seconds * _sampleRate, 0, _data!.FrameCount);
        ResetQueue(frame, _wantsPlay);
        if (_wantsPlay) Update();
    }

    /// <summary>Reposition a clip without starting its source. The caller may then buffer multiple tracks together.</summary>
    public void PreparePlayback(double seconds)
    {
        if (!IsLoaded) return;
        long frame = (long)Math.Clamp(seconds * _sampleRate, 0, _data!.FrameCount);
        ResetQueue(frame, false);
    }

    /// <summary>Keep a short lead in the queue before starting a prepared track.</summary>
    public bool BufferForStart()
    {
        if (!IsLoaded) return false;
        FillQueue();
        return IsLoaded && (_queued.Count >= 2 || _queued.Count > 0 &&
            (_nextFrame >= _data!.FrameCount || _endOfData));
    }

    /// <summary>Start ready sources in the same OpenAL mixer update.</summary>
    public static void PlayPrepared(AudioClip? first, AudioClip? second)
    {
        Span<int> sources = stackalloc int[2];
        int count = 0;
        if (first is { IsLoaded: true } && first._queued.Count != 0)
        {
            first._wantsPlay = true;
            sources[count++] = first._source;
        }
        if (second is { IsLoaded: true } && second._queued.Count != 0)
        {
            second._wantsPlay = true;
            sources[count++] = second._source;
        }
        if (count != 0) AL.SourcePlay(sources[..count]);
    }

    private void ResetQueue(long frame, bool playing)
    {
        _prefetch?.Dispose();
        _prefetch = null;
        _nextFrame = frame;
        _clockBase = frame / (double)_sampleRate;
        _endOfData = false;
        _wantsPlay = playing;
        if (_source == 0 || _disposed) return;
        AL.SourceStop(_source);
        // Queued buffers on an initial source are not processed yet. Detach the whole queue before recycling them.
        if (_queued.Count != 0) AL.Source(_source, ALSourcei.Buffer, 0);
        while (_queued.TryDequeue(out var buffer))
            _available.Enqueue(buffer.Buffer);
        // Sources left stopped report all subsequent buffers as processed until playback starts.
        AL.SourceRewind(_source);
        _queuedFrames = 0;
    }

    public void Update()
    {
        UpdateBuffers();
        if (IsLoaded && _wantsPlay && _queued.Count > 0 && !IsOutputPlaying)
            AL.SourcePlay(_source);
    }

    /// <summary>Refill a running source without automatically restarting it after an underrun.</summary>
    public void UpdateBuffers()
    {
        if (IsLoaded && _wantsPlay) FillQueue();
    }

    private ALSourceState SourceState()
    {
        AL.GetSource(_source, ALGetSourcei.SourceState, out int state);
        return (ALSourceState)state;
    }

    private void FillQueue()
    {
        int processed = 0;
        if (_wantsPlay)
            AL.GetSource(_source, ALGetSourcei.BuffersProcessed, out processed);
        while (processed-- > 0 && _queued.TryDequeue(out var finished))
        {
            AL.SourceUnqueueBuffer(_source);
            _available.Enqueue(finished.Buffer);
            _queuedFrames -= finished.Frames;
        }
        _clockBase = (_nextFrame - _queuedFrames) / (double)_sampleRate;

        var data = _data!;
        int framesPerBuffer = Math.Clamp((int)(_sampleRate * BufferDuration), 1, 65536);
        if (data.IsStreamed && _prefetch is null && _nextFrame < data.FrameCount && !_endOfData)
            _prefetch = new(data, _nextFrame, framesPerBuffer);
        while (_available.TryPeek(out int buffer) && _nextFrame < data.FrameCount && !_endOfData)
        {
            if (_prefetch is { } prefetch)
            {
                if (!prefetch.TryRead(out var block)) break;
                Upload(buffer, block.Samples.AsSpan(0, block.Frames * data.Channels), block.Frames);
                prefetch.Recycle(block.Samples);
            }
            else
            {
                int frames = (int)Math.Min(framesPerBuffer, data.FrameCount - _nextFrame);
                int frameBytes = data.Channels * sizeof(short);
                var bytes = data.Pcm.AsSpan((int)(_nextFrame * frameBytes), frames * frameBytes);
                Upload(buffer, System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(bytes), frames);
            }
        }

        if (_prefetch is { IsComplete: true } completed)
        {
            _endOfData = true;
            if (completed.Error is { } error)
            {
                Console.Error.WriteLine($"[Audio] {Name}: {error.GetBaseException().Message}");
                _failed = true;
                ResetQueue(_nextFrame - _queuedFrames, false);
                return;
            }
        }

        if (_queued.Count == 0 && (_nextFrame >= data.FrameCount || _endOfData))
        {
            _wantsPlay = false;
            _clockBase = _nextFrame / (double)_sampleRate;
            if (_nextFrame < data.FrameCount) _failed = true;
            _prefetch?.Dispose();
            _prefetch = null;
        }
    }

    private void Upload(int buffer, ReadOnlySpan<short> samples, int frames)
    {
        fixed (short* pointer = samples)
            AL.BufferData(buffer, _data!.Format, (nint)pointer, samples.Length * sizeof(short), _sampleRate);
        AL.SourceQueueBuffer(_source, buffer);
        _available.Dequeue();
        _queued.Enqueue((buffer, frames));
        _queuedFrames += frames;
        _nextFrame += frames;
    }

    private void ApplyGain()
    {
        if (_source != 0 && !_disposed) AL.Source(_source, ALSourcef.Gain, _volume);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        if (_source != 0) AL.DeleteSource(_source);
        foreach (int buffer in _buffers)
            if (buffer != 0) AL.DeleteBuffer(buffer);
        _data = null;
        _disposed = true;
    }
}
