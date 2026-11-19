using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace YMM4PlaybackTimingPlugin.Playback;

/// <summary>
/// Player-owned high-resolution waitable timer. This does not change the
/// process-wide timer resolution and is optional at runtime.
/// </summary>
internal sealed class PreviewHighResolutionWaitableTimer : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerModifyState = 0x0002;
    private const uint Synchronize = 0x00100000;
    private const uint WaitObject0 = 0x00000000;
    private readonly object Gate = new();
    private SafeWaitHandle? timer;
    private bool disposed;

    internal bool IsAvailable => Volatile.Read(ref timer) is { IsInvalid: false, IsClosed: false };

    internal void Initialize()
    {
        lock (Gate)
        {
            if (disposed || timer is { IsInvalid: false, IsClosed: false })
                return;
            try
            {
                var created = NativeMethods.CreateWaitableTimerEx(
                    IntPtr.Zero,
                    null,
                    CreateWaitableTimerHighResolution,
                    TimerModifyState | Synchronize);
                if (created.IsInvalid) created.Dispose();
                else timer = created;
            }
            catch (Exception ex) when (ex is DllNotFoundException
                or EntryPointNotFoundException
                or BadImageFormatException)
            {
                timer = null;
            }
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            disposed = true;
            var current = timer;
            timer = null;
            current?.Dispose();
        }
    }

    internal bool TryWait(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return !disposed;
        if (duration.TotalMilliseconds > PreviewFrameDeadlineWait.MaximumWaitSliceMilliseconds)
            return false;
        if (!Monitor.TryEnter(Gate))
            return false;
        try
        {
            var current = timer;
            if (disposed || current is null || current.IsInvalid || current.IsClosed)
                return false;
            var relativeDueTime = -Math.Max(1, duration.Ticks);
            try
            {
                if (!NativeMethods.SetWaitableTimerEx(
                        current,
                        in relativeDueTime,
                        0,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        0))
                {
                    DisableLocked(current);
                    return false;
                }
                // Never block teardown forever if the native timer fails to signal.
                var waitResult = NativeMethods.WaitForSingleObject(current, 100);
                if (waitResult == WaitObject0)
                    return true;
                DisableLocked(current);
                return false;
            }
            catch (Exception ex) when (ex is DllNotFoundException
                or EntryPointNotFoundException
                or BadImageFormatException)
            {
                DisableLocked(current);
                return false;
            }
        }
        finally
        {
            Monitor.Exit(Gate);
        }
    }

    private void DisableLocked(SafeWaitHandle current)
    {
        if (timer == current)
            timer = null;
        current.Dispose();
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeWaitHandle CreateWaitableTimerEx(
            IntPtr timerAttributes,
            string? timerName,
            uint flags,
            uint desiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWaitableTimerEx(
            SafeWaitHandle timer,
            in long dueTime,
            int period,
            IntPtr completionRoutine,
            IntPtr completionArgument,
            IntPtr wakeContext,
            uint tolerableDelay);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    }
}

