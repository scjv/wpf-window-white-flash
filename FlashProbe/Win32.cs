using System.Runtime.InteropServices;

namespace FlashProbe;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left, Top, Right, Bottom;
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public override readonly string ToString() => $"{Left},{Top} {Width}x{Height}";

    public static RECT Intersect(RECT a, RECT b)
    {
        int left = Math.Max(a.Left, b.Left), top = Math.Max(a.Top, b.Top);
        return new RECT { Left = left, Top = top, Right = Math.Max(left, Math.Min(a.Right, b.Right)), Bottom = Math.Max(top, Math.Min(a.Bottom, b.Bottom)) };
    }

    /// <summary>True when the centre of <paramref name="inner"/> lies inside this rectangle.</summary>
    public readonly bool ContainsCentreOf(RECT inner)
    {
        int x = (inner.Left + inner.Right) / 2, y = (inner.Top + inner.Bottom) / 2;
        return x >= Left && x < Right && y >= Top && y < Bottom;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X, Y;
}

/// <summary>Window lookups used to follow the target window.</summary>
internal static unsafe class Win32
{
    private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    private const int WS_CAPTION = 0x00C00000, WS_DISABLED = 0x08000000, WS_EX_LAYERED = 0x00080000;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    private static readonly nint DpiAwarenessPerMonitorV2 = -4;

    private delegate bool EnumProc(nint hwnd, nint param);

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref POINT point);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, void* value, int size);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);

    /// <summary>
    /// False when another top-level window lies over the centre of <paramref name="client"/>. That happens when
    /// someone uses the machine during a recording: a window that loses (or never gets) the foreground can end
    /// up behind another one, and then the recorded frames show that window instead.
    /// </summary>
    public static bool IsOnTop(nint hwnd, RECT client)
    {
        var centre = new POINT { X = (client.Left + client.Right) / 2, Y = (client.Top + client.Bottom) / 2 };
        var top = GetAncestor(WindowFromPoint(centre), 2 /* GA_ROOT */);
        if (top == hwnd) return true;
        // WindowFromPoint skips a transparent layered window, so the probe's own backdrop is found behind it.
        GetWindowThreadProcessId(top, out var owner);
        return (GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_LAYERED) != 0 && owner == Environment.ProcessId;
    }

    /// <summary>Physical pixels for every window, whatever DPI awareness the target process uses.</summary>
    public static void UsePhysicalPixels() => SetProcessDpiAwarenessContext(DpiAwarenessPerMonitorV2);

    /// <summary>
    /// First visible, enabled, captioned top-level window of the process. This skips WPF helper windows
    /// such as the hidden, disabled SystemResourceNotifyWindow.
    /// </summary>
    public static nint FindCaptionedWindow(int pid)
    {
        nint found = 0;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            var style = GetWindowLongW(hwnd, GWL_STYLE);
            if (owner != pid || (style & WS_CAPTION) != WS_CAPTION || (style & WS_DISABLED) != 0 || !IsWindowVisible(hwnd))
                return true;
            found = hwnd;
            return false;
        }, 0);
        return found;
    }

    public static bool IsCloaked(nint hwnd)
    {
        int cloaked;
        return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    /// <summary>The visible frame (without the shadow) and the client area, in screen pixels.</summary>
    public static bool TryGetGeometry(nint hwnd, out RECT frame, out RECT client)
    {
        frame = default;
        client = default;
        RECT bounds;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &bounds, sizeof(RECT)) != 0 || !GetClientRect(hwnd, out var size))
            return false;
        var origin = new POINT();
        if (!ClientToScreen(hwnd, ref origin)) return false;
        frame = bounds;
        client = new RECT { Left = origin.X, Top = origin.Y, Right = origin.X + size.Width, Bottom = origin.Y + size.Height };
        return bounds.Width > 0 && bounds.Height > 0;
    }

    public static string Describe(nint hwnd)
    {
        TryGetGeometry(hwnd, out var frame, out _);
        return $"visible={IsWindowVisible(hwnd)} iconic={IsIconic(hwnd)} cloaked={IsCloaked(hwnd)} frame={frame} " +
            $"style=0x{GetWindowLongW(hwnd, GWL_STYLE):X8} exstyle=0x{GetWindowLongW(hwnd, GWL_EXSTYLE):X8}";
    }
}
