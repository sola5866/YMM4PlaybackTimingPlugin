using System.Diagnostics;

namespace YMM4PlaybackTimingPlugin.Playback;

internal interface IPreviewFrameDeadlineRuntime
{
    long Frequency { get; }
    long GetTimestamp();
    void Sleep(TimeSpan duration);
    bool TryPreciseWait(TimeSpan duration);
    void Yield();
    void SpinOnce();
}

internal readonly record struct PreviewFrameWaitTelemetry(
    long CoarseRequestedTicks,
    long PreciseRequestedTicks,
    long MaximumCoarseCallTicks,
    long MaximumPreciseCallTicks,
    long MaximumYieldCallTicks,
    long MaximumSpinCallTicks);

internal readonly record struct PreviewFrameDeadlineWaitResult(
    bool Completed,
    string Reason,
    double RequestedMilliseconds,
    double ActualMilliseconds,
    int CoarseWaitCount,
    int PreciseWaitCount,
    int YieldCount,
    int SpinCount,
    int LateDeadlineBypassCount,
    long Frequency,
    long StartTimestamp,
    long EndTimestamp,
    long InitialDeadlineTimestamp,
    long FinalDeadlineTimestamp,
    long CoarseWaitDurationTicks,
    long PreciseWaitDurationTicks,
    long YieldDurationTicks,
    long SpinDurationTicks,
    PreviewFrameWaitTelemetry Telemetry)
{
    internal double OvershootMilliseconds => Math.Max(0, ActualMilliseconds - RequestedMilliseconds);
    internal double InitialDeadlineOffsetMilliseconds =>
        ToMilliseconds(InitialDeadlineTimestamp - StartTimestamp);
    internal double InitialDeadlineMilliseconds => ToMilliseconds(Math.Max(0, InitialDeadlineTimestamp - StartTimestamp));
    internal double FinalDeadlineMilliseconds => ToMilliseconds(Math.Max(0, FinalDeadlineTimestamp - StartTimestamp));
    internal double AnchorDeadlineShiftMilliseconds => ToMilliseconds(FinalDeadlineTimestamp - InitialDeadlineTimestamp);
    internal double SchedulerWakeDelayMilliseconds => ToMilliseconds(Math.Max(0, EndTimestamp - FinalDeadlineTimestamp));
    internal double CoarseWaitDurationMilliseconds => ToMilliseconds(CoarseWaitDurationTicks);
    internal double PreciseWaitDurationMilliseconds => ToMilliseconds(PreciseWaitDurationTicks);
    internal double YieldDurationMilliseconds => ToMilliseconds(YieldDurationTicks);
    internal double SpinDurationMilliseconds => ToMilliseconds(SpinDurationTicks);
    internal double CoarseWakeDelayMilliseconds =>
        ToMilliseconds(Math.Max(0, CoarseWaitDurationTicks - Telemetry.CoarseRequestedTicks));
    internal double PreciseWakeDelayMilliseconds =>
        ToMilliseconds(Math.Max(0, PreciseWaitDurationTicks - Telemetry.PreciseRequestedTicks));
    internal double YieldStallMilliseconds =>
        ToMilliseconds(Math.Max(0, Telemetry.MaximumYieldCallTicks - MillisecondsToTicks(0.5)));
    internal double SpinPreemptionMilliseconds =>
        ToMilliseconds(Math.Max(0, Telemetry.MaximumSpinCallTicks - MillisecondsToTicks(0.25)));

    private double ToMilliseconds(long ticks) =>
        Frequency <= 0 ? 0 : ticks * 1000.0 / Frequency;

    private long MillisecondsToTicks(double milliseconds) =>
        Frequency <= 0 ? 0 : (long)Math.Ceiling(milliseconds * Frequency / 1000.0);
}

internal readonly record struct PreviewFrameWaitProfile(string Name, double SpinBoundaryMilliseconds)
{
    internal static PreviewFrameWaitProfile Balanced { get; } = new("balanced", 0.25);
    internal static PreviewFrameWaitProfile LowCpu { get; } = new("low-cpu", 0.05);
    internal static PreviewFrameWaitProfile LowJitter { get; } = new("low-jitter", 0.75);

}

