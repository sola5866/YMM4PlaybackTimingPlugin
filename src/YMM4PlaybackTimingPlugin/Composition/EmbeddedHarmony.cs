using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace YMM4PlaybackTimingPlugin.Composition;

// Owns dependency resolution for the lifetime of this assembly's load context.
internal static class EmbeddedHarmony
{
#pragma warning disable CA2255 // A library module initializer is required before plugin construction.
    [ModuleInitializer]
    internal static void Initialize()=>AssemblyLoadContext.GetLoadContext(typeof(EmbeddedHarmony).Assembly)!.Resolving+=Resolve;
#pragma warning restore CA2255

    private static Assembly? Resolve(AssemblyLoadContext context,AssemblyName name)
    {
        if(name.Name!="0Harmony")return null;
        using var stream=typeof(EmbeddedHarmony).Assembly.GetManifestResourceStream("YMM4PlaybackTimingPlugin.Dependencies.0Harmony.dll");
        return stream is null?null:context.LoadFromStream(stream);
    }
}
