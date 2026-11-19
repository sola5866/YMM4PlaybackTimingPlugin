using System.Diagnostics;

namespace YMM4PlaybackTimingPlugin.Playback;

internal readonly record struct PreviewPlaybackClockSnapshot(
    bool Active,
    bool Disabled,
    string Reason,
    long Generation,
    long AnchorMediaTicks,
    long AnchorTimestamp,
    double PlaybackRateRatio,
    long LastPublishedMediaTicks,
    long MaximumMediaTicks);

internal readonly record struct PreviewPlaybackDiagnosticSnapshot(
    long ProjectedMediaTicks,
    long AnchorCorrectionSignedTotalTicks,
    long AnchorCorrectionAbsoluteTotalTicks,
    long AnchorCorrectionCount);

/// <summary>
/// A per-player monotonic media clock.  YMM4's audio position remains the
/// authority; Stopwatch interpolates between bounded backend samples without
/// depending on completion of the native UI position notifications.
/// </summary>
internal sealed class PreviewPlaybackClockState
{
    private const long MaximumAnchorCorrectionTicks = 250 * TimeSpan.TicksPerMillisecond;
    private const long SeekDiscontinuityTicks = 100 * TimeSpan.TicksPerMillisecond;
    private readonly object gate = new();
    private readonly long frequency;
    private bool active;
    private bool pausing;
    private bool waitingForBackend;
    private bool disabled;
    private string reason = "not-started";
    private long generation;
    private long anchorMediaTicks;
    private long anchorTimestamp;
    private double playbackRateRatio = 1.0;
    private long lastPublishedMediaTicks;
    private long maximumMediaTicks = TimeSpan.MaxValue.Ticks;
    private long anchorCorrectionSignedTotalTicks;
    private long anchorCorrectionAbsoluteTotalTicks;
    private long anchorCorrectionCount;