internal static class PreviewFrameDeadlineWait
{
    internal const double CoarseBoundaryMilliseconds = 2.0;
    internal const double MinimumCoarseSleepMilliseconds = 1.0;
    internal const double MaximumWaitSliceMilliseconds = 50.0;
    internal const double MaximumAcceptedDeadlineMilliseconds = 500.0;

    internal static PreviewFrameDeadlineWaitResult WaitUntil(
        PreviewPlaybackClockState clock,
        TimeSpan candidateTime,
        IPreviewFrameDeadlineRuntime runtime,
        bool capturePhaseTimings = false,
        object? timelineSource = null,
        long expectedSequence = 0,
        PreviewFrameWaitProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(runtime);
        var waitProfile = profile ?? PreviewFrameWaitProfile.Balanced;
        if (string.IsNullOrWhiteSpace(waitProfile.Name)
            || !double.IsFinite(waitProfile.SpinBoundaryMilliseconds)
            || waitProfile.SpinBoundaryMilliseconds <= 0
            || waitProfile.SpinBoundaryMilliseconds >= CoarseBoundaryMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(profile));
        var start = runtime.GetTimestamp();
        if (runtime.Frequency <= 0 || runtime.Frequency != clock.Frequency)
            return Failed("clock-runtime-frequency-mismatch", start, start, 0, 0, 0, 0, 0, runtime.Frequency);
        if (!clock.TryGetDeadline(candidateTime, start, out var generation, out var initialDeadline))
            return Failed("clock-inactive", start, start, 0, 0, 0, 0, 0, runtime.Frequency);

        if (timelineSource is not null
            && !PreviewFrameDeadlineRegistry.IsCurrentPlaying(timelineSource, expectedSequence))
        {
            return Failed(
                "deadline-candidate-changed",
                start,
                start,
                0,
                0,
                0,
                0,
                0,
                runtime.Frequency);
        }

        if (initialDeadline <= start)
        {
            var committed = clock.ValidateWaitCompletion(generation);
            var end = runtime.GetTimestamp();
            return new(
                committed,
                committed ? "completed-late-bypass" : "commit-rejected",
                0,
                ToMilliseconds(Math.Max(0, end - start), runtime.Frequency),
                0,
                0,
                0,
                0,
                1,
                runtime.Frequency,
                start,
                end,
                initialDeadline,
                initialDeadline,
                0,
                0,
                0,
                0,
                default);
        }

        var requestedTicks = Math.Max(0, initialDeadline - start);
        var requestedMilliseconds = ToMilliseconds(requestedTicks, runtime.Frequency);
        if (requestedMilliseconds > MaximumAcceptedDeadlineMilliseconds)
        {
            return Failed(
                "deadline-exceeded-500ms-frame-bypass",
                start,
                start,
                requestedMilliseconds,
                0,
                0,
                0,
                0,
                runtime.Frequency,
                initialDeadline,
                initialDeadline);
        }

        var coarseBoundaryTicks = MillisecondsToTimestampTicks(CoarseBoundaryMilliseconds, runtime.Frequency);
        var spinBoundaryTicks = MillisecondsToTimestampTicks(
            waitProfile.SpinBoundaryMilliseconds,
            runtime.Frequency);
        var maximumWaitSliceTicks = MillisecondsToTimestampTicks(MaximumWaitSliceMilliseconds, runtime.Frequency);
        var minimumCoarseSleepTicks = MillisecondsToTimestampTicks(MinimumCoarseSleepMilliseconds, runtime.Frequency);
        var coarseCount = 0;
        var preciseCount = 0;
        var yieldCount = 0;
        var spinCount = 0;
        var coarseDurationTicks = 0L;
        var preciseDurationTicks = 0L;
        var yieldDurationTicks = 0L;
        var spinDurationTicks = 0L;
        var coarseRequestedTicks = 0L;
        var preciseRequestedTicks = 0L;
        var maximumCoarseCallTicks = 0L;
        var maximumPreciseCallTicks = 0L;
        var maximumYieldCallTicks = 0L;
        var maximumSpinCallTicks = 0L;
        var finalDeadline = initialDeadline;
        var pendingPhase = 0;
        var pendingPhaseStart = 0L;
        var pendingRequestedTicks = 0L;

        while (true)
        {
            var now = runtime.GetTimestamp();
            if (capturePhaseTimings && pendingPhase != 0)
            {
                var elapsed = Math.Max(0, now - pendingPhaseStart);
                if (pendingPhase == 1)
                {
                    coarseDurationTicks += elapsed;
                    coarseRequestedTicks += pendingRequestedTicks;
                    maximumCoarseCallTicks = Math.Max(maximumCoarseCallTicks, elapsed);
                }
                else if (pendingPhase == 2)
                {
                    yieldDurationTicks += elapsed;
                    maximumYieldCallTicks = Math.Max(maximumYieldCallTicks, elapsed);
                }
                else if (pendingPhase == 3)
                {
                    spinDurationTicks += elapsed;
                    maximumSpinCallTicks = Math.Max(maximumSpinCallTicks, elapsed);
                }
                else
                {
                    preciseDurationTicks += elapsed;
                    preciseRequestedTicks += pendingRequestedTicks;
                    maximumPreciseCallTicks = Math.Max(maximumPreciseCallTicks, elapsed);
                }
                pendingPhase = 0;
                pendingRequestedTicks = 0;
            }

            var telemetry = new PreviewFrameWaitTelemetry(
                coarseRequestedTicks,
                preciseRequestedTicks,
                maximumCoarseCallTicks,
                maximumPreciseCallTicks,
                maximumYieldCallTicks,
                maximumSpinCallTicks);
            if (timelineSource is not null
                && !PreviewFrameDeadlineRegistry.IsCurrentPlaying(timelineSource, expectedSequence))
            {
                return Failed(
                    "deadline-candidate-changed",
                    start,
                    now,
                    requestedMilliseconds,
                    coarseCount,
                    preciseCount,
                    yieldCount,
                    spinCount,
                    runtime.Frequency,
                    initialDeadline,
                    finalDeadline,
                    coarseDurationTicks,
                    preciseDurationTicks,
                    yieldDurationTicks,
                    spinDurationTicks,
                    telemetry);
            }
            if (!clock.TryGetDeadline(candidateTime, now, out var currentGeneration, out var deadline))
            {
                return Failed(
                    "clock-became-inactive",
                    start,
                    now,
                    requestedMilliseconds,
                    coarseCount,
                    preciseCount,
                    yieldCount,
                    spinCount,
                    runtime.Frequency,
                    initialDeadline,
                    finalDeadline,
                    coarseDurationTicks,
                    preciseDurationTicks,
                    yieldDurationTicks,
                    spinDurationTicks,
                    telemetry);
            }
            if (currentGeneration != generation)
            {
                return Failed(
                    "playback-session-changed",
                    start,
                    now,
                    requestedMilliseconds,
                    coarseCount,
                    preciseCount,
                    yieldCount,
                    spinCount,
                    runtime.Frequency,
                    initialDeadline,
                    finalDeadline,
                    coarseDurationTicks,
                    preciseDurationTicks,
                    yieldDurationTicks,
                    spinDurationTicks,
                    telemetry);
            }

            finalDeadline = deadline;
            var ticksLeft = deadline - now;
            if (ticksLeft <= 0)
            {
                var completed = clock.ValidateWaitCompletion(generation);
                var end = runtime.GetTimestamp();
                return new(
                    completed,
                    completed ? "completed" : "commit-rejected",
                    requestedMilliseconds,
                    ToMilliseconds(Math.Max(0, end - start), runtime.Frequency),
                    coarseCount,
                    preciseCount,
                    yieldCount,
                    spinCount,
                    0,
                    runtime.Frequency,
                    start,
                    end,
                    initialDeadline,
                    finalDeadline,
                    coarseDurationTicks,
                    preciseDurationTicks,
                    yieldDurationTicks,
                    spinDurationTicks,
                    telemetry);
            }

            if (ticksLeft > spinBoundaryTicks)
            {
                var preciseTicks = Math.Min(ticksLeft - spinBoundaryTicks, maximumWaitSliceTicks);
                if (preciseTicks > 0)
                {
                    if (capturePhaseTimings)
                    {
                        pendingPhase = 4;
                        pendingPhaseStart = now;
                        pendingRequestedTicks = preciseTicks;
                    }
                    if (runtime.TryPreciseWait(TimestampTicksToTimeSpan(preciseTicks, runtime.Frequency)))
                    {
                        preciseCount++;
                        continue;
                    }
                    pendingPhase = 0;
                    pendingRequestedTicks = 0;
                    // A failed native wait may still have consumed its entire
                    // bounded timeout. Recheck cancellation and the deadline
                    // before using Sleep/Yield with the old time remaining.
                    now = runtime.GetTimestamp();
                    if (timelineSource is not null
                        && !PreviewFrameDeadlineRegistry.IsCurrentPlaying(timelineSource, expectedSequence))
                        continue;
                    if (!clock.TryGetDeadline(candidateTime, now, out var afterWaitGeneration, out deadline)
                        || afterWaitGeneration != generation)
                        continue;
                    finalDeadline = deadline;
                    ticksLeft = deadline - now;
                    if (ticksLeft <= 0)
                        continue;
                }
            }

            if (ticksLeft > coarseBoundaryTicks)
            {
                var sleepTicks = Math.Min(ticksLeft - coarseBoundaryTicks, maximumWaitSliceTicks);
                if (sleepTicks >= minimumCoarseSleepTicks)
                {
                    if (capturePhaseTimings)
                    {
                        pendingPhase = 1;
                        pendingPhaseStart = now;
                        pendingRequestedTicks = sleepTicks;
                    }
                    runtime.Sleep(TimestampTicksToTimeSpan(sleepTicks, runtime.Frequency));
                    coarseCount++;
                }
                else
                {
                    if (capturePhaseTimings)
                    {
                        pendingPhase = 2;
                        pendingPhaseStart = now;
                    }
                    runtime.Yield();
                    yieldCount++;
                }
            }
            else if (ticksLeft > spinBoundaryTicks)
            {
                if (capturePhaseTimings)
                {
                    pendingPhase = 2;
                    pendingPhaseStart = now;
                }
                runtime.Yield();
                yieldCount++;
            }
            else
            {
                if (capturePhaseTimings)
                {
                    pendingPhase = 3;
                    pendingPhaseStart = now;
                }
                runtime.SpinOnce();
                spinCount++;
            }
        }
    }

