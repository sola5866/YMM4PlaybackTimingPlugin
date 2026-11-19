namespace YMM4PlaybackTimingPlugin.Diagnostics;

// This model owns no native object, UI dispatcher, clock override, or file I/O.
internal sealed class PreviewMeasurements(long frequency)
{
    internal readonly record struct Ticket(long Epoch);
    internal sealed record Snapshot(bool Enabled, bool Playing, bool Overflow, long Draws, long Sends,
        long Failures, double Seconds, double? DrawFps, double? SendFps, double? MaxIntervalMs, int? LastError);
    private const int Capacity = 16384;
    private readonly object gate = new();
    private readonly Queue<long> draws = new(), sends = new();
    private readonly Queue<(long Stamp, double Ms)> intervals = new();
    private bool enabled, playing, overflow;
    private long epoch, drawCount, sendCount, failures, elapsed, started, segment;
    private long lastSend = -1;
    private long lastStateStamp = long.MinValue;
    private int? lastError;

    internal void SetEnabled(bool value, bool isPlaying, long now)
    {
        lock (gate)
        {
            Accumulate(now);
            enabled = value;
            playing = isPlaying;
            lastStateStamp = now;
            started = now;
            BreakCore(now);
        }
    }

    internal void SetPlaying(bool value, long now)
    {
        lock (gate)
        {
            if (now < lastStateStamp) return;
            lastStateStamp = now;
            if (playing == value) return;
            Accumulate(now);
            playing = value;
            started = now;
            BreakCore(now);
        }
    }

    internal void Break(long now) { lock (gate) BreakCore(now); }
    private void BreakCore(long now)
    {
        epoch++;
        lastStateStamp = Math.Max(lastStateStamp, now);
        segment = now;
        lastSend = -1;
        draws.Clear(); sends.Clear(); intervals.Clear();
        overflow = false;
    }
    private void Accumulate(long now)
    {
        if (enabled && playing) elapsed += Math.Max(0, now - started);
        started = now;
    }
    internal void Reset(long now)
    {
        lock (gate)
        {
            drawCount = sendCount = failures = elapsed = 0;
            lastError = null;
            started = now;
            BreakCore(now);
        }
    }
    internal Ticket? Capture()
    {
        lock (gate) return enabled && playing ? new Ticket(epoch) : null;
    }
    private bool Accept(Ticket ticket) => enabled && playing && ticket.Epoch == epoch;
    internal void Draw(Ticket ticket, long now)
    {
        lock (gate)
        {
            if (!Accept(ticket)) return;
            drawCount++;
            Add(draws, now);
        }
    }
    internal void Present(Ticket ticket, int result, long now)
    {
        lock (gate)
        {
            if (!Accept(ticket)) return;
            if (result < 0) { failures++; lastError = result; return; }
            if (result != 0) return; // S_OK only; occlusion is not a delivered frame.
            sendCount++;
            Add(sends, now);
            TrimIntervals(now);
            if (lastSend >= 0 && now >= lastSend)
            {
                if (intervals.Count == Capacity) { intervals.Dequeue(); overflow = true; }
                intervals.Enqueue((now, (now - lastSend) * 1000d / frequency));
            }
            lastSend = now;
        }
    }
    private void Add(Queue<long> queue, long now)
    {
        Trim(queue, now);
        if (queue.Count == Capacity) { queue.Dequeue(); overflow = true; }
        queue.Enqueue(now);
    }
    private void Trim(Queue<long> queue, long now)
    { while (queue.TryPeek(out var stamp) && stamp <= now - frequency) queue.Dequeue(); }
    private void TrimIntervals(long now)
    { while (intervals.TryPeek(out var interval) && interval.Stamp <= now - frequency * 5) intervals.Dequeue(); }
    internal Snapshot Read(long now)
    {
        lock (gate)
        {
            Trim(draws, now); Trim(sends, now); TrimIntervals(now);
            var valid = enabled && playing && !overflow && now - segment >= frequency;
            var seconds = (elapsed + (enabled && playing ? Math.Max(0, now - started) : 0)) / (double)frequency;
            return new(enabled, playing, overflow, drawCount, sendCount, failures, seconds,
                valid ? draws.Count : null, valid ? sends.Count : null,
                enabled && playing && !overflow && intervals.Count != 0 ? intervals.Max(x => x.Ms) : null, lastError);
        }
    }
}