    internal PreviewPlaybackClockState(long? frequency = null)
    {
        this.frequency = frequency ?? Stopwatch.Frequency;
        if (this.frequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequency));
    }

    internal long Frequency => frequency;

    internal void Start(
        TimeSpan rawPosition,
        double playbackRatePercent,
        long timestamp,
        TimeSpan? maximumPosition = null,
        bool waitForBackend = false)
    {
        lock (gate)
        {
            generation++;
            disabled = !TryRateRatio(playbackRatePercent, out playbackRateRatio);
            active = !disabled && !waitForBackend;
            pausing = false;
            waitingForBackend = !disabled && waitForBackend;
            reason = disabled ? "invalid-playback-rate" : waitForBackend ? "starting-audio" : "active";
            anchorMediaTicks = Clamp(rawPosition.Ticks, 0, ResolveMaximum(maximumPosition));
            anchorTimestamp = timestamp;
            lastPublishedMediaTicks = anchorMediaTicks;
            maximumMediaTicks = ResolveMaximum(maximumPosition);
            anchorCorrectionSignedTotalTicks = 0;
            anchorCorrectionAbsoluteTotalTicks = 0;
            anchorCorrectionCount = 0;
        }
    }

    internal void Stop(TimeSpan rawPosition, long timestamp)
    {
        lock (gate)
        {
            active = false;
            pausing = false;
            waitingForBackend = false;
            generation++;
            anchorMediaTicks = Clamp(rawPosition.Ticks, 0, maximumMediaTicks);
            anchorTimestamp = timestamp;
            lastPublishedMediaTicks = anchorMediaTicks;
            reason = disabled ? reason : "stopped";
        }
    }

    internal void Reanchor(
        TimeSpan rawPosition,
        double playbackRatePercent,
        long timestamp,
        TimeSpan? maximumPosition = null)
    {
        lock (gate)
        {
            if (!active || disabled)
                return;
            if (!TryRateRatio(playbackRatePercent, out var ratio))
            {
                DisableLocked("invalid-playback-rate");
                return;
            }

            var nextMaximum = ResolveMaximum(maximumPosition);
            var rawTicks = Clamp(rawPosition.Ticks, 0, nextMaximum);
            var projectedBeforeAnchor = ProjectLocked(timestamp);
            var correctionTicks = rawTicks - projectedBeforeAnchor;
            if (Math.Abs(correctionTicks) > MaximumAnchorCorrectionTicks)
            {
                DisableLocked("anchor-correction-exceeded-250ms");
                return;
            }

            anchorCorrectionSignedTotalTicks += correctionTicks;
            anchorCorrectionAbsoluteTotalTicks += Math.Abs(correctionTicks);
            anchorCorrectionCount++;
            anchorMediaTicks = rawTicks;
            anchorTimestamp = timestamp;
            playbackRateRatio = ratio;
            maximumMediaTicks = nextMaximum;
            lastPublishedMediaTicks = Clamp(lastPublishedMediaTicks, 0, maximumMediaTicks);
        }
    }

    // A stop request cancels old waits without stopping the live audio clock.
    internal void InvalidateWaits()
    {
        lock (gate) generation++;
    }

    // Called only at the first backend Stop, after the live audio drain.
    internal bool BeginAudioPause(TimeSpan audioPosition, long timestamp)
    {
        lock (gate)
        {
            if ((!active && !waitingForBackend) || disabled) return false;
            active = false;
            pausing = true;
            waitingForBackend = false;
            generation++;
            anchorMediaTicks = Clamp(audioPosition.Ticks, 0, maximumMediaTicks);
            anchorTimestamp = timestamp;
            lastPublishedMediaTicks = anchorMediaTicks;
            reason = "pausing-audio";
            return true;
        }
    }

    internal bool BeginAudioBackendChange(long timestamp, out TimeSpan position)
    {
        lock (gate)
        {
            position = TimeSpan.FromTicks(lastPublishedMediaTicks);
            if (waitingForBackend && !disabled) return true;
            if (!active || disabled) return false;
            lastPublishedMediaTicks = Math.Max(lastPublishedMediaTicks, ProjectLocked(timestamp));
            position = TimeSpan.FromTicks(lastPublishedMediaTicks);
            anchorMediaTicks = lastPublishedMediaTicks;
            anchorTimestamp = timestamp;
            active = false;
            waitingForBackend = true;
            generation++;
            reason = "waiting-audio-backend";
            return true;
        }
    }

    internal bool ResumeAudioBackend(TimeSpan audioPosition, double rate, long timestamp, TimeSpan? maximum)
    {
        lock (gate)
        {
            if (!waitingForBackend || disabled || !TryRateRatio(rate, out var ratio)) return false;
            var limit = ResolveMaximum(maximum);
            var ticks = Clamp(audioPosition.Ticks, 0, limit);
            if (ticks < lastPublishedMediaTicks) return false;
            waitingForBackend = false;
            active = true;
            generation++;
            anchorMediaTicks = lastPublishedMediaTicks = ticks;
            anchorTimestamp = timestamp;
            playbackRateRatio = ratio;
            maximumMediaTicks = limit;
            reason = "active-after-audio-backend";
            return true;
        }
    }

    internal bool CompleteAudioPause(TimeSpan audioPosition, long timestamp, Action<TimeSpan> commitNative)
    {
        lock (gate)
        {
            if (!pausing || disabled) return false;
            var position = TimeSpan.FromTicks(Clamp(audioPosition.Ticks, 0, maximumMediaTicks));
            // Caller holds Activation before this clock gate. Native Position's
            // verified setter only assigns; native notifications happen later.
            commitNative(position);
            pausing = false;
            anchorMediaTicks = lastPublishedMediaTicks = position.Ticks;
            anchorTimestamp = timestamp;
            reason = "stopped-audio";
            return true;
        }
    }

    internal bool ReanchorOrSeek(
        TimeSpan rawPosition,
        double playbackRatePercent,
        long timestamp,
        TimeSpan? maximumPosition = null)
    {
        lock (gate)
        {
            if (!active || disabled)
                return false;
            if (!TryRateRatio(playbackRatePercent, out var ratio))
            {
                DisableLocked("invalid-playback-rate");
                return false;
            }

            var nextMaximum = ResolveMaximum(maximumPosition);
            var rawTicks = Clamp(rawPosition.Ticks, 0, nextMaximum);
            var projectedBeforeAnchor = ProjectLocked(timestamp);
            var correctionTicks = rawTicks - projectedBeforeAnchor;
            if (Math.Abs(correctionTicks) < SeekDiscontinuityTicks)
            {
                anchorCorrectionSignedTotalTicks += correctionTicks;
                anchorCorrectionAbsoluteTotalTicks += Math.Abs(correctionTicks);
                anchorCorrectionCount++;
                anchorMediaTicks = rawTicks;
                anchorTimestamp = timestamp;
                playbackRateRatio = ratio;
                maximumMediaTicks = nextMaximum;
                lastPublishedMediaTicks = Clamp(lastPublishedMediaTicks, 0, maximumMediaTicks);
                return false;
            }

            generation++;
            anchorMediaTicks = rawTicks;
            anchorTimestamp = timestamp;
            playbackRateRatio = ratio;
            maximumMediaTicks = nextMaximum;
            lastPublishedMediaTicks = rawTicks;
            anchorCorrectionSignedTotalTicks = 0;
            anchorCorrectionAbsoluteTotalTicks = 0;
            anchorCorrectionCount = 0;
            reason = "active-after-seek";
            return true;
        }
    }

    internal TimeSpan GetPosition(TimeSpan rawFallback, long timestamp)
    {
        lock (gate)
        {
            if (pausing || waitingForBackend) return TimeSpan.FromTicks(lastPublishedMediaTicks);
            if (!active || disabled)
                return rawFallback;
            var projected = ProjectLocked(timestamp);
            if (projected > lastPublishedMediaTicks)
                lastPublishedMediaTicks = projected;
            return TimeSpan.FromTicks(lastPublishedMediaTicks);
        }
    }

    internal bool TryGetDeadline(
        TimeSpan candidateTime,
        long timestamp,
        out long sessionGeneration,
        out long deadlineTimestamp)
    {
        lock (gate)
        {
            sessionGeneration = generation;
            deadlineTimestamp = timestamp;
            if (!active || disabled || candidateTime < TimeSpan.Zero)
                return false;

            var candidateTicks = Math.Min(candidateTime.Ticks, maximumMediaTicks);
            if (candidateTicks <= lastPublishedMediaTicks)
                return true;

            var mediaTicksFromAnchor = candidateTicks - anchorMediaTicks;
            if (mediaTicksFromAnchor <= 0)
                return true;
            var stopwatchTicks = Math.Ceiling(
                mediaTicksFromAnchor * (double)frequency
                / TimeSpan.TicksPerSecond
                / playbackRateRatio);
            if (!double.IsFinite(stopwatchTicks) || stopwatchTicks >= long.MaxValue)
                return false;
            var offset = Math.Max(0, (long)stopwatchTicks);
            deadlineTimestamp = offset > long.MaxValue - anchorTimestamp
                ? long.MaxValue
                : anchorTimestamp + offset;
            return true;
        }
    }

    // Waiting does not prove that native Present accepted the frame. Only audio
    // samples and GetPosition interpolation may publish media time.
    internal bool ValidateWaitCompletion(long sessionGeneration)
    {
        lock (gate)
        {
            return active && !disabled && generation == sessionGeneration;
        }
    }

    internal void Disable(string disabledReason)
    {
        lock (gate)
            DisableLocked(disabledReason);
    }

    internal PreviewPlaybackClockSnapshot GetSnapshot()
    {
        lock (gate)
        {
            return new(
                active,
                disabled,
                reason,
                generation,
                anchorMediaTicks,
                anchorTimestamp,
                playbackRateRatio,
                lastPublishedMediaTicks,
                maximumMediaTicks);
        }
    }

    internal PreviewPlaybackDiagnosticSnapshot GetDiagnosticSnapshot(long timestamp)
    {
        lock (gate)
        {
            return new(
                ProjectLocked(timestamp),
                anchorCorrectionSignedTotalTicks,
                anchorCorrectionAbsoluteTotalTicks,
                anchorCorrectionCount);
        }
    }

    private long ProjectLocked(long timestamp)
    {
        var elapsed = Math.Max(0, timestamp - anchorTimestamp);
        var mediaDelta = elapsed * (double)TimeSpan.TicksPerSecond / frequency * playbackRateRatio;
        if (!double.IsFinite(mediaDelta))
            return lastPublishedMediaTicks;
        var projected = mediaDelta >= long.MaxValue - anchorMediaTicks
            ? long.MaxValue
            : anchorMediaTicks + (long)Math.Floor(mediaDelta);
        return Clamp(projected, 0, maximumMediaTicks);
    }

    private void DisableLocked(string disabledReason)
    {
        disabled = true;
        active = false;
        pausing = false;
        waitingForBackend = false;
        reason = disabledReason;
        generation++;
    }

    private static bool TryRateRatio(double percent, out double ratio)
    {
        ratio = percent / 100.0;
        return double.IsFinite(ratio) && ratio > 0;
    }

    private static long ResolveMaximum(TimeSpan? maximumPosition) =>
        maximumPosition is { Ticks: >= 0 } value ? value.Ticks : TimeSpan.MaxValue.Ticks;

    private static long Clamp(long value, long minimum, long maximum) =>
        Math.Min(Math.Max(value, minimum), maximum);
}

