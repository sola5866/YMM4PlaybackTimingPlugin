using System.Diagnostics;
using YukkuriMovieMaker.Project;

namespace YMM4PlaybackTimingPlugin.Diagnostics;

internal static class PreviewDiagnosticsService
{
    internal sealed class Subscription(object player, PreviewMeasurements measurements)
    {
        internal readonly WeakReference<object> Player = new(player);
        internal readonly PreviewMeasurements Measurements = measurements;
        internal WeakReference<object>? Source;
        internal bool Matches(object player) => Player.TryGetTarget(out var target) && ReferenceEquals(target, player);
    }
    private static readonly object gate = new();
    private static readonly List<WeakReference<object>> players = [];
    private static Subscription? subscription;
    internal static bool Installed { get; set; }
    internal static string? Failure { get; set; }
    internal static void ObservePlayer(object player)
    {
        try
        {
            lock (gate)
            {
                players.RemoveAll(x => !x.TryGetTarget(out _));
                if (!players.Any(x => x.TryGetTarget(out var target) && ReferenceEquals(target, player))) players.Add(new(player));
            }
        }
        catch (Exception error) { Trace.TraceWarning("Preview diagnostics discovery: {0}", error.Message); }
    }
    // Called on the UI thread. Do not retain a player or host across a panel unload.
    internal static object? FindPlayer(Timeline timeline)
    {
        var contract = PreviewDiagnosticsConnection.Cached.Value;
        if (contract is null) return null;
        object[] snapshot;
        lock (gate) snapshot = players.Select(x => x.TryGetTarget(out var player) ? player : null).OfType<object>().ToArray();
        var matches = snapshot.Where(player => ReferenceEquals(PreviewDiagnosticsConnection.Timeline(player), timeline)
            && (int)contract.Disposing.GetValue(player)! == 0 && (bool)contract.Editable.GetValue(player)!
            && contract.Host.GetValue(player) is System.Windows.Forms.Integration.WindowsFormsHost { IsVisible: true }).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    internal static Subscription Select(object player, PreviewMeasurements measurements)
    {
        var value = new Subscription(player, measurements);
        Volatile.Write(ref subscription, value);
        return value;
    }
    internal static void Release(Subscription? value)
    {
        if (value is not null && ReferenceEquals(Interlocked.CompareExchange(ref subscription, null, value), value))
            value.Measurements.SetEnabled(false, false, Stopwatch.GetTimestamp());
    }
    internal static (PreviewMeasurements Model, PreviewMeasurements.Ticket Ticket)? Capture(object player)
    {
        var value = Volatile.Read(ref subscription);
        if (value is null || !value.Matches(player)) return null;
        var now = Stopwatch.GetTimestamp();
        value.Measurements.SetPlaying(PreviewDiagnosticsConnection.Playing(player), now);
        var source = PreviewDiagnosticsConnection.Source(player);
        if (source is not null && (value.Source is null || !value.Source.TryGetTarget(out var previous) || !ReferenceEquals(source, previous)))
        { value.Source = new(source); value.Measurements.Break(now); }
        var ticket = value.Measurements.Capture();
        return ticket is {} token ? (value.Measurements, token) : null;
    }
    internal static void PlayingChanged(object player)
    {
        var value = Volatile.Read(ref subscription);
        var now = Stopwatch.GetTimestamp();
        if (value?.Matches(player) == true) value.Measurements.SetPlaying(PreviewDiagnosticsConnection.Playing(player), now);
    }
    internal static void Break(object player)
    {
        var value = Volatile.Read(ref subscription);
        if (value?.Matches(player) == true) value.Measurements.Break(Stopwatch.GetTimestamp());
    }
    internal static void Stop()
    {
        Installed = false;
        Release(Volatile.Read(ref subscription));
    }
}
