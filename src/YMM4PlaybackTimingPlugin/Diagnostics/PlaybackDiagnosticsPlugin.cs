using YukkuriMovieMaker.Plugin;

namespace YMM4PlaybackTimingPlugin.Diagnostics;

public sealed class PlaybackDiagnosticsPlugin : IToolPlugin
{
    public string Name => "プレビュー再生診断";
    public Type ViewModelType => typeof(PlaybackDiagnosticsViewModel);
    public Type ViewType => typeof(PlaybackDiagnosticsView);
    public bool AllowMultipleInstances => false;
}
