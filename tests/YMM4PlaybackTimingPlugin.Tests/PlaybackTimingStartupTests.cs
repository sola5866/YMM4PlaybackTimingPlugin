using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using YMM4PlaybackTimingPlugin.Composition;
using YMM4PlaybackTimingPlugin.Integration;
using YMM4PlaybackTimingPlugin.Playback;

internal static class PlaybackTimingStartupTests
{
    internal static void BoundariesAndIdentity()
    {
        var assembly=typeof(PlaybackTimingStartup).Assembly;
        Check(!assembly.GetReferencedAssemblies().Any(a=>a.Name!.StartsWith("YMM4")&&!a.Name!.StartsWith("YMM4PlaybackTimingPlugin")),"Timing references an external plugin");
        var pluginType=typeof(YMM4PlaybackTimingPlugin.YMM4PlaybackTimingPlugin);
        Check(assembly.GetName().Name=="YMM4PlaybackTimingPlugin"&&pluginType.FullName=="YMM4PlaybackTimingPlugin.YMM4PlaybackTimingPlugin","standalone public identity mismatch");
        Check(assembly.GetManifestResourceNames().Contains("YMM4PlaybackTimingPlugin.Localization.PlaybackTimingResources.resources"),"standalone resource identity mismatch");
        var entries=assembly.GetTypes().Where(t=>!t.IsAbstract&&typeof(YukkuriMovieMaker.Plugin.IPlugin).IsAssignableFrom(t)).ToArray();
        Check(entries.Length==2&&entries.Contains(pluginType)&&entries.Contains(typeof(YMM4PlaybackTimingPlugin.Diagnostics.PlaybackDiagnosticsPlugin)),"Expected playback and diagnostics entries");
        var tool=new YMM4PlaybackTimingPlugin.Diagnostics.PlaybackDiagnosticsPlugin();
        Check(!tool.AllowMultipleInstances&&tool.ViewType==typeof(YMM4PlaybackTimingPlugin.Diagnostics.PlaybackDiagnosticsView)&&tool.ViewModelType==typeof(YMM4PlaybackTimingPlugin.Diagnostics.PlaybackDiagnosticsViewModel),"diagnostics tool contract");
        var plugin=(YMM4PlaybackTimingPlugin.YMM4PlaybackTimingPlugin)RuntimeHelpers.GetUninitializedObject(pluginType);
        var culture=YMM4PlaybackTimingPlugin.Localization.PlaybackTimingResources.Culture;
        try
        {
            plugin.SetCulture(CultureInfo.GetCultureInfo("ja-JP"));
            Check(plugin.Name == "YMM4再生タイミング調整プラグイン", "Japanese plugin name mismatch");
            plugin.SetCulture(CultureInfo.GetCultureInfo("en-US"));
            Check(plugin.Name == "YMM4 Playback Timing Plugin", "English plugin name mismatch");
        }
        finally{YMM4PlaybackTimingPlugin.Localization.PlaybackTimingResources.Culture=culture;}
        var first=new object();var second=new object();PreviewFrameDeadlineRegistry.Register(first,TimeSpan.FromMilliseconds(16),true);
        Check(PreviewFrameDeadlineRegistry.TryGet(first,out _)&&!PreviewFrameDeadlineRegistry.TryGet(second,out _),"native reference identity lost");
        PreviewFrameDeadlineRegistry.Forget(first);Check(!PreviewFrameDeadlineRegistry.TryGet(first,out _),"ended source retained");
    }
    internal static void Settings()
    {
        Check(typeof(PlaybackTimingStartup).GetInterfaces().Length==0,"startup unexpectedly implements a participant interface");
        Check(typeof(PlaybackTimingStartup).GetFields(BindingFlags.Instance|BindingFlags.NonPublic).All(f=>!f.FieldType.FullName!.Contains("Settings")),"startup retains a settings owner");
        Check(typeof(PlaybackTimingStartup).GetMethod("Initialize",BindingFlags.Instance|BindingFlags.NonPublic)!.GetParameters().All(p=>p.Name is not ("features" or "register")),"old control input remains");
    }
    internal static void RegistrationRollback()
    {
        Check(PreviewNativePlaybackContract.Cached.Available,PreviewNativePlaybackContract.Cached.Failure??"playback unavailable");
        Check(PreviewNativePacingContract.Cached.Available,PreviewNativePacingContract.Cached.Failure??"pacing unavailable");
        var startup=new PlaybackTimingStartup();var expected=new InvalidOperationException("injected registration failure");
        startup.Initialize(afterPatch:count=>{if(count==3)throw expected;});
        Check(!startup.IsActive&&startup.Reason.StartsWith("playback-timing-unavailable:"),"failed startup exposed pacing");
        Exception? observed=null;try{startup.CloseAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();}catch(Exception error){observed=error;}
        Check(ReferenceEquals(observed,expected),"primary registration error replaced: "+observed);
        foreach(var spec in PlaybackTimingPatchRegistration.Resolve())Check(!Owned(spec.Target),"rollback left own patch");
        using var replacement=new PlaybackTimingRegistrationOwner();Check(replacement.IsActive,"rollback prevented replacement");replacement.CloseAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name!.StartsWith("YMM4")&&!a.GetName().Name!.StartsWith("YMM4PlaybackTimingPlugin")),"Timing activated an external plugin");
    }
    private static bool Owned(MethodInfo target)=>Harmony.GetPatchInfo(target)?.Owners.Any(o=>o.StartsWith("YMM4PlaybackTimingPlugin."))==true;
    internal static void FailedRetirementRetainsSession()
    {
        foreach(var endSource in new[]{false,true})
        {
            var contract=PreviewNativePlaybackContract.Cached.Value!;var player=RuntimeHelpers.GetUninitializedObject(contract.Video);var source=RuntimeHelpers.GetUninitializedObject(contract.Source);contract.TimelineSource.SetValue(player,source);
            var runtime=new FailedRuntime();var activation=new PlaybackTimingClockHooks.Activation(()=>new(new PreviewPlaybackClockState(1_000_000),()=>runtime));activation.Commit();
            var session=activation.GetOrCreateSession(player)!;session.Clock.Start(TimeSpan.Zero,100,0);
            PreviewFrameDeadlineRegistry.Register(source,TimeSpan.FromMilliseconds(16),true);PreviewFrameDeadlineRegistry.TryGet(source,out var deadline);session.TryWait(source,deadline.CandidateTime,deadline.Sequence,out _);
            if(endSource)activation.EndSource(source);else activation.EndPlayer(player);
            var failed=false;try{activation.CloseAsync().GetAwaiter().GetResult();}catch(InvalidOperationException){failed=true;}
            Check(failed&&activation.RetainedRetirementCount==1&&runtime.Disposals==1,"failed retirement lost session ownership");
        }
    }
    private sealed class FailedRuntime:IPreviewFrameDeadlineRuntime,IDisposable
    {
        private long timestamp;internal int Disposals;
        public long Frequency=>1_000_000;public long GetTimestamp()=>timestamp;
        public void Sleep(TimeSpan t)=>timestamp+=PreviewFrameDeadlineWait.DurationToTimestampTicks(t,Frequency);
        public bool TryPreciseWait(TimeSpan t){Sleep(t);return true;}public void Yield()=>timestamp+=100;public void SpinOnce()=>timestamp+=10;
        public void Dispose(){Disposals++;throw new InvalidOperationException("injected retirement failure");}
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
