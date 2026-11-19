using System.Diagnostics;
using YMM4PlaybackTimingPlugin.Playback;
using YukkuriMovieMaker.Player.Video;

namespace YMM4PlaybackTimingPlugin.Integration;

// Versioned native audio observation and pause-position handoff.
// A clock and its borrowed backend samples belong to one native video player.
internal static partial class PlaybackTimingClockHooks
{
    internal readonly record struct PlayingState(Activation? Owner, bool Captured, bool Value);
    internal readonly record struct RateState(Activation? Owner, bool Active, TimeSpan Position);

    private static Activation? current;
    internal static Action<object,TimeSpan,TimeSpan>? PositionReadForTests;

    internal static Activation Prepare(Func<PreviewPlaybackSession>? createSession = null, bool enabled = true)
    {
        var candidate = new Activation(createSession ?? (static () => new PreviewPlaybackSession()), enabled);
        if (Interlocked.CompareExchange(ref current, candidate, null) is not null)
            throw new InvalidOperationException("Another playback-timing callback owner remains registered.");
        PreviewFrameDeadlineRegistry.Reset();
        return candidate;
    }

    internal static Callback? TryEnter() => Volatile.Read(ref current)?.TryEnter();

    internal static void Reset()
    {
        var owner = Volatile.Read(ref current);
        if (owner is not null) owner.Dispose();
        else PreviewFrameDeadlineRegistry.Reset();
    }

    internal static bool TryGetClock(object player, out PreviewPlaybackClockState clock)
    {
        var owner = Volatile.Read(ref current);
        if (owner is not null && owner.TryGetSession(player, out var session))
        { clock = session.Clock; return true; }
        clock = null!;
        return false;
    }

    public static void PositionGetterPostfix(object __instance, ref TimeSpan __result)
    {
        using var callback = TryEnter();
        if (callback is null) return;
        if (!IsVideo(__instance)) return;
        var raw=__result;
        try
        {
            callback.Owner.GetOrCreateSession(__instance);
            if (callback.Owner.TryReadAudioPosition(__instance, out var position))
                __result = position;
            PositionReadForTests?.Invoke(__instance,raw,__result);
        }
        catch (Exception error) { DisablePlayer(__instance, "position-getter:" + error.GetType().Name); }
    }

    public static void PositionSetterPostfix(object __instance, TimeSpan __0)
    {
        using var callback = TryEnter();
        if (callback is null) return;
        if (!IsVideo(__instance) || !TryGetClock(__instance, out var clock)) return;
        try
        {
            if (callback.Owner.ObserveAudioPosition(__instance)) return;
            var contract = PreviewNativePlaybackContract.Cached.Value!;
            // A prior native call can finish after callback ownership changes.
            // The current raw field, rather than that call's stale argument,
            // remains the authoritative audio position.
            if (clock.ReanchorOrSeek(RawPosition(contract, __instance), RawRate(contract, __instance), Stopwatch.GetTimestamp(), Maximum(contract, __instance)))
                InvalidateSource(contract, __instance);
        }
        catch (Exception error) { clock.Disable("position-setter:" + error.GetType().Name); }
    }

    public static void PlayingSetterPrefix(object __instance, ref PlayingState __state, bool __0 = true)
    {
        __state = default;
        using var callback = TryEnter();
        if (callback is null) return;
        __state = new(callback.Owner, false, false);
        if (!IsVideo(__instance)) return;
        try
        {
            var previous = (bool)PreviewNativePlaybackContract.Cached.Value!.Playing.GetValue(__instance)!;
            __state = new(callback.Owner, true, previous);
            if (previous && !__0) callback.Owner.CommitAudioPause(__instance);
        }
        catch (Exception error) { DisablePlayer(__instance, "playing-prefix:" + error.GetType().Name); }
    }

