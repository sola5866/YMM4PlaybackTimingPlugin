using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using YMM4PlaybackTimingPlugin.Composition;
using YMM4PlaybackTimingPlugin.Integration;
using YMM4PlaybackTimingPlugin.Playback;
using YukkuriMovieMaker.Player.Video;

internal static class IndependentBoundaryTests
{
    internal static void PublicEntry()
    {
        var first=new YMM4PlaybackTimingPlugin.YMM4PlaybackTimingPlugin();
        var count=Harmony.GetAllPatchedMethods().Count();var second=new YMM4PlaybackTimingPlugin.YMM4PlaybackTimingPlugin();
        var startup=(PlaybackTimingStartup)typeof(YMM4PlaybackTimingPlugin.YMM4PlaybackTimingPlugin).GetField("startup",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
        Check(startup.IsActive&&Harmony.GetAllPatchedMethods().Count()==count,"public plugin entry did not initialize once");
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name!.StartsWith("YMM4")&&!a.GetName().Name!.StartsWith("YMM4PlaybackTimingPlugin")),"public plugin startup loaded an external plugin");
        first.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();second.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Check(!startup.IsActive,"public plugin did not close");Console.WriteLine("PASS public plugin entry auto-starts once and closes independently");
    }
    private static void Check(bool value,string reason){if(!value)throw new Exception(reason);}
    internal static void DrainAndCancellation()
    {
        using var owner=new PlaybackTimingRegistrationOwner();var n=PreviewNativePlaybackContract.Cached.Value!;
        var source=RuntimeHelpers.GetUninitializedObject(n.Source);var player=RuntimeHelpers.GetUninitializedObject(n.Video);n.TimelineSource.SetValue(player,source);
        PlaybackTimingDrawHooks.UpdatePrefix(source,TimeSpan.FromMilliseconds(16),TimelineSourceUsage.Playing,out var canceled);
        PreviewFrameDeadlineRegistry.Invalidate(source);PlaybackTimingDrawHooks.UpdateFinalizer(source,canceled,null);
        Check(!PreviewFrameDeadlineRegistry.TryGet(source,out var candidate)||!candidate.Playing,"canceled/nonloop Update revived a candidate");
        PlaybackTimingDrawHooks.Prefix(player,out var attempt);var primary=new OperationCanceledException(new CancellationToken(true));
        Check(ReferenceEquals(PlaybackTimingDrawHooks.Finalizer(primary,attempt),primary)&&ReferenceEquals(PlaybackTimingDrawHooks.Finalizer(primary,attempt),primary),"finalizer replaced native cancellation or repeated cleanup");
        PlaybackTimingDrawHooks.UpdatePrefix(source,TimeSpan.Zero,TimelineSourceUsage.Playing,out var held);
        var close=owner.CloseAsync();Check(!close.IsCompleted&&PlaybackTimingDrawHooks.ActiveForTests==1,"active Update excluded from independent drain");
        PlaybackTimingDrawHooks.UpdateFinalizer(source,held,primary);PlaybackTimingDrawHooks.UpdateFinalizer(source,held,primary);
        close.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();Check(PlaybackTimingDrawHooks.ActiveForTests==0,"callback use returned twice or remained retained");
    }
    internal static void PlayerCollection()
    {
        var runtime=new CollectionRuntime();using var owner=new PlaybackTimingRegistrationOwner(createSession:()=>new(new PreviewPlaybackClockState(1_000_000),()=>runtime));var weak=MakePlayer();
        for(var i=0;i<4&&weak.Any(w=>w.IsAlive);i++){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
        Check(weak.All(w=>!w.IsAlive),"Timing clock/Draw/registry retained an otherwise unowned native player or source");owner.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();Check(runtime.Disposals==1,"GC orphaned a managed session runtime");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] MakePlayer()
    {
        var player=RuntimeHelpers.GetUninitializedObject(PreviewNativePlaybackContract.Cached.Value!.Video);
        var n=PreviewNativePlaybackContract.Cached.Value!;var source=RuntimeHelpers.GetUninitializedObject(n.Source);n.TimelineSource.SetValue(player,source);
        using(var callback=PlaybackTimingClockHooks.TryEnter()!)
        {
            var session=callback.Owner.GetOrCreateSession(player)!;session.Clock.Start(TimeSpan.Zero,100,0);
            PreviewFrameDeadlineRegistry.Register(source,TimeSpan.FromMilliseconds(16),true);PreviewFrameDeadlineRegistry.TryGet(source,out var deadline);
            Check(session.TryWait(source,deadline.CandidateTime,deadline.Sequence,out _),"clock runtime not exercised");
        }
        PlaybackTimingDrawHooks.UpdatePrefix(source,TimeSpan.Zero,TimelineSourceUsage.Playing,out var updated);PlaybackTimingDrawHooks.UpdateFinalizer(source,updated,null);
        PlaybackTimingDrawHooks.Prefix(player,out var attempt);PlaybackTimingDrawHooks.Finalizer(null,attempt);var updateOnly=RuntimeHelpers.GetUninitializedObject(n.Source);PlaybackTimingDrawHooks.UpdatePrefix(updateOnly,TimeSpan.Zero,TimelineSourceUsage.Playing,out var other);PlaybackTimingDrawHooks.UpdateFinalizer(updateOnly,other,null);return [new(player),new(source),new(updateOnly)];
    }
    private sealed class CollectionRuntime:IPreviewFrameDeadlineRuntime,IDisposable
    {
        private long ticks;internal int Disposals;public long Frequency=>1_000_000;public long GetTimestamp()=>ticks;
        public void Sleep(TimeSpan duration)=>ticks+=PreviewFrameDeadlineWait.DurationToTimestampTicks(duration,Frequency);
        public bool TryPreciseWait(TimeSpan duration){Sleep(duration);return true;}public void Yield()=>ticks+=100;public void SpinOnce()=>ticks+=10;public void Dispose()=>Disposals++;
    }
    public static MethodInfo Factory(MethodBase original)=>typeof(IndependentBoundaryTests).GetMethod(nameof(Noop))!;
    public static void Noop(){}
    internal static void ForeignFactory()
    {
        var h=new Harmony("independent.boundary.factory");var factory=typeof(IndependentBoundaryTests).GetMethod(nameof(Factory))!;
        h.Patch(PlaybackTimingDrawHooks.Draw,prefix:new HarmonyMethod(factory));
        try
        {
            PlaybackTimingRegistrationOwner? retained=null;var rejected=false;
            try{_=new PlaybackTimingRegistrationOwner(retain:value=>retained=value);}catch(NotSupportedException){rejected=true;}
            Check(rejected&&retained is not null&&!retained.IsActive,"foreign factory was admitted");
            try{retained!.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();}catch(NotSupportedException){}
            Check(Harmony.GetPatchInfo(PlaybackTimingDrawHooks.Draw)!.Prefixes.Any(p=>p.owner==h.Id&&p.PatchMethod==factory),"rollback removed a foreign factory");
        }
        finally{h.Unpatch(PlaybackTimingDrawHooks.Draw,factory);}
        using var replacement=new PlaybackTimingRegistrationOwner();replacement.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }
}
