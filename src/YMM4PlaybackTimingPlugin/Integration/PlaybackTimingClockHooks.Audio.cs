using System.Diagnostics;
using System.Runtime.CompilerServices;
using YukkuriMovieMaker.Player.Audio;
using YMM4PlaybackTimingPlugin.Playback;

namespace YMM4PlaybackTimingPlugin.Integration;

internal static partial class PlaybackTimingClockHooks
{
    // Native audio objects remain borrowed. Each player's existing callback
    // owner owns the sample and its gate; the playback clock owns no native object.
    private sealed class AudioState
    {
        internal readonly object Gate = new();
        internal AudioPlayer? Backend, StoppingBackend, RestartingBackend;
        internal WeakReference<AudioPlayer>? BackendWeak;
        internal long NextSample;
        internal TimeSpan Position, Start;
        internal bool FinalCaptured, HasSample;
        internal TimeSpan? BackendInitialPosition;
        internal long Sequence;
    }

    internal readonly record struct AudioEndState(Activation? Owner, WeakReference<object>? Native, long Sequence);

    internal readonly record struct BackendRateObservation(WeakReference<Activation>? Owner,
        WeakReference<object>? Native, WeakReference<AudioPlayer>? Backend, double Rate, WeakReference<object> Source, long Feature);
    [ThreadStatic] private static BackendRateObservation rateObservation;
    [ThreadStatic] private static AudioPlayerPlaybackRateMode requestedMode;
    [ThreadStatic] private static bool hasRequestedMode;
    [ThreadStatic] private static int audioReadDepth;

