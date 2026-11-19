using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using YMM4PlaybackTimingPlugin.Integration;
using YMM4PlaybackTimingPlugin.Playback;
using YukkuriMovieMaker.Player.Audio;

internal static class AudioPauseTests
{
    internal static void DrainFollowsAudio()
    {
        var guard = new Harmony("YMM4PlaybackTimingPlugin.Tests.DrainingAudio");
        guard.Patch(PreviewNativePlaybackContract.Cached.Value!.AudioClock.PositionGetter,
            prefix: new HarmonyMethod(typeof(AudioPauseTests), nameof(ReadControlledAudio)));
        try
        {
            foreach (var delay in new[] { 0, 5, 33, 80 })
            foreach (var phase in new[] { 0.1, 0.49, 0.51, 0.9 })
            using (var fixture = new Fixture(true))
            {
                controlledBackend = fixture.Backend;
                controlledPosition = TimeSpan.FromSeconds(1.5 + phase / 60);
                fixture.Owner.ObserveAudioPosition(fixture.Player, force: true);
                var sequence = fixture.Owner.BeginAudioEnd(fixture.Player);
                Check(sequence > 0 && fixture.Clock.GetSnapshot().Active, "pause request froze live audio");
                if (delay == 33)
                {
                    fixture.Owner.SetEnabled(false);
                    Check(fixture.Owner.TryReadAudioPosition(fixture.Player, out _), "PlaybackTiming OFF abandoned admitted drain");
                    fixture.Owner.SetEnabled(true);
                }
                controlledPosition += TimeSpan.FromMilliseconds(delay);
                // Native EndAudioTask clears player before invoking backend.Stop.
                fixture.Contract.AudioClock.Backend.SetValue(fixture.Player, null);
                Check(fixture.Owner.ObserveAudioPosition(fixture.Player, force: true), "draining backend was lost after native field clear");
                var final = controlledPosition + TimeSpan.FromSeconds(8);
                Check(fixture.Clock.GetSnapshot().AnchorMediaTicks == final.Ticks, "draining clock did not follow audio");
                fixture.Owner.AudioStopping(fixture.Backend);
                Check(!fixture.Clock.GetSnapshot().Active, "first Stop did not freeze clock");
                forbiddenBackend = fixture.Backend;
                controlledPosition = TimeSpan.Zero;
                fixture.Owner.AudioStopping(fixture.Backend);
                fixture.Owner.TryReadAudioPosition(fixture.Player, out var frozen);
                Check(frozen == final, "final sample was extrapolated or reread");
                fixture.Owner.CommitAudioPause(fixture.Player);
                fixture.Owner.CommitAudioPause(fixture.Player);
                Check((TimeSpan)fixture.Contract.Position.GetValue(fixture.Player)! == final, "final native handoff changed audio position");
                fixture.Owner.AudioEndCompleted(fixture.Player, sequence, Task.CompletedTask);
                Check(!fixture.Clock.GetSnapshot().Disabled, "successful pause was treated as incomplete");
                forbiddenBackend = null;
            }
        }
        finally { guard.UnpatchAll(guard.Id); controlledBackend = forbiddenBackend = null; }
    }

    internal static void StartWaitsForAudio()
    {
        using var session = new PreviewPlaybackSession(new PreviewPlaybackClockState(1_000_000));
        Check(session.TryStart(TimeSpan.FromSeconds(5), 200, 0, null), "start rejected");
        Check(!session.Clock.GetSnapshot().Active, "IsPlaying admitted interpolation before audio ready");
        Check(session.Clock.GetPosition(TimeSpan.Zero, 500_000) == TimeSpan.FromSeconds(5), "unstarted audio extrapolated");
        Check(!session.Clock.TryGetDeadline(TimeSpan.FromSeconds(6), 500_000, out _, out _), "unstarted audio admitted candidate");
        using var fixture = new Fixture(true);
        var nativeSession = fixture.Owner.GetOrCreateSession(fixture.Player)!;
        SetBackendPlaying(fixture.Backend, false);
        fixture.Owner.TryReadAudioPosition(fixture.Player, out var held);
        Check(held == TimeSpan.FromSeconds(9.5) && !fixture.Clock.GetSnapshot().Active, "unplayed native backend activated clock");
        Check(nativeSession.WaitingForAudio, "unplayed backend lost startup state");
        SetBackendPlaying(fixture.Backend, true);
        fixture.Owner.TryReadAudioPosition(fixture.Player, out var ready);
        Check(ready >= held && fixture.Clock.GetSnapshot().Active && !nativeSession.WaitingForAudio, "valid native backend sample did not activate clock");
    }

