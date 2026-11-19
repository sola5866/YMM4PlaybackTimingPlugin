using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using YMM4PlaybackTimingPlugin.Composition;

namespace YMM4PlaybackTimingPlugin.Integration;

internal static class PlaybackTimingPatchDependencies
{
    internal static bool IsFactory(Patch patch)=>patch.PatchMethod.IsStatic&&
        (patch.PatchMethod.ReturnType==typeof(MethodInfo)||patch.PatchMethod.ReturnType==typeof(DynamicMethod))&&
        patch.PatchMethod.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(MethodBase)});
    internal static void Preflight()
    {
        var native=PreviewNativePlaybackContract.Cached;var pacing=PreviewNativePacingContract.Cached;
        if(!native.Available||!pacing.Available)throw new NotSupportedException(native.Failure??pacing.Failure);
        var n=native.Value!;
        var composable=new[]{n.Update,PlaybackTimingDrawHooks.Draw,PlaybackTimingDrawHooks.Begin,n.PlayerDispose,n.SourceDispose};
        foreach(var target in composable.Concat(new[]{n.PositionGetter,n.PositionSetter,n.PlayingSetter,n.RateSetter,pacing.Loop!,
            n.AudioClock.End,n.AudioClock.EndStateMachine,n.AudioClock.PositionGetter,n.AudioClock.Stop,n.AudioClock.Dispose,n.AudioClock.PositionLoop,
            n.AudioClock.BackendRateGetter,n.AudioClock.RequestedRateGetter,n.AudioClock.BackendModeGetter,n.AudioClock.RequestedModeGetter}))
        {
            var info=Harmony.GetPatchInfo(target);if(info is null)continue;
            foreach(var (list,kind) in new[]{(info.Prefixes,"Prefix"),(info.Postfixes,"Postfix"),(info.Transpilers,"Transpiler"),(info.Finalizers,"Finalizer")})
            foreach(var patch in list)
            {
                if(IsFactory(patch))throw new NotSupportedException("Native observer factory is not supported: "+target+" / "+patch.PatchMethod);
                if(Harmony.GetPatchInfo(patch.PatchMethod) is {} changed&&changed.Prefixes.Concat(changed.Postfixes).Concat(changed.Transpilers).Concat(changed.Finalizers).Any())
                    throw new NotSupportedException("Native observer itself is patched.");
                if(PlaybackTimingRegistrationOwner.IsOwnedCallback(target,patch.PatchMethod,kind,patch.owner))continue;
                // These shared native phases are checked by our input IL, provenance and final phase stamp.
                // No peer assembly/method-name allowlist is used. Clock/loop semantics remain exclusive.
                if(composable.Contains(target))continue;
                throw new NotSupportedException("Foreign native clock/loop dependency: "+target.Name);
            }
        }
    }
}
