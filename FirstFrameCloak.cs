using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DarkStartupMinimal;

/// <summary>
/// Keeps DWM from showing a new window before WPF has presented its first frame.
/// Until WPF presents that frame, DWM composes the window from an unpainted, white surface, which shows as a
/// light flash (softened by the Windows 11 open animation) before the dark content appears. A cloaked window
/// stays visible to Win32 and WPF keeps rendering into it; DWM just does not draw it.
/// </summary>
internal sealed class FirstFrameCloak
{
    private const int WM_ACTIVATE = 0x0006, WM_NCACTIVATE = 0x0086;
    private const int DWMWA_CLOAK = 13;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    // MediaContext.CompleteRender is internal: "a sync flush, which will only return after the last frame is
    // presented". WPF calls it itself to keep window resizing smooth. If a future WPF drops it, the window is
    // uncloaked on ContentRendered alone, when the frame has been committed and almost always presented.
    private static readonly Action<Dispatcher>? CompleteRender = FindCompleteRender();

    private readonly Window _window;
    private readonly HwndSourceHook _hook;
    private HwndSource? _source;
    private bool _cloaked;

    private FirstFrameCloak(Window window)
    {
        _window = window;
        _hook = OnMessage;
        window.SourceInitialized += OnSourceInitialized;
        window.ContentRendered += OnContentRendered;
    }

    /// <summary>
    /// Cloaks the window when it is activated as it opens, and uncloaks it once its first frame is presented.
    /// Activation happens inside ShowWindow, just after the window became visible and before DWM draws it; an
    /// activated window then needs about 40 ms more than an inactive one to render its first frame. A window
    /// that opens without activation renders its first frame before DWM draws it and is left alone: cloaking
    /// it would make DWM show the white surface right after uncloaking. Call before the window is shown.
    /// </summary>
    internal static void Attach(Window window) => _ = new FirstFrameCloak(window);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _window.SourceInitialized -= OnSourceInitialized;
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        _source.AddHook(_hook);
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_cloaked && msg is WM_NCACTIVATE or WM_ACTIVATE && wParam != IntPtr.Zero && IsWindowVisible(hwnd))
        {
            _cloaked = true;
            SetCloaked(hwnd, true);
        }
        return IntPtr.Zero;
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        _window.ContentRendered -= OnContentRendered;
        _source?.RemoveHook(_hook);
        if (!_cloaked) return;
        // ContentRendered means the UI thread has committed the first frame; wait until it is on screen.
        try { CompleteRender?.Invoke(_window.Dispatcher); }
        catch (TargetInvocationException) { }
        SetCloaked(new WindowInteropHelper(_window).Handle, false);
    }

    private static void SetCloaked(IntPtr hwnd, bool cloaked)
    {
        var value = cloaked ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref value, sizeof(int));
    }

    private static Action<Dispatcher>? FindCompleteRender()
    {
        var type = typeof(Visual).Assembly.GetType("System.Windows.Media.MediaContext");
        var from = type?.GetMethod("From", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Dispatcher)]);
        var complete = type?.GetMethod("CompleteRender", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes);
        if (from is null || complete is null) return null;
        return dispatcher => complete.Invoke(from.Invoke(null, [dispatcher]), null);
    }
}
