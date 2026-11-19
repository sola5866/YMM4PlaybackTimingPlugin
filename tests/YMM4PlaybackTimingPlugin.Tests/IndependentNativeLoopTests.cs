using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using HarmonyLib;
using YMM4PlaybackTimingPlugin.Composition;
using YMM4PlaybackTimingPlugin.Integration;
using YukkuriMovieMaker.Player.Audio;
using YMM4PlaybackTimingPlugin.Playback;
using YMM4PlaybackTimingPlugin.Diagnostics;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

internal static class IndependentNativeLoopTests
{
    private sealed class Stats{internal int Draws,Presents,Refreshes;internal Task? Worker;internal readonly List<object> Sources=[];internal readonly List<(int Il,TimeSpan Value)> Reads=[];internal readonly List<TimeSpan> Candidates=[];}
    private static readonly ConcurrentDictionary<object,Stats> players=new(ReferenceEqualityComparer.Instance);
    private static string mode="";private static bool refreshing;private static int injected,updateCalls,skipDraws,staleWaits;private static Harmony? foreign;
    private static PreviewNativePlaybackContract.Contract Native=>PreviewNativePlaybackContract.Cached.Value!;
    private static readonly Exception updateFailure=new InvalidOperationException("late native Update failure");
    private static readonly List<object> waits=[];
    private static object? warmPlayer;private static int warmPresents;
    private static int observedSuccessfulPresents,observedFailedPresents;
    private static void Check(bool value,string reason){if(!value)throw new Exception(reason);}
    internal static int Run(string selected)
    {
        Exception? failure=null;var thread=new Thread(()=>{try{Execute(selected);}catch(Exception error){failure=error;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(50))){Console.WriteLine("FAIL native Timing worker test timeout");return 1;}
        if(failure is not null){Console.WriteLine("FAIL independent native Timing: "+failure);return 1;}
        return 0;
    }
    private static void Execute(string selected)
    {
        mode=selected;var loop=PreviewNativeVideoLoopContract.Cached.Loop!;var original=loop.GetMethodBody()!.GetILAsByteArray()!;
        var observers=new Harmony("test.independent.observers."+Guid.NewGuid().ToString("N"));foreign=new("test.independent.foreign."+Guid.NewGuid().ToString("N"));
        PlaybackTimingRegistrationOwner? owner=null;var controls=new List<WindowsFormsHost>();var samplers=new List<Task>();using var stop=new CancellationTokenSource();
        PreviewMeasurements? diagnostics=null;PreviewDiagnosticsService.Subscription? diagnosticSubscription=null;
        try
        {
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name!.StartsWith("YMM4")&&!a.GetName().Name!.StartsWith("YMM4PlaybackTimingPlugin")),"standalone fixture loaded an external plugin");
            if(mode is "warm" or "warm-long")WarmOriginal(observers);
            var runtimes=new List<ControlledRuntime>();var runtime=new ControlledRuntime();Task? closing=null;
            owner=new(createSession:()=>{var owned=runtimes.Count==0?runtime:new ControlledRuntime();runtimes.Add(owned);return new PreviewPlaybackSession(createRuntime:()=>owned);});
            if(mode is "stop-wait" or "runtime-failure")runtime.BeforeWait=()=>{
                if(Interlocked.CompareExchange(ref injected,1,0)!=0)return;
                if(mode=="runtime-failure")throw new InvalidOperationException("injected runtime failure");
                closing=owner.CloseAsync();Check(!closing.IsCompleted&&runtime.Disposals==0,"active wait or worker was freed during close");
            };

            PlaybackTimingClockHooks.PositionReadForTests=RecordPosition;
            PlaybackTimingDrawHooks.WaitResultForTests=result=>{if(mode=="consecutive-skips"&&players.Values.Sum(s=>s.Draws)<=2||mode=="consecutive-errors"&&players.Values.Sum(s=>s.Presents)<=1)staleWaits++;var activePlayer=players.Keys.FirstOrDefault(p=>Field(p,"videoTask").GetValue(p) is Task t&&t.Id==Task.CurrentId);
                if(activePlayer is not null&&Native.TimelineSource.GetValue(activePlayer) is {} src&&YMM4PlaybackTimingPlugin.Playback.PreviewFrameDeadlineRegistry.TryGet(src,out var candidate))players[activePlayer].Candidates.Add(candidate.CandidateTime);
                lock(waits)waits.Add(new{result.Completed,result.Reason,result.ActualMilliseconds});};
            var draw=PlaybackTimingDrawHooks.Draw;
            // These registrations occur after Timing's constructor; Begin authenticates its own successful regeneration before launching workers.
            observers.Patch(draw,postfix:new HarmonyMethod(typeof(IndependentNativeLoopTests),nameof(DrawComplete)));
            observers.Patch(Native.Video.GetMethod("Present",BindingFlags.Instance|BindingFlags.NonPublic)!,postfix:new HarmonyMethod(typeof(IndependentNativeLoopTests),nameof(Present)));
            if(mode=="diagnostics")observers.Patch(PreviewDiagnosticsConnection.Cached.Value!.Present,transpiler:new HarmonyMethod(typeof(IndependentNativeLoopTests),nameof(ObserveNativePresent)));
            if(mode is "failed-update" or "edit-refresh")observers.Patch(Native.Update,finalizer:new HarmonyMethod(typeof(IndependentNativeLoopTests),nameof(AfterUpdate)){priority=int.MinValue});
            if(mode=="edit-refresh")observers.Patch(Native.Video.GetMethod("Edit",BindingFlags.Instance|BindingFlags.NonPublic)!,postfix:new HarmonyMethod(typeof(IndependentNativeLoopTests),nameof(EditRefresh)));
            if(mode=="recoverable")observers.Patch(draw,prefix:new HarmonyMethod(typeof(IndependentNativeLoopTests),nameof(DeviceFault)){priority=Priority.First});
            if(mode=="consecutive-skips")observers.Patch(draw,prefix:new HarmonyMethod(typeof(IndependentNativeLoopTests),nameof(SkipFirstDraw)){priority=Priority.First});
            if(mode is "consecutive-skips" or "skipped-update")observers.Patch(Native.Update,prefix:new HarmonyMethod(typeof(IndependentNativeLoopTests),nameof(SkipUpdate)){priority=Priority.First});
            if(mode=="consecutive-errors")
            {
                observers.Patch(draw,prefix:new HarmonyMethod(typeof(IndependentNativeLoopTests),nameof(ThrowFirstDraw)){priority=Priority.First},finalizer:new HarmonyMethod(typeof(IndependentNativeLoopTests),nameof(SuppressUpdate)){priority=Priority.First});
                observers.Patch(Native.Update,prefix:new HarmonyMethod(typeof(IndependentNativeLoopTests),nameof(ThrowSecondUpdate)){priority=Priority.First},finalizer:new HarmonyMethod(typeof(IndependentNativeLoopTests),nameof(SuppressUpdate)){priority=Priority.First});
            }
            if(mode=="suppressed-update")observers.Patch(Native.Update,prefix:new HarmonyMethod(typeof(IndependentNativeLoopTests),nameof(ThrowUpdate)){priority=Priority.First},finalizer:new HarmonyMethod(typeof(IndependentNativeLoopTests),nameof(SuppressUpdate)){priority=Priority.First});
            var count=mode=="multiple"?2:1;
            for(var i=0;i<count;i++)
            {
                var timeline=new Timeline();timeline.VideoInfo.Width=16;timeline.VideoInfo.Height=16;timeline.VideoInfo.FPS=60;
                // A future item supplies real native duration without evaluating its source during this short playback.
                timeline.Items=ImmutableList.Create<IItem>(new ShapeItem{Frame=10000,Length=600});timeline.RefreshTimelineLengthAndMaxLayer();
                var scenes=new Scenes(false);scenes.AddScene(timeline);
                var host=new WindowsFormsHost{Width=16,Height=16};host.Measure(new System.Windows.Size(16,16));host.Arrange(new System.Windows.Rect(0,0,16,16));controls.Add(host);
                var player=Activator.CreateInstance(Native.Video,[host,timeline,scenes,scenes.UndoRedoManager,false])!;players[player]=new();
                Field(player,"hostWidth").SetValue(player,16f);Field(player,"hostHeight").SetValue(player,16f);
                var backend=(AudioPlayer)RuntimeHelpers.GetUninitializedObject(typeof(AudioPlayer));
                GetterField(typeof(AudioPlayer).GetProperty("IsPlaying")!.GetMethod!,typeof(bool)).SetValue(backend,true);
                var started=DateTime.Now;
                GetterField(Native.AudioClock.PositionGetter,typeof(DateTime)).SetValue(backend,started);
                Native.AudioClock.Backend.SetValue(player,backend);Native.AudioClock.Start.SetValue(player,TimeSpan.Zero);
                Native.Playing.SetValue(player,mode!="paused");Native.Rate.SetValue(player,100d);
                if(mode=="diagnostics")
                {
                    diagnostics=new(Stopwatch.Frequency);diagnostics.SetEnabled(true,true,Stopwatch.GetTimestamp());
                    diagnosticSubscription=PreviewDiagnosticsService.Select(player,diagnostics);PreviewGeometry.Request(player);
                }
                // Independently sampled native audio position, never a candidate/wait-completion write.
                samplers.Add(Task.Run(async()=>{while(!stop.IsCancellationRequested){Native.Position.SetValue(player,DateTime.Now-started);try{await Task.Delay(33,stop.Token);}catch(OperationCanceledException){break;}}}));
                Native.Video.GetMethod("BeginVideoTask")!.Invoke(player,null);players[player].Worker=(Task)Field(player,"videoTask").GetValue(player)!;
            }
            var watch=Stopwatch.StartNew();var restarted=false;
            while(players.Values.Any(s=>!s.Worker!.IsCompleted)||mode=="restart"&&!restarted)
            {
                if(mode=="restart"&&!restarted&&players.Values.All(s=>s.Worker!.IsCompleted))
                {
                    var player=players.Keys.Single();var stats=players[player];stats.Worker!.GetAwaiter().GetResult();
                    Check(stats.Presents==16,"first worker did not reach Present boundary");
                    Field(player,"videoTask").SetValue(player,null);Field(player,"videoTask_canceled").SetValue(player,false);
                    Native.Video.GetMethod("BeginVideoTask")!.Invoke(player,null);stats.Worker=(Task)Field(player,"videoTask").GetValue(player)!;restarted=true;
                }
                if(watch.Elapsed>TimeSpan.FromSeconds(20))throw new TimeoutException("native worker did not complete");
                var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,()=>frame.Continue=false);Dispatcher.PushFrame(frame);Thread.Sleep(1);
            }
            foreach(var (player,stats) in players)
            {
                stats.Worker!.GetAwaiter().GetResult();var crashed=(bool)Native.Video.GetProperty("IsCrashed")!.GetValue(player)!;
                Check(crashed==(mode=="failed-update"),"unexpected native worker crash");
                Check(new[]{"timelineVideo","renderTarget","devicesAndContext","devices"}.All(n=>Field(player,n).GetValue(player) is null),"native finally did not return its resources");
            }
            var draws=players.Values.Sum(s=>s.Draws);var presents=players.Values.Sum(s=>s.Presents);var refreshes=players.Values.Sum(s=>s.Refreshes);
            if(mode=="diagnostics")
            {
                var measured=diagnostics!.Read(Stopwatch.GetTimestamp());
                Check(measured.Draws==draws&&measured.Sends==observedSuccessfulPresents&&measured.Failures==observedFailedPresents,"diagnostics differs from independently observed native boundaries");
                var geometry=PreviewGeometry.Request(players.Keys.Single());
                Check(geometry is not null&&geometry.BufferWidth==16&&geometry.BufferHeight==16,"render-thread geometry was not captured");
                Check(Math.Abs(geometry!.Transform.M11-2)<.001,"geometry stayed stale after zoom without another UI poll");
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{diagnosticDraws=measured.Draws,diagnosticSends=measured.Sends,observedSuccessfulPresents,measured.Failures,geometry}));
            }
            if(mode=="stop-wait")Check(injected==1&&closing is not null&&presents==16,"stop during real wait was not exercised");
            else if(mode=="runtime-failure")Check(injected==1&&presents==16,"wait failure escaped into native playback");
            else if(mode=="restart")Check(draws==32&&presents==32&&PlaybackTimingDrawHooks.WaitsForTests==32&&players.Values.SelectMany(s=>s.Sources).Distinct(ReferenceEqualityComparer.Instance).Count()==2,"new worker/source was not independently rebound");
            else if(mode=="failed-update")Check(injected==1&&refreshes==1&&PlaybackTimingDrawHooks.WaitsForTests==0,"failed Update refresh waited");
            else if(mode=="paused")Check(draws==1&&presents==1&&PlaybackTimingDrawHooks.WaitsForTests==0,"paused Draw waited");
            else if(mode=="regenerate")Check(injected==1&&PlaybackTimingDrawHooks.WaitsForTests==0,"unknown in-flight generation resumed waiting");
            else if(mode=="recoverable")Check(injected==1&&players.Values.SelectMany(s=>s.Sources).Distinct(ReferenceEqualityComparer.Instance).Count()==2&&PlaybackTimingDrawHooks.WaitsForTests==16,"recoverable source exchange lost native pacing/session");
            else if(mode=="consecutive-errors")Check(staleWaits==0&&PlaybackTimingDrawHooks.WaitsForTests==14&&presents==16,"consecutive suppressed Draw/Update failures reused old candidate: stale="+staleWaits+" waits="+PlaybackTimingDrawHooks.WaitsForTests);
            else if(mode=="consecutive-skips")Check(staleWaits==0&&PlaybackTimingDrawHooks.WaitsForTests==draws-2,"consecutive skipped Draw/Update reused the old candidate: stale="+staleWaits+" waits="+PlaybackTimingDrawHooks.WaitsForTests);
            else if(mode is "skipped-update" or "suppressed-update")Check(PlaybackTimingDrawHooks.WaitsForTests==1&&presents==16,"skipped/failed and suppressed Update admitted stale waits");
            else Check(draws>=16*count&&presents==draws&&PlaybackTimingDrawHooks.WaitsForTests>=8*count,"normal native loop did not pace/present: draws="+draws+" waits="+PlaybackTimingDrawHooks.WaitsForTests);
            if(mode=="edit-refresh")Check(refreshes==1&&injected==1,"actual Edit refresh was not exercised");
            if(mode is "cold-long" or "warm-long")foreach(var stats in players.Values)
            {
                Check(stats.Draws==180&&stats.Reads.Count==720&&stats.Candidates.Count==180,"not all native getter/wait phases reached: reads="+stats.Reads.Count+" candidates="+stats.Candidates.Count);
                for(var i=0;i<180;i++){Check(stats.Reads.Skip(i*4).Take(4).Select(r=>r.Il).SequenceEqual(new[]{377,512,536,592}),"native getter callsite changed");Check(stats.Candidates[i]<=stats.Reads[i*4+2].Value,"positive residual native Delay after Timing wait");}
            }
            Check(Harmony.GetPatchInfo(loop) is null&&original.SequenceEqual(loop.GetMethodBody()!.GetILAsByteArray()!),"native loop was changed");
            Check(!Harmony.GetAllPatchedMethods().Any(m=>m.DeclaringType?.Assembly==typeof(Harmony).Assembly),"Harmony was patched");
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{mode,syntheticBackend=true,physicalAudio=false,draws,presents,refreshes,waits=PlaybackTimingDrawHooks.WaitsForTests,rejected=PlaybackTimingDrawHooks.RejectedForTests,results=waits.ToArray(),getterReads=players.Values.Select(s=>s.Reads.Count).ToArray(),loopPatched=false}));
            stop.Cancel();Task.WhenAll(samplers).GetAwaiter().GetResult();
            foreach(var player in players.Keys)((IDisposable)player).Dispose();foreach(var host in controls)host.Dispose();
            owner.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();owner=null;if(mode is "stop-wait" or "runtime-failure")Check(runtime.Disposals==1,"session wait runtime was not disposed once after worker drain");
            Check(!PlaybackTimingPatchRegistration.Resolve().Any(p=>Harmony.GetPatchInfo(p.Target)?.Owners.Any(o=>o.StartsWith("YMM4PlaybackTimingPlugin."))==true),"Timing patches remain after terminal proof");
            Console.WriteLine("PASS independent native loop "+mode+": own audio clock/worker proof, no loop/Harmony patch");
        }
        finally
        {
            PreviewDiagnosticsService.Release(diagnosticSubscription);
            foreach(var player in players.Keys)Field(player,"videoTask_canceled").SetValue(player,true);
            stop.Cancel();Task.WhenAll(samplers).Wait(TimeSpan.FromSeconds(5));
            var terminal=players.Values.All(s=>s.Worker?.IsCompleted!=false);
            if(terminal){foreach(var player in players.Keys)((IDisposable)player).Dispose();foreach(var host in controls)host.Dispose();if(owner is not null)owner.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();}
            observers.UnpatchAll(observers.Id);foreign.UnpatchAll(foreign.Id);PlaybackTimingDrawHooks.WaitResultForTests=null;PlaybackTimingDrawHooks.BeforeWaitForTests=null;PlaybackTimingClockHooks.PositionReadForTests=null;
            Check(terminal,"nonterminal native worker must retain its resources");
        }
    }
    private static FieldInfo Field(object player,string name)=>Native.Video.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!;
    public static IEnumerable<CodeInstruction> ObserveNativePresent(IEnumerable<CodeInstruction> instructions)
    {
        foreach(var code in instructions)
        {
            yield return code;
            if(code.Calls(PreviewDiagnosticsHooks.DxgiPresent))
            {yield return new(System.Reflection.Emit.OpCodes.Dup);yield return new(System.Reflection.Emit.OpCodes.Call,typeof(IndependentNativeLoopTests).GetMethod(nameof(NativePresentResult))!);}
        }
    }
    public static void NativePresentResult(SharpGen.Runtime.Result result)
    {if(result.Code==0)observedSuccessfulPresents++;else if(result.Code<0)observedFailedPresents++;}
    private static FieldInfo GetterField(MethodInfo getter,Type type)=>PatchProcessor.GetOriginalInstructions(getter).Select(c=>c.operand).OfType<FieldInfo>().Single(f=>f.FieldType==type);
    public static void DrawComplete(object __instance)
    {
        if(!players.TryGetValue(__instance,out var stats))return;
        if(refreshing){stats.Refreshes++;return;}stats.Draws++;if(Native.TimelineSource.GetValue(__instance) is {} source)stats.Sources.Add(source);
        if(mode=="diagnostics"&&stats.Draws==1)Native.Video.GetProperty("PreviewDisplayZoom")!.SetValue(__instance,2f);
        if(mode=="regenerate"&&Interlocked.CompareExchange(ref injected,1,0)==0)foreign!.Patch(PlaybackTimingDrawHooks.Draw,postfix:new HarmonyMethod(typeof(IndependentNativeLoopTests),nameof(Noop)));
    }
    public static void Present(object __instance)
    {if(players.TryGetValue(__instance,out var stats)&&++stats.Presents>=(mode=="paused"?1:mode.EndsWith("-long",StringComparison.Ordinal)?180:mode=="restart"&&players[__instance].Sources.Distinct(ReferenceEqualityComparer.Instance).Count()>1?32:16))Field(__instance,"videoTask_canceled").SetValue(__instance,true);}
    public static Exception? AfterUpdate(object __instance,Exception? __exception)
    {
        if(Interlocked.CompareExchange(ref injected,1,0)!=0)return __exception;
        var player=players.Keys.Single(p=>ReferenceEquals(Native.TimelineSource.GetValue(p),__instance));
        refreshing=true;
        try{if(mode=="edit-refresh")Native.Video.GetMethod("Edit",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(player,null);else PlaybackTimingDrawHooks.Draw.Invoke(player,null);}
        finally{refreshing=false;}
        return mode=="failed-update"?updateFailure:__exception;
    }
    public static void EditRefresh(object __instance){if(refreshing)PlaybackTimingDrawHooks.Draw.Invoke(__instance,null);}
    public static void DeviceFault(){if(players.Values.Sum(s=>s.Draws)==1&&Interlocked.CompareExchange(ref injected,1,0)==0)throw new SharpGen.Runtime.SharpGenException(new SharpGen.Runtime.Result(unchecked((int)0x887a0005)),"native recoverable fixture fault");}
    private static void RecordPosition(object player,TimeSpan raw,TimeSpan value)
    {
        if(!players.TryGetValue(player,out var stats))return;
        var frames=new StackTrace().GetFrames();
        for(var i=0;i<frames.Length-1;i++)if(frames[i].GetMethod() is MethodInfo method&&Harmony.GetOriginalMethod(method)==Native.PositionGetter&&
            frames[i+1].GetMethod() is MethodInfo caller&&Harmony.GetOriginalMethod(caller)==PreviewNativeVideoLoopContract.Cached.Loop)
        {stats.Reads.Add((frames[i+1].GetILOffset(),value));return;}
    }
    private static void WarmOriginal(Harmony observer)
    {
        var present=Native.Video.GetMethod("Present",BindingFlags.Instance|BindingFlags.NonPublic)!;var method=typeof(IndependentNativeLoopTests).GetMethod(nameof(WarmPresent))!;
        observer.Patch(present,postfix:new HarmonyMethod(method));
        var timeline=new Timeline();timeline.VideoInfo.Width=16;timeline.VideoInfo.Height=16;timeline.VideoInfo.FPS=60;
        var scenes=new Scenes(false);scenes.AddScene(timeline);using var control=new WindowsFormsHost{Width=16,Height=16};control.Measure(new System.Windows.Size(16,16));control.Arrange(new System.Windows.Rect(0,0,16,16));
        var player=Activator.CreateInstance(Native.Video,[control,timeline,scenes,scenes.UndoRedoManager,false])!;warmPlayer=player;Native.Playing.SetValue(player,true);Native.Rate.SetValue(player,100d);
        Field(player,"hostWidth").SetValue(player,16f);Field(player,"hostHeight").SetValue(player,16f);
        using var stop=new CancellationTokenSource();var start=Stopwatch.GetTimestamp();
        var sampler=Task.Run(async()=>{while(!stop.IsCancellationRequested){Native.Position.SetValue(player,TimeSpan.FromSeconds((Stopwatch.GetTimestamp()-start)/(double)Stopwatch.Frequency));try{await Task.Delay(10,stop.Token);}catch(OperationCanceledException){break;}}});
        Native.Video.GetMethod("BeginVideoTask")!.Invoke(player,null);var worker=(Task)Field(player,"videoTask").GetValue(player)!;var watch=Stopwatch.StartNew();
        try
        {
            while(!worker.IsCompleted){if(watch.Elapsed>TimeSpan.FromSeconds(10))throw new TimeoutException("warm native worker");var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,()=>frame.Continue=false);Dispatcher.PushFrame(frame);Thread.Sleep(1);}
            worker.GetAwaiter().GetResult();Check(warmPresents>=4,"original native warmup did not Present");
        }
        finally{Field(player,"videoTask_canceled").SetValue(player,true);stop.Cancel();sampler.GetAwaiter().GetResult();if(worker.IsCompleted)((IDisposable)player).Dispose();observer.Unpatch(present,method);warmPlayer=null;}
    }
    public static void WarmPresent(object __instance){if(ReferenceEquals(__instance,warmPlayer)&&++warmPresents>=4)Field(__instance,"videoTask_canceled").SetValue(__instance,true);}
    private sealed class ControlledRuntime:IPreviewFrameDeadlineRuntime,IDisposable
    {
        private PreviewPlaybackWaitRuntime? inner;internal Action? BeforeWait;internal int Disposals;
        private PreviewPlaybackWaitRuntime Inner=>inner??=new();
        public long Frequency=>Stopwatch.Frequency;public long GetTimestamp()=>Stopwatch.GetTimestamp();
        public void Sleep(TimeSpan duration){BeforeWait?.Invoke();Inner.Sleep(duration);}
        public bool TryPreciseWait(TimeSpan duration){BeforeWait?.Invoke();return Inner.TryPreciseWait(duration);}
        public void Yield(){BeforeWait?.Invoke();Inner.Yield();}public void SpinOnce(){BeforeWait?.Invoke();Inner.SpinOnce();}
        public void Dispose(){Disposals++;inner?.Dispose();}
    }
    public static void Noop(){}
    public static bool SkipFirstDraw()=>Interlocked.Increment(ref skipDraws)!=1;
    public static bool SkipUpdate(){var call=Interlocked.Increment(ref updateCalls);return mode=="consecutive-skips"?call!=2:call==1;}
    public static void ThrowFirstDraw(){if(Interlocked.Increment(ref skipDraws)==1)throw updateFailure;}
    public static void ThrowSecondUpdate(){if(Interlocked.Increment(ref updateCalls)==2)throw updateFailure;}
    public static void ThrowUpdate(){if(Interlocked.Increment(ref updateCalls)>1)throw updateFailure;}
    public static Exception? SuppressUpdate(Exception? __exception)=>ReferenceEquals(__exception,updateFailure)?null:__exception;
}
