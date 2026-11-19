namespace YMM4PlaybackTimingPlugin.Playback;

// One native player's clock and optional timer. The caller owns this session;
// this layer never retains the player, its source, or a frame borrow.
internal sealed class PreviewPlaybackSession : IDisposable
{
    private readonly object gate = new();
    private readonly Func<IPreviewFrameDeadlineRuntime> createRuntime;
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IPreviewFrameDeadlineRuntime? runtime;
    private bool waiting, closing, disposed;
    internal enum AudioPhase { Paused, StartingAudio, Playing, ChangingAudio, PausingAudio, FinalizingAudio, StoppedAudio }
    private AudioPhase phase;
    private bool waitingForAudio;

    internal PreviewPlaybackClockState Clock { get; }
    internal AudioPhase Phase { get { lock (gate) return phase; } }
    internal bool IsPausing { get { lock (gate) return phase is AudioPhase.PausingAudio or AudioPhase.FinalizingAudio or AudioPhase.StoppedAudio; } }
    internal bool WaitingForAudio { get { lock (gate) return waitingForAudio; } }

    internal bool TryStart(TimeSpan position, double rate, long timestamp, TimeSpan? maximum)
    {
        lock (gate)
        {
            if (closing) return false;
            Clock.Start(position, rate, timestamp, maximum, waitForBackend: true);
            phase = AudioPhase.StartingAudio;
            waitingForAudio = true;
            return true;
        }
    }

    internal void Stop(TimeSpan position, long timestamp)
    {
        lock (gate)
        {
            Clock.Stop(position, timestamp);
            phase = AudioPhase.Paused;
            waitingForAudio = false;
        }
    }

    internal bool RequestAudioPause()
    {
        lock (gate)
        {
            if (closing || IsPausing || Clock.GetSnapshot().Disabled
                || (!Clock.GetSnapshot().Active && !waitingForAudio)) return false;
            phase = AudioPhase.PausingAudio;
            Clock.InvalidateWaits();
            return true;
        }
    }

    internal bool FreezeAudioPause(TimeSpan position, long timestamp)
    {
        lock (gate)
        {
            if (closing || phase != AudioPhase.PausingAudio || !Clock.BeginAudioPause(position, timestamp)) return false;
            phase = AudioPhase.FinalizingAudio;
            waitingForAudio = false;
            return true;
        }
    }

    internal bool CompleteAudioPause(TimeSpan position, long timestamp, Action<TimeSpan> commitNative)
    {
        lock (gate)
        {
            if (closing || phase != AudioPhase.FinalizingAudio || !Clock.CompleteAudioPause(position, timestamp, commitNative)) return false;
            phase = AudioPhase.StoppedAudio;
            return true;
        }
    }

    internal bool BeginAudioBackendChange(long timestamp, out TimeSpan position)
    {
        lock (gate)
        {
            position = default;
            if (closing || !Clock.BeginAudioBackendChange(timestamp, out position)) return false;
            waitingForAudio = true;
            if (!IsPausing) phase = AudioPhase.ChangingAudio;
            return true;
        }
    }

    internal bool ObserveAudioSample(TimeSpan position, double rate, long timestamp, TimeSpan? maximum)
    {
        lock (gate)
        {
            if (closing || phase is AudioPhase.FinalizingAudio or AudioPhase.StoppedAudio) return false;
            if (waitingForAudio)
            {
                if (Clock.ResumeAudioBackend(position, rate, timestamp, maximum))
                {
                    waitingForAudio = false;
                    if (!IsPausing) phase = AudioPhase.Playing;
                }
                return false;
            }
            return Clock.ReanchorOrSeek(position, rate, timestamp, maximum);
        }
    }

    internal PreviewPlaybackSession(
        PreviewPlaybackClockState? clock = null,
        Func<IPreviewFrameDeadlineRuntime>? createRuntime = null)
    {
        Clock = clock ?? new PreviewPlaybackClockState();
        this.createRuntime = createRuntime ?? (static () => new PreviewPlaybackWaitRuntime());
    }

    internal bool TryWait(object source, TimeSpan candidate, long sequence, out PreviewFrameDeadlineWaitResult result)
    {
        result = default;
        IPreviewFrameDeadlineRuntime activeRuntime;
        lock (gate)
        {
            if (closing || waiting || !Clock.GetSnapshot().Active
                || !PreviewFrameDeadlineRegistry.IsCurrentPlaying(source, sequence)) return false;
            activeRuntime = runtime ??= createRuntime();
            waiting = true;
        }
        try
        {
            result = PreviewFrameDeadlineWait.WaitUntil(Clock, candidate, activeRuntime,
                capturePhaseTimings: false, timelineSource: source, expectedSequence: sequence);
            return true;
        }
        finally
        {
            lock (gate)
            {
                waiting = false;
                if (closing) drained.TrySetResult();
            }
        }
    }

    internal void CloseAdmission()
    {
        lock (gate)
        {
            closing = true;
            Clock.Disable("playback-session-ended");
            if (!waiting) drained.TrySetResult();
        }
    }

    internal async Task CloseAsync()
    {CloseAdmission();await drained.Task.ConfigureAwait(false);Dispose();}

    public void Dispose() => Dispose(TimeSpan.FromSeconds(10));

    internal void Dispose(TimeSpan timeout)
    {
        CloseAdmission();
        // The runtime stays owned on timeout; callers must retain and retry it.
        if (!drained.Task.Wait(timeout))
            throw new TimeoutException("PlaybackTiming-preview player wait did not drain; restart required.");
        lock (gate)
        {
            if (disposed) return;
            (runtime as IDisposable)?.Dispose();
            runtime = null;
            disposed = true;
        }
    }
}