    public static void PlayingSetterPostfix(object __instance, bool __0, PlayingState __state)
    {
        using var callback = TryEnter();
        if (callback is null) return;
        if (!IsVideo(__instance)) return;
        if (!ReferenceEquals(__state.Owner, callback.Owner))
        { DisablePlayer(__instance, "playing-callback-owner-changed"); return; }
        if (!__state.Captured) { DisablePlayer(__instance, "playing-state-not-captured"); return; }
        if (__0 == __state.Value) return;
        var session = callback.Owner.GetOrCreateSession(__instance);
        if (session is null) return;
        var clock = session.Clock;
        try
        {
            var contract = PreviewNativePlaybackContract.Cached.Value!;
            var now = Stopwatch.GetTimestamp();
            var position = RawPosition(contract, __instance);
            if (__0)
            {
                callback.Owner.ResetAudio(__instance);
                session.TryStart(position, RawRate(contract, __instance), now, Maximum(contract, __instance));
            }
            else
            {
                session.Stop(position, now);
                callback.Owner.ResetAudio(__instance);
                InvalidateSource(contract, __instance);
            }
        }
        catch (Exception error) { clock.Disable("playing-setter:" + error.GetType().Name); }
    }

    public static void RateSetterPrefix(object __instance, ref RateState __state)
    {
        __state = default;
        using var callback = TryEnter();
        if (callback is null) return;
        __state = new(callback.Owner, false, TimeSpan.Zero);
        if (!IsVideo(__instance) || !TryGetClock(__instance, out var clock)) return;
        try
        {
            if (clock.GetSnapshot().Active)
            {
                var contract = PreviewNativePlaybackContract.Cached.Value!;
                __state = new(callback.Owner, true, clock.GetPosition(RawPosition(contract, __instance), Stopwatch.GetTimestamp()));
            }
        }
        catch (Exception error) { clock.Disable("rate-prefix:" + error.GetType().Name); }
    }

    public static void RateSetterPostfix(object __instance, double __0, RateState __state)
    {
        using var callback = TryEnter();
        if (callback is null) return;
        if (!IsVideo(__instance)) return;
        if (!ReferenceEquals(__state.Owner, callback.Owner))
        { DisablePlayer(__instance, "rate-callback-owner-changed"); return; }
        if (!__state.Active || !TryGetClock(__instance, out var clock)) return;
        try
        {
            if (callback.Owner.ObserveAudioPosition(__instance, force:true)) return;
            var contract = PreviewNativePlaybackContract.Cached.Value!;
            clock.Reanchor(__state.Position, __0, Stopwatch.GetTimestamp(), Maximum(contract, __instance));
        }
        catch (Exception error) { clock.Disable("rate-postfix:" + error.GetType().Name); }
    }

    private static bool IsVideo(object player) => PreviewNativePlaybackContract.Cached.Value?.Video.IsInstanceOfType(player) == true;
    private static TimeSpan RawPosition(PreviewNativePlaybackContract.Contract contract, object player) =>
        (TimeSpan)contract.Position.GetValue(player)!;
    private static double RawRate(PreviewNativePlaybackContract.Contract contract, object player) =>
        (double)contract.Rate.GetValue(player)!;
    private static TimeSpan? Maximum(PreviewNativePlaybackContract.Contract contract, object player)
    {
        var timeline = contract.Timeline.GetValue(player);
        if (timeline is null) return null;
        var videoInfo = contract.VideoInfoGetter.Invoke(timeline, null);
        if (videoInfo is null) return null;
        var length = (int)contract.LengthGetter.Invoke(timeline, null)!;
        return (TimeSpan)contract.GetTimeFrom.Invoke(videoInfo, [length])!;
    }
    private static void InvalidateSource(PreviewNativePlaybackContract.Contract contract, object player)
    {
        if (contract.TimelineSource.GetValue(player) is { } source)
            PreviewFrameDeadlineRegistry.Invalidate(source);
    }
    private static void DisablePlayer(object player, string reason)
    {
        if (TryGetClock(player, out var clock)) clock.Disable(reason);
        try { InvalidateSource(PreviewNativePlaybackContract.Cached.Value!, player); }
        catch { /* A disabled clock cannot commit a stale deadline. */ }
    }

    // Native identities and retirement belong only to this Timing activation.
    internal static void EndPlayer(object player) => Volatile.Read(ref current)?.EndPlayer(player);
    internal static void EndSource(object source)
    {
        var owner = Volatile.Read(ref current);
        if (owner is not null) owner.EndSource(source);
        else PreviewFrameDeadlineRegistry.Invalidate(source);
    }

