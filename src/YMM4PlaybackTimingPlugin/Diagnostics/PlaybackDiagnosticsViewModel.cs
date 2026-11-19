using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Threading;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;

namespace YMM4PlaybackTimingPlugin.Diagnostics;

public sealed class PlaybackDiagnosticsViewModel : INotifyPropertyChanged, ITimelineToolViewModel
{
    private readonly Dictionary<string, string> values = new();
    private Timeline? timeline;
    private WeakReference<object>? player;
    private PreviewMeasurements? measurements;
    private PreviewDiagnosticsService.Subscription? subscription;
    private PreviewMeasurements.Snapshot? frozen;
    private DispatcherTimer? timer;
    private bool attached, measuring, displayBusy;
    private long displayStamp, displayGeneration;
    private IntPtr hwnd;
    private WindowsPreviewDisplay.Snapshot? display;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key] => values.GetValueOrDefault(key, "—");
    public bool HasMonitorNote => this["MonitorNote"] is not ("" or "—");
    public bool HasMessage => this["Message"] is not ("" or "—");
    public bool CanMeasure => PreviewDiagnosticsService.Installed;
    public bool IsMeasuring
    {
        get => measuring;
        set
        {
            if (measuring == value) return;
            if (!value && measurements is not null) frozen = measurements.Read(Stopwatch.GetTimestamp());
            measuring = value;
            if (!value) { PreviewDiagnosticsService.Release(subscription); subscription = null; }
            else if (player?.TryGetTarget(out var target) == true && measurements is not null) Activate(target);
            PropertyChanged?.Invoke(this, new(nameof(IsMeasuring)));
            if (attached) Refresh();
        }
    }
    public void SetTimelineToolInfo(TimelineToolInfo info)
    {
        if (ReferenceEquals(timeline, info?.Timeline)) return;
        timeline = info?.Timeline;
        ChangePlayer(null);
        if (attached) Refresh();
    }
    internal void Attach()
    {
        if (attached) return;
        attached = true;
        timer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick -= Tick; timer.Tick += Tick;
        Refresh(); timer.Start();
    }
    internal void Detach()
    {
        if (!attached) return;
        IsMeasuring = false;
        attached = false;
        timer?.Stop();
        if (timer is not null) timer.Tick -= Tick;
        if (player?.TryGetTarget(out var target) == true) PreviewGeometry.Stop(target);
        displayGeneration++;
    }
    private void Tick(object? sender, EventArgs e) => Refresh();
    private void Activate(object target)
    {
        if (!PreviewDiagnosticsService.Installed || measurements is null || subscription is not null) return;
        measurements.SetEnabled(true, PreviewDiagnosticsConnection.Playing(target), Stopwatch.GetTimestamp());
        subscription = PreviewDiagnosticsService.Select(target, measurements);
        PreviewDiagnosticsService.PlayingChanged(target);
        frozen = null;
    }
    private void ChangePlayer(object? target)
    {
        if (player?.TryGetTarget(out var previous) == true) PreviewGeometry.Stop(previous);
        PreviewDiagnosticsService.Release(subscription); subscription = null;
        player = target is null ? null : new(target);
        measurements = target is null ? null : new(Stopwatch.Frequency);
        frozen = null;
        hwnd = IntPtr.Zero; display = null; displayStamp = 0; displayGeneration++;
        if (measuring && target is not null) Activate(target);
    }
    internal void Refresh()
    {
        try { RefreshCore(); }
        catch (Exception error)
        {
            PreviewDiagnosticsService.Release(subscription); subscription = null;
            values["State"] = "情報を取得できません";
            values["Message"] = "診断情報の取得に失敗しました。";
            foreach (var key in new[] { "Elapsed", "Speed", "DrawFps", "SendFps", "DrawCount", "SendCount", "MaxInterval", "Failures", "Viewport", "ImageSize" }) values[key] = "—";
            display = null; ApplyDisplay();
            Trace.TraceWarning("Preview diagnostics panel: {0}", error);
        }
        Notify();
    }
    private void RefreshCore()
    {
        var now = Stopwatch.GetTimestamp();
        values["PluginVersion"] = typeof(YMM4PlaybackTimingPlugin).Assembly.GetName().Version?.ToString(3) ?? "—";
        values["YmmVersion"] = typeof(Timeline).Assembly.GetName().Version?.ToString() ?? "—";
        values["Timing"] = YMM4PlaybackTimingPlugin.IsTimingActive ? "有効" : "無効";
        values["Scene"] = timeline?.Name ?? "—";
        values["ProjectResolution"] = timeline is null ? "—" : Size(timeline.VideoInfo.Width, timeline.VideoInfo.Height);
        values["ProjectFps"] = timeline is null ? "—" : $"{timeline.VideoInfo.FPS} fps";
        var target = timeline is null ? null : PreviewDiagnosticsService.FindPlayer(timeline);
        var previous = player?.TryGetTarget(out var existing) == true ? existing : null;
        if (!ReferenceEquals(target, previous)) ChangePlayer(target);
        if (target is not null && measurements is not null)
        {
            if (measuring) Activate(target);
            measurements.SetPlaying(PreviewDiagnosticsConnection.Playing(target), now);
            var stats = measuring ? measurements.Read(now) : frozen ?? measurements.Read(now);
            values["State"] = !PreviewDiagnosticsService.Installed ? "計測できません" : !measuring ? "計測停止" : stats.Overflow ? "計測上限を超えました" : !stats.Playing ? "計測中・再生停止中" : stats.SendFps is null ? "データ待ち" : "計測中・再生中";
            values["Elapsed"] = TimeSpan.FromSeconds(stats.Seconds).ToString(@"hh\:mm\:ss\.f", CultureInfo.InvariantCulture);
            values["Speed"] = $"{PreviewDiagnosticsConnection.Rate(target):0.##} %";
            values["DrawFps"] = Number(stats.DrawFps);
            values["SendFps"] = Number(stats.SendFps);
            values["DrawCount"] = stats.Draws.ToString("N0");
            values["SendCount"] = stats.Sends.ToString("N0");
            values["MaxInterval"] = stats.MaxIntervalMs is {} ms ? $"{ms:0.0} ms" : "—";
            values["Failures"] = $"{stats.Failures:N0} 回";
            var connection = PreviewDiagnosticsConnection.Cached.Value!;
            var nextHwnd = (IntPtr)connection.Hwnd.GetValue(target)!;
            if (nextHwnd != hwnd) { hwnd = nextHwnd; display = null; displayStamp = 0; displayGeneration++; }
            var viewport = WindowsPreviewDisplay.ReadViewport(hwnd);
            values["Viewport"] = viewport is {} area ? Size(area.Width, area.Height) : "—";
            var frame = PreviewGeometry.Request(target);
            var image = viewport is {} rect && frame is not null && frame.Width == timeline!.VideoInfo.Width && frame.Height == timeline.VideoInfo.Height
                ? PreviewGeometry.VisibleSize(frame, rect.Width, rect.Height) : null;
            values["ImageSize"] = image is {} imageSize ? Size(imageSize.Width, imageSize.Height) : "—";
            if (viewport is not null && !displayBusy && now - displayStamp >= Stopwatch.Frequency) QueryDisplay(hwnd, displayGeneration);
            if (viewport is null) { display = null; values["State"] = "プレビュー非表示"; }
        }
        else
        {
            foreach (var key in new[] { "Elapsed", "Speed", "DrawFps", "SendFps", "DrawCount", "SendCount", "MaxInterval", "Failures", "Viewport", "ImageSize" }) values[key] = "—";
            values["State"] = PreviewDiagnosticsService.Installed ? "対象なし" : "計測できません";
        }
        values["FpsLabel"] = measuring ? "FPS" : "FPS（最終値）";
        ApplyDisplay();
    }
    private async void QueryDisplay(IntPtr target, long generation)
    {
        displayBusy = true;
        displayStamp = Stopwatch.GetTimestamp();
        try
        {
            var result = await Task.Run(() => WindowsPreviewDisplay.Read(target));
            if (attached && generation == displayGeneration && target == hwnd && WindowsPreviewDisplay.ReadViewport(target) is not null)
            { display = result; ApplyDisplay(); Notify(); }
        }
        catch (Exception error) { Trace.TraceWarning("Preview display query: {0}", error.Message); }
        finally { displayBusy = false; }
    }
    private void ApplyDisplay()
    {
        values["MonitorName"] = display is null ? "—" : display.Name + Environment.NewLine + display.Device;
        values["MonitorResolution"] = display?.Resolution ?? "—";
        values["Refresh"] = display?.Refresh ?? "—";
        values["Mode"] = display?.Mode ?? "—";
        values["Output"] = display?.Output ?? "—";
        values["Scale"] = display?.Scale ?? "—";
        values["MonitorNote"] = display?.Note ?? "";
    }
    internal void Reset()
    {
        measurements?.Reset(Stopwatch.GetTimestamp()); frozen = null;
        values["Message"] = "";
        Refresh();
    }
    internal string Report()
    {
        var result = new StringBuilder("プレビュー再生診断\n");
        result.AppendLine(DateTimeOffset.Now.ToString("O"));
        foreach (var key in new[] { "PluginVersion", "YmmVersion", "Timing", "Scene", "ProjectResolution", "ProjectFps", "State", "Elapsed", "Speed", "DrawFps", "SendFps", "DrawCount", "SendCount", "MaxInterval", "Failures", "Viewport", "ImageSize", "MonitorName", "MonitorResolution", "Refresh", "Mode", "Output", "Scale", "MonitorNote" }) result.AppendLine($"{key}: {this[key]}");
        result.AppendLine("FPS: 直近1秒、累計: 再生中のみ、最大送信間隔: 直近5秒、送信: S_OKの提示要求（実表示FPSではありません）");
        result.AppendLine("DevicePath: " + display?.DevicePath);
        result.AppendLine("DisplayError: " + display?.Error);
        result.AppendLine("ConnectionError: " + (PreviewDiagnosticsService.Failure ?? PreviewDiagnosticsConnection.Failure));
        result.AppendLine("LastPresentError: " + (measurements?.Read(Stopwatch.GetTimestamp()).LastError is {} code ? $"0x{code:X8}" : "—"));
        return result.ToString();
    }
    internal void Message(string message) { values["Message"] = message; Notify(); }
    private void Notify()
    {
        PropertyChanged?.Invoke(this, new("Item[]"));
        PropertyChanged?.Invoke(this, new(nameof(CanMeasure)));
        PropertyChanged?.Invoke(this, new(nameof(HasMonitorNote)));
        PropertyChanged?.Invoke(this, new(nameof(HasMessage)));
    }
    private static string Size(int width, int height) => $"{width:N0} × {height:N0} px";
    private static string Number(double? value) => value is {} number ? number.ToString("0.0") : "—";
}