    internal static long DurationToTimestampTicks(TimeSpan duration, long frequency)
    {
        if (duration <= TimeSpan.Zero || frequency <= 0)
            return 0;
        var ticks = Math.Ceiling(duration.TotalSeconds * frequency);
        return double.IsFinite(ticks) && ticks < long.MaxValue
            ? Math.Max(1, (long)ticks)
            : long.MaxValue;
    }

    private static PreviewFrameDeadlineWaitResult Failed(
        string reason,
        long start,
        long end,
        double requestedMilliseconds,
        int coarse,
        int precise,
        int yields,
        int spins,
        long frequency,
        long initialDeadline = 0,
        long finalDeadline = 0,
        long coarseDurationTicks = 0,
        long preciseDurationTicks = 0,
        long yieldDurationTicks = 0,
        long spinDurationTicks = 0,
        PreviewFrameWaitTelemetry telemetry = default) =>
        new(
            false,
            reason,
            requestedMilliseconds,
            frequency <= 0 ? 0 : ToMilliseconds(Math.Max(0, end - start), frequency),
            coarse,
            precise,
            yields,
            spins,
            0,
            frequency,
            start,
            end,
            initialDeadline,
            finalDeadline,
            coarseDurationTicks,
            preciseDurationTicks,
            yieldDurationTicks,
            spinDurationTicks,
            telemetry);

    private static long MillisecondsToTimestampTicks(double milliseconds, long frequency) =>
        Math.Max(1, (long)Math.Ceiling(milliseconds * frequency / 1000.0));

    private static TimeSpan TimestampTicksToTimeSpan(long ticks, long frequency) =>
        TimeSpan.FromSeconds(ticks / (double)frequency);

    private static double ToMilliseconds(long ticks, long frequency) =>
        ticks * 1000.0 / frequency;
}

