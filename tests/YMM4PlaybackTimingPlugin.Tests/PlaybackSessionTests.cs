using YMM4PlaybackTimingPlugin.Playback;

internal static class PlaybackSessionTests
{
    internal static void Run()
    {
        LazyRuntime();
        IndependentPlayers();
        CancelAndDrain();
        ExceptionDrains();
        DisposalFailureRetainsRuntime();
        AudioTransitions();
    }

    private static void AudioTransitions()
    {
        foreach (var rate in new[] { 50, 100, 200 })
        foreach (var delay in new[] { 0, 5, 33, 80 })
        foreach (var framePhase in new[] { 0.1, 0.49, 0.51, 0.9 })
        {
            using var session = Session(() => new Runtime());
            var initial = TimeSpan.FromSeconds(10 + framePhase / 60);
            session.TryStart(initial, rate, 0, null);
            Check(!session.Clock.GetSnapshot().Active && session.WaitingForAudio, "start did not await audio");
            session.ObserveAudioSample(initial - TimeSpan.FromMilliseconds(1), rate, 100_000, null);
            Check(!session.Clock.GetSnapshot().Active, "backend before restart target resumed");
            session.ObserveAudioSample(initial, rate, 200_000, null);
            Check(session.Phase == PreviewPlaybackSession.AudioPhase.Playing, "ready backend did not activate session");
            var oldGeneration = session.Clock.GetSnapshot().Generation;
            Check(session.RequestAudioPause(), "pause request rejected");
            Check(!session.Clock.ValidateWaitCompletion(oldGeneration), "old candidate survived pause request");
            var target = initial + TimeSpan.FromMilliseconds(delay * rate / 100.0);
            var now = 200_000L + delay * 1000;
            Check(session.Clock.GetPosition(TimeSpan.Zero, now) == target, "drain did not interpolate live audio at speed");
            var drainingGeneration = session.Clock.GetSnapshot().Generation;
            Check(session.Clock.TryGetDeadline(target, now, out _, out _) && session.Clock.ValidateWaitCompletion(drainingGeneration),
                "fresh draining candidate rejected");
            Check(session.FreezeAudioPause(target, now), "final audio sample not adopted");
            Check(!session.Clock.ValidateWaitCompletion(drainingGeneration), "draining candidate survived final freeze");
            Check(session.Clock.GetPosition(TimeSpan.Zero, now + 1_000_000) == target, "finalized position advanced");
            var commits = 0;
            var raw = TimeSpan.Zero;
            Check(session.CompleteAudioPause(target, now, value => { commits++; raw = value; }), "native handoff failed");
            Check(!session.CompleteAudioPause(TimeSpan.Zero, now, _ => commits++), "native handoff repeated");
            Check(raw == target && commits == 1, "native handoff altered final sample");
            // Native StopAsync / repeat / paused seek explicitly replace raw.
            foreach (var explicitPosition in new[] { TimeSpan.Zero, initial, TimeSpan.FromSeconds(2) })
                Check(session.Clock.GetPosition(explicitPosition, now + 2_000_000) == explicitPosition, "native explicit position clamped");
            session.Stop(TimeSpan.FromSeconds(2), now);
            session.TryStart(TimeSpan.FromSeconds(2), rate, now, null);
            Check(session.WaitingForAudio && !session.Clock.GetSnapshot().Active
                && !session.FreezeAudioPause(target, now), "old finalization altered new startup");
        }
        using var closed = Session(() => new Runtime());
        closed.CloseAdmission();
        Check(!closed.TryStart(TimeSpan.Zero, 100, 0, null) && !closed.RequestAudioPause(), "closed session admitted audio transition");
        Console.WriteLine("PASS session audio startup/drain/finalization at 50/100/200%, explicit native positions and stale transitions");
    }

    private static void LazyRuntime()
    {
        var created = 0;
        var runtime = new Runtime();
        using (var session = Session(() => { created++; return runtime; }))
        {
            var source = new object();
            PreviewFrameDeadlineRegistry.Register(source, TimeSpan.FromMilliseconds(16), true);
            Check(!session.TryWait(source, TimeSpan.FromMilliseconds(16), Sequence(source), out _), "inactive waited");
        }
        Check(created == 0 && runtime.Disposals == 0, "unused runtime was created");

        using var active = Session(() => { created++; return runtime; });
        active.Clock.Start(TimeSpan.Zero, 100, 0);
        var activeSource = new object();
        foreach (var milliseconds in new[] { 16, 32 })
        {
            var time = TimeSpan.FromMilliseconds(milliseconds);
            PreviewFrameDeadlineRegistry.Register(activeSource, time, true);
            Check(active.TryWait(activeSource, time, Sequence(activeSource), out var result) && result.Completed,
                "active deadline did not complete");
        }
        Check(created == 1 && runtime.Disposals == 0, "runtime not reused");
        active.Dispose();
        active.Dispose();
        Check(runtime.Disposals == 1, "runtime disposal was not once");
        active.Clock.Start(TimeSpan.Zero, 100, 0);
        Check(!active.TryWait(activeSource, TimeSpan.FromMilliseconds(32), Sequence(activeSource), out _), "closed session reopened");
        Console.WriteLine("PASS session lazy runtime/reuse/once-only disposal");
    }

