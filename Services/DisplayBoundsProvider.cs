using System.Runtime.InteropServices;

namespace PokeQuad.Services;

public static class DisplayBoundsProvider
{
    public static IReadOnlyList<DisplayBounds> GetDisplays()
    {
        var displays = new List<DisplayBounds>();
        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                displays.Add(new DisplayBounds(
                    info.WorkArea.Left,
                    info.WorkArea.Top,
                    info.WorkArea.Right - info.WorkArea.Left,
                    info.WorkArea.Bottom - info.WorkArea.Top));
            }
            return true;
        }, IntPtr.Zero);

        return displays.Count > 0 ? displays : [new DisplayBounds(0, 0, 1920, 1080)];
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, IntPtr monitorRect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr deviceContext,
        IntPtr clipRect,
        MonitorEnumProc callback,
        IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public int Flags;
    }
}
