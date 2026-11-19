using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using YukkuriMovieMaker.Player.Audio;
using YukkuriMovieMaker.Settings;

namespace YMM4PlaybackTimingPlugin.Integration;

// Immutable native connection information. No clock, registration or capability policy lives here.
internal static class PreviewNativePlaybackContract
{
    internal sealed record Contract(Type Audio, Type Video, Type Source,
        MethodInfo PositionGetter, MethodInfo PositionSetter, MethodInfo RateSetter, MethodInfo PlayingSetter,
        FieldInfo Position, FieldInfo Rate, FieldInfo Playing, FieldInfo Timeline, FieldInfo TimelineSource,
        MethodInfo VideoInfoGetter, MethodInfo LengthGetter, MethodInfo GetTimeFrom, MethodInfo GetFrameFrom,
        MethodInfo Update, MethodInfo PlayerDispose, MethodInfo SourceDispose, AudioConnection AudioClock);
    internal sealed record AudioConnection(FieldInfo Backend, FieldInfo Start, FieldInfo WaveOut,
        MethodInfo End, MethodInfo EndStateMachine, MethodInfo PositionGetter, MethodInfo Stop, MethodInfo Dispose,
        MethodInfo PositionLoop, int RateCaptureIndex, MethodInfo RequestedRateGetter, MethodInfo BackendRateGetter,
        MethodInfo RequestedModeGetter, MethodInfo BackendModeGetter);
    internal sealed record Inspection(Contract? Value, string? Failure)
    { internal bool Available => Value is not null && Failure is null; }
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static readonly Lazy<Inspection> Native = new(() => Inspect(m => PatchProcessor.GetOriginalInstructions(m).ToArray()));
    internal static Inspection Cached => Native.Value;

