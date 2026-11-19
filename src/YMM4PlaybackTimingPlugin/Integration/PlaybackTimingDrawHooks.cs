using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using YMM4PlaybackTimingPlugin.Diagnostics;
using YMM4PlaybackTimingPlugin.Playback;
using YukkuriMovieMaker.Player.Video;

namespace YMM4PlaybackTimingPlugin.Integration;

// One Timing registration owns candidate provenance, Draw attempts and native worker proofs.
// Draw observation and playback pacing are registered independently.
internal static class PlaybackTimingDrawHooks
{
    internal sealed class PlayerState(object player)
    {
        internal readonly object Gate=new();internal readonly object Player=player;
        internal object? Source;internal Task? Worker;internal int Thread,Active;
        internal long Epoch;internal bool Closing,Disabled;
    }
    internal sealed class Attempt
    {
        internal bool Seen,Success,Tail,Closed;internal Attempt? Parent;
        internal PlayerState? Owner;internal object? Source;internal long Sequence,Epoch,Stamp,BodyStamp;
        internal TimeSpan Candidate;internal PlaybackTimingClockHooks.Callback? ClockUse;
    }
    internal sealed class UpdateAttempt(long sequence,bool playing,long stamp,PlaybackTimingClockHooks.Callback? clockUse)
    {internal readonly long Sequence=sequence,Stamp=stamp;internal readonly bool Playing=playing;internal readonly PlaybackTimingClockHooks.Callback? ClockUse=clockUse;internal bool Closed,Returned;}
    internal sealed class Operation(object player,Operation? parent)
    {internal readonly object Player=player;internal readonly Operation? Parent=parent;internal bool Closed;}
    private sealed record Observation(long Binding,WeakReference<object> Source,long Sequence,TimeSpan Candidate);
    private sealed record PatchIdentity(MethodInfo Method,string Owner,int Index,int Priority,string[] Before,string[] After)
    {
        internal static PatchIdentity From(Patch p)=>new(p.PatchMethod,p.owner,p.index,p.priority,p.before.ToArray(),p.after.ToArray());
        internal bool Matches(Patch p)=>Method==p.PatchMethod&&Owner==p.owner&&Index==p.index&&Priority==p.priority&&Before.SequenceEqual(p.before)&&After.SequenceEqual(p.after);
    }
    private static readonly object admissionGate=new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object,PlayerState> players=new();
    private static IEnumerable<PlayerState> PlayerStates=>players.Select(entry=>entry.Value);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object,object> sources=new();
    private static readonly HashSet<Task> workers=[];
    private static TaskCompletionSource drained=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static int active;private static long binding,emission,trustedEmission;
    private static bool closing=true,tailLast;
    private static PatchIdentity[][]? composition,updateComposition;
    [ThreadStatic]private static Attempt? current;
    [ThreadStatic]private static Operation? beginning;
    [ThreadStatic]private static Observation? observation;
    private static PreviewNativePlaybackContract.Contract Native=>PreviewNativePlaybackContract.Cached.Value!;
    internal static MethodInfo Draw=>Native.Video.GetMethod("Draw",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)!;
    internal static MethodInfo Begin=>Native.Video.GetMethod("BeginVideoTask",BindingFlags.Instance|BindingFlags.Public|BindingFlags.DeclaredOnly)!;
    private static readonly MethodInfo endDraw=typeof(ID2D1RenderTarget).GetMethod("EndDraw",Type.EmptyTypes)!;
    private static FieldInfo WorkerField=>Native.Video.GetField("videoTask",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)!;
    private static FieldInfo DisposedField=>Native.Video.GetField("disposeStarted",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)!;
    private static FieldInfo CanceledField=>Native.Video.GetField("videoTask_canceled",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)!;
    private static MethodInfo TailMethod=>typeof(PlaybackTimingDrawHooks).GetMethod(nameof(Tail))!;
    internal static Action? BeforeWaitForTests;
    internal static Action? BeforeWorker;
    internal static Action<object>? BeforeWorkerFinalizerForTests;
    internal static Action<PreviewFrameDeadlineWaitResult>? WaitResultForTests;
    internal static int WaitsForTests,RejectedForTests;
    internal static int ActiveForTests=>Volatile.Read(ref active);
    internal static long TrustedEmissionForTests=>Volatile.Read(ref trustedEmission);
    internal static bool IsCompositionCurrent=>CompositionCurrent(Volatile.Read(ref trustedEmission));
    internal static bool CanRegenerateBeforeWorker {get{lock(admissionGate)return !closing&&active==0&&workers.All(t=>t.IsCompleted);}}
    internal static void Prepare()
    {
        lock(admissionGate)
        {
            if(!closing||active!=0||workers.Any(t=>!t.IsCompleted))throw new InvalidOperationException("Previous Timing native callbacks remain retained.");
            players.Clear();sources.Clear();workers.Clear();binding++;emission++;trustedEmission=0;composition=updateComposition=null;tailLast=false;
            drained=new(TaskCreationOptions.RunContinuationsAsynchronously);closing=false;WaitsForTests=RejectedForTests=0;
        }
    }
    private static bool Enter(){lock(admissionGate){if(closing)return false;active++;return true;}}
    private static void Exit(){lock(admissionGate){active--;TryDrain();}}
    private static void TryDrain(){if(closing&&active==0&&workers.All(t=>t.IsCompleted))drained.TrySetResult();}
    private static void Capture(Task task)
    {
        lock(admissionGate)if(workers.Add(task))
            _=task.ContinueWith(done=>{lock(admissionGate){if(done.IsCompletedSuccessfully)workers.Remove(done);TryDrain();}},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    }
    internal static void BeginClose()
    {
        lock(admissionGate){closing=true;Volatile.Write(ref trustedEmission,0);}
        foreach(var state in PlayerStates)lock(state.Gate){state.Closing=true;state.Epoch++;if(state.Source is {} source)PreviewFrameDeadlineRegistry.Invalidate(source);}
        foreach(var source in sources.Select(entry=>entry.Key))PreviewFrameDeadlineRegistry.Invalidate(source);
        lock(admissionGate)TryDrain();
    }
    internal static async Task DrainAsync()
    {
        await drained.Task.ConfigureAwait(false);Task[] snapshot;lock(admissionGate)snapshot=workers.ToArray();
        await Task.WhenAll(snapshot).ConfigureAwait(false);
    }
    internal static void Release(){lock(admissionGate){players.Clear();sources.Clear();workers.Clear();composition=updateComposition=null;}}
    private static IEnumerable<Patch>[] Parts(Patches p)=>[p.Prefixes,p.Postfixes,p.Transpilers,p.Finalizers];
    internal static bool SealComposition(bool successfulRegistration)
    {
        if(!successfulRegistration)return false;
        var stamp=Volatile.Read(ref emission);var info=Harmony.GetPatchInfo(Draw);var updateInfo=Harmony.GetPatchInfo(Native.Update);
        if(info is null||updateInfo is null)return false;
        var parts=Parts(info);if(parts.SelectMany(p=>p).Any(PlaybackTimingPatchDependencies.IsFactory))return false;
        var own=typeof(PlaybackTimingDrawHooks);
        if(!info.Prefixes.Any(p=>p.PatchMethod==own.GetMethod(nameof(Prefix)))||!info.Transpilers.Any(p=>p.PatchMethod==own.GetMethod(nameof(Transpile)))||
            !info.Postfixes.Any(p=>p.PatchMethod==TailMethod)||!info.Finalizers.Any(p=>p.PatchMethod==own.GetMethod(nameof(Finalizer))))return false;
        var snapshot=parts.Select(p=>p.Select(PatchIdentity.From).ToArray()).ToArray();
        var updateParts=Parts(updateInfo);if(updateParts.SelectMany(p=>p).Any(PlaybackTimingPatchDependencies.IsFactory))return false;
        if(!updateInfo.Prefixes.Any(p=>p.PatchMethod==own.GetMethod(nameof(UpdatePrefix)))||!updateInfo.Finalizers.Any(p=>p.PatchMethod==own.GetMethod(nameof(UpdateFinalizer))))return false;
        if(PatchProcessor.GetSortedPatchMethods(Native.Update,updateInfo.Postfixes.ToArray()).LastOrDefault()!=own.GetMethod(nameof(UpdatePostfix)))return false;
        var updateSnapshot=updateParts.Select(p=>p.Select(PatchIdentity.From).ToArray()).ToArray();
        var last=PatchProcessor.GetSortedPatchMethods(Draw,info.Postfixes.ToArray()).LastOrDefault()==TailMethod;
        lock(admissionGate)
        {
            if(closing||stamp!=emission||active!=0||workers.Any(t=>!t.IsCompleted)||!last)return false;
            composition=snapshot;updateComposition=updateSnapshot;tailLast=last;Volatile.Write(ref trustedEmission,stamp);return true;
        }
    }
    private static bool StampCurrent(long stamp)=>stamp!=0&&stamp==Volatile.Read(ref trustedEmission)&&stamp==Volatile.Read(ref emission)&&!Volatile.Read(ref closing);
    private static bool CompositionCurrent(long stamp)
    {
        if(!StampCurrent(stamp))return false;
        return Matches(Draw,composition)&&Matches(Native.Update,updateComposition)&&StampCurrent(stamp);
    }
    private static bool Matches(MethodInfo target,PatchIdentity[][]? snapshot)
    {
        var info=Harmony.GetPatchInfo(target);if(snapshot is null||info is null)return false;
        var parts=Parts(info);
        for(var i=0;i<parts.Length;i++){var actual=parts[i].ToArray();if(actual.Length!=snapshot[i].Length)return false;for(var j=0;j<actual.Length;j++)if(!snapshot[i][j].Matches(actual[j]))return false;}
        return true;
    }
    private static MethodBase Normalize(MethodBase method)=>method is MethodInfo info?Harmony.GetOriginalMethod(info):method;
    private static bool NormalCaller(MethodInfo target)
    {
        var frames=new StackTrace().GetFrames();
        for(var i=0;i<frames.Length-1;i++)if(frames[i].GetMethod() is {} method&&Equals(Normalize(method),target))
            return frames[i+1].GetMethod() is {} caller&&Equals(Normalize(caller),PreviewNativeVideoLoopContract.Cached.Loop);
        return false;
    }
    public static void UpdatePrefix(object __instance,TimeSpan __0,TimelineSourceUsage __1,out UpdateAttempt? __state)
    {
        __state=null;observation=null;
        if(!Enter())return;
        var clockUse=PlaybackTimingClockHooks.TryEnter();
        try
        {
            sources.GetValue(__instance,static _=>new());var sequence=PreviewFrameDeadlineRegistry.RegisterPending(__instance,__0);
            var stamp=Volatile.Read(ref trustedEmission);
            var normal=__1==TimelineSourceUsage.Playing&&NormalCaller(Native.Update)&&CompositionCurrent(stamp);
            __state=new(sequence,normal,stamp,clockUse);
            if(normal)observation=new(binding,new(__instance),sequence,__0);
        }
        catch(Exception error){clockUse?.Dispose();Exit();Trace.TraceWarning("Timing Update observer disabled: {0}",error);}
    }
    public static void UpdatePostfix(UpdateAttempt? __state,bool __runOriginal)
    {if(__state is not null)__state.Returned=__runOriginal&&CompositionCurrent(__state.Stamp);}
    // A void finalizer cannot suppress or replace the native/foreign exception.
    public static void UpdateFinalizer(object __instance,UpdateAttempt? __state,Exception? __exception)
    {
        // An earlier prefix can throw before ours runs, and a foreign finalizer
        // can suppress that exception. Never carry the previous Update across it.
        if(__state is null){observation=null;if(PreviewFrameDeadlineRegistry.TryGet(__instance,out _))PreviewFrameDeadlineRegistry.Invalidate(__instance);return;}
        if(__state.Closed)return;__state.Closed=true;
        try{PreviewFrameDeadlineRegistry.Complete(__instance,__state.Sequence,__exception is null&&__state.Playing&&__state.Returned&&CompositionCurrent(__state.Stamp));}
        finally{__state.ClockUse?.Dispose();Exit();}
    }
    public static void Prefix(object __instance,out Attempt? __state)
    {
        __state=null;if(!Enter())return;
        var owner=players.GetValue(__instance,p=>new(p));var attempt=new Attempt{Parent=current,Owner=owner,ClockUse=PlaybackTimingClockHooks.TryEnter()};
        __state=attempt;current=attempt;Interlocked.Increment(ref owner.Active);
        try
        {
            var task=WorkerField.GetValue(__instance) as Task;if(task is not null)Capture(task);
            var observed=observation;
            if(attempt.Parent is not null||(int)DisposedField.GetValue(__instance)! !=0||!NormalCaller(Draw)||observed?.Binding!=binding||
                !observed.Source.TryGetTarget(out var source)||!ReferenceEquals(Native.TimelineSource.GetValue(__instance),source)){RejectedForTests++;return;}
            var stamp=Volatile.Read(ref trustedEmission);var valid=CompositionCurrent(stamp);
            lock(owner.Gate)
            {
                if(task is not null&&owner.Worker is not null&&!ReferenceEquals(owner.Worker,task)&&owner.Worker.IsCompleted&&owner.Active==1)
                {owner.Epoch++;owner.Worker=null;owner.Thread=0;}
                if(task is null||Task.CurrentId!=task.Id||owner.Worker is not null&&!ReferenceEquals(owner.Worker,task)||owner.Thread!=0&&owner.Thread!=Environment.CurrentManagedThreadId){RejectedForTests++;return;}
                owner.Worker=task;owner.Thread=Environment.CurrentManagedThreadId;
                if(!ReferenceEquals(owner.Source,source)){if(owner.Source is {} previous)PreviewFrameDeadlineRegistry.Invalidate(previous);owner.Source=source;owner.Epoch++;}
                if(owner.Closing||owner.Disabled||!valid||!StampCurrent(stamp)||!PreviewFrameDeadlineRegistry.IsCurrentPlaying(source,observed.Sequence)){RejectedForTests++;return;}
                attempt.Source=source;attempt.Candidate=observed.Candidate;attempt.Sequence=observed.Sequence;attempt.Epoch=owner.Epoch;attempt.Stamp=stamp;
            }
            observation=null;
        }
        catch(Exception error){lock(owner.Gate){owner.Disabled=true;owner.Epoch++;}Trace.TraceWarning("Timing Draw observer disabled: {0}",error);}
    }
    public static void ObserveEnd(Result result,object player,long stamp)
    {
        PreviewDiagnosticsHooks.ObserveDraw(result,player);
        if(current is {Source:not null} attempt&&ReferenceEquals(attempt.Owner?.Player,player))
        {attempt.BodyStamp=stamp;if(attempt.Stamp==stamp){attempt.Seen=true;attempt.Success=result.Success;}}
    }
    public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        long stamp;lock(admissionGate){Volatile.Write(ref trustedEmission,0);stamp=++emission;}
        foreach(var state in PlayerStates)lock(state.Gate){state.Epoch++;if(state.Source is {} source)PreviewFrameDeadlineRegistry.Invalidate(source);}
        foreach(var source in sources.Select(entry=>entry.Key))PreviewFrameDeadlineRegistry.Invalidate(source);
        var count=0;var images=0;var result=new List<CodeInstruction>();
        foreach(var original in instructions)
        {
            var code=new CodeInstruction(original);
            if(code.operand is MethodInfo image&&image.Name=="DrawImage"&&image.DeclaringType==typeof(ID2D1DeviceContext)&&images++==0)
            {
                var enter=new CodeInstruction(OpCodes.Ldarg_0);enter.labels.AddRange(code.labels);code.labels.Clear();enter.blocks.AddRange(code.blocks);code.blocks.Clear();
                result.Add(enter);result.Add(new(OpCodes.Call,typeof(PreviewGeometry).GetMethod(nameof(PreviewGeometry.Capture))!));
            }
            result.Add(new(code));
            if(code.Calls(endDraw))
            {count++;result.Add(new(OpCodes.Dup));result.Add(new(OpCodes.Ldarg_0));result.Add(new(OpCodes.Ldc_I8,stamp));result.Add(new(OpCodes.Call,typeof(PlaybackTimingDrawHooks).GetMethod(nameof(ObserveEnd))!));}
        }
        if(count!=1)throw new NotSupportedException("Exact native EndDraw changed.");
        return result;
    }
    public static void Tail(Attempt? __state)
    {if(__state is not null)__state.Tail=tailLast&&__state.BodyStamp==__state.Stamp&&CompositionCurrent(__state.Stamp);}
    public static Exception? Finalizer(Exception? __exception,Attempt? __state)
    {
        if(__state is null){observation=null;return __exception;}
        if(__state.Closed)return __exception;__state.Closed=true;
        if(ReferenceEquals(current,__state))current=__state.Parent;
        var owner=__state.Owner!;
        try
        {
            var valid=CompositionCurrent(__state.Stamp);bool admitted;
            BeforeWaitForTests?.Invoke();
            lock(owner.Gate)admitted=!owner.Closing&&!owner.Disabled&&!(bool)CanceledField.GetValue(owner.Player)!&&__exception is null&&__state.Tail&&__state.Seen&&__state.Success&&__state.Source is {} source&&
                owner.Epoch==__state.Epoch&&ReferenceEquals(Native.TimelineSource.GetValue(owner.Player),source)&&PreviewFrameDeadlineRegistry.IsCurrentPlaying(source,__state.Sequence)&&valid&&StampCurrent(__state.Stamp);
            if(admitted&&__state.ClockUse is {} use&&use.Owner.TryCaptureSession(owner.Player,out var session,out var currentSource,out var generation)&&
                ReferenceEquals(currentSource,__state.Source)&&use.Owner.IsFeatureCurrent(generation)&&session.Clock.GetSnapshot().Active)
            {
                try
                {
                    if(session.TryWait(__state.Source!,__state.Candidate,__state.Sequence,out var result))
                    {Interlocked.Increment(ref WaitsForTests);WaitResultForTests?.Invoke(result);if(result.Reason=="clock-runtime-frequency-mismatch")throw new InvalidOperationException(result.Reason);}
                }
                catch(Exception error){session.CloseAdmission();PreviewFrameDeadlineRegistry.Invalidate(__state.Source!);lock(owner.Gate){owner.Disabled=true;owner.Epoch++;}Trace.TraceWarning("Timing player wait stopped: {0}",error);}
            }
            return __exception;
        }
        catch(Exception error){lock(owner.Gate){owner.Disabled=true;owner.Epoch++;}Trace.TraceWarning("Timing finalizer stopped its observer: {0}",error);return __exception;}
        finally{__state.ClockUse?.Dispose();Interlocked.Decrement(ref owner.Active);Exit();}
    }
    public static void BeginPrefix(object __instance,out Operation? __state)
    {PreviewDiagnosticsService.ObservePlayer(__instance);BeforeWorker?.Invoke();__state=Enter()?new(__instance,beginning):null;if(__state is not null)beginning=__state;}
    public static void CaptureTask(Task task){if(beginning is not null)Capture(task);}
    public static IEnumerable<CodeInstruction> BeginTranspile(IEnumerable<CodeInstruction> instructions)
    {
        var count=0;var result=new List<CodeInstruction>();
        foreach(var original in instructions)
        {
            var code=new CodeInstruction(original);
            if(code.opcode==OpCodes.Stfld&&Equals(code.operand,WorkerField))
            {
                count++;var duplicate=new CodeInstruction(OpCodes.Dup);duplicate.labels.AddRange(code.labels);code.labels.Clear();duplicate.blocks.AddRange(code.blocks);code.blocks.Clear();
                result.Add(duplicate);result.Add(new(OpCodes.Call,typeof(PlaybackTimingDrawHooks).GetMethod(nameof(CaptureTask))!));
            }
            result.Add(code);
        }
        if(count!=1)throw new NotSupportedException("Exact native worker publication changed.");
        return result;
    }
    public static void BeginFinalizer(Operation? __state)
    {if(__state is null||__state.Closed)return;__state.Closed=true;try{BeforeWorkerFinalizerForTests?.Invoke(__state.Player);if(WorkerField.GetValue(__state.Player) is Task task)Capture(task);}finally{if(ReferenceEquals(beginning,__state))beginning=__state.Parent;Exit();}}
    public static void PlayerDisposing(object __instance,bool __0,out Operation? __state)
    {
        __state=__0&&Enter()?new(__instance,null):null;if(__state is null)return;
        if(WorkerField.GetValue(__instance) is Task task)Capture(task);
        if(players.TryGetValue(__instance,out var state))lock(state.Gate){state.Closing=true;state.Epoch++;if(state.Source is {} source)PreviewFrameDeadlineRegistry.Invalidate(source);}
        PlaybackTimingClockHooks.EndPlayer(__instance);
    }
    public static void SourceDisposing(object __instance,bool __0,out Operation? __state)
    {
        __state=__0&&Enter()?new(__instance,null):null;if(__state is null)return;
        foreach(var state in PlayerStates)lock(state.Gate)if(ReferenceEquals(state.Source,__instance)){state.Epoch++;state.Source=null;}
        PlaybackTimingClockHooks.EndSource(__instance);PreviewFrameDeadlineRegistry.Forget(__instance);sources.Remove(__instance);
        if(observation?.Source.TryGetTarget(out var source)==true&&ReferenceEquals(source,__instance))observation=null;
    }
    public static void LifetimeFinalizer(Operation? __state){if(__state is null||__state.Closed)return;__state.Closed=true;Exit();}
}
