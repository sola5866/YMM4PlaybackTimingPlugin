using System.Reflection;
using System.Windows.Forms.Integration;
using YMM4PlaybackTimingPlugin.Integration;
using YukkuriMovieMaker.Project;

namespace YMM4PlaybackTimingPlugin.Diagnostics;

internal static class PreviewDiagnosticsConnection
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal sealed record Contract(MethodInfo Present, MethodInfo Seek, FieldInfo Hwnd, FieldInfo Host,
        FieldInfo Editable, FieldInfo Disposing);
    internal static readonly Lazy<Contract?> Cached = new(Resolve);
    internal static string? Failure { get; private set; }
    private static Contract? Resolve()
    {
        try
        {
            var native = PreviewNativePlaybackContract.Cached.Value ?? throw new NotSupportedException("再生接続がありません。");
            var type = native.Video;
            FieldInfo Field(string name, Type expected)
            {
                var field = type.GetField(name, Fields);
                if (field is null || field.IsStatic || field.FieldType != expected) throw new NotSupportedException(name);
                return field;
            }
            var present = type.GetMethod("Present", Fields, null, Type.EmptyTypes, null)!;
            var seek = type.GetMethod("SeekAsync", Fields, null, [typeof(TimeSpan)], null)!;
            if (present is null || present.ReturnType != typeof(void) || present.IsVirtual || seek is null)
                throw new NotSupportedException("Present/SeekAsyncの構造が異なります。");
            return new(present, seek, Field("targetPtr", typeof(IntPtr)), Field("host", typeof(WindowsFormsHost)),
                Field("isEditable", typeof(bool)), Field("disposeStarted", typeof(int)));
        }
        catch (Exception error) { Failure = error.Message; return null; }
    }
    internal static Timeline? Timeline(object player) => PreviewNativePlaybackContract.Cached.Value?.Timeline.GetValue(player) as Timeline;
    internal static bool Playing(object player) => (bool?)PreviewNativePlaybackContract.Cached.Value?.Playing.GetValue(player) == true;
    internal static double Rate(object player) => (double?)PreviewNativePlaybackContract.Cached.Value?.Rate.GetValue(player) ?? 100;
    internal static object? Source(object player) => PreviewNativePlaybackContract.Cached.Value?.TimelineSource.GetValue(player);
}