    internal static Inspection Inspect(Func<MethodInfo, IReadOnlyList<CodeInstruction>> read)
    {
        try
        {
            var video = typeof(YukkuriMovieMaker.Project.Project).Assembly.GetType("YukkuriMovieMaker.Player.TimelineVideoPlayer") ?? throw new NotSupportedException("Video player unavailable.");
            var audio = video.Assembly.GetType("YukkuriMovieMaker.Player.TimelineAudioPlayer") ?? throw new NotSupportedException("Audio player unavailable.");
            var source = video.Assembly.GetType("YukkuriMovieMaker.Player.Video.TimelineSource") ?? throw new NotSupportedException("Timeline source unavailable.");
            Require(audio.IsAssignableFrom(video) && typeof(IDisposable).IsAssignableFrom(video) && typeof(IDisposable).IsAssignableFrom(source), "Native player/source lifetime changed.");
            var position = Property(audio, "Position", typeof(TimeSpan));
            var rate = Property(audio, "PlaybackRate", typeof(double));
            var playing = Property(audio, "IsPlaying", typeof(bool));
            Require(position.GetMethod!.IsPublic && position.SetMethod!.IsPrivate
                && rate.GetMethod!.IsPublic && rate.SetMethod!.IsPublic && playing.GetMethod!.IsPublic && playing.SetMethod!.IsPublic,
                "Native playback accessor visibility changed.");
            var positionField = GetterField(position.GetMethod!, read(position.GetMethod!));
            var rateField = GetterField(rate.GetMethod!, read(rate.GetMethod!));
            var playingField = GetterField(playing.GetMethod!, read(playing.GetMethod!));
            Require(InspectSetter(position.SetMethod!, positionField, read(position.SetMethod!)) is null, "Position assignment changed.");
            Require(InspectSetter(rate.SetMethod!, rateField, read(rate.SetMethod!)) is null, "PlaybackRate assignment changed.");
            Require(InspectPlayingSetter(playing.SetMethod!, playingField, read(playing.SetMethod!)) is null, "IsPlaying assignment/notification changed.");
            var timeline = Field(audio, "timeline");
            var timelineSource = Field(video, "timelineVideo");
            Require(timelineSource.FieldType == source, "Timeline source owner/type changed.");
            var videoInfo = Property(timeline.FieldType, "VideoInfo", null, setterRequired: false).GetMethod!;
            var length = Property(timeline.FieldType, "Length", typeof(int), setterRequired: false).GetMethod!;
            Require(videoInfo.IsPublic && length.IsPublic && FixedDispatch(videoInfo) && FixedDispatch(length), "Timeline bounds getters changed.");
            var getTime = Method(videoInfo.ReturnType, "GetTimeFrom", typeof(TimeSpan), typeof(int));
            var getFrame = Method(videoInfo.ReturnType, "GetFrameFrom", typeof(int), typeof(TimeSpan));
            Require(getTime.IsPublic && getFrame.IsPublic && FixedDispatch(getTime) && FixedDispatch(getFrame), "Timeline time/frame conversion changed.");
            var updateCandidates = source.GetMethods(Instance).Where(m => m.Name == "Update" && !m.IsStatic && !m.IsGenericMethod
                && m.IsPublic && m.ReturnType == typeof(void) && m.GetParameters() is [{ ParameterType: var time }, { ParameterType: var usage }]
                && time == typeof(TimeSpan) && usage.IsEnum).ToArray();
            Require(updateCandidates.Length == 1, "Source Update signature is ambiguous or unavailable.");
            var update = updateCandidates[0];
            var usageType = update.GetParameters()[1].ParameterType;
            Require(FixedDispatch(update) && EnumValue(usageType, "Playing") == 0 && EnumValue(usageType, "Paused") == 1 && EnumValue(usageType, "Exporting") == 2,
                "Source Update usage values changed.");
            var playerDispose = Method(video, "Dispose", typeof(void), typeof(bool));
            var sourceDispose = Method(source, "Dispose", typeof(void), typeof(bool));
            Require(playerDispose.IsFamily && playerDispose.IsVirtual && sourceDispose.IsFamily && sourceDispose.IsVirtual,
                "Native player/source disposal boundary changed.");
            var backend = Field(audio, "player");
            var start = GetterField(Property(audio, "StartPosition", typeof(TimeSpan)).GetMethod!,
                read(Property(audio, "StartPosition", typeof(TimeSpan)).GetMethod!));
            Require(backend.FieldType == typeof(AudioPlayer) && start.FieldType == typeof(TimeSpan), "Native audio clock owner changed.");
            var audioEnd = Method(audio, "EndAudioTask", typeof(Task));
            var audioEndState = audioEnd.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
                .GetMethod("MoveNext", BindingFlags.Instance|BindingFlags.NonPublic)
                ?? throw new NotSupportedException("Native audio stop state machine unavailable.");
            var endCodes = read(audioEndState);
            var clear = endCodes.Select((code,index)=>(code,index)).Where(x=>x.code.opcode==OpCodes.Stfld && Equals(x.code.operand,backend)).ToArray();
            var stopped = endCodes.Select((code,index)=>(code,index)).Where(x=>x.code.Calls(playing.SetMethod!)).ToArray();
            Require(clear.Length==1 && clear[0].index>0 && endCodes[clear[0].index-1].opcode==OpCodes.Ldnull
                && stopped.Length==1 && stopped[0].index>clear[0].index && endCodes[stopped[0].index-1].LoadsConstant(0),
                "Native audio clear/stop/notification order changed.");
            var audioPosition = Property(typeof(AudioPlayer), "CurrentPosition", typeof(TimeSpan), setterRequired:false).GetMethod!;
            var audioStop = Method(typeof(AudioPlayer), "Stop", typeof(void));
            var audioDispose = Method(typeof(AudioPlayer), "Dispose", typeof(void), typeof(bool));
            var waveOut = Field(typeof(AudioPlayer), "waveOut");
            var (positionLoop, rateCapture) = InspectAudioRateCapture(audio, position.GetMethod!, audioStop, read);
            var backendRate=Property(typeof(AudioPlayer),"PlaybackRate",typeof(double),setterRequired:false).GetMethod!;
            var backendMode=Property(typeof(AudioPlayer),"PlaybackRateMode",typeof(AudioPlayerPlaybackRateMode),setterRequired:false).GetMethod!;
            var requestedMode=Property(typeof(YMMSettings),"AudioPlayerPlaybackRateMode",typeof(AudioPlayerPlaybackRateMode),setterRequired:false).GetMethod!;
            var rateCodes=read(positionLoop);
            int CallBefore(MethodInfo method) => rateCodes.Select((c,i)=>(c,i))
                .Where(x=>x.i<rateCapture && x.c.Calls(method)).Select(x=>x.i).Single();
            var requestedAt=CallBefore(rate.GetMethod!);
            Require(CallBefore(requestedMode)<CallBefore(backendRate) && CallBefore(backendRate)<requestedAt
                && requestedAt<CallBefore(backendMode) && rateCodes[requestedAt+1].LoadsConstant(100d)
                && rateCodes[requestedAt+2].opcode==OpCodes.Div, "Native audio rate/mode comparison changed.");
            Require(audioEnd.IsPrivate && audioStop.IsPublic && audioPosition.IsPublic && audioDispose.IsFamily,
                "Native audio lifecycle visibility changed.");
            return new(new(audio, video, source, position.GetMethod!, position.SetMethod!, rate.SetMethod!, playing.SetMethod!,
                positionField, rateField, playingField, timeline, timelineSource, videoInfo, length, getTime, getFrame, update, playerDispose, sourceDispose,
                new(backend,start,waveOut,audioEnd,audioEndState,audioPosition,audioStop,audioDispose,positionLoop,rateCapture,
                    rate.GetMethod!,backendRate,requestedMode,backendMode)), null);
        }
        catch (Exception error) { return new(null, "Native playback connection unavailable: " + error.Message); }
    }