    internal static void WaitDoesNotPublishMediaTime()
    {
        var clock = new PreviewPlaybackClockState(1_000_000);
        clock.Start(TimeSpan.FromSeconds(10), 100, 0);
        var runtime = new VirtualDeadlineRuntime(frequency: 1_000_000);
        var before = clock.GetSnapshot().LastPublishedMediaTicks;
        Check(PreviewFrameDeadlineWait.WaitUntil(clock, TimeSpan.FromSeconds(10.016), runtime).Completed, "fixture wait failed");
        Check(clock.GetSnapshot().LastPublishedMediaTicks == before, "candidate completion published unpresented media time");
        clock.Reanchor(TimeSpan.FromSeconds(10), 100, runtime.GetTimestamp());
        Check(clock.GetPosition(TimeSpan.Zero, runtime.GetTimestamp()) == TimeSpan.FromSeconds(10), "candidate time overrode audio sample");
    }

    internal static void StartingPauseAndEpochs()
    {
        using var fixture = new Fixture(true);
        var session = fixture.Owner.GetOrCreateSession(fixture.Player)!;
        fixture.Owner.ResetAudio(fixture.Player);
        fixture.Contract.AudioClock.Backend.SetValue(fixture.Player, null);
        session.TryStart(TimeSpan.FromSeconds(9.5), 100, System.Diagnostics.Stopwatch.GetTimestamp(), null);
        var unstarted = fixture.Owner.BeginAudioEnd(fixture.Player);
        Check(unstarted > 0 && !fixture.Clock.GetSnapshot().Active, "starting pause not admitted");
        fixture.Owner.CommitAudioPause(fixture.Player);
        fixture.Owner.AudioEndCompleted(fixture.Player, unstarted, Task.CompletedTask);
        Check(!fixture.Clock.GetSnapshot().Disabled && (TimeSpan)fixture.Contract.Position.GetValue(fixture.Player)! == TimeSpan.FromSeconds(9.5),
            "unobserved startup pause changed native position or disabled PlaybackTiming");

        fixture.Owner.ResetAudio(fixture.Player);
        fixture.Contract.AudioClock.Backend.SetValue(fixture.Player, fixture.Backend);
        session.TryStart(TimeSpan.FromSeconds(9.5), 100, System.Diagnostics.Stopwatch.GetTimestamp(), null);
        Check(fixture.Owner.TryReadAudioPosition(fixture.Player, out _) && fixture.Clock.GetSnapshot().Active, "new epoch did not acquire audio");
        var current = fixture.Owner.BeginAudioEnd(fixture.Player);
        Check(current > unstarted && fixture.Owner.BeginAudioEnd(fixture.Player) == 0, "duplicate pause replaced active sequence");
        fixture.Owner.AudioEndCompleted(fixture.Player, unstarted, Task.FromCanceled(new CancellationToken(true)));
        Check(!fixture.Clock.GetSnapshot().Disabled && session.IsPausing, "old completion changed new epoch");
        fixture.Owner.AudioEndCompleted(fixture.Player, current, Task.FromCanceled(new CancellationToken(true)));
        Check(fixture.Clock.GetSnapshot() is { Disabled: true, Active: false, Reason: "audio-stop-failed" }, "current cancellation left a live clock");
    }

    private static AudioPlayer? controlledBackend;
    private static TimeSpan controlledPosition;
    private static bool ReadControlledAudio(AudioPlayer __instance, ref TimeSpan __result)
    {
        if (!ReferenceEquals(__instance, controlledBackend)) return true;
        Check(!ReferenceEquals(__instance, forbiddenBackend), "stopped backend was reread");
        __result = controlledPosition;
        return false;
    }