    public static void AudioBackendRatePostfix(AudioPlayer __instance, double __result)
    {
        rateObservation=default;
        if(audioReadDepth!=0)return;
        using var callback=TryEnter();
        if(callback is not null)callback.Owner.ObserveBackend(__instance,__result,out rateObservation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void AudioRequestedRatePostfix(object __instance, double __result)
    {
        var observed=rateObservation;
        rateObservation=default;
        if(audioReadDepth!=0 || observed.Rate==__result/100.0 || observed.Native is null
            || !observed.Native.TryGetTarget(out var native) || !ReferenceEquals(native,__instance)
            || observed.Backend is null || !observed.Backend.TryGetTarget(out var backend))return;
        using var callback=TryEnter();
        if(callback is null || observed.Owner is null || !observed.Owner.TryGetTarget(out var owner)
            || !ReferenceEquals(owner,callback.Owner)
            || !ReferenceEquals(PreviewNativePlaybackContract.Cached.Value!.AudioClock.Backend.GetValue(native),backend)
            || !callback.Owner.IsObservationCurrent(native,observed)
            || !IsDirectAudioLoopCaller())return;
        try{callback.Owner.CaptureAudioRatePosition(native);}
        catch(Exception error){DisablePlayer(native,"audio-rate-capture:"+error.GetType().Name);}
        finally{rateObservation=default;}
    }

    public static void AudioRequestedModePostfix(AudioPlayerPlaybackRateMode __result)
    {requestedMode=__result;hasRequestedMode=true;}

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void AudioBackendModePostfix(AudioPlayer __instance, AudioPlayerPlaybackRateMode __result)
    {
        var expected=requestedMode;
        var ready=hasRequestedMode;
        hasRequestedMode=false;
        if(audioReadDepth!=0 || !ready || expected==__result)return;
        using var callback=TryEnter();
        if(callback is null || !callback.Owner.ObserveBackend(__instance,0,out var observed)
            || !observed.Native!.TryGetTarget(out var native)
            || !ReferenceEquals(PreviewNativePlaybackContract.Cached.Value!.AudioClock.Backend.GetValue(native),__instance)
            || !callback.Owner.IsObservationCurrent(native,observed)
            || !IsDirectAudioLoopCaller())return;
        try{callback.Owner.CaptureAudioRatePosition(native);}
        catch(Exception error){DisablePlayer(native,"audio-mode-capture:"+error.GetType().Name);}
        finally{rateObservation=default;}
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsDirectAudioLoopCaller()
    {
        var native=PreviewNativePlaybackContract.Cached.Value!;
        foreach(var frame in new StackTrace(false).GetFrames())
        {
            var method=frame.GetMethod();
            if(method?.DeclaringType is null || method.DeclaringType==typeof(PlaybackTimingClockHooks))continue;
            if((method.DeclaringType==native.Audio || method.DeclaringType==typeof(AudioPlayer))
                && method.Name.StartsWith("get_PlaybackRate",StringComparison.Ordinal))continue;
            return method==native.AudioClock.PositionLoop;
        }
        return false;
    }

    public static void AudioEndPrefix(object __instance, out AudioEndState __state)
    {
        __state = default;
        using var callback = TryEnter();
        if (callback is null || !IsVideo(__instance)) return;
        try
        {
            var sequence = callback.Owner.BeginAudioEnd(__instance);
            if (sequence != 0) __state = new(callback.Owner, new(__instance), sequence);
        }
        catch (Exception error) { DisablePlayer(__instance, "audio-end:" + error.GetType().Name); }
    }

    public static void AudioEndPostfix(Task __result, AudioEndState __state)
    {
        if (__state.Owner is null) return;
        _ = __result.ContinueWith(done =>
        {
            using var callback = TryEnter();
            if (callback is not null && ReferenceEquals(callback.Owner, __state.Owner)
                && __state.Native!.TryGetTarget(out var native))
                callback.Owner.AudioEndCompleted(native, __state.Sequence, done);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // Serializes the native poll and PlaybackTiming's bounded sampler, including the
    // backend's lazy clock initialization. Never hold this lease across Stop/Join.
    public static void AudioPositionPrefix(object __instance, out AudioReadLease? __state)
    {
        __state = null;
        var callback = TryEnter();
        if (callback is null) return;
        try
        {
            __state = callback.Owner.BorrowAudioRead(__instance, callback);
            if (__state is not null) callback = null;
        }
        finally { callback?.Dispose(); }
    }

    public static Exception? AudioPositionFinalizer(Exception? __exception, AudioReadLease? __state)
    {
        __state?.Dispose();
        return __exception;
    }

    public static void AudioStoppingPrefix(object __instance)
    {
        using var callback = TryEnter();
        if (callback is null) return;
        callback.Owner.AudioStopping(__instance);
    }

    internal sealed class AudioReadLease : IDisposable
    {
        private readonly object gate;
        private readonly Callback callback;
        private int closed;
        internal AudioReadLease(object gate,Callback callback)
        {this.gate=gate;this.callback=callback;audioReadDepth++;}
        public void Dispose()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            audioReadDepth--;
            Monitor.Exit(gate);
            callback.Dispose();
        }
    }

    internal sealed partial class Activation
    {
        private long audioPauseSequence;
        private WeakReference<Activation>? audioOwnerWeak;

        internal bool ObserveBackend(AudioPlayer backend,double rate,out BackendRateObservation observation)
        {
            lock(gate)
            {
                observation=default;
                if(!committed || ending || !enabled || FindBackend(backend) is not {} entry)return false;
                lock(entry.Audio.Gate)
                {
                    if(entry.Native is null || !entry.Native.TryGetTarget(out var native)
                        || !ReferenceEquals(PreviewNativePlaybackContract.Cached.Value!.AudioClock.Backend.GetValue(native),backend))return false;
                    BindAudio(native,entry);
                    observation=new(audioOwnerWeak??=new(this),entry.Native,entry.Audio.BackendWeak,rate,new(entry.Source),entry.FeatureGeneration);
                    return true;
                }
            }
        }

        internal bool IsObservationCurrent(object native,BackendRateObservation observation)
        {
            lock(gate)return enabled && FindEntry(native,out var entry) && observation.Source.TryGetTarget(out var source)&&ReferenceEquals(entry.Source,source)
                && entry.FeatureGeneration==observation.Feature && observation.Feature==featureGeneration;
        }
        private bool FindEntry(object native, out Entry entry)
        {
            entry = null!;
            return committed && !ending && sessions.TryGetValue(
                native, out entry!);
        }

        internal bool TryReadAudioPosition(object native, out TimeSpan position)
        {
            lock (gate)
            {
                position = default;
                if (!FindEntry(native, out var entry)) return false;
                // A pause already admitted while enabled must finish even when
                // the user disables PlaybackTiming during the native asynchronous stop.
                if (!entry.Session.IsPausing && !TryGetEnabledClock(native, out _)) return false;
                lock (entry.Audio.Gate)
                {
                    if (entry.Session.Clock.GetSnapshot().Active || entry.Session.WaitingForAudio)
                    {
                        if (!RefreshAudio(native, entry, false))
                        {
                            // IsPlaying precedes construction/start of native audio.
                            position = entry.Audio.HasSample
                                ? TimeSpan.FromTicks(entry.Session.Clock.GetSnapshot().LastPublishedMediaTicks)
                                : RawPosition(PreviewNativePlaybackContract.Cached.Value!, native);
                            return true;
                        }
                    }
                    // Ignore the native getter's earlier __result. It may have
                    // read raw before a concurrent audio-pause commit.
                    position = entry.Session.Clock.GetPosition(RawPosition(PreviewNativePlaybackContract.Cached.Value!, native), Stopwatch.GetTimestamp());
                    return true;
                }
            }
        }

        internal bool ObserveAudioPosition(object native, bool force = false)
        {
            lock (gate)
            {
                if (!FindEntry(native, out var entry)) return false;
                lock (entry.Audio.Gate)
                {
                    if (entry.Session.IsPausing && !entry.Session.Clock.GetSnapshot().Active && !entry.Session.WaitingForAudio) return true;
                    if ((!enabled && !entry.Session.IsPausing)
                        || (!entry.Session.Clock.GetSnapshot().Active && !entry.Session.WaitingForAudio)) return false;
                    return RefreshAudio(native, entry, force) || entry.Audio.HasSample;
                }
            }
        }

        private static AudioPlayer? BindAudio(object native, Entry entry)
        {
            var connection = PreviewNativePlaybackContract.Cached.Value!.AudioClock;
            var backend = connection.Backend.GetValue(native) as AudioPlayer;
            if (backend is not null && !ReferenceEquals(backend, entry.Audio.Backend))
            {
                entry.Audio.Backend = backend;
                entry.Audio.BackendWeak = new(backend);
                entry.Audio.NextSample = 0;
                entry.Audio.BackendInitialPosition = null;
            }
            entry.Audio.Start = (TimeSpan)connection.Start.GetValue(native)!;
            return backend;
        }

        private static bool RefreshAudio(object native, Entry entry, bool force)
        {
            var backend = BindAudio(native, entry);
            var audio = entry.Audio;
            // EndAudioTask detaches player before Stop. Only this admitted
            // epoch may keep sampling its existing, not-yet-stopped backend.
            var detached = backend is null && entry.Session.Phase == PreviewPlaybackSession.AudioPhase.PausingAudio;
            if (detached) backend = audio.Backend;
            if (backend is null) return false;
            if (ReferenceEquals(backend, audio.StoppingBackend)
                || entry.Session.WaitingForAudio && ReferenceEquals(backend,audio.RestartingBackend)) return true;
            if (!backend.IsPlaying) return false;
            var now = Stopwatch.GetTimestamp();
            if (!force && now < audio.NextSample) return true;
            var connection = PreviewNativePlaybackContract.Cached.Value!;
            var start = audio.Start;
            var position = backend.CurrentPosition + start;
            var sampled = Stopwatch.GetTimestamp();
            if (!(ReferenceEquals(connection.AudioClock.Backend.GetValue(native), backend)
                    || detached && connection.AudioClock.Backend.GetValue(native) is null && ReferenceEquals(audio.Backend, backend))
                || !Equals(connection.AudioClock.Start.GetValue(native), start)) return true;
            // CurrentPosition already contains the backend speed. The native
            // no-device fallback is wall time, regardless of requested speed.
            var rate = connection.AudioClock.WaveOut.GetValue(backend) is null ? 100.0 : backend.PlaybackRate * 100.0;
            audio.Position = position;
            audio.NextSample = sampled + (long)Math.Ceiling(Stopwatch.Frequency * 0.033);
            if(entry.Session.WaitingForAudio && audio.RestartingBackend is not null)
            {
                if(audio.BackendInitialPosition is null){audio.BackendInitialPosition=position;return true;}
                if(position<=audio.BackendInitialPosition.Value)return true;
            }
            if (entry.Session.ObserveAudioSample(position, rate, sampled, Maximum(connection, native)))
                InvalidateSource(connection, native);
            if (!entry.Session.WaitingForAudio) audio.RestartingBackend = null;
            audio.HasSample |= entry.Session.Clock.GetSnapshot().Active;
            return true;
        }

        internal TimeSpan CaptureAudioRatePosition(object native)
        {
            lock(gate)
            {
                var connection=PreviewNativePlaybackContract.Cached.Value!;
                if(!enabled || !FindEntry(native,out var entry) || !TryGetEnabledClock(native,out _))return RawPosition(connection,native);
                lock(entry.Audio.Gate)
                {
                    BindAudio(native,entry);
                    if(!entry.Session.IsPausing && !entry.Session.WaitingForAudio && entry.Session.Clock.GetSnapshot().Active)
                        RefreshAudio(native,entry,true);
                    if(entry.Session.BeginAudioBackendChange(Stopwatch.GetTimestamp(),out var position))
                    {
                        entry.Audio.RestartingBackend=entry.Audio.Backend;
                        entry.Audio.BackendInitialPosition=null;
                        InvalidateSource(connection,native);
                        return position;
                    }
                    return entry.Session.Clock.GetPosition(RawPosition(connection,native),Stopwatch.GetTimestamp());
                }
            }
        }

        internal long BeginAudioEnd(object native)
        {
            lock (gate)
            {
                if (!enabled || !FindEntry(native, out var entry) || entry.Session.IsPausing
                    || (!entry.Session.Clock.GetSnapshot().Active && !entry.Session.WaitingForAudio)) return 0;
                lock (entry.Audio.Gate)
                {
                    // During replacement the backend can be assigned before
                    // Play. EndAudioTask will await the native position task;
                    // adopt its held restart target now and sample at Stop.
                    if (entry.Session.WaitingForAudio) BindAudio(native, entry);
                    else if (!RefreshAudio(native, entry, true) || entry.Audio.Backend is null) return 0;
                    if (!entry.Session.RequestAudioPause()) return 0;
                    entry.Audio.FinalCaptured = false;
                    InvalidateSource(PreviewNativePlaybackContract.Cached.Value!, native);
                    return entry.Audio.Sequence = checked(++audioPauseSequence);
                }
            }
        }

        internal void CommitAudioPause(object native)
        {
            lock (gate)
            {
                if (!FindEntry(native, out var entry) || !entry.Session.IsPausing) return;
                lock (entry.Audio.Gate)
                {
                    if (!entry.Audio.FinalCaptured)
                    {
                        // A pause can enter while StartAudioTask still owns its
                        // semaphore, before any backend/sample was observable.
                        // PlaybackTiming has never published an interpolated position in
                        // this epoch; native raw remains the sole authority.
                        if (!entry.Audio.HasSample && entry.Audio.RestartingBackend is null && entry.Session.WaitingForAudio
                            && !entry.Session.Clock.GetSnapshot().Active)
                        {
                            entry.Session.Stop(RawPosition(PreviewNativePlaybackContract.Cached.Value!, native), Stopwatch.GetTimestamp());
                            return;
                        }
                        entry.Session.Clock.Disable("audio-stop-position-missing");
                        return;
                    }
                    var connection = PreviewNativePlaybackContract.Cached.Value!;
                    entry.Session.CompleteAudioPause(entry.Audio.Position, Stopwatch.GetTimestamp(),
                        position => connection.PositionSetter.Invoke(native, [position]));
                }
            }
        }

        internal void AudioEndCompleted(object native, long sequence, Task task)
        {
            lock (gate)
            {
                if (!FindEntry(native, out var entry) || entry.Audio.Sequence != sequence || !entry.Session.IsPausing
                    || entry.Session.Phase == PreviewPlaybackSession.AudioPhase.StoppedAudio) return;
                lock (entry.Audio.Gate)
                {
                    entry.Session.Clock.Disable(task.IsCompletedSuccessfully ? "audio-stop-not-committed" : "audio-stop-failed");
                    InvalidateSource(PreviewNativePlaybackContract.Cached.Value!, native);
                    Trace.TraceWarning("PlaybackTiming audio pause did not complete: {0}", task.Exception?.GetBaseException().Message ?? task.Status.ToString());
                }
            }
        }

        internal void ResetAudio(object native, bool preserveStoppedBackend = false)
        {
            lock (gate)
            {
                if (!FindEntry(native, out var entry)) return;
                lock (entry.Audio.Gate)
                {
                    entry.Audio.Backend = entry.Audio.RestartingBackend = null;
                    if(!preserveStoppedBackend)entry.Audio.StoppingBackend=null;
                    entry.Audio.NextSample = 0;
                    entry.Audio.FinalCaptured = entry.Audio.HasSample = false;
                    entry.Audio.BackendInitialPosition=null;
                }
            }
        }

        private Entry? FindBackend(object backend)
        {
            foreach (var entry in SessionEntries)
            {
                if (entry.Native is null || !entry.Native.TryGetTarget(out var native)) continue;
                if (ReferenceEquals(entry.Audio.Backend, backend)
                    || ReferenceEquals(PreviewNativePlaybackContract.Cached.Value!.AudioClock.Backend.GetValue(native), backend)) return entry;
            }
            return null;
        }

        internal AudioReadLease? BorrowAudioRead(object backend, Callback callback)
        {
            lock (gate)
            {
                if (!committed || ending || FindBackend(backend) is not { } entry) return null;
                Monitor.Enter(entry.Audio.Gate);
                if(entry.Native!.TryGetTarget(out var native)
                    && ReferenceEquals(PreviewNativePlaybackContract.Cached.Value!.AudioClock.Backend.GetValue(native),backend)) BindAudio(native,entry);
                return new(entry.Audio.Gate, callback);
            }
        }

        internal void AudioStopping(object backend)
        {
            lock (gate)
            {
                if (!committed || ending || FindBackend(backend) is not { } entry) return;
                lock (entry.Audio.Gate)
                {
                    var audio = entry.Audio;
                    if (ReferenceEquals(audio.StoppingBackend, backend)) return;
                    try
                    {
                        // Rate changes stop an old backend while the native
                        // player field is still populated. EndAudioTask clears
                        // that field before its final Stop/Dispose sequence.
                        if (entry.Session.Phase == PreviewPlaybackSession.AudioPhase.PausingAudio && !audio.FinalCaptured
                            && ReferenceEquals(audio.Backend, backend) && entry.Native!.TryGetTarget(out var native)
                            && PreviewNativePlaybackContract.Cached.Value!.AudioClock.Backend.GetValue(native) is null)
                        {
                            audio.Position = ((AudioPlayer)backend).CurrentPosition + audio.Start;
                            audio.FinalCaptured = entry.Session.FreezeAudioPause(audio.Position, Stopwatch.GetTimestamp());
                            InvalidateSource(PreviewNativePlaybackContract.Cached.Value!, native);
                        }
                    }
                    catch (Exception error)
                    {
                        entry.Session.Clock.Disable("audio-stop-position:" + error.GetType().Name);
                        Trace.TraceWarning("PlaybackTiming audio stop sample rejected: {0}", error);
                    }
                    finally { audio.StoppingBackend = (AudioPlayer)backend; }
                    // Release before the native Stop's thread Join or Dispose.
                }
            }
        }
    }
}