    private static (MethodInfo Loop, int Capture) InspectAudioRateCapture(Type audio, MethodInfo position, MethodInfo stop,
        Func<MethodInfo,IReadOnlyList<CodeInstruction>> read)
    {
        MethodInfo MoveNext(MethodInfo method) => method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            .GetMethod("MoveNext", BindingFlags.Instance|BindingFlags.NonPublic)
            ?? throw new NotSupportedException("Native audio async loop unavailable.");
        var start = MoveNext(Method(audio, "StartAudioTask", typeof(Task)));
        var delegates = read(start).Where(c=>c.opcode==OpCodes.Ldftn && c.operand is MethodInfo m
            && m.ReturnType==typeof(Task) && m.GetParameters().Length==0 && m.IsDefined(typeof(AsyncStateMachineAttribute),false))
            .Select(c=>(MethodInfo)c.operand).ToArray();
        Require(delegates.Length==1, "Native audio position task ambiguous.");
        var loop = MoveNext(delegates[0]);
        var codes = read(loop);
        int Unique(MethodInfo target)
        {
            var matches=codes.Select((c,i)=>(c,i)).Where(x=>x.c.Calls(target)).Select(x=>x.i).ToArray();
            Require(matches.Length==1, "Native audio rate call ambiguous: "+target.Name);
            return matches[0];
        }
        var stopAt = Unique(stop);
        var captures = codes.Select((c,i)=>(c,i)).Where(x=>x.i<stopAt && x.c.Calls(position)).Select(x=>x.i).ToArray();
        Require(captures.Length==1 && stopAt-captures[0]>1, "Native audio rate position capture changed.");
        var capture = captures[0];
        Require(stopAt-capture<10 || HasVolumeCapture(audio,loop,codes,capture,stopAt), "Native audio rate position capture changed.");
        var slot = LocalSlot(codes[capture+1], store:true);
        Require(slot>=0 && loop.GetMethodBody()!.LocalVariables[slot].LocalType==typeof(TimeSpan), "Native audio rate position local changed.");
        var seek = Method(Field(audio,"source").FieldType,"Seek",typeof(void),typeof(TimeSpan));
        var seekAt = Unique(seek);
        var setStart = Property(audio,"StartPosition",typeof(TimeSpan)).SetMethod!;
        var setAt = Unique(setStart);
        var playAt = Unique(Method(typeof(AudioPlayer),"Play",typeof(void),typeof(double),typeof(AudioPlayerPlaybackRateMode)));
        Require(capture<stopAt && stopAt<seekAt && seekAt<setAt && setAt<playAt
            && LocalSlot(codes[seekAt-1],store:false)==slot && LocalSlot(codes[setAt-1],store:false)==slot
            && !codes.Skip(capture+2).Take(setAt-capture-2).Any(c=>LocalSlot(c,store:true)==slot || LocalSlot(c,store:null)==slot),
            "Native audio rate Seek/StartPosition no longer uses captured position.");
        return (loop,capture);
    }