    internal static void Clock()
    {
        var clock = new PreviewPlaybackClockState(1_000_000);
        clock.Start(TimeSpan.FromSeconds(10), 100, 0);
        var generation = clock.GetSnapshot().Generation;
        Check(clock.GetPosition(TimeSpan.FromSeconds(9), 50_000) == TimeSpan.FromSeconds(10.05), "fixture projection");
        Check(clock.BeginAudioPause(TimeSpan.FromSeconds(10.049), 50_000), "pause not adopted");
        Check(clock.GetPosition(TimeSpan.Zero, 5_000_000) == TimeSpan.FromSeconds(10.049), "pause extrapolated or used stale raw");
        Check(!clock.ValidateWaitCompletion(generation), "old wait completed after pause");
        Check(!clock.TryGetDeadline(TimeSpan.FromSeconds(11), 51_000, out _, out _), "pause admitted wait");
        var raw = TimeSpan.Zero;
        Check(clock.CompleteAudioPause(TimeSpan.FromSeconds(10.073), 52_000, value => raw = value), "stop not committed");
        Check(raw == TimeSpan.FromSeconds(10.073) && clock.GetPosition(raw, 60_000) == raw, "final audio not authoritative");
        Check(!clock.CompleteAudioPause(TimeSpan.Zero, 60_000, _ => throw new Exception("double commit")), "double stop admitted");
        raw = TimeSpan.FromSeconds(2);
        Check(clock.GetPosition(raw, 70_000) == raw, "explicit backward seek clamped");
        clock.Start(raw, 200, 70_000);
        Check(clock.GetPosition(raw, 80_000) == TimeSpan.FromSeconds(2.02), "resume/rate broken");
        Check(clock.BeginAudioPause(TimeSpan.FromSeconds(2.019), 80_000), "second pause");
        clock.CompleteAudioPause(TimeSpan.FromSeconds(2.019), 80_000, value => raw = value);
        Check(raw == TimeSpan.FromSeconds(2.019), "audio final replaced by video time");
        var native = PreviewNativePlaybackContract.Cached.Value!;
        var rejected = PreviewNativePlaybackContract.Inspect(method =>
        {
            var codes = PatchProcessor.GetOriginalInstructions(method).Select(x=>new CodeInstruction(x)).ToArray();
            if (method == native.AudioClock.EndStateMachine)
            {
                var clear = Array.FindIndex(codes, x => x.opcode == System.Reflection.Emit.OpCodes.Stfld && Equals(x.operand, native.AudioClock.Backend));
                codes[clear-1].opcode = System.Reflection.Emit.OpCodes.Ldarg_0;
            }
            return codes;
        });
        Check(!rejected.Available, "changed native audio stop lifecycle accepted");
    }

    internal static void NativeRateContract()
    {
        var inspection = PreviewNativePlaybackContract.Cached;
        Check(inspection.Available, inspection.Failure ?? "native playback unavailable");
        Check(PreviewNativePacingContract.Cached.Available, PreviewNativePacingContract.Cached.Failure ?? "native pacing unavailable");
        var native = inspection.Value!;
        var loop = native.AudioClock.PositionLoop;
        var original = PatchProcessor.GetOriginalInstructions(loop).ToArray();
        var capture = native.AudioClock.RateCaptureIndex;
        var seek = original.Select((c, i) => (c, i)).Single(x => x.c.operand is MethodInfo m && m.Name == "Seek").i;
        var start = original.Select((c, i) => (c, i)).Single(x => x.c.operand is MethodInfo m && m.Name == "set_StartPosition").i;
        void Reject(string name, Action<List<CodeInstruction>> mutate)
        {
            var changed = original.Select(c => new CodeInstruction(c)).ToList();
            mutate(changed);
            var result = PreviewNativePlaybackContract.Inspect(m => m == loop ? changed : PatchProcessor.GetOriginalInstructions(m).ToArray());
            Check(!result.Available && result.Failure is not null, name + " admitted");
        }
        Reject("restart Seek uses another local", c => c[seek - 1] = new(System.Reflection.Emit.OpCodes.Ldloc_0));
        Reject("restart StartPosition uses another local", c => c[start - 1] = new(System.Reflection.Emit.OpCodes.Ldloc_0));
        Reject("restart overwrites captured position", c => c.Insert(seek - 1, new CodeInstruction(original[capture + 1])));
        Reject("restart exposes captured position by reference", c => c.Insert(seek - 1, new(System.Reflection.Emit.OpCodes.Ldloca_S, original[capture + 1].operand)));
        var stop = Array.FindIndex(original, c => c.Calls(native.AudioClock.Stop));
        Reject("duplicate audio Stop", c => c.Insert(stop, new CodeInstruction(original[stop])));
        if (stop - capture == 10)
        {
            var legacy = original.Select(c => new CodeInstruction(c)).ToList();
            legacy.RemoveRange(capture + 2, 5);
            var legacyResult = PreviewNativePlaybackContract.Inspect(m => m == loop ? legacy : PatchProcessor.GetOriginalInstructions(m).ToArray());
            Check(legacyResult.Available, "previous capture without Volume preservation rejected: " + legacyResult.Failure);
            // 4.57 saves Volume before replacing the audio backend. Only this
            // same-owner, read-only insertion may extend the old capture gap.
            Reject("another getter in Volume capture", c => c[capture + 5].operand = native.AudioClock.BackendRateGetter);
            Reject("another backend in Volume capture", c => c[capture + 4] = new(System.Reflection.Emit.OpCodes.Ldnull));
            Reject("another owner in Volume capture", c => c[capture + 3] = new(System.Reflection.Emit.OpCodes.Nop));
            Reject("Volume saved into the position local", c => c[capture + 6] = new CodeInstruction(original[capture + 1]));
            var label = new System.Reflection.Emit.DynamicMethod("volumeEntry", typeof(void), Type.EmptyTypes).GetILGenerator().DefineLabel();
            Reject("entry inside Volume capture", c => c[capture + 5].labels.Add(label));
            Reject("branch inside Volume capture", c => c[capture + 2] = new(System.Reflection.Emit.OpCodes.Br_S, label));
        }
    }

