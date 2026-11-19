using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace YMM4PlaybackTimingPlugin.Integration;

// PlaybackTiming validates its candidate/deadline loop. Shared Draw rewriting belongs to Windows.
internal static class PreviewNativeVideoLoopContract
{
    internal sealed record Inspection(MethodInfo? Loop,string? DiscoveryFailure)
    {internal bool DiscoveryAvailable=>Loop is not null&&DiscoveryFailure is null;}
    private static readonly Lazy<Inspection> native=new(Read);
    internal static Inspection Cached=>native.Value;
    private static Inspection Read()
    {
        try
        {
            var player=PreviewNativePlaybackContract.Cached.Value?.Video??throw new NotSupportedException("Native playback unavailable.");
            var begin=player.GetMethod("BeginVideoTask",BindingFlags.Instance|BindingFlags.Public|BindingFlags.DeclaredOnly,null,Type.EmptyTypes,null)!;
            var task=player.GetField("videoTask",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)!;
            var codes=PatchProcessor.GetOriginalInstructions(begin).ToArray();
            var action=typeof(Action).GetConstructor([typeof(object),typeof(IntPtr)])!;
            var run=typeof(Task).GetMethod(nameof(Task.Run),[typeof(Action)])!;
            var indexes=codes.Select((code,index)=>(code,index)).Where(c=>c.code.opcode==OpCodes.Newobj&&Equals(c.code.operand,action)).ToArray();
            if(indexes.Length!=1)throw new NotSupportedException("Native launch delegate count differs.");
            var i=indexes[0].index;
            if(i<3||i+2>=codes.Length||codes[i-3].opcode!=OpCodes.Ldarg_0||codes[i-2].opcode!=OpCodes.Ldarg_0||
                codes[i-1].opcode!=OpCodes.Ldftn||codes[i-1].operand is not MethodInfo loop||loop.DeclaringType!=player||
                !loop.IsPrivate||loop.IsStatic||loop.IsGenericMethod||loop.ReturnType!=typeof(void)||loop.GetParameters().Length!=0||
                !loop.IsDefined(typeof(CompilerGeneratedAttribute),false)||!codes[i+1].Calls(run)||codes[i+2].opcode!=OpCodes.Stfld||
                !Equals(codes[i+2].operand,task)||task.FieldType!=typeof(Task)||
                codes.Count(c=>c.Calls(run))!=1||codes.Count(c=>c.opcode==OpCodes.Stfld&&Equals(c.operand,task))!=1||
                codes.Skip(i-3).Take(6).Any(c=>c.labels.Count!=0||c.blocks.Count!=0))
                throw new NotSupportedException("Native same-player Task.Run publication differs.");
            return new(loop,null);
        }
        catch(Exception error){return new(null,error.Message);}
    }
}