    // 4.57 preserves Volume before stopping/replacing the audio backend. Accept
    // that exact read-only insertion on the same captured owner, not a wider gap.
    private static bool HasVolumeCapture(Type audio, MethodInfo loop, IReadOnlyList<CodeInstruction> codes, int capture, int stopAt)
    {
        if(capture<2 || stopAt-capture!=10) return false;
        var backend=Field(audio,"player");
        var volume=Property(typeof(AudioPlayer),"Volume",typeof(double)).GetMethod!;
        var locals=loop.GetMethodBody()!.LocalVariables;
        var ownerSlot=LocalSlot(codes[capture-2],store:false);
        var volumeSlot=LocalSlot(codes[capture+6],store:true);
        bool Same(int left,int right)=>codes[left].opcode==codes[right].opcode && Equals(codes[left].operand,codes[right].operand);
        return ownerSlot>=0 && ownerSlot<locals.Count
            && codes[capture-1].opcode==OpCodes.Ldfld && codes[capture-1].operand is FieldInfo owner
            && !owner.IsStatic && owner.FieldType==audio && owner.DeclaringType==locals[ownerSlot].LocalType
            && Same(capture+2,capture-2) && Same(capture+3,capture-1)
            && codes[capture+4].opcode==OpCodes.Ldfld && Equals(codes[capture+4].operand,backend)
            && codes[capture+5].Calls(volume) && volumeSlot>=0 && volumeSlot<locals.Count && locals[volumeSlot].LocalType==typeof(double)
            && Same(capture+7,capture-2) && Same(capture+8,capture-1)
            && codes[capture+9].opcode==OpCodes.Ldfld && Equals(codes[capture+9].operand,backend)
            && NoEntries(codes.Skip(capture+1).Take(stopAt-capture).ToArray());
    }

    private static int LocalSlot(CodeInstruction code, bool? store)
    {
        var op=code.opcode;
        if(store is null)
        {
            if(op!=OpCodes.Ldloca && op!=OpCodes.Ldloca_S)return -1;
        }
        else
        {
            if(op==(store.Value?OpCodes.Stloc_0:OpCodes.Ldloc_0))return 0;
            if(op==(store.Value?OpCodes.Stloc_1:OpCodes.Ldloc_1))return 1;
            if(op==(store.Value?OpCodes.Stloc_2:OpCodes.Ldloc_2))return 2;
            if(op==(store.Value?OpCodes.Stloc_3:OpCodes.Ldloc_3))return 3;
            if(op!=(store.Value?OpCodes.Stloc:OpCodes.Ldloc)&&op!=(store.Value?OpCodes.Stloc_S:OpCodes.Ldloc_S))return -1;
        }
        return code.operand switch {LocalBuilder b=>b.LocalIndex,LocalVariableInfo v=>v.LocalIndex,byte b=>b,int i=>i,_=>-1};
    }