    private static void IndependentPlayers()
    {
        var firstRuntime = new Runtime();
        var secondRuntime = new Runtime { Precise = false };
        using var first = Session(() => firstRuntime);
        using var second = Session(() => secondRuntime);
        first.Clock.Start(TimeSpan.Zero, 100, 0);
        second.Clock.Start(TimeSpan.Zero, 100, 0);
        first.Dispose();
        var source = new object();
        PreviewFrameDeadlineRegistry.Register(source, TimeSpan.FromMilliseconds(16), true);
        Check(second.TryWait(source, TimeSpan.FromMilliseconds(16), Sequence(source), out var result)
            && result.Completed && result.CoarseWaitCount > 0, "other player or timer fallback stopped");
        Check(firstRuntime.Disposals == 0 && secondRuntime.Disposals == 0, "runtime crossed players");
        second.Dispose();
        Check(secondRuntime.Disposals == 1, "second runtime leaked");
        Console.WriteLine("PASS session independent players/timer fallback");
    }

    private static void CancelAndDrain()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var runtime = new Runtime { OnWait = () => { entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(5)), "test release missing"); } };
        using var session = Session(() => runtime);
        session.Clock.Start(TimeSpan.Zero, 100, 0);
        var source = new object();
        PreviewFrameDeadlineRegistry.Register(source, TimeSpan.FromMilliseconds(16), true);
        var sequence = Sequence(source);
        var task = Task.Run(() => { session.TryWait(source, TimeSpan.FromMilliseconds(16), sequence, out var result); return result; });
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "wait never entered");
            Check(!session.TryWait(source, TimeSpan.FromMilliseconds(16), sequence, out _), "two waits admitted");
            session.CloseAdmission();
            var timedOut = false;
            try { session.Dispose(TimeSpan.Zero); } catch (TimeoutException) { timedOut = true; }
            Check(timedOut && runtime.Disposals == 0, "runtime freed before drain");
            Check(!session.TryWait(source, TimeSpan.FromMilliseconds(16), sequence, out _), "closing admitted wait");
        }
        finally { release.Set(); }
        Check(task.Wait(TimeSpan.FromSeconds(5)), "wait did not drain");
        Check(!task.Result.Completed, "cancelled wait committed");
        session.Dispose();
        Check(runtime.Disposals == 1, "retry did not dispose drained runtime");
        Console.WriteLine("PASS session concurrent rejection/cancel/timeout retention/retry");
    }

    private static void ExceptionDrains()
    {
        var expected = new InvalidOperationException("injected runtime failure");
        var runtime = new Runtime { OnWait = () => throw expected };
        using var session = Session(() => runtime);
        session.Clock.Start(TimeSpan.Zero, 100, 0);
        var source = new object();
        PreviewFrameDeadlineRegistry.Register(source, TimeSpan.FromMilliseconds(16), true);
        Exception? observed = null;
        try { session.TryWait(source, TimeSpan.FromMilliseconds(16), Sequence(source), out _); }
        catch (Exception error) { observed = error; }
        Check(ReferenceEquals(observed, expected), "runtime exception replaced");
        session.Dispose(TimeSpan.Zero);
        Check(runtime.Disposals == 1, "exception left wait undrained");
        Console.WriteLine("PASS session exception releases wait admission");
    }

    private static void DisposalFailureRetainsRuntime()
    {
        var runtime = new Runtime { FailDisposal = true };
        using var session = Session(() => runtime);
        session.Clock.Start(TimeSpan.Zero, 100, 0);
        var source = new object();
        PreviewFrameDeadlineRegistry.Register(source, TimeSpan.FromMilliseconds(16), true);
        session.TryWait(source, TimeSpan.FromMilliseconds(16), Sequence(source), out _);
        var failed = false;
        try { session.Dispose(); } catch (InvalidOperationException) { failed = true; }
        Check(failed && runtime.Disposals == 0, "dispose failure lost ownership");
        runtime.FailDisposal = false;
        session.Dispose();
        Check(runtime.Disposals == 1, "failed disposal not retried");
        Console.WriteLine("PASS session failed cleanup retains runtime for retry");
    }

    private static PreviewPlaybackSession Session(Func<IPreviewFrameDeadlineRuntime> factory) =>
        new(new PreviewPlaybackClockState(1_000_000), factory);
    private static long Sequence(object source)
    {
        Check(PreviewFrameDeadlineRegistry.TryGet(source, out var value), "candidate missing");
        return value.Sequence;
    }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private sealed class Runtime : IPreviewFrameDeadlineRuntime, IDisposable
    {
        private long timestamp;
        internal Action? OnWait;
        internal bool Precise = true, FailDisposal;
        internal int Disposals;
        public long Frequency => 1_000_000;
        public long GetTimestamp() => timestamp;
        public bool TryPreciseWait(TimeSpan duration)
        {
            OnWait?.Invoke();
            if (!Precise) return false;
            timestamp += PreviewFrameDeadlineWait.DurationToTimestampTicks(duration, Frequency);
            return true;
        }
        public void Sleep(TimeSpan duration) => timestamp += PreviewFrameDeadlineWait.DurationToTimestampTicks(duration, Frequency);
        public void Yield() => timestamp += 100;
        public void SpinOnce() => timestamp += 10;
        public void Dispose()
        {
            if (FailDisposal) throw new InvalidOperationException("injected disposal failure");
            Disposals++;
        }
    }
}
