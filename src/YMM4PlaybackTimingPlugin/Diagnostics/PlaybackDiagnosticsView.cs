using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using YukkuriMovieMaker.Controls;

namespace YMM4PlaybackTimingPlugin.Diagnostics;

public sealed class PlaybackDiagnosticsView : UserControl
{
    private PlaybackDiagnosticsViewModel? attached;
    public PlaybackDiagnosticsView()
    {
        MinWidth = 280;
        var panel = new StackPanel { Margin = new Thickness(6) };
        var general = Section(panel, "全般");
        Row(general, "プラグインのバージョン", "PluginVersion");
        Row(general, "YMM4のバージョン", "YmmVersion");
        Row(general, "タイミング調整", "Timing");
        var project = Section(panel, "プロジェクト");
        Row(project, "対象シーン", "Scene");
        Row(project, "解像度", "ProjectResolution", "プレビュー対象シーンの設定値です。書き出し時の解像度とは区別します。");
        Row(project, "フレームレート", "ProjectFps", "対象シーンの設定値です。実測FPSではありません。");
        var preview = Section(panel, "プレビュー");
        var toggleRow = NewRow(preview, "計測");
        var toggle = new ToggleSlider { Name = "DiagnosticsMeasure", Width = 80, HorizontalAlignment = HorizontalAlignment.Right };
        toggle.SetBinding(ToggleSlider.ValueProperty, new Binding(nameof(PlaybackDiagnosticsViewModel.IsMeasuring)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        toggle.SetBinding(IsEnabledProperty, new Binding(nameof(PlaybackDiagnosticsViewModel.CanMeasure)));
        AutomationProperties.SetName(toggle, "計測");
        Grid.SetColumn(toggle, 1); toggleRow.Children.Add(toggle);
        Row(preview, "状態", "State");
        Row(preview, "計測した再生時間", "Elapsed", "計測開始またはリセット以降、再生中のみの経過時間です。");
        Row(preview, "再生速度", "Speed");
        Metrics(preview);
        Row(preview, "最大送信間隔", "MaxInterval", "直近5秒の最大値です。停止・シーク・再生速度変更をまたぐ間隔は含みません。");
        Row(preview, "送信失敗数", "Failures", "再生中に提示要求が失敗した回数です。非表示状態の通知は含みません。");
        Row(preview, "プレビュー領域サイズ", "Viewport", "画面上の横×縦のピクセル数です。余白を含み、枠やツールバーを含みません。");
        Row(preview, "映像部分のサイズ", "ImageSize", "余白を除いた、領域内に収まる映像部分の横×縦です。ズームとパンを反映します。他のウィンドウによる遮蔽は差し引きません。");
        var monitor = Section(panel, "モニター");
        Row(monitor, "名前・識別名", "MonitorName");
        Row(monitor, "解像度", "MonitorResolution");
        Row(monitor, "リフレッシュレート", "Refresh", "Windowsの設定値です。VRR動作中の瞬間的な実測値ではありません。");
        Row(monitor, "表示モード", "Mode", "Windowsの有効モードです。SDR ACMはSDRで自動カラー管理が有効な状態です。");
        Row(monitor, "出力形式", "Output", "Windowsが報告する色形式と1色成分あたりのビット数です。素材・描画バッファ・パネル自体のビット深度とは区別します。");
        Row(monitor, "Windowsの拡大率", "Scale", "表示先モニターの拡大率です。YMM4のUI拡大率やプレビューのズームとは区別します。");
        var note = Value("MonitorNote");
        note.SetBinding(VisibilityProperty, new Binding(nameof(PlaybackDiagnosticsViewModel.HasMonitorNote)) { Converter = new BooleanToVisibilityConverter() });
        monitor.Children.Add(note);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        var reset = new Button { Name = "DiagnosticsReset", Content = "リセット", MinWidth = 76, Margin = new Thickness(0, 0, 8, 0), ToolTip = "累計と計測時間を0に戻します。再生位置は変更しません。" };
        reset.Click += (_, _) => Model?.Reset();
        var copy = new Button { Name = "DiagnosticsCopy", Content = "結果をコピー", MinWidth = 96, ToolTip = "計測結果と、デバイスパス・エラーコードなどの診断情報をコピーします。" };
        copy.Click += (_, _) =>
        {
            try { if (Model is {} model) { Clipboard.SetText(model.Report()); model.Message("結果をコピーしました。"); } }
            catch (System.Runtime.InteropServices.ExternalException) { Model?.Message("コピーできませんでした。もう一度お試しください。"); }
        };
        buttons.Children.Add(reset); buttons.Children.Add(copy); panel.Children.Add(buttons);
        var message = Value("Message"); message.HorizontalAlignment = HorizontalAlignment.Right;
        message.SetBinding(VisibilityProperty, new Binding(nameof(PlaybackDiagnosticsViewModel.HasMessage)) { Converter = new BooleanToVisibilityConverter() });
        panel.Children.Add(message);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = panel };
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => { attached?.Detach(); attached = null; };
        DataContextChanged += (_, _) => { attached?.Detach(); attached = null; if (IsLoaded) Attach(); };
    }
    private PlaybackDiagnosticsViewModel? Model => DataContext as PlaybackDiagnosticsViewModel;
    private void Attach() { attached = Model; attached?.Attach(); }
    private static StackPanel Section(Panel parent, string title)
    {
        var content = new StackPanel { Margin = new Thickness(6, 4, 0, 8) };
        parent.Children.Add(new Expander { Header = title, IsExpanded = true, Content = content });
        return content;
    }
    private static Grid NewRow(Panel parent, string label, string? tooltip = null)
    {
        var grid = new Grid { MinHeight = 26 };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1.05, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = label, ToolTip = tooltip, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        parent.Children.Add(grid);
        return grid;
    }
    private static TextBlock Value(string key, string? tooltip = null)
    {
        var value = new TextBlock { Name = "Diagnostics" + key, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, ToolTip = tooltip };
        value.SetBinding(TextBlock.TextProperty, new Binding("[" + key + "]"));
        AutomationProperties.SetName(value, key);
        return value;
    }
    private static void Row(Panel parent, string label, string key, string? tooltip = null)
    {
        var grid = NewRow(parent, label, tooltip);
        var value = Value(key, tooltip);
        AutomationProperties.SetName(value, label);
        Grid.SetColumn(value, 1); grid.Children.Add(value);
    }
    private static void Metrics(Panel parent)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 6) };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1.6, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++) grid.RowDefinitions.Add(new() { Height = GridLength.Auto, MinHeight = 26 });
        void Add(FrameworkElement element, int row, int column) { Grid.SetRow(element, row); Grid.SetColumn(element, column); grid.Children.Add(element); }
        Add(new TextBlock { Text = "描画", TextAlignment = TextAlignment.Right, ToolTip = "描画処理が例外なく完了し、EndDrawが成功した回数です。" }, 0, 1);
        Add(new TextBlock { Text = "送信", TextAlignment = TextAlignment.Right, ToolTip = "提示要求が正常に受理された回数です。モニターの実表示FPSを測定するものではありません。" }, 0, 2);
        var fps = Value("FpsLabel", "直近1秒の実時間から算出します。累計は再生中のみです。"); fps.TextAlignment = TextAlignment.Left;
        Add(fps, 1, 0); Add(Value("DrawFps"), 1, 1); Add(Value("SendFps"), 1, 2);
        Add(new TextBlock { Text = "累計フレーム数", TextWrapping = TextWrapping.Wrap }, 2, 0);
        Add(Value("DrawCount"), 2, 1); Add(Value("SendCount"), 2, 2);
        parent.Children.Add(grid);
    }
}
