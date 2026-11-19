using System.Reflection;
using HarmonyLib;
using YMM4PlaybackTimingPlugin.Playback;

if(args is ["--native-loop",var nativeMode])return IndependentNativeLoopTests.Run(nativeMode);
if(args is ["--plugin-startup"]){IndependentBoundaryTests.PublicEntry();return 0;}
if(args is ["--diagnostics-ui",var output])return PreviewDiagnosticsUiTests.Run(output);

var tests = new (string Name, Action Run)[]
{
    ("Diagnostics windows, pause, reset, failures and bounded storage", PreviewDiagnosticsTests.Measurements),
    ("Diagnostics native boundaries exclude skips and preserve exceptions", PreviewDiagnosticsTests.NativeBoundarySemantics),
    ("Diagnostics screen geometry and display classification", PreviewDiagnosticsTests.GeometryAndDisplay),
    ("Optional diagnostics rollback preserves pacing and foreign patches", PreviewDiagnosticsTests.OptionalRegistration),
    ("Diagnostics discovery, selection and geometry weakly own players", PreviewDiagnosticsTests.WeakOwnership),
    ("Playback clock is continuous and monotonic", PlaybackClockIsContinuousAndMonotonic),
    ("Playback clock preserves position across rate change", PlaybackClockPreservesPositionAcrossRateChange),
    ("Pause drains audio until the first backend Stop", AudioPauseTests.DrainFollowsAudio),
    ("Starting audio cannot interpolate or admit candidates", AudioPauseTests.StartWaitsForAudio),
    ("Wait completion never publishes candidate media time", AudioPauseTests.WaitDoesNotPublishMediaTime),
    ("Startup pause and stale completions respect audio epochs", AudioPauseTests.StartingPauseAndEpochs),
    ("Audio pause owns final position and cancels old waits", AudioPauseTests.Clock),
    ("Native audio restart contract accepts safe Volume capture and rejects altered flow", AudioPauseTests.NativeRateContract),
    ("Audio snapshots ignore stale UI raw and commit first stop", AudioPauseTests.NativeHandoff),
    ("Audio stop serializes reads and respects PlaybackTiming OFF", AudioPauseTests.SerializationAndOff),
    ("Unstarted replacement backend never exposes stale UI time", AudioPauseTests.BackendReplacement),
    ("Backend exchange freezes the exact native restart position", AudioPauseTests.BackendExchange),
    ("Pause during exchange and feature changes retain audio lifecycle", AudioPauseTests.PauseDuringExchange),
    ("Deadline wait reaches the candidate time", DeadlineWaitReachesCandidateTime),
    ("Precise wait removes deadline-window yields", PreciseWaitRemovesDeadlineWindowYields),
    ("Wait profiles trade spin time for timer wait", WaitProfilesTradeSpinTimeForTimerWait),
    ("Player-owned high-resolution timer is available", ProcessLocalHighResolutionTimerIsAvailable),
    ("Late candidates bypass all waiting", LateCandidatesBypassAllWaiting),
    ("Deadline wait separates anchor and phase timing", DeadlineWaitSeparatesAnchorAndPhaseTiming),
    ("Sub-millisecond slices yield without a precise timer", SubMillisecondSlicesYield),
    ("Seek discontinuity cancels an in-progress deadline", SeekDiscontinuityCancelsInProgressDeadline),
    ("Seek invalidation prevents an old candidate from being reused", SeekInvalidationPreventsOldCandidateReuse),
    ("A newer candidate cancels an in-progress deadline", NewCandidateCancelsInProgressDeadline),
    ("Stopped playback never waits", StoppedPlaybackNeverWaits),
    ("Low-speed long deadlines bypass one frame without disabling", LowSpeedDeadlineBypassesOneFrame),
    ("Optional phase diagnostics preserve deadline outcomes", PhaseDiagnosticsPreserveDeadlineOutcomes),
    ("Independent players do not share cancellation", IndependentPlayersDoNotShareCancellation),
    ("Timer absence and disposal select fallback", TimerAbsenceAndDisposalSelectFallback),
    ("Clock runtime frequency mismatch rejects waiting", FrequencyMismatchRejectsWaiting),
    ("Stopping during a wait cancels the deadline", StoppingDuringWaitCancelsDeadline),
    ("A failed timer rechecks cancellation before fallback", FailedTimerRechecksCancellation),
    ("Playback sessions own and drain their wait runtime", PlaybackSessionTests.Run),
    ("Independent Timing references and native identity", PlaybackTimingStartupTests.BoundariesAndIdentity),
    ("Independent startup has no settings or participant input", PlaybackTimingStartupTests.Settings),
    ("PlaybackTiming registration failure rolls back native ownership", PlaybackTimingStartupTests.RegistrationRollback),
    ("Failed player/source retirement retains its session", PlaybackTimingStartupTests.FailedRetirementRetainsSession),
    ("Independent callbacks drain and preserve cancellation", IndependentBoundaryTests.DrainAndCancellation),
    ("Independent Draw state does not retain a native player", IndependentBoundaryTests.PlayerCollection),
    ("Independent registration rejects and preserves foreign factories", IndependentBoundaryTests.ForeignFactory),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine("PASS " + test.Name);
    }
    catch (Exception ex)
    {
        failures.Add(test.Name + ": " + ex.Message);
        Console.WriteLine("FAIL " + test.Name + ": " + ex.Message);
    }
}

