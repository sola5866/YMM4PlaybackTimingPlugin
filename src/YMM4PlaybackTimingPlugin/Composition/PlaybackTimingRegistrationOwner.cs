using System.Reflection;
using HarmonyLib;
using YMM4PlaybackTimingPlugin.Diagnostics;
using YMM4PlaybackTimingPlugin.Integration;
using YMM4PlaybackTimingPlugin.Playback;

namespace YMM4PlaybackTimingPlugin.Composition;

// Timing owns its clocks, Draw phase, native worker evidence and exact patches independently.
internal sealed class PlaybackTimingRegistrationOwner:IDisposable,IAsyncDisposable
{
    private static readonly object ownersGate=new();private static PlaybackTimingRegistrationOwner? current;
    private readonly object gate=new();
    private readonly Harmony harmony=new("YMM4PlaybackTimingPlugin."+Guid.NewGuid().ToString("N"));
    private readonly List<PlaybackTimingPatchRegistration.PatchSpec> patches=[];
    private readonly TaskCompletionSource completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PlaybackTimingClockHooks.Activation? callbacks;
    private bool drawPrepared;
    private bool closing,installing=true,finishing;
    private Exception? preparationFailure;
    internal bool IsActive=>!Volatile.Read(ref closing)&&callbacks?.IsActive==true&&PlaybackTimingDrawHooks.IsCompositionCurrent;
    internal Task Completion=>completion.Task;
    internal PlaybackTimingRegistrationOwner(Action<int>? afterPatch=null,Action<PlaybackTimingPatchRegistration.PatchSpec>? beforeUnpatch=null,Action<PlaybackTimingRegistrationOwner>? retain=null,Func<PreviewPlaybackSession>? createSession=null,bool enabled=true)
    {
        BeforeUnpatch=beforeUnpatch;
        lock(ownersGate){if(current is not null)throw new InvalidOperationException("Previous PlaybackTiming owner remains retained.");current=this;}
        try
        {
            retain?.Invoke(this);
            RequireOpen();callbacks=PlaybackTimingClockHooks.Prepare(createSession,enabled);
            PlaybackTimingDrawHooks.Prepare();drawPrepared=true;PlaybackTimingDrawHooks.BeforeWorker=ReauthenticateBeforeWorker;
            PlaybackTimingPatchDependencies.Preflight();
            foreach(var spec in PlaybackTimingPatchRegistration.Resolve())
            {
                RequireOpen();lock(gate)patches.Add(spec);
                var patch=new HarmonyMethod(spec.Method){priority=spec.Priority};
                if(spec.Kind==HarmonyPatchType.Prefix)harmony.Patch(spec.Target,prefix:patch);
                else if(spec.Kind==HarmonyPatchType.Postfix)harmony.Patch(spec.Target,postfix:patch);
                else if(spec.Kind==HarmonyPatchType.Transpiler)harmony.Patch(spec.Target,transpiler:patch);
                else harmony.Patch(spec.Target,finalizer:patch);
                afterPatch?.Invoke(patches.Count);
            }
            InstallDiagnostics();
            PlaybackTimingPatchDependencies.Preflight();RequireOpen();
            if(!PlaybackTimingDrawHooks.SealComposition(true))throw new NotSupportedException("Timing Draw composition cannot be authenticated.");
            callbacks.Commit();
            PreviewDiagnosticsService.Installed=PreviewDiagnosticsConnection.Cached.Value is not null&&PreviewDiagnosticsService.Failure is null;
        }
        catch(Exception error){preparationFailure=error;CloseAsync();throw;}
        finally{lock(gate)installing=false;StartFinish();}
    }
    private Action<PlaybackTimingPatchRegistration.PatchSpec>? BeforeUnpatch {get;}
    private void InstallDiagnostics()
    {
        PreviewDiagnosticsService.Failure=null;
        var added=new List<PlaybackTimingPatchRegistration.PatchSpec>();
        try
        {
            foreach(var spec in PreviewDiagnosticsHooks.Resolve())
            {
                RequireOpen();lock(gate)patches.Add(spec);added.Add(spec);
                var patch=new HarmonyMethod(spec.Method){priority=spec.Priority};
                if(spec.Kind==HarmonyPatchType.Prefix)harmony.Patch(spec.Target,prefix:patch);
                else if(spec.Kind==HarmonyPatchType.Postfix)harmony.Patch(spec.Target,postfix:patch);
                else if(spec.Kind==HarmonyPatchType.Transpiler)harmony.Patch(spec.Target,transpiler:patch);
                else harmony.Patch(spec.Target,finalizer:patch);
            }
        }
        catch(Exception error)
        {
            // Failure of optional telemetry must not turn off playback pacing.
            foreach(var spec in added.AsEnumerable().Reverse()) { harmony.Unpatch(spec.Target,spec.Method);lock(gate)patches.Remove(spec); }
            PreviewDiagnosticsService.Failure=error.Message;
            System.Diagnostics.Trace.TraceWarning("Preview diagnostics unavailable: {0}",error);
        }
    }
    internal void SetEnabled(bool enabled)=>callbacks?.SetEnabled(enabled);
    internal static bool IsOwnedCallback(MethodBase target,MethodInfo method,string kind,string owner)
    {
        PlaybackTimingRegistrationOwner? value;lock(ownersGate)value=current;
        if(value is null||value.harmony.Id!=owner)return false;
        lock(value.gate)return value.patches.Any(p=>p.Target==target&&p.Method==method&&p.Kind.ToString()==kind);
    }
    private void ReauthenticateBeforeWorker()
    {
        if(PlaybackTimingDrawHooks.IsCompositionCurrent||!PlaybackTimingDrawHooks.CanRegenerateBeforeWorker)return;
        lock(gate)
        {
            if(closing||installing||!PlaybackTimingDrawHooks.CanRegenerateBeforeWorker)return;
            try
            {
                PlaybackTimingPatchDependencies.Preflight();
                var tail=patches.Single(p=>p.Target==PlaybackTimingDrawHooks.Draw&&p.Kind==HarmonyPatchType.Postfix);
                harmony.Unpatch(tail.Target,tail.Method);
                harmony.Patch(tail.Target,postfix:new HarmonyMethod(tail.Method){priority=tail.Priority});
                if(!PlaybackTimingDrawHooks.SealComposition(true))throw new NotSupportedException("Timing Draw regeneration cannot be authenticated.");
            }
            catch(Exception error){System.Diagnostics.Trace.TraceWarning("Timing worker connection stopped: {0}",error);CloseAsync();}
        }
    }
    private void RequireOpen(){lock(gate)ObjectDisposedException.ThrowIf(closing,this);}
    internal Task CloseAsync()
    {
        lock(gate){if(closing)return Completion;closing=true;}
        PreviewDiagnosticsService.Stop();
        if(drawPrepared)PlaybackTimingDrawHooks.BeginClose();callbacks?.CloseAdmission();StartFinish();return Completion;
    }
    private void StartFinish()
    {lock(gate){if(!closing||installing||finishing)return;finishing=true;}_=Finish(preparationFailure);}
    private async Task Finish(Exception? preparationFailure)
    {
        try
        {
            if(drawPrepared)await PlaybackTimingDrawHooks.DrainAsync().ConfigureAwait(false);
            if(callbacks is not null)await callbacks.CloseAsync().ConfigureAwait(false);
            var errors=new List<Exception>();
            foreach(var spec in patches.AsEnumerable().Reverse())
                try{BeforeUnpatch?.Invoke(spec);harmony.Unpatch(spec.Target,spec.Method);}catch(Exception error){errors.Add(error);}
            foreach(var spec in patches)
            {var info=Harmony.GetPatchInfo(spec.Target);if(info is not null&&info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Any(p=>p.owner==harmony.Id&&p.PatchMethod==spec.Method))errors.Add(new InvalidOperationException("Owned PlaybackTiming patch remains installed."));}
            if(errors.Count!=0)throw new AggregateException("PlaybackTiming patch closure unconfirmed.",errors);
            if(drawPrepared){PlaybackTimingDrawHooks.BeforeWorker=null;PlaybackTimingDrawHooks.Release();}
            lock(ownersGate)if(ReferenceEquals(current,this))current=null;
            if(preparationFailure is not null)completion.TrySetException(preparationFailure);else completion.TrySetResult();
        }
        catch(Exception error){completion.TrySetException(preparationFailure is null?error:new AggregateException(preparationFailure,error));}
    }
    public void Dispose()=>CloseAsync();
    public ValueTask DisposeAsync()=>new(CloseAsync());
}
