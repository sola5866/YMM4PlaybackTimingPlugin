using System.Globalization;
using System.Resources;

namespace YMM4PlaybackTimingPlugin.Localization;

internal static class PlaybackTimingResources
{
    private static readonly ResourceManager Manager = new("YMM4PlaybackTimingPlugin.Localization.PlaybackTimingResources", typeof(PlaybackTimingResources).Assembly);
    internal static CultureInfo? Culture { get; set; }
    internal static string PluginName
    {
        get
        {
            var culture = Culture ?? CultureInfo.CurrentUICulture;
            var key = culture.TwoLetterISOLanguageName == "ja" ? "PluginNameJapanese" : nameof(PluginName);
            return Manager.GetString(key, CultureInfo.InvariantCulture) ?? throw new MissingManifestResourceException(key);
        }
    }
}