    internal static void NativeHandoff()
    {
        using var fixture = new Fixture(true);
        var contract = fixture.Contract;
        contract.Position.SetValue(fixture.Player, TimeSpan.Zero);
        var staleNativeRead = TimeSpan.Zero;
        Check(fixture.Owner.TryReadAudioPosition(fixture.Player, out var sampled) && sampled.TotalSeconds > 9.4, "audio did not replace stale UI position");
        fixture.Owner.BeginAudioEnd(fixture.Player);
        Check(fixture.Clock.GetSnapshot().Active, "stop request froze the draining audio clock");
        contract.AudioClock.Backend.SetValue(fixture.Player, null);
        fixture.Owner.AudioStopping(fixture.Backend);
        SetFallbackStart(fixture.Backend, DateTime.Now);
        fixture.Owner.AudioStopping(fixture.Backend);
        fixture.Owner.CommitAudioPause(fixture.Player);
        var committed = (TimeSpan)contract.Position.GetValue(fixture.Player)!;
        PlaybackTimingClockHooks.PositionGetterPostfix(fixture.Player, ref staleNativeRead);
        Check(staleNativeRead == committed, "getter that read raw before commit returned stale __result");
        Check(committed.TotalSeconds > 9.4, "reset backend position replaced first final sample");
        Check(fixture.Owner.TryReadAudioPosition(fixture.Player, out var result) && result == committed, "getter returned old native read after handoff");
        contract.Position.SetValue(fixture.Player, TimeSpan.FromSeconds(1));
        Check(fixture.Owner.TryReadAudioPosition(fixture.Player, out result) && result == TimeSpan.FromSeconds(1), "paused explicit seek ignored");
    }