    internal static FieldInfo GetterField(MethodInfo method, IReadOnlyList<CodeInstruction> codes)
    {
        Require(!method.IsStatic && !method.IsGenericMethod && FixedDispatch(method) && method.GetParameters().Length == 0
            && codes.Count == 3 && codes[0].opcode == OpCodes.Ldarg_0 && codes[1].opcode == OpCodes.Ldfld
            && codes[1].operand is FieldInfo && codes[2].opcode == OpCodes.Ret && NoEntries(codes), "Raw playback getter changed.");
        var field = (FieldInfo)codes[1].operand;
        Require(!field.IsStatic && field.DeclaringType == method.DeclaringType && field.FieldType == method.ReturnType, "Raw playback getter field owner/type changed.");
        return field;
    }
    internal static string? InspectSetter(MethodInfo method, FieldInfo field, IReadOnlyList<CodeInstruction> codes)
    {
        if (!SetterSignature(method, field) || codes.Count != 4 || !NoEntries(codes)
            || codes[0].opcode != OpCodes.Ldarg_0 || codes[1].opcode != OpCodes.Ldarg_1
            || codes[2].opcode != OpCodes.Stfld || !Equals(codes[2].operand, field) || codes[3].opcode != OpCodes.Ret)
            return "Setter must assign its same-player raw field from the supplied argument exactly once.";
        return null;
    }
    internal static string? InspectPlayingSetter(MethodInfo method, FieldInfo field, IReadOnlyList<CodeInstruction> codes)
    {
        var equals = AccessTools.Method(typeof(object), nameof(object.Equals), [typeof(object), typeof(object)]);
        var notify = method.DeclaringType?.GetMethod("OnPropertyChanged", Instance, null, [typeof(string)], null);
        if (!SetterSignature(method, field) || field.FieldType != typeof(bool) || notify is null || notify.IsStatic
            || notify.IsGenericMethod || notify.ReturnType != typeof(void) || codes.Count != 15 || codes.Any(c => c.blocks.Count != 0)
            || codes.Where((c, i) => i != 8).Any(c => c.labels.Count != 0)
            || codes[0].opcode != OpCodes.Ldarg_0 || codes[1].opcode != OpCodes.Ldfld || !Equals(codes[1].operand, field)
            || codes[2].opcode != OpCodes.Box || !Equals(codes[2].operand, typeof(bool)) || codes[3].opcode != OpCodes.Ldarg_1
            || codes[4].opcode != OpCodes.Box || !Equals(codes[4].operand, typeof(bool)) || !codes[5].Calls(equals)
            || codes[6].opcode != OpCodes.Brfalse && codes[6].opcode != OpCodes.Brfalse_S
            || codes[6].operand is not Label target || codes[8].labels.Count != 1 || codes[8].labels[0] != target
            || codes[7].opcode != OpCodes.Ret || codes[8].opcode != OpCodes.Ldarg_0 || codes[9].opcode != OpCodes.Ldarg_1
            || codes[10].opcode != OpCodes.Stfld || !Equals(codes[10].operand, field) || codes[11].opcode != OpCodes.Ldarg_0
            || codes[12].opcode != OpCodes.Ldstr || !Equals(codes[12].operand, "IsPlaying") || !codes[13].Calls(notify) || codes[14].opcode != OpCodes.Ret)
            return "IsPlaying must compare its raw field, assign a changed argument, then notify once on the same player.";
        return null;
    }
    private static bool FixedDispatch(MethodInfo method) => !method.IsVirtual || method.IsFinal;
    private static bool SetterSignature(MethodInfo method, FieldInfo field) => !method.IsStatic && !method.IsGenericMethod && FixedDispatch(method)
        && !field.IsStatic && method.DeclaringType == field.DeclaringType && method.ReturnType == typeof(void)
        && method.GetParameters() is [{ ParameterType: var value }] && value == field.FieldType;
    private static bool NoEntries(IReadOnlyList<CodeInstruction> codes) => codes.All(c => c.labels.Count == 0 && c.blocks.Count == 0);
    private static int EnumValue(Type type, string name) => Convert.ToInt32(type.GetField(name)?.GetRawConstantValue() ?? -1);
    private static PropertyInfo Property(Type type, string name, Type? expected, bool setterRequired = true)
    {
        var property = type.GetProperty(name, Instance) ?? throw new NotSupportedException(name + " property unavailable.");
        Require(property.GetIndexParameters().Length == 0 && (expected is null || property.PropertyType == expected)
            && property.GetMethod is { IsStatic: false, IsGenericMethod: false } getter && getter.ReturnType == property.PropertyType
            && getter.GetParameters().Length == 0 && (!setterRequired || property.SetMethod is { IsStatic: false, IsGenericMethod: false }), name + " property signature changed.");
        return property;
    }
    private static FieldInfo Field(Type type, string name)
    { var field = type.GetField(name, Instance); Require(field is { IsStatic: false }, name + " field unavailable."); return field!; }
    private static MethodInfo Method(Type type, string name, Type result, params Type[] parameters)
    {
        var method = type.GetMethod(name, Instance, null, parameters, null);
        Require(method is { IsStatic: false, IsGenericMethod: false } && method.ReturnType == result, name + " signature changed."); return method!;
    }
    private static void Require(bool value, string failure) { if (!value) throw new NotSupportedException(failure); }
}
