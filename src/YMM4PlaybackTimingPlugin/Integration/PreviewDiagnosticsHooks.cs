using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SharpGen.Runtime;
using Vortice.DXGI;
using YMM4PlaybackTimingPlugin.Diagnostics;

namespace YMM4PlaybackTimingPlugin.Integration;

internal static class PreviewDiagnosticsHooks
{
    internal sealed class Sample(object player, PreviewMeasurements model, PreviewMeasurements.Ticket ticket, Sample? parent)
    {
        internal readonly object Player = player;
        internal readonly PreviewMeasurements Model = model;
        internal readonly PreviewMeasurements.Ticket Ticket = ticket;
        internal readonly Sample? Parent = parent;
        internal bool Seen, Success, Entered, Closed;
        internal long CompletedAt;
    }
    [ThreadStatic] private static Sample? drawing;
    [ThreadStatic] private static Sample? presenting;
    internal static MethodInfo DxgiPresent => typeof(IDXGISwapChain).GetMethod(nameof(IDXGISwapChain.Present), [typeof(int), typeof(PresentFlags)])!;
    internal static IReadOnlyList<PlaybackTimingPatchRegistration.PatchSpec> Resolve()
    {
        var contract = PreviewDiagnosticsConnection.Cached.Value;
        if (contract is null) { PreviewDiagnosticsService.Failure = PreviewDiagnosticsConnection.Failure; return []; }
        var original = PatchProcessor.GetOriginalInstructions(contract.Present).ToArray();
        if (original.Count(c => c.Calls(DxgiPresent)) != 1)
        { PreviewDiagnosticsService.Failure = "Presentの構造が異なります。"; return []; }
        static PlaybackTimingPatchRegistration.PatchSpec Spec(MethodInfo target, string method, HarmonyPatchType kind) =>
            new(target, typeof(PreviewDiagnosticsHooks).GetMethod(method)!, kind);
        var native = PreviewNativePlaybackContract.Cached.Value!;
        return [
            Spec(PlaybackTimingDrawHooks.Draw, nameof(DrawPrefix), HarmonyPatchType.Prefix),
            Spec(PlaybackTimingDrawHooks.Draw, nameof(DrawFinalizer), HarmonyPatchType.Finalizer),
            Spec(contract.Present, nameof(PresentPrefix), HarmonyPatchType.Prefix),
            Spec(contract.Present, nameof(PresentTranspile), HarmonyPatchType.Transpiler),
            Spec(contract.Present, nameof(PresentFinalizer), HarmonyPatchType.Finalizer),
            Spec(native.PlayingSetter, nameof(PlayingPostfix), HarmonyPatchType.Postfix),
            Spec(contract.Seek, nameof(BreakPrefix), HarmonyPatchType.Prefix),
            Spec(native.RateSetter, nameof(BreakPrefix), HarmonyPatchType.Prefix)];
    }
    private static Sample? Begin(object player, Sample? parent)
    {
        try
        {
            var capture = PreviewDiagnosticsService.Capture(player);
            return capture is {} value ? new(player, value.Model, value.Ticket, parent) : null;
        }
        catch (Exception error) { Trace.TraceWarning("Preview diagnostics sample: {0}", error.Message); return null; }
    }
    public static void DrawPrefix(object __instance, out Sample? __state)
    { __state = Begin(__instance, drawing); if (__state is not null) drawing = __state; }
    public static void ObserveDraw(Result result, object player)
    {
        if (drawing is {} sample && ReferenceEquals(sample.Player, player)) { sample.Seen = true; sample.Success = result.Success; sample.CompletedAt = Stopwatch.GetTimestamp(); }
    }
    public static void DrawFinalizer(Sample? __state, Exception? __exception)
    {
        if (__state is null || __state.Closed) return;
        __state.Closed = true;
        if (ReferenceEquals(drawing, __state)) drawing = __state.Parent;
        try { if (__state.Seen && __state.Success && __exception is null) __state.Model.Draw(__state.Ticket, __state.CompletedAt); }
        catch (Exception error) { Trace.TraceWarning("Preview diagnostics Draw: {0}", error.Message); }
    }
    public static void PresentPrefix(object __instance, out Sample? __state)
    { __state = Begin(__instance, presenting); if (__state is not null) presenting = __state; }
    public static void EnterPresent(object player)
    { if (presenting is {} sample && ReferenceEquals(sample.Player, player)) sample.Entered = true; }
    public static void ObservePresent(Result result, object player)
    {
        try
        {
            if (presenting is not {} sample || !ReferenceEquals(sample.Player, player)) return;
            sample.Seen = true;
            sample.Model.Present(sample.Ticket, result.Code, Stopwatch.GetTimestamp());
        }
        catch (Exception error) { Trace.TraceWarning("Preview diagnostics Present: {0}", error.Message); }
    }
    public static IEnumerable<CodeInstruction> PresentTranspile(IEnumerable<CodeInstruction> instructions)
    {
        var count = 0;
        // Do not use yield here: an iterator would expose Harmony in its generated
        // type's interfaces before YMM4 runs our embedded-dependency initializer.
        var result = new List<CodeInstruction>();
        foreach (var original in instructions)
        {
            var code = new CodeInstruction(original);
            if (!code.Calls(DxgiPresent)) { result.Add(code); continue; }
            count++;
            var enter = new CodeInstruction(OpCodes.Ldarg_0);
            enter.labels.AddRange(code.labels); code.labels.Clear();
            enter.blocks.AddRange(code.blocks); code.blocks.Clear();
            result.Add(enter);
            result.Add(new(OpCodes.Call, typeof(PreviewDiagnosticsHooks).GetMethod(nameof(EnterPresent))!));
            result.Add(code);
            result.Add(new(OpCodes.Dup));
            result.Add(new(OpCodes.Ldarg_0));
            result.Add(new(OpCodes.Call, typeof(PreviewDiagnosticsHooks).GetMethod(nameof(ObservePresent))!));
        }
        if (count != 1) throw new NotSupportedException("Exact native Present changed.");
        return result;
    }
    public static void PresentFinalizer(Sample? __state, Exception? __exception)
    {
        if (__state is null || __state.Closed) return;
        __state.Closed = true;
        if (ReferenceEquals(presenting, __state)) presenting = __state.Parent;
        try
        {
            if (__state.Entered && !__state.Seen && __exception is not null)
                __state.Model.Present(__state.Ticket, __exception.HResult < 0 ? __exception.HResult : unchecked((int)0x80004005), Stopwatch.GetTimestamp());
        }
        catch (Exception error) { Trace.TraceWarning("Preview diagnostics failure: {0}", error.Message); }
    }
    public static void PlayingPostfix(object __instance)
    { try { PreviewDiagnosticsService.PlayingChanged(__instance); } catch (Exception error) { Trace.TraceWarning("Preview diagnostics state: {0}", error.Message); } }
    public static void BreakPrefix(object __instance)
    { try { PreviewDiagnosticsService.Break(__instance); } catch (Exception error) { Trace.TraceWarning("Preview diagnostics discontinuity: {0}", error.Message); } }
}