    internal static void SerializationAndOff()
    {
        using (var fixture = new Fixture(false))
        {
            var before = (TimeSpan)fixture.Contract.Position.GetValue(fixture.Player)!;
            fixture.Owner.BeginAudioEnd(fixture.Player);
            fixture.Owner.AudioStopping(fixture.Backend);
            fixture.Owner.CommitAudioPause(fixture.Player);
            Check(!fixture.Owner.TryReadAudioPosition(fixture.Player, out _)
                && before == (TimeSpan)fixture.Contract.Position.GetValue(fixture.Player)!, "PlaybackTiming OFF corrected native position");
        }
        using (var fixture = new Fixture(true))
        {
            fixture.Owner.BeginAudioEnd(fixture.Player);
            fixture.Contract.AudioClock.Backend.SetValue(fixture.Player, null);
            using var readEntered = new ManualResetEventSlim();
            using var releaseRead = new ManualResetEventSlim();
            var reader = Task.Run(() =>
            {
                using var callback = fixture.Owner.TryEnter()!;
                using var borrow = fixture.Owner.BorrowAudioRead(fixture.Backend, callback)!;
                readEntered.Set();
                Check(releaseRead.Wait(TimeSpan.FromSeconds(5)), "reader release timeout");
            });
            Check(readEntered.Wait(TimeSpan.FromSeconds(5)), "read not entered");
            var stopper = Task.Run(() => fixture.Owner.AudioStopping(fixture.Backend));
            try { Check(!stopper.Wait(30), "stop raced with in-progress audio query"); }
            finally { releaseRead.Set(); }
            Check(Task.WaitAll([reader, stopper], TimeSpan.FromSeconds(5)), "audio locks did not drain");
            fixture.Owner.SetEnabled(false);
            fixture.Owner.CommitAudioPause(fixture.Player);
            Check(((TimeSpan)fixture.Contract.Position.GetValue(fixture.Player)!).TotalSeconds > 9.4, "feature change abandoned final audio position");
            var staleNativeRead = TimeSpan.Zero;
            PlaybackTimingClockHooks.PositionGetterPostfix(fixture.Player, ref staleNativeRead);
            Check(staleNativeRead.TotalSeconds > 9.4, "feature change exposed old raw during admitted pause");
        }
        using (var fixture = new Fixture(true))
        {
            var sequence = fixture.Owner.BeginAudioEnd(fixture.Player);
            fixture.Owner.AudioEndCompleted(fixture.Player, sequence, Task.FromException(new InvalidOperationException("stop fixture")));
            Check(fixture.Clock.GetSnapshot() is { Active: false, Disabled: true, Reason: "audio-stop-failed" }, "faulted audio stop left an active pause");
        }
        using (var fixture = new Fixture(true))
        {
            var sequence = fixture.Owner.BeginAudioEnd(fixture.Player);
            fixture.Owner.AudioEndCompleted(fixture.Player, sequence, Task.CompletedTask);
            Check(fixture.Clock.GetSnapshot() is { Active: false, Disabled: true, Reason: "audio-stop-not-committed" }, "missing native handoff left an active clock");
        }
        using (var fixture = new Fixture(true))
        {
            var oldSequence = fixture.Owner.BeginAudioEnd(fixture.Player);
            var oldSource = fixture.Contract.TimelineSource.GetValue(fixture.Player)!;
            fixture.Owner.EndSource(oldSource);
            fixture.Contract.TimelineSource.SetValue(fixture.Player, RuntimeHelpers.GetUninitializedObject(fixture.Contract.Source));
            var next = fixture.Owner.GetOrCreateSession(fixture.Player)!;
            next.TryStart(TimeSpan.FromSeconds(9.5), 100, System.Diagnostics.Stopwatch.GetTimestamp(), null);
            var nextSequence = fixture.Owner.BeginAudioEnd(fixture.Player);
            Check(oldSequence > 0 && nextSequence > oldSequence
                && next.Phase == PreviewPlaybackSession.AudioPhase.PausingAudio, "source-generation pause fixture not adopted");
            fixture.Owner.AudioEndCompleted(fixture.Player, oldSequence, Task.FromException(new Exception("old source stop")));
            Check(nextSequence != oldSequence && !next.Clock.GetSnapshot().Disabled, "old source completion disabled new pause");
        }
    }

    internal static void BackendReplacement()
    {
        using var fixture = new Fixture(true);
        Check(fixture.Owner.TryReadAudioPosition(fixture.Player, out _), "initial audio sample missing");
        var held = TimeSpan.FromTicks(fixture.Clock.GetSnapshot().LastPublishedMediaTicks);
        var replacement = (AudioPlayer)RuntimeHelpers.GetUninitializedObject(typeof(AudioPlayer));
        SetFallbackStart(replacement, DateTime.Now - TimeSpan.FromSeconds(1.5));
        fixture.Contract.AudioClock.Backend.SetValue(fixture.Player, replacement);
        fixture.Contract.Position.SetValue(fixture.Player, TimeSpan.Zero);
        Check(fixture.Owner.TryReadAudioPosition(fixture.Player, out var position) && position == held,
            "native backend transition used stale UI raw");
        var playingField = PatchProcessor.GetOriginalInstructions(typeof(AudioPlayer).GetProperty("IsPlaying")!.GetMethod!)
            .Select(x=>x.operand).OfType<FieldInfo>().Single(x=>x.FieldType==typeof(bool));
        SetFallbackStart(replacement, DateTime.Now - TimeSpan.FromSeconds(1.8));
        playingField.SetValue(replacement, true);
        Check(fixture.Owner.TryReadAudioPosition(fixture.Player, out position) && position > held + TimeSpan.FromSeconds(0.2),
            "replacement backend did not resume audio sampling");
    }

