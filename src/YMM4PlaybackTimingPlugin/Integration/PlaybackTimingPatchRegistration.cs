using System.Reflection;
using HarmonyLib;

namespace YMM4PlaybackTimingPlugin.Integration;

internal static class PlaybackTimingPatchRegistration
{
    // GetTypes runs before module initialization in YMM4: keep field layout free of external value types.
    internal sealed record PatchSpec
    {
        private readonly int kind;
        internal MethodInfo Target {get;init;}
        internal MethodInfo Method {get;init;}
        internal HarmonyPatchType Kind {get=>(HarmonyPatchType)kind;init=>kind=(int)value;}
        internal int Priority {get;init;}
        internal PatchSpec(MethodInfo Target,MethodInfo Method,HarmonyPatchType Kind,int Priority=HarmonyLib.Priority.Normal)
        {this.Target=Target;this.Method=Method;kind=(int)Kind;this.Priority=Priority;}
    }
    internal static IReadOnlyList<PatchSpec> Resolve()
    {
        var native=PreviewNativePlaybackContract.Cached;
        if(!native.Available)throw new NotSupportedException(native.Failure);
        var value=native.Value!;
        static PatchSpec Spec(MethodInfo target,string name,HarmonyPatchType kind)=>new(target,typeof(PlaybackTimingClockHooks).GetMethod(name)!,kind);
        return [
            new(value.Update,typeof(PlaybackTimingDrawHooks).GetMethod(nameof(PlaybackTimingDrawHooks.UpdatePrefix))!,HarmonyPatchType.Prefix),
            new(value.Update,typeof(PlaybackTimingDrawHooks).GetMethod(nameof(PlaybackTimingDrawHooks.UpdatePostfix))!,HarmonyPatchType.Postfix,int.MinValue),
            new(value.Update,typeof(PlaybackTimingDrawHooks).GetMethod(nameof(PlaybackTimingDrawHooks.UpdateFinalizer))!,HarmonyPatchType.Finalizer,Priority.Last),
            new(PlaybackTimingDrawHooks.Draw,typeof(PlaybackTimingDrawHooks).GetMethod(nameof(PlaybackTimingDrawHooks.Prefix))!,HarmonyPatchType.Prefix),
            new(PlaybackTimingDrawHooks.Draw,typeof(PlaybackTimingDrawHooks).GetMethod(nameof(PlaybackTimingDrawHooks.Transpile))!,HarmonyPatchType.Transpiler),
            new(PlaybackTimingDrawHooks.Draw,typeof(PlaybackTimingDrawHooks).GetMethod(nameof(PlaybackTimingDrawHooks.Tail))!,HarmonyPatchType.Postfix,int.MinValue),
            new(PlaybackTimingDrawHooks.Draw,typeof(PlaybackTimingDrawHooks).GetMethod(nameof(PlaybackTimingDrawHooks.Finalizer))!,HarmonyPatchType.Finalizer),
            new(PlaybackTimingDrawHooks.Begin,typeof(PlaybackTimingDrawHooks).GetMethod(nameof(PlaybackTimingDrawHooks.BeginPrefix))!,HarmonyPatchType.Prefix),
            new(PlaybackTimingDrawHooks.Begin,typeof(PlaybackTimingDrawHooks).GetMethod(nameof(PlaybackTimingDrawHooks.BeginTranspile))!,HarmonyPatchType.Transpiler),
            new(PlaybackTimingDrawHooks.Begin,typeof(PlaybackTimingDrawHooks).GetMethod(nameof(PlaybackTimingDrawHooks.BeginFinalizer))!,HarmonyPatchType.Finalizer),
            new(value.PlayerDispose,typeof(PlaybackTimingDrawHooks).GetMethod(nameof(PlaybackTimingDrawHooks.PlayerDisposing))!,HarmonyPatchType.Prefix),
            new(value.PlayerDispose,typeof(PlaybackTimingDrawHooks).GetMethod(nameof(PlaybackTimingDrawHooks.LifetimeFinalizer))!,HarmonyPatchType.Finalizer),
            new(value.SourceDispose,typeof(PlaybackTimingDrawHooks).GetMethod(nameof(PlaybackTimingDrawHooks.SourceDisposing))!,HarmonyPatchType.Prefix),
            new(value.SourceDispose,typeof(PlaybackTimingDrawHooks).GetMethod(nameof(PlaybackTimingDrawHooks.LifetimeFinalizer))!,HarmonyPatchType.Finalizer),
            Spec(value.PositionGetter,nameof(PlaybackTimingClockHooks.PositionGetterPostfix),HarmonyPatchType.Postfix),
            Spec(value.PositionSetter,nameof(PlaybackTimingClockHooks.PositionSetterPostfix),HarmonyPatchType.Postfix),
            Spec(value.PlayingSetter,nameof(PlaybackTimingClockHooks.PlayingSetterPrefix),HarmonyPatchType.Prefix),
            Spec(value.PlayingSetter,nameof(PlaybackTimingClockHooks.PlayingSetterPostfix),HarmonyPatchType.Postfix),
            Spec(value.RateSetter,nameof(PlaybackTimingClockHooks.RateSetterPrefix),HarmonyPatchType.Prefix),
            Spec(value.RateSetter,nameof(PlaybackTimingClockHooks.RateSetterPostfix),HarmonyPatchType.Postfix),
            Spec(value.AudioClock.End,nameof(PlaybackTimingClockHooks.AudioEndPrefix),HarmonyPatchType.Prefix),
            Spec(value.AudioClock.End,nameof(PlaybackTimingClockHooks.AudioEndPostfix),HarmonyPatchType.Postfix),
            Spec(value.AudioClock.PositionGetter,nameof(PlaybackTimingClockHooks.AudioPositionPrefix),HarmonyPatchType.Prefix),
            Spec(value.AudioClock.PositionGetter,nameof(PlaybackTimingClockHooks.AudioPositionFinalizer),HarmonyPatchType.Finalizer),
            Spec(value.AudioClock.Stop,nameof(PlaybackTimingClockHooks.AudioStoppingPrefix),HarmonyPatchType.Prefix),
            Spec(value.AudioClock.Dispose,nameof(PlaybackTimingClockHooks.AudioStoppingPrefix),HarmonyPatchType.Prefix),
            Spec(value.AudioClock.BackendRateGetter,nameof(PlaybackTimingClockHooks.AudioBackendRatePostfix),HarmonyPatchType.Postfix),
            Spec(value.AudioClock.RequestedRateGetter,nameof(PlaybackTimingClockHooks.AudioRequestedRatePostfix),HarmonyPatchType.Postfix),
            Spec(value.AudioClock.RequestedModeGetter,nameof(PlaybackTimingClockHooks.AudioRequestedModePostfix),HarmonyPatchType.Postfix),
            Spec(value.AudioClock.BackendModeGetter,nameof(PlaybackTimingClockHooks.AudioBackendModePostfix),HarmonyPatchType.Postfix)];
    }
}
