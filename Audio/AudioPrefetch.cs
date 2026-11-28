using System.Threading.Channels;

namespace Thrust.Audio;

internal sealed class AudioPrefetch : IDisposable
{
    private const int BlockCount = 4;
    internal readonly record struct Block(short[] Samples, int Frames);

    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<short[]> _free = Channel.CreateBounded<short[]>(BlockCount);
    private readonly Channel<Block> _ready = Channel.CreateBounded<Block>(BlockCount);
    private readonly Task _producer;

    internal AudioPrefetch(DecodedAudio audio, long first, int blockFrames)
    {
        for (int i = 0; i < BlockCount; i++)
            _free.Writer.TryWrite(new short[blockFrames * audio.Channels]);
        _producer = Task.Run(() => Produce(audio, first, _stop.Token));
    }

    internal bool TryRead(out Block block) => _ready.Reader.TryRead(out block);
    internal void Recycle(short[] samples) => _free.Writer.TryWrite(samples);
    internal bool IsComplete => _ready.Reader.Completion.IsCompleted;
    internal Exception? Error => _ready.Reader.Completion.Exception;

    private async Task Produce(DecodedAudio audio, long first, CancellationToken cancellation)
    {
        Exception? failure = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using var reader = audio.OpenReader();
            if (first != 0) reader.Seek(first);
            long framesLeft = audio.FrameCount - first;
            while (framesLeft > 0)
            {
                var samples = await _free.Reader.ReadAsync(cancellation).ConfigureAwait(false);
                int count = (int)Math.Min(framesLeft, samples.Length / audio.Channels);
                int frames = reader.Read(samples.AsSpan(0, count * audio.Channels));
                if (frames == 0) break;
                await _ready.Writer.WriteAsync(new(samples, frames), cancellation).ConfigureAwait(false);
                framesLeft -= frames;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { failure = error; }
        finally { _ready.Writer.TryComplete(failure); }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _ = _producer.ContinueWith(task =>
        {
            // A canceled consumer may leave queued blocks; drain them to observe any read failure.
            while (_ready.Reader.TryRead(out _)) { }
            _ = _ready.Reader.Completion.Exception;
            _stop.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
