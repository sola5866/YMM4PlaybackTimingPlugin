using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace YMM4PlaybackTimingPlugin.Integration;

internal static class PreviewNativePacingContract
{
    internal sealed record Inspection(MethodInfo? Loop, string? Failure)
    { internal bool Available => Loop is not null && Failure is null; }
    private static readonly Lazy<Inspection> Native = new(Read);
    internal static Inspection Cached => Native.Value;
    private static Inspection Read()
    {
        var launch = PreviewNativeVideoLoopContract.Cached;
        var playback = PreviewNativePlaybackContract.Cached;
        if (!launch.DiscoveryAvailable) return new(null, launch.DiscoveryFailure);
        if (!playback.Available) return new(null, playback.Failure);
        try
        {
            var loop = Inspect(launch.Loop!, PatchProcessor.GetOriginalInstructions(launch.Loop!).ToArray(), playback.Value!);
            if (!loop.Available) return loop;
            var lockFailure = InspectUpdateLock(playback.Value!.Update,
                PatchProcessor.GetOriginalInstructions(playback.Value.Update).ToArray());
            return lockFailure is null ? loop : new(null, lockFailure);
        }
        catch (Exception error) { return new(null, "Native pacing inspection failed: " + error.Message); }
    }

    internal static Inspection Inspect(MethodInfo loop, IReadOnlyList<CodeInstruction> codes, PreviewNativePlaybackContract.Contract native)
    {
        try
        {
            Require(loop == PreviewNativeVideoLoopContract.Cached.Loop && loop.DeclaringType == native.Video, "Pacing loop identity changed.");
            var edit = AccessTools.Method(native.Video, "Edit", Type.EmptyTypes);
            var draw = AccessTools.Method(native.Video,"Draw",Type.EmptyTypes)!;
            var present = AccessTools.Method(native.Video, "Present", Type.EmptyTypes);
            var playing = AccessTools.PropertyGetter(native.Audio, "IsPlaying");
            var ticks = AccessTools.PropertyGetter(typeof(TimeSpan), nameof(TimeSpan.Ticks));
            var fromTicks = AccessTools.Method(typeof(TimeSpan), nameof(TimeSpan.FromTicks), [typeof(long)]);
            var delay = AccessTools.Method(typeof(Task), nameof(Task.Delay), [typeof(TimeSpan)]);
            var pausedDelay = AccessTools.Method(typeof(Task), nameof(Task.Delay), [typeof(int)]);
            var wait = AccessTools.Method(typeof(Task), nameof(Task.Wait), Type.EmptyTypes);
            var rects = AccessTools.PropertySetter(native.Source, "NeedTimelineItemRects");
            var mouse = AccessTools.Field(native.Video, "isMouseOverPreviewArea");
            var changed = AccessTools.Field(native.Video, "isTimelineChanged");
            Require(rects is { IsPublic: true, IsStatic: false, IsGenericMethod: false } && (!rects.IsVirtual || rects.IsFinal)
                && rects.ReturnType == typeof(void) && rects.GetParameters() is [{ ParameterType: var rectArg }] && rectArg == typeof(bool)
                && mouse is { IsStatic: false } && mouse.DeclaringType == native.Video && mouse.FieldType == typeof(bool)
                && changed is { IsStatic: false } && changed.DeclaringType == native.Video && changed.FieldType == typeof(bool), "Native candidate source fields changed.");
            int first = Unique(codes, edit) + 1;
            var draws = Indexes(codes, draw);
            var updates = Indexes(codes, native.Update);
            var presents = Indexes(codes, present);
            Require(first > 0 && draws.Length == 2 && updates.Length == 2 && presents.Length == 2
                && draws[0] == first + 46 && updates[0] == draws[0] - 2, "Native candidate loop cardinality/order changed.");
            int afterDraw = draws[0] + 1;
            Require(afterDraw + 75 < codes.Count && Unique(codes, delay) == afterDraw + 23
                && Indexes(codes, pausedDelay).Count(index => index >= afterDraw + 43 && index < afterDraw + 75) == 1
                && codes[afterDraw + 73].Calls(pausedDelay) && draws[1] == afterDraw + 65
                && updates[1] == afterDraw + 63 && presents[0] == afterDraw + 37 && presents[1] == afterDraw + 67,
                "Native delay/paused loop cardinality/order changed.");
            var locals = loop.GetMethodBody()!.LocalVariables;
            int Capture(int index, LocalKind kind, Type type)
            {
                int local = Local(codes[index], kind);
                Require(local >= 0 && local < locals.Count && locals[local].LocalType == type, "Native candidate local type changed.");
                return local;
            }
            int position = Capture(first + 2, LocalKind.Store, typeof(TimeSpan));
            int frame = Capture(first + 8, LocalKind.Store, typeof(int));
            int time = Capture(first + 14, LocalKind.Store, typeof(TimeSpan));
            int candidateFrame = Capture(first + 19, LocalKind.Store, typeof(int));
            int candidate = Capture(first + 21, LocalKind.Store, typeof(TimeSpan));
            int previousFrame = Capture(first + 22, LocalKind.Load, typeof(int));
            int current = Capture(afterDraw + 4, LocalKind.Store, typeof(TimeSpan));
            int delta = Capture(afterDraw + 16, LocalKind.Store, typeof(long));
            int usage = Capture(afterDraw + 43, LocalKind.Load, native.Update.GetParameters()[1].ParameterType);
            int[] captured = [position, frame, time, candidateFrame, candidate, previousFrame, current, delta, usage];
            Require(captured.Distinct().Count() == captured.Length, "Candidate/source/current-position locals alias.");

            Func<CodeInstruction, bool> Op(OpCode opcode) => c => c.opcode == opcode;
            Func<CodeInstruction, bool> Call(MethodInfo? method) => c => method is not null && c.Calls(method);
            Func<CodeInstruction, bool> Field(FieldInfo field, OpCode? opcode = null) => c => c.opcode == (opcode ?? OpCodes.Ldfld) && Equals(c.operand, field);
            Func<CodeInstruction, bool> Load(int local) => c => Local(c, LocalKind.Load) == local;
            Func<CodeInstruction, bool> Store(int local) => c => Local(c, LocalKind.Store) == local;
            Func<CodeInstruction, bool> Address(int local) => c => Local(c, LocalKind.Address) == local;
            Func<CodeInstruction, bool> Constant(int value) => c => c.LoadsConstant(value);
            Func<CodeInstruction, bool> Branch(OpCode longForm, OpCode shortForm, int target) => c =>
                (c.opcode == longForm || c.opcode == shortForm) && c.operand is Label label && codes[target].labels.Contains(label);
            var arg0 = Op(OpCodes.Ldarg_0);
            // Typed data flow, not byte fingerprints: local numbers and branch labels are bound from the actual body.
            Func<CodeInstruction, bool>[] generation = [
                arg0, Call(native.PositionGetter), Store(position),
                arg0, Field(native.Timeline), Call(native.VideoInfoGetter), Load(position), Call(native.GetFrameFrom), Store(frame),
                arg0, Field(native.Timeline), Call(native.VideoInfoGetter), Load(frame), Call(native.GetTimeFrom), Store(time),
                arg0, Call(playing), Branch(OpCodes.Brfalse, OpCodes.Brfalse_S, afterDraw + 43),
                Load(frame), Store(candidateFrame), Load(time), Store(candidate), Load(previousFrame), Load(frame),
                Branch(OpCodes.Bne_Un, OpCodes.Bne_Un_S, first + 35), Load(candidateFrame), Constant(1), Op(OpCodes.Add), Store(candidateFrame),
                arg0, Field(native.Timeline), Call(native.VideoInfoGetter), Load(candidateFrame), Call(native.GetTimeFrom), Store(candidate),
                arg0, Field(native.TimelineSource), arg0, Field(mouse!), Call(rects),
                arg0, Field(native.TimelineSource), Load(candidate), Constant(0), Call(native.Update), arg0, Call(draw)];
            Func<CodeInstruction, bool>[] completion = [
                Address(position), Call(ticks), arg0, Call(native.PositionGetter), Store(current), Address(current), Call(ticks),
                Branch(OpCodes.Bgt, OpCodes.Bgt_S, afterDraw + 25),
                Address(candidate), Call(ticks), arg0, Call(native.PositionGetter), Store(current), Address(current), Call(ticks), Op(OpCodes.Sub), Store(delta),
                Constant(0), Op(OpCodes.Conv_I8), Load(delta), Branch(OpCodes.Bge, OpCodes.Bge_S, afterDraw + 25),
                Load(delta), Call(fromTicks), Call(delay), Call(wait),
                arg0, Call(playing), Branch(OpCodes.Brfalse, OpCodes.Brfalse_S, afterDraw + 75),
                Load(candidateFrame), arg0, Field(native.Timeline), Call(native.VideoInfoGetter), arg0, Call(native.PositionGetter), Call(native.GetFrameFrom),
                Branch(OpCodes.Bgt, OpCodes.Bgt_S, afterDraw + 75), arg0, Call(present), Load(candidateFrame), Store(previousFrame), Constant(0), Store(usage),
                Branch(OpCodes.Br, OpCodes.Br_S, afterDraw + 75),
                Load(usage), Constant(1), Branch(OpCodes.Bne_Un, OpCodes.Bne_Un_S, afterDraw + 52),
                Load(previousFrame), Load(frame), Branch(OpCodes.Bne_Un, OpCodes.Bne_Un_S, afterDraw + 52),
                arg0, Field(changed!), Branch(OpCodes.Brfalse, OpCodes.Brfalse_S, afterDraw + 72),
                arg0, Constant(0), Field(changed!, OpCodes.Stfld), arg0, Field(native.TimelineSource), Constant(1), Call(rects),
                arg0, Field(native.TimelineSource), Load(time), Constant(1), Call(native.Update), arg0, Call(draw), arg0, Call(present),
                Load(frame), Store(previousFrame), Constant(1), Store(usage), Constant(16), Call(pausedDelay), Call(wait)];
            Require(generation.Length == 47 && completion.Length == 75
                && generation.Select((predicate, i) => predicate(codes[first + i])).All(value => value)
                && completion.Select((predicate, i) => predicate(codes[afterDraw + i])).All(value => value),
                "Native candidate -> Update -> Draw -> positive candidate/Position delta -> Delay -> Present relationship changed.");
            var entries = new HashSet<int> { first + 35, afterDraw + 25, afterDraw + 43, afterDraw + 52, afterDraw + 72 };
            int end = afterDraw + 75;
            Require(Enumerable.Range(first, end - first).All(i => codes[i].blocks.Count == 0 && (codes[i].labels.Count == 0 || entries.Contains(i))),
                "Unexpected entry/EH inside candidate and native wait flow.");
            for (int i = 0; i < codes.Count; i++)
            {
                if (i >= first && i < end) continue;
                foreach (var label in BranchLabels(codes[i]))
                    Require(!Enumerable.Range(first, end - first).Any(target => codes[target].labels.Contains(label)), "External branch bypasses candidate/native wait guards.");
            }
            Require(!codes.Any(c => c.operand is MethodInfo method && (method.DeclaringType == typeof(Monitor)
                || method.DeclaringType?.FullName?.Contains("Multithread", StringComparison.Ordinal) == true) && method.Name is "Enter" or "Exit" or "Leave"),
                "Native loop may hold a lock across Draw completion/wait.");
            return new(loop, null);
        }
        catch (Exception error) { return new(null, "Native pacing unavailable: " + error.Message); }
    }
    internal static string? InspectUpdateLock(MethodInfo update, IReadOnlyList<CodeInstruction> codes)
    {
        var enter = AccessTools.Method(typeof(Monitor), nameof(Monitor.Enter), [typeof(object), typeof(bool).MakeByRefType()]);
        var exit = AccessTools.Method(typeof(Monitor), nameof(Monitor.Exit), [typeof(object)]);
        var enters = Indexes(codes, enter);
        var exits = Indexes(codes, exit);
        if (enters.Length != 1 || exits.Length != 1 || enters[0] < 4 || exits[0] <= enters[0] + 3 || exits[0] + 1 >= codes.Count
            || update.GetMethodBody()?.ExceptionHandlingClauses.Any(c => c.Flags == ExceptionHandlingClauseOptions.Finally) != true)
            return "Source Update lock acquisition/release structure changed.";
        int acquired = enters[0], released = exits[0];
        int lockObject = Local(codes[acquired - 2], LocalKind.Load);
        int flag = Local(codes[acquired - 1], LocalKind.Address);
        bool OtherAcquisition(int index) => codes[index].operand is MethodInfo method && method.DeclaringType == typeof(Monitor)
            && method.Name is nameof(Monitor.Enter) or nameof(Monitor.TryEnter) or nameof(Monitor.Exit)
            && (index != acquired || !Equals(method, enter)) && (index != released || !Equals(method, exit));
        var protectedRegions = new Stack<int>();
        bool matchingFinally = false, matchingEnd = false, validBlocks = true;
        for (int i = 0; i <= released + 1; i++)
        {
            foreach (var block in codes[i].blocks)
            {
                if (block.blockType == ExceptionBlockType.BeginExceptionBlock) protectedRegions.Push(i);
                if (i == released - 3 && block.blockType == ExceptionBlockType.BeginFinallyBlock)
                    matchingFinally = protectedRegions.TryPeek(out var owner) && owner == acquired - 2;
                if (block.blockType != ExceptionBlockType.EndExceptionBlock) continue;
                if (!protectedRegions.TryPop(out var closed)) { validBlocks = false; continue; }
                if (i == released + 1) matchingEnd = closed == acquired - 2;
            }
        }
        if (lockObject < 0 || flag < 0 || lockObject == flag
            || Local(codes[acquired - 3], LocalKind.Store) != flag || !codes[acquired - 4].LoadsConstant(0)
            || update.GetMethodBody()!.LocalVariables[flag].LocalType != typeof(bool)
            || codes[acquired - 2].blocks.All(b => b.blockType != ExceptionBlockType.BeginExceptionBlock)
            || Local(codes[released - 3], LocalKind.Load) != flag
            || codes[released - 3].blocks.All(b => b.blockType != ExceptionBlockType.BeginFinallyBlock)
            || codes[released - 2].opcode != OpCodes.Brfalse && codes[released - 2].opcode != OpCodes.Brfalse_S
            || codes[released - 2].operand is not Label skip || !codes[released + 1].labels.Contains(skip)
            || Local(codes[released - 1], LocalKind.Load) != lockObject
            || codes[released + 1].opcode != OpCodes.Endfinally
            || codes[released + 1].blocks.All(b => b.blockType != ExceptionBlockType.EndExceptionBlock)
            || !validBlocks || !matchingFinally || !matchingEnd
            || Enumerable.Range(0, codes.Count).Any(OtherAcquisition)
            || Enumerable.Range(acquired + 1, released - acquired - 3).Any(i =>
                Local(codes[i], LocalKind.Store) == lockObject || Local(codes[i], LocalKind.Store) == flag
                || Local(codes[i], LocalKind.Address) == lockObject || Local(codes[i], LocalKind.Address) == flag
                || codes[i].opcode == OpCodes.Ret))
            return "Source Update must exit the same monitor under its acquisition flag in a finally block.";
        return null;
    }
    private static IEnumerable<Label> BranchLabels(CodeInstruction code) => code.opcode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch
        ? code.operand switch { Label label => [label], Label[] labels => labels, _ => [] } : [];
    private enum LocalKind { Load, Store, Address }
    private static int Local(CodeInstruction code, LocalKind kind)
    {
        OpCode[] shortCodes = kind == LocalKind.Load ? [OpCodes.Ldloc_0, OpCodes.Ldloc_1, OpCodes.Ldloc_2, OpCodes.Ldloc_3]
            : kind == LocalKind.Store ? [OpCodes.Stloc_0, OpCodes.Stloc_1, OpCodes.Stloc_2, OpCodes.Stloc_3] : [];
        int shorthand = Array.FindIndex(shortCodes, op => op == code.opcode);
        if (shorthand >= 0) return shorthand;
        var (longForm, shortForm) = kind switch { LocalKind.Load => (OpCodes.Ldloc, OpCodes.Ldloc_S), LocalKind.Store => (OpCodes.Stloc, OpCodes.Stloc_S), _ => (OpCodes.Ldloca, OpCodes.Ldloca_S) };
        if (code.opcode != longForm && code.opcode != shortForm) return -1;
        return code.operand switch { LocalVariableInfo local => local.LocalIndex, byte local => local, short local => local, int local => local, _ => -1 };
    }
    private static int[] Indexes(IReadOnlyList<CodeInstruction> codes, MethodInfo? method) => Enumerable.Range(0, codes.Count).Where(i => method is not null && codes[i].Calls(method)).ToArray();
    private static int Unique(IReadOnlyList<CodeInstruction> codes, MethodInfo? method) { var indexes = Indexes(codes, method); return indexes.Length == 1 ? indexes[0] : -2; }
    private static void Require(bool value, string reason) { if (!value) throw new NotSupportedException(reason); }
}
