using System.Diagnostics;

namespace YMM4PlaybackTimingPlugin.Playback;

// Owned by one playback session, never shared between players. The caller stops
// its clock and drains its callback before disposing this optional timer.
internal sealed class PreviewPlaybackWaitRuntime : IPreviewFrameDeadlineRuntime, IDisposable
{
    private readonly PreviewHighResolutionWaitableTimer timer = new();
    internal PreviewPlaybackWaitRuntime() => timer.Initialize();
    public long Frequency => Stopwatch.Frequency;
    public long GetTimestamp() => Stopwatch.GetTimestamp();
    public void Sleep(TimeSpan duration) => Thread.Sleep(duration);
    public bool TryPreciseWait(TimeSpan duration) => timer.TryWait(duration);
    public void Yield() => Thread.Yield();
    public void SpinOnce() => Thread.SpinWait(16);
    public void Dispose() => timer.Dispose();
}
