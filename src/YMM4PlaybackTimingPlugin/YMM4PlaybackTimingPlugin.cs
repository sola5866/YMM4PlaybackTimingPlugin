using YMM4PlaybackTimingPlugin.Localization;
using YukkuriMovieMaker.Plugin;
using YMM4PlaybackTimingPlugin.Composition;

namespace YMM4PlaybackTimingPlugin;

public sealed class YMM4PlaybackTimingPlugin:IPlugin,ILocalizePlugin,IAsyncDisposable
{
    private static readonly PlaybackTimingStartup startup=new();
    internal static bool IsTimingActive=>startup.IsActive;
    public string Name=>PlaybackTimingResources.PluginName;
    public void SetCulture(System.Globalization.CultureInfo cultureInfo)=>PlaybackTimingResources.Culture=cultureInfo??throw new ArgumentNullException(nameof(cultureInfo));
    public YMM4PlaybackTimingPlugin()=>startup.Initialize();
    public ValueTask DisposeAsync()=>new(startup.CloseAsync());
}
