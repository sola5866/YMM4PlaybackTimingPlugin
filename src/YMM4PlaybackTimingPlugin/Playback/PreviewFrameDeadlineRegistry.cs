using System.Runtime.CompilerServices;

namespace YMM4PlaybackTimingPlugin.Playback;

internal readonly record struct PreviewFrameDeadlineSnapshot(
    TimeSpan CandidateTime,
    bool Playing,
    long Sequence);

internal static class PreviewFrameDeadlineRegistry
{
    private sealed class Holder
    {
        internal readonly object Gate = new();
        internal PreviewFrameDeadlineSnapshot Snapshot;
        internal bool Pending;
    }

    private static ConditionalWeakTable<object, Holder> states = new();
    private static long sequence;

    internal static void Register(object timelineSource, TimeSpan candidateTime, bool playing)
        => RegisterCandidate(timelineSource, candidateTime, playing);

    internal static long RegisterPending(object timelineSource, TimeSpan candidateTime)
        => RegisterCandidate(timelineSource, candidateTime, playing: false, pending: true);

    private static long RegisterCandidate(object timelineSource, TimeSpan candidateTime, bool playing, bool pending = false)
    {
        var holder = states.GetValue(timelineSource, static _ => new Holder());
        var next = Interlocked.Increment(ref sequence);
        lock (holder.Gate)
        {
            holder.Snapshot = new(candidateTime, playing, next);
            holder.Pending = pending;
        }
        return next;
    }

    internal static void Complete(object timelineSource, long expectedSequence, bool playing)
    {
        if (!states.TryGetValue(timelineSource, out var holder)) return;
        lock (holder.Gate)
            if (holder.Pending && holder.Snapshot.Sequence == expectedSequence && expectedSequence != 0)
            {
                holder.Snapshot = holder.Snapshot with { Playing = playing };
                holder.Pending = false;
            }
    }

    internal static bool TryGet(object timelineSource, out PreviewFrameDeadlineSnapshot snapshot)
    {
        if (!states.TryGetValue(timelineSource, out var holder))
        {
            snapshot = default;
            return false;
        }
        lock (holder.Gate)
        {
            snapshot = holder.Snapshot;
            return snapshot.Sequence != 0;
        }
    }

    internal static bool IsCurrentPlaying(object timelineSource, long expectedSequence)
    {
        if (!states.TryGetValue(timelineSource, out var holder))
            return false;
        lock (holder.Gate)
            return holder.Snapshot.Playing && holder.Snapshot.Sequence == expectedSequence;
    }

    internal static void Invalidate(object timelineSource)
    {
        var holder = states.GetValue(timelineSource, static _ => new Holder());
        var next = Interlocked.Increment(ref sequence);
        lock (holder.Gate)
        {
            holder.Snapshot = new(TimeSpan.Zero, false, next);
            holder.Pending = false;
        }
    }

    internal static void Forget(object source)=>states.Remove(source);

    internal static void Reset()
    {
        states = new();
        Volatile.Write(ref sequence, 0);
    }
}


