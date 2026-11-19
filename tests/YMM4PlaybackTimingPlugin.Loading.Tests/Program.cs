using System.IO;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;

internal static class Program
{
    // No product or Harmony reference: the test must use the distributable DLL.
    private static async Task<int> Main(string[] args)
    {
        if(args.Length<3)throw new ArgumentException("mode plugin.dll YMM4-directory [external-Harmony.dll or native-test.dll]");
        var mode=args[0];var pluginPath=Path.GetFullPath(args[1]);var sdk=Path.GetFullPath(args[2]);
        if(File.Exists(Path.Combine(AppContext.BaseDirectory,"0Harmony.dll"))||File.Exists(Path.Combine(Path.GetDirectoryName(pluginPath)!,"0Harmony.dll")))throw new Exception("Physical Harmony masks the embedded dependency");
        AssemblyLoadContext.Default.Resolving+=(_,name)=>name.Name=="0Harmony"?null:File.Exists(Path.Combine(sdk,name.Name+".dll"))?AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(sdk,name.Name+".dll")):null;
        Assembly? external=null;
        if(mode=="external-first")external=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[3]));
        var product=AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);
        var entries=product.GetTypes().Where(t=>!t.IsAbstract&&t.GetInterfaces().Any(i=>i.FullName=="YukkuriMovieMaker.Plugin.IPlugin")).ToArray();
        Check(entries.Length==2&&entries.Any(t=>t.FullName=="YMM4PlaybackTimingPlugin.Diagnostics.PlaybackDiagnosticsPlugin"),"playback and diagnostics entries not discovered");
        var playbackEntry=entries.Single(t=>t.FullName=="YMM4PlaybackTimingPlugin.YMM4PlaybackTimingPlugin");
        var toolEntry=entries.Single(t=>t.FullName=="YMM4PlaybackTimingPlugin.Diagnostics.PlaybackDiagnosticsPlugin");
        var tool=Activator.CreateInstance(toolEntry)!;
        Check((string)toolEntry.GetProperty("Name")!.GetValue(tool)! =="プレビュー再生診断","diagnostics tool name");
        Check(!(bool)toolEntry.GetProperty("AllowMultipleInstances")!.GetValue(tool)!,"diagnostics singleton tool");
        var viewType=(Type)toolEntry.GetProperty("ViewType")!.GetValue(tool)!;
        var viewModelType=(Type)toolEntry.GetProperty("ViewModelType")!.GetValue(tool)!;
        Check(viewType.GetConstructor(Type.EmptyTypes) is not null&&viewModelType.GetInterfaces().Any(t=>t.FullName=="YukkuriMovieMaker.Plugin.ITimelineToolViewModel"),"diagnostics view/active timeline contract");
        var viewModel=Activator.CreateInstance(viewModelType)!;
        Check(!(bool)viewModelType.GetProperty("IsMeasuring")!.GetValue(viewModel)!,"diagnostics must start OFF");
        if(mode=="parallel"){
            RuntimeHelpers.RunModuleConstructor(product.ManifestModule.ModuleHandle);
            var loads=new Assembly[16];
            Parallel.For(0,loads.Length,i=>loads[i]=AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("0Harmony, Version=2.4.2.0, Culture=neutral, PublicKeyToken=null")));
            Check(loads.All(a=>ReferenceEquals(a,loads[0])),"parallel resolution returned duplicate assemblies");
        }
        var plugin=Activator.CreateInstance(playbackEntry)!;
        foreach (var (locale, expectedName) in new[]
        {
            ("ja-JP", "YMM4再生タイミング調整プラグイン"),
            ("ja", "YMM4再生タイミング調整プラグイン"),
            ("en-US", "YMM4 Playback Timing Plugin"),
            ("fr-FR", "YMM4 Playback Timing Plugin")
        })
        {
            playbackEntry.GetMethod("SetCulture")!.Invoke(plugin, [CultureInfo.GetCultureInfo(locale)]);
            Check((string)playbackEntry.GetProperty("Name")!.GetValue(plugin)! == expectedName, $"localized plugin name differs for {locale}");
        }
        var flags=BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Static|BindingFlags.Instance;
        var startup=playbackEntry.GetField("startup",flags)!.GetValue(null)!;
        Check((bool)startup.GetType().GetProperty("IsActive",flags)!.GetValue(startup)!,"single DLL startup was inactive");
        var harmony=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="0Harmony");
        if(mode=="embedded-first")external=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[3]));
        if(external is not null)Check(ReferenceEquals(harmony,external),"peer and Timing loaded different Harmony instances");
        else Check(harmony.Location=="","embedded Harmony was loaded from a file");
        var h=harmony.GetType("HarmonyLib.Harmony")!;var hm=harmony.GetType("HarmonyLib.HarmonyMethod")!;
        var peer=Activator.CreateInstance(h,["single-dll.test.peer"])!;
        var target=typeof(Program).GetMethod(nameof(PeerTarget),flags)!;var postfix=typeof(Program).GetMethod(nameof(PeerPostfix),flags)!;
        h.GetMethod("Patch")!.Invoke(peer,[target,null,Activator.CreateInstance(hm,[postfix]),null,null]);
        Check(PeerTarget()==2,"peer patch did not execute");
        var patched=(IEnumerable)h.GetMethod("GetAllPatchedMethods")!.Invoke(null,null)!;
        Check(patched.Cast<MethodBase>().Any(m=>m.DeclaringType?.Assembly.GetName().Name=="YukkuriMovieMaker"),"Timing patch metadata was not shared with peer");
        await ((IAsyncDisposable)plugin).DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        Check(PeerTarget()==2,"Timing disposal removed peer patch");
        h.GetMethod("Unpatch",[typeof(MethodBase),typeof(MethodInfo)])!.Invoke(peer,[target,postfix]);
        Check(PeerTarget()==1,"peer patch cleanup failed");
        Console.WriteLine(JsonSerializer.Serialize(new{mode,Product=product.Location,Harmony=harmony.FullName,harmony.Location,Context=AssemblyLoadContext.GetLoadContext(harmony)!.Name,HarmonyCount=AppDomain.CurrentDomain.GetAssemblies().Count(a=>a.GetName().Name=="0Harmony")}));
        if(mode=="native-loop"){
            var fixture=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[3]));
            var result=(int)fixture.GetType("IndependentNativeLoopTests")!.GetMethod("Run",flags)!.Invoke(null,["warm-long"])!;
            Check(result==0,"embedded Harmony native loop failed");
        }
        Console.WriteLine("PASS single DLL loading "+mode);return 0;
    }
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    [MethodImpl(MethodImplOptions.NoInlining)]private static int PeerTarget()=>1;
    private static void PeerPostfix(ref int __result)=>__result=2;
}