if (failures.Count != 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine($"All {tests.Length} PlaybackTiming tests passed.");
return 0;

static void PlaybackClockIsContinuousAndMonotonic()
{
    var clock = new PreviewPlaybackClockState(frequency: 1_000_000);
    clock.Start(TimeSpan.FromSeconds(1), playbackRatePercent: 100, timestamp: 0);
    Equal(TimeSpan.FromMilliseconds(1_016), clock.GetPosition(TimeSpan.FromSeconds(1), 16_000));
    clock.Reanchor(TimeSpan.FromMilliseconds(1_047), playbackRatePercent: 100, timestamp: 47_000);
    Equal(TimeSpan.FromMilliseconds(1_063), clock.GetPosition(TimeSpan.FromMilliseconds(1_047), 63_000));
    clock.Reanchor(TimeSpan.FromMilliseconds(1_061), playbackRatePercent: 100, timestamp: 64_000);
    Equal(TimeSpan.FromMilliseconds(1_063), clock.GetPosition(TimeSpan.FromMilliseconds(1_061), 64_000));
}

static void PlaybackClockPreservesPositionAcrossRateChange()
{
    var clock = new PreviewPlaybackClockState(frequency: 1_000_000);
    clock.Start(TimeSpan.Zero, playbackRatePercent: 100, timestamp: 0);
    var positionAtChange = clock.GetPosition(TimeSpan.Zero, 100_000);
    clock.Reanchor(positionAtChange, playbackRatePercent: 200, timestamp: 100_000);
    Equal(TimeSpan.FromMilliseconds(200), clock.GetPosition(TimeSpan.Zero, 150_000));
}

static void DeadlineWaitReachesCandidateTime()
{
    var clock = new PreviewPlaybackClockState(frequency: 1_000_000);
    var runtime = new VirtualDeadlineRuntime(frequency: 1_000_000);
    clock.Start(TimeSpan.Zero, playbackRatePercent: 100, timestamp: runtime.GetTimestamp());
    var result = PreviewFrameDeadlineWait.WaitUntil(clock, TimeSpan.FromMilliseconds(16), runtime);
    True(result.Completed);
    True(runtime.GetTimestamp() >= 16_000);
    True(result.PreciseWaitCount >= 1);
    Equal(0, result.YieldCount);
}

static void PreciseWaitRemovesDeadlineWindowYields()
{
    var clock = new PreviewPlaybackClockState(frequency: 1_000_000);
    var runtime = new VirtualDeadlineRuntime(frequency: 1_000_000, preciseWaitSupported: true);
    clock.Start(TimeSpan.Zero, playbackRatePercent: 100, timestamp: 0);
    var result = PreviewFrameDeadlineWait.WaitUntil(clock, TimeSpan.FromMilliseconds(16), runtime);
    True(result.Completed);
    True(result.PreciseWaitCount >= 1);
    Equal(0, result.CoarseWaitCount);
    Equal(0, result.YieldCount);
    True(result.SpinCount > 0);
}

static void WaitProfilesTradeSpinTimeForTimerWait()
{
        var lowCpu = RunWaitProfile(PreviewFrameWaitProfile.LowCpu);
        var balanced = RunWaitProfile(PreviewFrameWaitProfile.Balanced);
        var lowJitter = RunWaitProfile(PreviewFrameWaitProfile.LowJitter);
        True(lowCpu.SpinCount < balanced.SpinCount);
        True(balanced.SpinCount < lowJitter.SpinCount);
        True(lowCpu.PreciseWaitDurationTicks > balanced.PreciseWaitDurationTicks);
        True(balanced.PreciseWaitDurationTicks > lowJitter.PreciseWaitDurationTicks);
}

static PreviewFrameDeadlineWaitResult RunWaitProfile(PreviewFrameWaitProfile profile)
{
    var clock = new PreviewPlaybackClockState(frequency: 1_000_000);
    var runtime = new VirtualDeadlineRuntime(frequency: 1_000_000);
    clock.Start(TimeSpan.Zero, playbackRatePercent: 100, timestamp: 0);
    return PreviewFrameDeadlineWait.WaitUntil(clock, TimeSpan.FromMilliseconds(16), runtime, capturePhaseTimings: true, profile: profile);
}

static void LateCandidatesBypassAllWaiting()
{
    var clock = new PreviewPlaybackClockState(frequency: 1_000_000);
    var runtime = new VirtualDeadlineRuntime(frequency: 1_000_000);
    clock.Start(TimeSpan.Zero, playbackRatePercent: 100, timestamp: 0);
    runtime.Advance(TimeSpan.FromMilliseconds(30));
    var result = PreviewFrameDeadlineWait.WaitUntil(clock, TimeSpan.FromMilliseconds(16), runtime);
    True(result.Completed);
    Equal("completed-late-bypass", result.Reason);
    Equal(1, result.LateDeadlineBypassCount);
    Equal(0, result.CoarseWaitCount);
    Equal(0, result.PreciseWaitCount);
    Equal(0, result.YieldCount);
    Equal(0, result.SpinCount);
}

static void ProcessLocalHighResolutionTimerIsAvailable()
{
    using var timer = new PreviewHighResolutionWaitableTimer();
    timer.Initialize();
    try
    {
        True(timer.IsAvailable);
        True(timer.TryWait(TimeSpan.FromMilliseconds(1)));
    }
    finally
    {
        timer.Dispose();
    }
}

static void SubMillisecondSlicesYield()
{
    var clock = new PreviewPlaybackClockState(frequency: 1_000_000);
    var runtime = new VirtualDeadlineRuntime(frequency: 1_000_000, preciseWaitSupported: false);
    clock.Start(TimeSpan.Zero, playbackRatePercent: 100, timestamp: 0);
    var result = PreviewFrameDeadlineWait.WaitUntil(clock, TimeSpan.FromMilliseconds(2.5), runtime);
    True(result.Completed);
    Equal(0, result.CoarseWaitCount);
    True(result.YieldCount >= 1);
}

static void DeadlineWaitSeparatesAnchorAndPhaseTiming()
{
    var clock = new PreviewPlaybackClockState(frequency: 1_000_000);
    var runtime = new ReanchoringDeadlineRuntime(clock, frequency: 1_000_000);
    clock.Start(TimeSpan.Zero, playbackRatePercent: 100, timestamp: 0);
    var result = PreviewFrameDeadlineWait.WaitUntil(
        clock,
        TimeSpan.FromMilliseconds(16),
        runtime,
        capturePhaseTimings: true);
    True(result.Completed);
    True(result.FinalDeadlineTimestamp > result.InitialDeadlineTimestamp);
    Near(4, result.AnchorDeadlineShiftMilliseconds, 0.01);
    True(result.CoarseWaitDurationMilliseconds > 0);
    True(result.YieldDurationMilliseconds > 0);
    True(result.SpinDurationMilliseconds > 0);
}

static void SeekDiscontinuityCancelsInProgressDeadline()
{
    var clock = new PreviewPlaybackClockState(frequency: 1_000_000);
    var runtime = new SeekingDeadlineRuntime(clock, frequency: 1_000_000);
    clock.Start(TimeSpan.Zero, playbackRatePercent: 100, timestamp: 0);
    var result = PreviewFrameDeadlineWait.WaitUntil(
        clock,
        TimeSpan.FromMilliseconds(200),
        runtime);
    True(!result.Completed);
    Equal("playback-session-changed", result.Reason);
    True(clock.GetSnapshot().Generation > 1);
}

static void SeekInvalidationPreventsOldCandidateReuse()
{
    var source = new object();
    PreviewFrameDeadlineRegistry.Reset();
    PreviewFrameDeadlineRegistry.Register(source, TimeSpan.FromMilliseconds(500), playing: true);
    True(PreviewFrameDeadlineRegistry.TryGet(source, out var before));
    True(before.Playing);
    PreviewFrameDeadlineRegistry.Invalidate(source);
    True(PreviewFrameDeadlineRegistry.TryGet(source, out var after));
    True(!after.Playing);
    True(after.Sequence > before.Sequence);
}

static void StoppedPlaybackNeverWaits()
{
    var clock = new PreviewPlaybackClockState(frequency: 1_000_000);
    var runtime = new VirtualDeadlineRuntime(frequency: 1_000_000);
    clock.Start(TimeSpan.Zero, playbackRatePercent: 100, timestamp: 0);
    clock.Stop(TimeSpan.Zero, timestamp: 0);
    var result = PreviewFrameDeadlineWait.WaitUntil(
        clock,
        TimeSpan.FromMilliseconds(16),
        runtime);
    True(!result.Completed);
    Equal("clock-inactive", result.Reason);
    Equal(0L, runtime.GetTimestamp());
}

static void NewCandidateCancelsInProgressDeadline()
{
    var source = new object();
    PreviewFrameDeadlineRegistry.Reset();
    PreviewFrameDeadlineRegistry.Register(source, TimeSpan.FromMilliseconds(200), playing: true);
    True(PreviewFrameDeadlineRegistry.TryGet(source, out var candidate));

    var clock = new PreviewPlaybackClockState(frequency: 1_000_000);
    var runtime = new CandidateChangingDeadlineRuntime(source, frequency: 1_000_000);
    clock.Start(TimeSpan.Zero, playbackRatePercent: 100, timestamp: 0);
    var result = PreviewFrameDeadlineWait.WaitUntil(
        clock,
        candidate.CandidateTime,
        runtime,
        capturePhaseTimings: true,
        timelineSource: source,
        expectedSequence: candidate.Sequence);

    True(!result.Completed);
    Equal("deadline-candidate-changed", result.Reason);
    True(clock.GetSnapshot().Active);
}

static void LowSpeedDeadlineBypassesOneFrame()
{
    var clock = new PreviewPlaybackClockState(frequency: 1_000_000);
    var runtime = new VirtualDeadlineRuntime(frequency: 1_000_000);
    clock.Start(TimeSpan.Zero, playbackRatePercent: 5, timestamp: 0);

    var result = PreviewFrameDeadlineWait.WaitUntil(
        clock,
        TimeSpan.FromMilliseconds(30),
        runtime);

    True(!result.Completed);
    Equal("deadline-exceeded-500ms-frame-bypass", result.Reason);
    True(clock.GetSnapshot().Active);
    True(!clock.GetSnapshot().Disabled);
}

static void PhaseDiagnosticsPreserveDeadlineOutcomes()
{
    foreach (var precise in new[] { true, false })
    foreach (var stopped in new[] { true, false })
    foreach (var milliseconds in new[] { 16, 600 })
    {
        PreviewFrameDeadlineWaitResult Run(bool capture)
        {
            var clock = new PreviewPlaybackClockState(1_000_000);
            var runtime = new VirtualDeadlineRuntime(1_000_000, precise);
            clock.Start(TimeSpan.Zero, 100, 0);
            if (stopped) clock.Stop(TimeSpan.Zero, 0);
            return PreviewFrameDeadlineWait.WaitUntil(clock, TimeSpan.FromMilliseconds(milliseconds), runtime, capturePhaseTimings: capture);
        }
        var measured = Run(true); var normal = Run(false);
        Equal(measured.Completed, normal.Completed); Equal(measured.Reason, normal.Reason);
        Equal(measured.StartTimestamp, normal.StartTimestamp); Equal(measured.EndTimestamp, normal.EndTimestamp);
        Equal(measured.InitialDeadlineTimestamp, normal.InitialDeadlineTimestamp); Equal(measured.FinalDeadlineTimestamp, normal.FinalDeadlineTimestamp);
        Equal(measured.CoarseWaitCount, normal.CoarseWaitCount); Equal(measured.PreciseWaitCount, normal.PreciseWaitCount);
        Equal(measured.YieldCount, normal.YieldCount); Equal(measured.SpinCount, normal.SpinCount);
        Equal(measured.RequestedMilliseconds, normal.RequestedMilliseconds); Equal(measured.ActualMilliseconds, normal.ActualMilliseconds);
        Equal(0L, normal.CoarseWaitDurationTicks + normal.PreciseWaitDurationTicks + normal.YieldDurationTicks + normal.SpinDurationTicks);
    }
}

static void IndependentPlayersDoNotShareCancellation()
{
    var first = new PreviewPlaybackClockState(1_000_000);
    var second = new PreviewPlaybackClockState(1_000_000);
    first.Start(TimeSpan.Zero, 100, 0);
    second.Start(TimeSpan.Zero, 100, 0);
    first.Stop(TimeSpan.Zero, 0);
    True(!PreviewFrameDeadlineWait.WaitUntil(first, TimeSpan.FromMilliseconds(16), new VirtualDeadlineRuntime(1_000_000)).Completed);
    True(PreviewFrameDeadlineWait.WaitUntil(second, TimeSpan.FromMilliseconds(16), new VirtualDeadlineRuntime(1_000_000)).Completed);
}

static void TimerAbsenceAndDisposalSelectFallback()
{
    using var absent = new PreviewHighResolutionWaitableTimer();
    True(!absent.TryWait(TimeSpan.FromMilliseconds(1)));
    absent.Dispose();
    absent.Initialize();
    True(!absent.IsAvailable);
    True(!absent.TryWait(TimeSpan.FromMilliseconds(1)));
    var clock = new PreviewPlaybackClockState(1_000_000);
    clock.Start(TimeSpan.Zero, 100, 0);
    var runtime = new VirtualDeadlineRuntime(1_000_000, preciseWaitSupported: false);
    var result = PreviewFrameDeadlineWait.WaitUntil(clock, TimeSpan.FromMilliseconds(16), runtime);
    True(result.Completed);
    True(result.CoarseWaitCount > 0);
    Equal(0, result.PreciseWaitCount);
}

static void FrequencyMismatchRejectsWaiting()
{
    var clock = new PreviewPlaybackClockState(1_000_000);
    clock.Start(TimeSpan.Zero, 100, 0);
    var result = PreviewFrameDeadlineWait.WaitUntil(clock, TimeSpan.FromMilliseconds(16), new VirtualDeadlineRuntime(10_000));
    True(!result.Completed);
    Equal("clock-runtime-frequency-mismatch", result.Reason);
}

static void StoppingDuringWaitCancelsDeadline()
{
    var clock = new PreviewPlaybackClockState(1_000_000);
    clock.Start(TimeSpan.Zero, 100, 0);
    var runtime = new StoppingDeadlineRuntime(clock);
    var result = PreviewFrameDeadlineWait.WaitUntil(clock, TimeSpan.FromMilliseconds(16), runtime);
    True(!result.Completed);
    Equal("clock-became-inactive", result.Reason);
    Equal(1, runtime.WaitCalls);
}

static void FailedTimerRechecksCancellation()
{
    var clock = new PreviewPlaybackClockState(1_000_000);
    clock.Start(TimeSpan.Zero, 100, 0);
    var runtime = new FailedTimerDeadlineRuntime(timestamp => clock.Stop(TimeSpan.Zero, timestamp));
    var result = PreviewFrameDeadlineWait.WaitUntil(clock, TimeSpan.FromMilliseconds(200), runtime);
    True(!result.Completed);
    Equal("clock-became-inactive", result.Reason);
    Equal(0, runtime.FallbackCalls);

    var lateClock = new PreviewPlaybackClockState(1_000_000);
    lateClock.Start(TimeSpan.Zero, 100, 0);
    var lateRuntime = new FailedTimerDeadlineRuntime(_ => { });
    True(PreviewFrameDeadlineWait.WaitUntil(lateClock, TimeSpan.FromMilliseconds(16), lateRuntime).Completed);
    Equal(0, lateRuntime.FallbackCalls);

    var source = new object();
    var candidateClock = new PreviewPlaybackClockState(1_000_000);
    candidateClock.Start(TimeSpan.Zero, 100, 0);
    PreviewFrameDeadlineRegistry.Register(source, TimeSpan.FromMilliseconds(200), true);
    True(PreviewFrameDeadlineRegistry.TryGet(source, out var candidate));
    var candidateRuntime = new FailedTimerDeadlineRuntime(_ => PreviewFrameDeadlineRegistry.Invalidate(source));
    var cancelled = PreviewFrameDeadlineWait.WaitUntil(candidateClock, candidate.CandidateTime, candidateRuntime,
        timelineSource: source, expectedSequence: candidate.Sequence);
    True(!cancelled.Completed);
    Equal("deadline-candidate-changed", cancelled.Reason);
    Equal(0, candidateRuntime.FallbackCalls);
}

static void True(bool value)
{
    if (!value)
        throw new InvalidOperationException("assertion failed");
}

static void Equal<T>(T expected, T actual) where T : IEquatable<T>
{
    if (!expected.Equals(actual))
        throw new InvalidOperationException($"expected {expected}, actual {actual}");
}

static void Near(double expected, double actual, double tolerance)
{
    if (Math.Abs(expected - actual) > tolerance)
        throw new InvalidOperationException($"expected {expected} +/- {tolerance}, actual {actual}");
}

sealed class VirtualDeadlineRuntime(long frequency, bool preciseWaitSupported = true)
    : IPreviewFrameDeadlineRuntime
{
    private long timestamp;
    public long Frequency { get; } = frequency;
    public long GetTimestamp() => timestamp;
    public void Advance(TimeSpan duration) =>
        timestamp += PreviewFrameDeadlineWait.DurationToTimestampTicks(duration, Frequency);
    public void Sleep(TimeSpan duration) => Advance(duration);
    public bool TryPreciseWait(TimeSpan duration)
    {
        if (!preciseWaitSupported)
            return false;
        Advance(duration);
        return true;
    }
    public void Yield() => timestamp += Math.Max(1, Frequency / 10_000);
    public void SpinOnce() => timestamp += Math.Max(1, Frequency / 100_000);
}

sealed class StoppingDeadlineRuntime(PreviewPlaybackClockState clock) : IPreviewFrameDeadlineRuntime
{
    public long Frequency => 1_000_000;
    public long GetTimestamp() => 0;
    internal int WaitCalls { get; private set; }
    public void Sleep(TimeSpan duration) => Stop();
    public bool TryPreciseWait(TimeSpan duration) { Stop(); return true; }
    public void Yield() => Stop();
    public void SpinOnce() => Stop();
    private void Stop() { WaitCalls++; clock.Stop(TimeSpan.Zero, 0); }
}

sealed class FailedTimerDeadlineRuntime(Action<long> onFailure) : IPreviewFrameDeadlineRuntime
{
    private long timestamp;
    public long Frequency => 1_000_000;
    public long GetTimestamp() => timestamp;
    internal int FallbackCalls { get; private set; }
    public bool TryPreciseWait(TimeSpan duration)
    {
        timestamp += 100_000;
        onFailure(timestamp);
        return false;
    }
    public void Sleep(TimeSpan duration) => FallbackCalls++;
    public void Yield() => FallbackCalls++;
    public void SpinOnce() => FallbackCalls++;
}

sealed class ReanchoringDeadlineRuntime(PreviewPlaybackClockState clock, long frequency)
    : IPreviewFrameDeadlineRuntime
{
    private long timestamp;
    private bool reanchored;
    public long Frequency { get; } = frequency;
    public long GetTimestamp() => timestamp;
    public void Sleep(TimeSpan duration)
    {
        timestamp += PreviewFrameDeadlineWait.DurationToTimestampTicks(duration, Frequency);
        if (reanchored)
            return;
        reanchored = true;
        clock.Reanchor(TimeSpan.FromMilliseconds(10), playbackRatePercent: 100, timestamp);
    }
    public bool TryPreciseWait(TimeSpan duration) => false;
    public void Yield() => timestamp += Math.Max(1, Frequency / 10_000);
    public void SpinOnce() => timestamp += Math.Max(1, Frequency / 100_000);
}

sealed class SeekingDeadlineRuntime(PreviewPlaybackClockState clock, long frequency)
    : IPreviewFrameDeadlineRuntime
{
    private long timestamp;
    private bool seeked;
    public long Frequency { get; } = frequency;
    public long GetTimestamp() => timestamp;
    public void Sleep(TimeSpan duration) => AdvanceAndSeek(duration);
    public bool TryPreciseWait(TimeSpan duration)
    {
        AdvanceAndSeek(duration);
        return true;
    }
    public void Yield() => AdvanceAndSeek(TimeSpan.FromMilliseconds(0.1));
    public void SpinOnce() => AdvanceAndSeek(TimeSpan.FromMilliseconds(0.01));

    private void AdvanceAndSeek(TimeSpan duration)
    {
        timestamp += PreviewFrameDeadlineWait.DurationToTimestampTicks(duration, Frequency);
        if (seeked)
            return;
        seeked = true;
        if (!clock.ReanchorOrSeek(
                TimeSpan.FromSeconds(5),
                playbackRatePercent: 100,
                timestamp))
        {
            throw new InvalidOperationException("seek discontinuity was not detected");
        }
    }
}

sealed class CandidateChangingDeadlineRuntime(object source, long frequency)
    : IPreviewFrameDeadlineRuntime
{
    private long timestamp;
    private bool changed;
    public long Frequency { get; } = frequency;
    public long GetTimestamp() => timestamp;
    public void Sleep(TimeSpan duration) => AdvanceAndChange(duration);
    public bool TryPreciseWait(TimeSpan duration)
    {
        AdvanceAndChange(duration);
        return true;
    }
    public void Yield() => AdvanceAndChange(TimeSpan.FromMilliseconds(0.1));
    public void SpinOnce() => AdvanceAndChange(TimeSpan.FromMilliseconds(0.01));

    private void AdvanceAndChange(TimeSpan duration)
    {
        timestamp += PreviewFrameDeadlineWait.DurationToTimestampTicks(duration, Frequency);
        if (changed)
            return;
        changed = true;
        PreviewFrameDeadlineRegistry.Register(
            source,
            TimeSpan.FromMilliseconds(40),
            playing: true);
    }
}
