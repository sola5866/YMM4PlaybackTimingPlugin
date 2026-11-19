using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vortice.Direct2D1;

namespace YMM4PlaybackTimingPlugin.Diagnostics;

internal static class PreviewGeometry
{
    internal sealed record Frame(int Width, int Height, Matrix3x2 Transform, double DpiX, double DpiY,
        int BufferWidth, int BufferHeight);
    private sealed class Observation { internal int Active; internal Frame? Value; }
    private static readonly ConditionalWeakTable<object, Observation> observations = new();
    internal static Frame? Request(object player)
    {
        var observation = observations.GetOrCreateValue(player);
        Volatile.Write(ref observation.Active, 1);
        return Volatile.Read(ref observation.Value);
    }
    internal static void Stop(object player)
    { if (observations.TryGetValue(player, out var observation)) Volatile.Write(ref observation.Active, 0); }
    // Runs on the native render thread, immediately before the first DrawImage.
    // UI code never reads a live Direct2D context.
    public static void Capture(object player)
    {
        if (!observations.TryGetValue(player, out var observation) || Volatile.Read(ref observation.Active) == 0) return;
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var contextField = player.GetType().GetField("devicesAndContext", flags)!;
            var contextOwner = contextField.GetValue(player)!;
            var context = (ID2D1DeviceContext)contextField.FieldType.GetProperty("DeviceContext")!.GetValue(contextOwner)!;
            var timeline = PreviewDiagnosticsConnection.Timeline(player)!;
            var dpi = context.Dpi;
            var size = context.PixelSize;
            Volatile.Write(ref observation.Value, new(timeline.VideoInfo.Width, timeline.VideoInfo.Height,
                context.Transform, dpi.Width, dpi.Height, size.Width, size.Height));
        }
        catch (Exception) { Volatile.Write(ref observation.Value, null); }
    }
    internal static (int Width, int Height)? VisibleSize(Frame frame, int viewportWidth, int viewportHeight)
    {
        if (frame.BufferWidth <= 0 || frame.BufferHeight <= 0 || viewportWidth <= 0 || viewportHeight <= 0
            || frame.DpiX <= 0 || frame.DpiY <= 0) return null;
        Vector2[] points = [new(0, 0), new(frame.Width, 0), new(0, frame.Height), new(frame.Width, frame.Height)];
        for (var i = 0; i < points.Length; i++)
        {
            var point = Vector2.Transform(points[i], frame.Transform);
            points[i] = new((float)(point.X * frame.DpiX / 96 * viewportWidth / frame.BufferWidth),
                (float)(point.Y * frame.DpiY / 96 * viewportHeight / frame.BufferHeight));
            if (!float.IsFinite(points[i].X) || !float.IsFinite(points[i].Y)) return null;
        }
        var left = Math.Clamp(points.Min(p => p.X), 0, viewportWidth);
        var right = Math.Clamp(points.Max(p => p.X), 0, viewportWidth);
        var top = Math.Clamp(points.Min(p => p.Y), 0, viewportHeight);
        var bottom = Math.Clamp(points.Max(p => p.Y), 0, viewportHeight);
        return ((int)Math.Ceiling(right) - (int)Math.Floor(left), (int)Math.Ceiling(bottom) - (int)Math.Floor(top));
    }
}
