using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace WinUiFlashLab;

/// <summary>
/// Keeps DWM from showing a new WinUI 3 window before XAML has presented its first frame.
/// Until then, DWM composes the window's client area from an empty surface (activated or not), black in the
/// app's dark theme and white in the light one, which the Windows 11 open animation fades in before the content
/// appears. A cloaked window stays visible to Win32 and XAML keeps rendering into it; DWM just does not draw it.
/// </summary>
internal sealed class WinUiFirstFrameCloak
{
    private const int DWMWA_CLOAK = 13;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    private readonly Window _window;
    private readonly nint _hwnd;

    private WinUiFirstFrameCloak(Window window)
    {
        _window = window;
        _hwnd = WindowNative.GetWindowHandle(window);
        SetCloaked(true);
        if (window.Content is FrameworkElement { IsLoaded: false } content)
            content.Loaded += OnLoaded;
        else
            CompositionTarget.Rendered += OnRendered;
    }

    /// <summary>
    /// Cloaks the window now and uncloaks it once the first frame XAML renders while the window is visible has
    /// been committed. Call it after the window is created and its content is set, before
    /// <see cref="Window.Activate"/> or <c>AppWindow.Show</c>. Works with a deferred show as well.
    /// </summary>
    internal static void Attach(Window window) => _ = new WinUiFirstFrameCloak(window);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ((FrameworkElement)sender).Loaded -= OnLoaded;
        // Rendered is raised for every XAML frame of this thread; the next one after Loaded holds this window.
        CompositionTarget.Rendered += OnRendered;
    }

    private async void OnRendered(object? sender, RenderedEventArgs e)
    {
        // A frame rendered before the window is shown does not count: when it is shown, DWM starts again from
        // an empty surface, and since the cloak took the open animation away, that surface would be seen.
        // XAML renders again as soon as the window is shown.
        if (!IsWindowVisible(_hwnd)) return;
        CompositionTarget.Rendered -= OnRendered;
        // XAML has rendered the frame; wait until the compositor has committed it.
        try { await _window.Compositor.RequestCommitAsync(); }
        catch (COMException) { }
        SetCloaked(false);
    }

    private void SetCloaked(bool cloaked)
    {
        var value = cloaked ? 1 : 0;
        DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref value, sizeof(int));
    }
}