    internal static void BackendExchange()
    {
        var clock=new PreviewPlaybackClockState(1_000_000);
        clock.Start(TimeSpan.FromSeconds(10),100,0);
        var oldGeneration=clock.GetSnapshot().Generation;
        Check(clock.BeginAudioBackendChange(20_000,out var held) && held==TimeSpan.FromSeconds(10.02),"backend freeze time");
        Check(clock.GetPosition(TimeSpan.Zero,500_000)==held && !clock.ValidateWaitCompletion(oldGeneration),"clock advanced during backend replacement");
        Check(!clock.ResumeAudioBackend(held-TimeSpan.FromMilliseconds(120),200,500_000,null),"backward backend sample adopted");
        Check(clock.ResumeAudioBackend(held+TimeSpan.FromMilliseconds(1),200,500_000,null),"backend did not resume");
        Check(clock.GetPosition(TimeSpan.Zero,510_000)==held+TimeSpan.FromMilliseconds(21),"backend resumed with wrong rate");

        using var fixture=new Fixture(true);
        fixture.Owner.TryReadAudioPosition(fixture.Player,out _);
        held=fixture.Owner.CaptureAudioRatePosition(fixture.Player);
        Check(fixture.Clock.GetSnapshot().Reason=="waiting-audio-backend","exchange not held");
        Check(fixture.Owner.TryReadAudioPosition(fixture.Player,out var value) && value==held,"old playing backend reactivated held clock");
        fixture.Owner.AudioStopping(fixture.Backend);
        var next=NewBackend(true,TimeSpan.Zero);
        fixture.Contract.AudioClock.Backend.SetValue(fixture.Player,next);
        fixture.Contract.AudioClock.Start.SetValue(fixture.Player,held); // same native local as source.Seek
        fixture.Owner.TryReadAudioPosition(fixture.Player,out value);
        Check(!fixture.Clock.GetSnapshot().Active && value==held,"new backend resumed before clock progress");
        SetFallbackStart(next,DateTime.Now-TimeSpan.FromMilliseconds(120));
        fixture.Owner.ObserveAudioPosition(fixture.Player,force:true);
        Check(fixture.Clock.GetSnapshot().Active && fixture.Owner.TryReadAudioPosition(fixture.Player,out value)
            && value>held+TimeSpan.FromMilliseconds(100),"progressing new backend did not resume");
        // A diagnostic/own-sampler getter sequence is not the native rate branch.
        PlaybackTimingClockHooks.AudioBackendRatePostfix(next,1);
        PlaybackTimingClockHooks.AudioRequestedRatePostfix(fixture.Player,200);
        Check(fixture.Clock.GetSnapshot().Active,"non-native rate getter froze playback");
    }

    internal static void PauseDuringExchange()
    {
        foreach(var playing in new[]{false,true})
        using(var fixture=new Fixture(true))
        {
            fixture.Owner.TryReadAudioPosition(fixture.Player,out _);
            var held=fixture.Owner.CaptureAudioRatePosition(fixture.Player);
            fixture.Owner.AudioStopping(fixture.Backend);
            var next=NewBackend(playing,TimeSpan.Zero);
            fixture.Contract.AudioClock.Backend.SetValue(fixture.Player,next);
            fixture.Contract.AudioClock.Start.SetValue(fixture.Player,held);
            Check(fixture.Owner.BeginAudioEnd(fixture.Player)>0,"pause during exchange not admitted");
            Check(fixture.Clock.GetPosition(TimeSpan.Zero,System.Diagnostics.Stopwatch.GetTimestamp())==held,"exchange pause lost held position");
            SetBackendPlaying(next,true);
            SetFallbackStart(next,DateTime.Now);
            fixture.Contract.AudioClock.Backend.SetValue(fixture.Player,null);
            fixture.Owner.AudioStopping(next);
            fixture.Owner.CommitAudioPause(fixture.Player);
            Check(((TimeSpan)fixture.Contract.Position.GetValue(fixture.Player)!)>=held,"exchange pause returned below native restart target");
        }
        using(var fixture=new Fixture(true))
        {
            fixture.Owner.TryReadAudioPosition(fixture.Player,out _);
            fixture.Owner.AudioStopping(fixture.Backend);
            fixture.Owner.SetEnabled(false);
            fixture.Owner.SetEnabled(true);
            var guard=new Harmony("YMM4PlaybackTimingPlugin.Tests.StoppedAudio");
            forbiddenBackend=fixture.Backend;
            forbiddenReads=0;
            guard.Patch(fixture.Contract.AudioClock.PositionGetter,prefix:new HarmonyMethod(typeof(AudioPauseTests),nameof(RejectStoppedRead)));
            try
            {
                fixture.Owner.TryReadAudioPosition(fixture.Player,out _);
                Check(forbiddenReads==0,"feature generation cleared stopped-backend protection");
            }
            finally{guard.UnpatchAll(guard.Id);forbiddenBackend=null;}
        }
    }

