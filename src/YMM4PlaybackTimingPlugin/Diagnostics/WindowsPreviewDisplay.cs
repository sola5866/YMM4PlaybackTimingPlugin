using System.Runtime.InteropServices;

namespace YMM4PlaybackTimingPlugin.Diagnostics;

internal static class WindowsPreviewDisplay
{
    internal sealed record Snapshot(string Name, string Device, string Resolution, string Refresh,
        string Mode, string Output, string Scale, string? Note, string? DevicePath, string? Error);
    internal readonly record struct Viewport(int Width, int Height, int Dpi);
    [StructLayout(LayoutKind.Sequential)] private struct Luid { internal uint Low; internal int High; }
    [StructLayout(LayoutKind.Sequential)] private struct Source { internal Luid Adapter; internal uint Id, Mode, Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Target
    { internal Luid Adapter; internal uint Id, Mode, Technology, Rotation, Scaling, RefreshN, RefreshD, Scan; internal int Available; internal uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Path { internal Source Source; internal Target Target; internal uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    { internal int Size; internal Rect Monitor, Work; internal uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Device; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string name, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] Path[] values, ref uint modes, IntPtr data, IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(IntPtr packet);
    private static bool TryRect(IntPtr hwnd, out Rect rect)
    {
        rect = default;
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(GetAncestor(hwnd, 2)) || !GetClientRect(hwnd, out var client)) return false;
        var first = new Point(); var last = new Point { X = client.Right, Y = client.Bottom };
        if (!ClientToScreen(hwnd, ref first) || !ClientToScreen(hwnd, ref last)) return false;
        rect = new() { Left = first.X, Top = first.Y, Right = last.X, Bottom = last.Y };
        return rect.Right > rect.Left && rect.Bottom > rect.Top;
    }
    internal static Viewport? ReadViewport(IntPtr hwnd)
    {
        var previous = SetThreadDpiAwarenessContext(new(-4));
        try { return previous != IntPtr.Zero && TryRect(hwnd, out var rect) ? new(rect.Right - rect.Left, rect.Bottom - rect.Top, (int)GetDpiForWindow(hwnd)) : null; }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }
    internal static Snapshot Read(IntPtr hwnd)
    {
        var previous = SetThreadDpiAwarenessContext(new(-4));
        try { return previous != IntPtr.Zero ? ReadCore(hwnd) : Unknown("dpi-context-unavailable"); }
        catch (Exception error) { return Unknown(error.Message); }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }
    private static Snapshot Unknown(string error) => new("—", "—", "—", "—", "取得できません", "取得できません", "—", "モニター情報を取得できません。", null, error);
    private static Snapshot ReadCore(IntPtr hwnd)
    {
        if (!TryRect(hwnd, out var rect)) return Unknown("preview-not-visible");
        var monitor = MonitorFromRect(ref rect, 0);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return Unknown("monitor-not-found");
        var scale = ReadScale(info.Monitor);
        var matches = new List<Path>();
        foreach (var path in ReadPaths())
        {
            var packet = Packet(1, 84, path.Source.Adapter, path.Source.Id);
            try
            {
                if (DisplayConfigGetDeviceInfo(packet) == 0 && Marshal.PtrToStringUni(packet + 20, 32)!.TrimEnd('\0') == info.Device) matches.Add(path);
            }
            finally { Marshal.FreeHGlobal(packet); }
        }
        if (matches.Count != 1) return new("—", info.Device, $"{info.Monitor.Right - info.Monitor.Left:N0} × {info.Monitor.Bottom - info.Monitor.Top:N0} px",
            "—", "取得できません", "取得できません", scale, matches.Count > 1 ? "複製表示のため、物理モニターを一意に特定できません。" : "表示先の構成を取得できません。", null, "display-path-ambiguous-or-missing");
        var selected = matches[0];
        string name = "—", devicePath = "";
        var request = Packet(2, 420, selected.Target.Adapter, selected.Target.Id);
        try
        {
            if (DisplayConfigGetDeviceInfo(request) == 0)
            { name = Marshal.PtrToStringUni(request + 36, 64)!.TrimEnd('\0'); devicePath = Marshal.PtrToStringUni(request + 164, 128)!.TrimEnd('\0'); }
        }
        finally { Marshal.FreeHGlobal(request); }
        var color = QueryColor(selected, out var colorError);
        var refresh = selected.Target.RefreshD != 0 ? $"{selected.Target.RefreshN / (double)selected.Target.RefreshD:0.##} Hz" : "取得できません";
        var crosses = rect.Left < info.Monitor.Left || rect.Top < info.Monitor.Top || rect.Right > info.Monitor.Right || rect.Bottom > info.Monitor.Bottom;
        var note = crosses ? "複数のモニターにまたがっています。最も広く重なるモニターを表示しています。" : null;
        if (colorError is not null) note = (note is null ? "" : note + " ") + "表示モードを取得できません。";
        return new(name, info.Device, $"{info.Monitor.Right - info.Monitor.Left:N0} × {info.Monitor.Bottom - info.Monitor.Top:N0} px",
            refresh, color.Mode, color.Output, scale, note, devicePath, colorError);
    }
    internal static (string Mode, string Output) FormatColor(int? mode, int encoding, int bits) =>
        (mode switch { 0 => "SDR", 1 => "SDR ACM", 2 => "HDR", _ => "取得できません" },
        encoding switch { 0 => "RGB", 1 => "YCbCr 4:4:4", 2 => "YCbCr 4:2:2", 3 => "YCbCr 4:2:0", 4 => "Intensity", _ => "不明" }
            + (bits > 0 ? $" / {bits} bit" : " / 不明"));
    private static string ReadScale(Rect monitor)
    {
        // GetScaleFactorForMonitor may quantize 150% to 140%. Use the DPI API
        // on our own invisible per-monitor-aware window on the selected output.
        // This also works when YMM4's main window uses another monitor's DPI.
        var probe = CreateWindowEx(0, "STATIC", "", 0x80000000,
            monitor.Left + 1, monitor.Top + 1, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (probe == IntPtr.Zero) return "取得できません";
        try { var dpi = GetDpiForWindow(probe); return dpi > 0 ? $"{dpi / 96d * 100:0.##} %" : "取得できません"; }
        finally { DestroyWindow(probe); }
    }
    private static (string Mode, string Output) QueryColor(Path path, out string? error)
    {
        var packet = Packet(15, 36, path.Target.Adapter, path.Target.Id);
        try
        {
            var result = DisplayConfigGetDeviceInfo(packet);
            if (result == 0) { error = null; return FormatColor(Marshal.ReadInt32(packet, 32), Marshal.ReadInt32(packet, 24), Marshal.ReadInt32(packet, 28)); }
            error = "advanced-color-info-2:" + result;
        }
        finally { Marshal.FreeHGlobal(packet); }
        // The old API can report format/depth, but cannot safely classify SDR ACM vs HDR.
        packet = Packet(9, 32, path.Target.Adapter, path.Target.Id);
        try
        {
            if (DisplayConfigGetDeviceInfo(packet) == 0) return FormatColor(null, Marshal.ReadInt32(packet, 24), Marshal.ReadInt32(packet, 28));
            return ("取得できません", "取得できません");
        }
        finally { Marshal.FreeHGlobal(packet); }
    }
    private static IntPtr Packet(int type, int size, Luid adapter, uint id)
    {
        var pointer = Marshal.AllocHGlobal(size);
        Marshal.Copy(new byte[size], 0, pointer, size);
        Marshal.WriteInt32(pointer, type); Marshal.WriteInt32(pointer, 4, size);
        Marshal.WriteInt32(pointer, 8, unchecked((int)adapter.Low)); Marshal.WriteInt32(pointer, 12, adapter.High);
        Marshal.WriteInt32(pointer, 16, unchecked((int)id));
        return pointer;
    }
    private static Path[] ReadPaths()
    {
        if (Marshal.SizeOf<Path>() != 72) throw new InvalidOperationException("DISPLAYCONFIG_PATH_INFO layout");
        for (var retry = 0; retry < 3; retry++)
        {
            var error = GetDisplayConfigBufferSizes(2, out var count, out var modes);
            if (error != 0) throw new InvalidOperationException("display-buffer:" + error);
            var paths = new Path[count];
            var memory = Marshal.AllocHGlobal(checked((int)modes * 64));
            try
            {
                error = QueryDisplayConfig(2, ref count, paths, ref modes, memory, IntPtr.Zero);
                if (error == 122) continue;
                if (error != 0) throw new InvalidOperationException("display-paths:" + error);
                Array.Resize(ref paths, (int)count);
                return paths;
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        throw new InvalidOperationException("display-topology-unstable");
    }
}