    internal sealed partial class Activation(Func<PreviewPlaybackSession> createSession, bool initiallyEnabled = true) : IDisposable
    {
        private sealed record Entry(PreviewPlaybackSession Session,object Source)
        {
            internal long FeatureGeneration;
            internal WeakReference<object>? Native;
            internal readonly AudioState Audio = new();
        }
        private sealed record Retirement(PreviewPlaybackSession Session,Task Completion);
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object,Entry> sessions=new();
        // Keep managed clocks/timers until confirmed cleanup, without rooting native players or sources.
        private readonly HashSet<PreviewPlaybackSession> ownedSessions=[];
        private IEnumerable<Entry> SessionEntries=>sessions.Select(pair=>pair.Value);
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object,object> endedSources=new();
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object,object> endedPlayers=new();
        private readonly List<Retirement> retiring=[];
        internal int RetainedRetirementCount {get{lock(gate)return retiring.Count;}}
        private readonly object gate=new();
        private readonly TaskCompletionSource drained=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? completion;
        private bool committed,ending;
        private bool enabled=initiallyEnabled;
        private long featureGeneration;
        private int callbacks;
        internal bool IsActive {get{lock(gate)return committed&&!ending&&enabled;}}
        internal void SetEnabled(bool value)
        {
            lock(gate)
            {
                if(ending||enabled==value)return;
                enabled=value;featureGeneration=checked(featureGeneration+1);
                foreach(var entry in SessionEntries)PreviewFrameDeadlineRegistry.Invalidate(entry.Source);
            }
        }
        internal bool IsFeatureCurrent(long generation){lock(gate)return committed&&!ending&&enabled&&featureGeneration==generation;}
        internal bool TryCaptureSession(object player,out PreviewPlaybackSession session,out object source,out long generation)
        {
            lock(gate)
            {
                generation=featureGeneration;
                if(committed&&!ending&&enabled&&sessions.TryGetValue(player,out var entry)&&entry.FeatureGeneration==featureGeneration)
                {session=entry.Session;source=entry.Source;return true;}
                session=null!;source=null!;return false;
            }
        }
        internal bool TryGetEnabledClock(object player,out PreviewPlaybackClockState clock)
        {
            GetOrCreateSession(player);
            var id=player;
            lock(gate)
            {
                clock=null!;
                if(!committed||ending||!enabled||!sessions.TryGetValue(id,out var entry))return false;
                clock=entry.Session.Clock;
                if(entry.FeatureGeneration!=featureGeneration)
                {
                    ResetAudio(player,preserveStoppedBackend:true);
                    var contract=PreviewNativePlaybackContract.Cached.Value!;
                    var position=RawPosition(contract,player);var now=Stopwatch.GetTimestamp();
                    if((bool)contract.Playing.GetValue(player)!)entry.Session.TryStart(position,RawRate(contract,player),now,Maximum(contract,player));
                    else entry.Session.Stop(position,now);
                    entry.FeatureGeneration=featureGeneration;
                    PreviewFrameDeadlineRegistry.Invalidate(entry.Source);
                }
                return true;
            }
        }
        internal bool TryGetSession(object player,out PreviewPlaybackSession session)=>
            TryGetSession(player,out session,out _);
        internal bool TryGetSession(object player,out PreviewPlaybackSession session,out object source)
        {
            lock(gate){if(sessions.TryGetValue(player,out var entry)){session=entry.Session;source=entry.Source;return true;}session=null!;source=null!;return false;}
        }
        internal PreviewPlaybackSession? GetOrCreateSession(object player)
        {
            var id=player;
            var native=PreviewNativePlaybackContract.Cached.Value!.TimelineSource.GetValue(player);
            if(native is null)return null;
            var source=native;
            lock(gate)
            {
                if(!committed||ending||endedPlayers.TryGetValue(id,out _)||endedSources.TryGetValue(source,out _))return null;
                if(sessions.TryGetValue(id,out var existing))
                {
                    if(ReferenceEquals(existing.Source,source))return existing.Session;
                    PreviewFrameDeadlineRegistry.Invalidate(existing.Source);sessions.Remove(id);Retire(existing.Session);
                }
                var session=createSession();ownedSessions.Add(session);sessions.Add(id,new(session,source){FeatureGeneration=featureGeneration,Native=new(player)});
                var contract=PreviewNativePlaybackContract.Cached.Value!;
                if((bool)contract.Playing.GetValue(player)!)session.TryStart(RawPosition(contract,player),RawRate(contract,player),Stopwatch.GetTimestamp(),Maximum(contract,player));
                return session;
            }
        }
        internal void EndPlayer(object player)
        {
            using var callback=TryEnter();if(callback is null)return;
            PreviewPlaybackSession? session=null;
            lock(gate){endedPlayers.GetValue(player,static _=>new());if(sessions.TryGetValue(player,out var entry)){sessions.Remove(player);session=entry.Session;}}
            if(session is not null)Retire(session);
        }
        internal void EndSource(object source)
        {
            using var callback=TryEnter();if(callback is null)return;
            Entry[] matching;
            lock(gate){endedSources.GetValue(source,static _=>new());PreviewFrameDeadlineRegistry.Forget(source);var entries=sessions.Where(e=>ReferenceEquals(e.Value.Source,source)).ToArray();matching=entries.Select(e=>e.Value).ToArray();foreach(var entry in entries)sessions.Remove(entry.Key);}
            foreach(var entry in matching)Retire(entry.Session);
        }
        private void Retire(PreviewPlaybackSession session)
        {
            var task=session.CloseAsync();var retirement=new Retirement(session,task);lock(gate)retiring.Add(retirement);
            _=task.ContinueWith(done=>{if(done.IsCompletedSuccessfully){lock(gate){retiring.Remove(retirement);ownedSessions.Remove(session);}}else Trace.TraceWarning("PlaybackTiming player cleanup retained: {0}",done.Exception);},TaskScheduler.Default);
        }
        internal long RegisterCandidate(object native,TimeSpan time)
        {
            var source=native;
            lock(gate){if(!committed||ending||!enabled||endedSources.TryGetValue(source,out _))return 0;return PreviewFrameDeadlineRegistry.RegisterPending(source,time);}
        }
        internal void Commit(){lock(gate){if(ending)throw new InvalidOperationException("PlaybackTiming callback owner is ending.");committed=true;}}
        internal Callback? TryEnter(){lock(gate){if(!committed||ending)return null;callbacks++;return new(this);}}
        internal void Exit(){lock(gate){callbacks--;if(ending&&callbacks==0)drained.TrySetResult();}}
        internal void CloseAdmission()
        {
            PreviewPlaybackSession[] snapshot;
            lock(gate){ending=true;committed=false;snapshot=ownedSessions.ToArray();if(callbacks==0)drained.TrySetResult();}
            foreach(var session in snapshot)session.CloseAdmission();
        }
        internal Task CloseAsync()
        {
            TaskCompletionSource promise;
            lock(gate){if(completion is not null)return completion;promise=new(TaskCreationOptions.RunContinuationsAsynchronously);completion=promise.Task;}
            _=Finish(promise);return promise.Task;
        }
        private async Task Finish(TaskCompletionSource promise)
        {
            try
            {
                CloseAdmission();await drained.Task.ConfigureAwait(false);
                PreviewPlaybackSession[] snapshot;Task[] retiringSnapshot;
                lock(gate){snapshot=ownedSessions.Where(session=>!retiring.Any(entry=>ReferenceEquals(entry.Session,session))).ToArray();retiringSnapshot=retiring.Select(e=>e.Completion).ToArray();}
                await Task.WhenAll(snapshot.Select(session=>session.CloseAsync()).Concat(retiringSnapshot)).ConfigureAwait(false);
                lock(gate){sessions.Clear();ownedSessions.Clear();endedPlayers.Clear();endedSources.Clear();retiring.Clear();PreviewFrameDeadlineRegistry.Reset();Interlocked.CompareExchange(ref current,null,this);}
                promise.TrySetResult();
            }
            catch(Exception error){promise.TrySetException(error);}
        }
        public void Dispose()=>Dispose(TimeSpan.FromSeconds(10));
        internal void Dispose(TimeSpan timeout)=>CloseAsync().WaitAsync(timeout).GetAwaiter().GetResult();
    }

    internal sealed class Callback(Activation owner) : IDisposable
    {
        internal Activation Owner => owner;
        private int released;
        public void Dispose()
        { if (Interlocked.Exchange(ref released, 1) == 0) owner.Exit(); }
    }
}