    private static AudioPlayer? forbiddenBackend;
    private static int forbiddenReads;
    private static void RejectStoppedRead(object __instance)
    {
        if(!ReferenceEquals(__instance,forbiddenBackend))return;
        forbiddenReads++;
        throw new InvalidOperationException("stopped backend query");
    }

    private static AudioPlayer NewBackend(bool playing,TimeSpan elapsed)
    {
        var backend=(AudioPlayer)RuntimeHelpers.GetUninitializedObject(typeof(AudioPlayer));
        SetFallbackStart(backend,DateTime.Now-elapsed);
        SetBackendPlaying(backend,playing);
        return backend;
    }

    private static void SetBackendPlaying(AudioPlayer backend,bool playing)
    {
        var playingField=PatchProcessor.GetOriginalInstructions(typeof(AudioPlayer).GetProperty("IsPlaying")!.GetMethod!)
            .Select(c=>c.operand).OfType<FieldInfo>().Single(f=>f.FieldType==typeof(bool));
        playingField.SetValue(backend,playing);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly PreviewNativePlaybackContract.Contract Contract = PreviewNativePlaybackContract.Cached.Value!;
        internal readonly object Player;
        internal readonly AudioPlayer Backend;
        internal readonly PlaybackTimingClockHooks.Activation Owner;
        internal readonly PreviewPlaybackClockState Clock;
        internal Fixture(bool enabled)
        {
            Player = RuntimeHelpers.GetUninitializedObject(Contract.Video);
            Contract.TimelineSource.SetValue(Player, RuntimeHelpers.GetUninitializedObject(Contract.Source));
            Contract.Position.SetValue(Player, TimeSpan.FromSeconds(9.5));
            Contract.Playing.SetValue(Player, true);
            Backend = (AudioPlayer)RuntimeHelpers.GetUninitializedObject(typeof(AudioPlayer));
            var playingField = PatchProcessor.GetOriginalInstructions(typeof(AudioPlayer).GetProperty("IsPlaying")!.GetMethod!)
                .Select(x => x.operand).OfType<FieldInfo>().Single(x => x.FieldType == typeof(bool));
            playingField.SetValue(Backend, true);
            SetFallbackStart(Backend, DateTime.Now - TimeSpan.FromSeconds(1.5));
            Contract.AudioClock.Backend.SetValue(Player, Backend);
            Contract.AudioClock.Start.SetValue(Player, TimeSpan.FromSeconds(8));
            Owner = PlaybackTimingClockHooks.Prepare(enabled:enabled);
            Owner.Commit();
            var session = Owner.GetOrCreateSession(Player)!;
            Clock = session.Clock;
            session.TryStart(TimeSpan.FromSeconds(9.5), 100, System.Diagnostics.Stopwatch.GetTimestamp(), null);
        }
        public void Dispose()
        {
            Owner.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }

    private static void SetFallbackStart(AudioPlayer player, DateTime value)
    {
        var field = PatchProcessor.GetOriginalInstructions(typeof(AudioPlayer).GetProperty("CurrentPosition")!.GetMethod!)
            .Select(x => x.operand).OfType<FieldInfo>().Single(x => x.FieldType == typeof(DateTime));
        field.SetValue(player, value);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
