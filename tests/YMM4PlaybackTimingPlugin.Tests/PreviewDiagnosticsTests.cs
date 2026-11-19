using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using SharpGen.Runtime;
using HarmonyLib;
using System.Reflection.Emit;
using YMM4PlaybackTimingPlugin.Composition;
using YMM4PlaybackTimingPlugin.Diagnostics;
using YMM4PlaybackTimingPlugin.Integration;

internal static class PreviewDiagnosticsTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static void Measurements()
    {
        var model = new PreviewMeasurements(1000);
        Check(model.Capture() is null, "measurement must default off");
        model.SetEnabled(true, true, 0);
        var first = model.Capture()!.Value;
        model.Draw(first, 0); model.Present(first, 0, 0);
        model.Draw(first, 500); model.Present(first, 0, 500);
        Check(model.Read(500).SendFps is null, "partial windows must not report exaggerated fps");
        var full = model.Read(1000);
        Check(full.Draws == 2 && full.Sends == 2 && full.SendFps == 1 && full.MaxIntervalMs == 500, "rolling window / first timestamp zero / totals");
        Check(model.Read(2000).SendFps == 0, "idle playback must decay to zero");
        model.SetPlaying(false, 2000);
        model.SetPlaying(true, 1900);
        Check(model.Capture() is null, "an older render-thread observation must not undo a newer pause notification");
        model.Present(first, 0, 2100);
        Check(model.Read(5000).Sends == 2 && model.Read(5000).Seconds == 2 && model.Read(5000).SendFps is null, "pause must freeze time and invalidate in-flight frames");
        model.SetPlaying(true, 5000);
        var resumed = model.Capture()!.Value;
        model.Present(resumed, 0, 5100);
        Check(model.Read(5200).MaxIntervalMs is null, "pause must not enter interval statistics");
        model.Break(5300);
        model.Present(resumed, 0, 5400);
        Check(model.Read(5500).Sends == 3, "seek/source exchange must reject stale completion");
        var afterSeek = model.Capture()!.Value;
        model.Present(afterSeek, 0x087A0001, 5500);
        model.Present(afterSeek, unchecked((int)0x887A0005), 5600);
        Check(model.Read(5700).Sends == 3 && model.Read(5700).Failures == 1, "S_OK, occlusion, and failure must be separate");
        model.SetEnabled(false, true, 6000);
        model.Present(afterSeek, 0, 6100);
        Check(model.Read(9000).Seconds == 3 && model.Read(9000).Sends == 3, "measurement OFF must freeze totals and time");
        model.SetEnabled(true, true, 10000);
        var beforeReset = model.Capture()!.Value;
        model.Reset(10100); model.Draw(beforeReset, 10200);
        Check(model.Read(10200).Draws == 0 && Math.Abs(model.Read(10200).Seconds - .1) < .0001, "reset must reject old work and reset elapsed time");
        var bounded = model.Capture()!.Value;
        for (var i = 0; i < 17000; i++) model.Draw(bounded, 10300);
        Check(model.Read(11500).Overflow && model.Read(11500).DrawFps is null && model.Read(11500).Draws == 17000, "bounded storage must report overflow without corrupting totals");
    }
    internal static void NativeBoundarySemantics()
    {
        var native = PreviewNativePlaybackContract.Cached.Value!;
        var player = RuntimeHelpers.GetUninitializedObject(native.Video);
        native.Playing.SetValue(player, true);
        var model = new PreviewMeasurements(Stopwatch.Frequency);
        model.SetEnabled(true, true, Stopwatch.GetTimestamp());
        var subscription = PreviewDiagnosticsService.Select(player, model);
        try
        {
            PreviewDiagnosticsHooks.DrawPrefix(player, out var skipped);
            PreviewDiagnosticsHooks.DrawFinalizer(skipped, null);
            PreviewDiagnosticsHooks.DrawPrefix(player, out var failed);
            PreviewDiagnosticsHooks.ObserveDraw(Result.Ok, player);
            PreviewDiagnosticsHooks.DrawFinalizer(failed, new InvalidOperationException());
            PreviewDiagnosticsHooks.DrawPrefix(player, out var success);
            PreviewDiagnosticsHooks.ObserveDraw(Result.Ok, player);
            PreviewDiagnosticsHooks.DrawFinalizer(success, null); PreviewDiagnosticsHooks.DrawFinalizer(success, null);
            Check(model.Read(Stopwatch.GetTimestamp()).Draws == 1, "skipped/failed/double-finalized Draw counted");
            PreviewDiagnosticsHooks.PresentPrefix(player, out var sent);
            PreviewDiagnosticsHooks.EnterPresent(player); PreviewDiagnosticsHooks.ObservePresent(Result.Ok, player);
            PreviewDiagnosticsHooks.PresentFinalizer(sent, new InvalidOperationException());
            PreviewDiagnosticsHooks.PresentPrefix(player, out var notCalled);
            PreviewDiagnosticsHooks.PresentFinalizer(notCalled, new InvalidOperationException());
            PreviewDiagnosticsHooks.PresentPrefix(player, out var exception);
            PreviewDiagnosticsHooks.EnterPresent(player);
            PreviewDiagnosticsHooks.PresentFinalizer(exception, new InvalidOperationException());
            PreviewDiagnosticsHooks.PresentFinalizer(exception, new InvalidOperationException());
            var result = model.Read(Stopwatch.GetTimestamp());
            Check(result.Sends == 1 && result.Failures == 1, "count the native return, not outer success or a skipped native call");
            PreviewDiagnosticsHooks.DrawPrefix(new object(), out var other);
            Check(other is null, "independent player contaminated selected preview");
        }
        finally { PreviewDiagnosticsService.Release(subscription); }
    }
    internal static void GeometryAndDisplay()
    {
        var frame = new PreviewGeometry.Frame(1920, 1080, Matrix3x2.Identity, 96, 96, 1920, 1080);
        Check(PreviewGeometry.VisibleSize(frame, 1440, 810) == (1440, 810), "swapchain stretch geometry");
        var letterbox = frame with { Transform = Matrix3x2.CreateScale(.75f) * Matrix3x2.CreateTranslation(240, 135) };
        Check(PreviewGeometry.VisibleSize(letterbox, 1440, 810) == (1080, 608), "letterbox geometry");
        var panned = frame with { Transform = Matrix3x2.CreateTranslation(-960, 0) };
        Check(PreviewGeometry.VisibleSize(panned, 1440, 810) == (720, 810), "pan clipping");
        var zoomed = frame with { Transform = Matrix3x2.CreateScale(2, 2) };
        Check(PreviewGeometry.VisibleSize(zoomed, 1440, 810) == (1440, 810), "zoom clipping");
        Check(PreviewGeometry.VisibleSize(frame with { DpiX = double.NaN }, 1440, 810) is null, "non-finite geometry");
        Check(WindowsPreviewDisplay.FormatColor(1, 0, 10) == ("SDR ACM", "RGB / 10 bit"), "ACM classification");
        Check(WindowsPreviewDisplay.FormatColor(2, 2, 12) == ("HDR", "YCbCr 4:2:2 / 12 bit"), "HDR/output classification");
        Check(WindowsPreviewDisplay.FormatColor(null, 0, 8).Mode == "取得できません", "old API must not invent HDR/ACM state");
        Check(WindowsPreviewDisplay.ReadViewport(IntPtr.Zero) is null && WindowsPreviewDisplay.Read(IntPtr.Zero).Error is not null, "invalid HWND handling");
    }
    internal static void OptionalRegistration()
    {
        var foreign=new Harmony("diagnostics.test.optional");
        var target=PreviewDiagnosticsConnection.Cached.Value!.Present;
        foreign.Patch(target,transpiler:new HarmonyMethod(typeof(PreviewDiagnosticsTests),nameof(RemovePresent)));
        try
        {
            using var owner=new PlaybackTimingRegistrationOwner();
            Check(owner.IsActive&&!PreviewDiagnosticsService.Installed&&PreviewDiagnosticsService.Failure is not null,"optional diagnostics failure disabled timing or claimed success");
            owner.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Check(Harmony.GetPatchInfo(target)?.Transpilers.Any(p=>p.owner==foreign.Id)==true,"optional rollback removed foreign code");
        }
        finally{foreign.Unpatch(target,typeof(PreviewDiagnosticsTests).GetMethod(nameof(RemovePresent))!);}
        using var recovered=new PlaybackTimingRegistrationOwner();
        Check(recovered.IsActive&&PreviewDiagnosticsService.Installed,"diagnostics did not recover after clean registration");
        recovered.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }
    public static IEnumerable<CodeInstruction> RemovePresent(IEnumerable<CodeInstruction> instructions)=>[new(OpCodes.Ret)];
    internal static void WeakOwnership()
    {
        var (subscription,weak)=RegisterWeakPlayer();
        try
        {
            for(var i=0;i<4&&weak.IsAlive;i++){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
            Check(!weak.IsAlive,"diagnostics discovery, subscription, or geometry retained the native player");
        }
        finally{PreviewDiagnosticsService.Release(subscription);}
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (PreviewDiagnosticsService.Subscription,WeakReference) RegisterWeakPlayer()
    {
        var player=RuntimeHelpers.GetUninitializedObject(PreviewNativePlaybackContract.Cached.Value!.Video);
        PreviewDiagnosticsService.ObservePlayer(player);PreviewGeometry.Request(player);
        return (PreviewDiagnosticsService.Select(player,new(Stopwatch.Frequency)),new(player));
    }
}
