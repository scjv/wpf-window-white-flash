using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WinRT.Interop;

namespace WinUiFlashLab;

// The WinUI 3 counterpart of FlashLab: a dark window with switchable startup variants. FlashProbe starts it
// and records every frame DWM composes. The mode is a list of flags joined by '+', for example
// "cloak=rendered+onactivate"; the flags are listed in ../README.md. Each step is written to stdout as
// "EVT <Stopwatch.GetTimestamp()> <name>" so that FlashProbe can place it on its frame timeline.
internal static class Program
{
    private const int WM_ACTIVATE = 0x0006, WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_SHOWWINDOW = 0x0018;
    private const int WM_WINDOWPOSCHANGED = 0x0047, WM_NCPAINT = 0x0085, WM_NCACTIVATE = 0x0086;
    private const int SWP_SHOWWINDOW = 0x0040;
    private const int GWL_EXSTYLE = -20, WS_EX_LAYERED = 0x00080000, LWA_ALPHA = 0x2;
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3, DWMWA_CLOAK = 13, DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private delegate nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data);

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLongW(nint hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);

    private static readonly Dictionary<uint, int> SeenMessages = [];
    private static readonly SubclassProc Subclass = OnMessage;
    private static string[] _flags = [];
    private static string? _cloak;
    private static bool _cloaked;

    private static bool Has(string flag) => _flags.Contains(flag);

    // light: the app in the light theme; systheme: the app follows the Windows app mode.
    internal static ApplicationTheme? Theme => Has("systheme") ? null : Has("light") ? ApplicationTheme.Light : ApplicationTheme.Dark;

    [STAThread]
    private static void Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "baseline";
        _flags = mode.Split('+');
        // cloak=<when>: cloak the window right after it is created, before its first show (with onactivate: on
        // its first activation), and uncloak it at <when>.
        _cloak = _flags.FirstOrDefault(f => f.StartsWith("cloak=", StringComparison.Ordinal))?["cloak=".Length..];
        Log("Main " + mode);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callback =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }

    internal static void Launch()
    {
        Log("Launched");
        var window = new MainWindow();
        var hwnd = WindowNative.GetWindowHandle(window);
        Log("WindowCreated");
        window.Closed += (_, _) => Application.Current.Exit();

        // 720 x 440 DIP in the middle of the primary monitor, like the WPF repro. The window is created on the
        // primary monitor, so its DPI is that monitor's.
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var area = DisplayArea.Primary.WorkArea;
        int width = (int)(720 * scale), height = (int)(440 * scale);
        window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        // WPF sets the dark title bar itself; WinUI leaves it light unless asked. The light theme gets the
        // light Windows 11 background, #F3F3F3.
        var dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        Log(dark ? "ThemeDark" : "ThemeLight");
        SetAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, dark ? 1 : 0);
        if (!dark)
        {
            window.Root.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3));
            window.Label.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black);
            window.Label.Text = "Light window";
        }
        SetWindowSubclass(hwnd, Subclass, 1, 0);

        if (Has("mica"))
        {
            window.SystemBackdrop = new MicaBackdrop();
            window.Root.Background = null;
        }
        if (Has("heavy")) AddHeavyContent(window.Heavy);
        if (Has("nofade")) SetAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, 1);
        if (Has("layered"))
        {
            // The workaround from microsoft-ui-xaml#10259: transparent until the first Rendering tick.
            SetWindowLongW(hwnd, GWL_EXSTYLE, GetWindowLongW(hwnd, GWL_EXSTYLE) | WS_EX_LAYERED);
            SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);
            Log("LayeredTransparent");
        }
        if (_cloak is not null && !Has("onactivate"))
        {
            SetCloaked(hwnd, true);
            Log("Cloaked");
        }

        var revealed = false;
        void Reveal()
        {
            if (revealed || _cloak is null) return;
            revealed = true;
            SetCloaked(hwnd, false);
            Log("Uncloaked");
        }

        var queue = window.DispatcherQueue;
        void After(int milliseconds, Action action)
        {
            var timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => action();
            timer.Start();
        }

        window.Root.Loaded += (_, _) =>
        {
            Log("Loaded");
            if (_cloak == "loaded") Reveal();
            if (_cloak == "late") After(300, Reveal);
        };

        var ticks = 0;
        EventHandler<object>? onRendering = null;
        onRendering = (_, _) =>
        {
            if (++ticks > 3)
            {
                CompositionTarget.Rendering -= onRendering;
                return;
            }
            Log($"Rendering {ticks}");
            if (ticks > 1) return;
            if (Has("layered"))
            {
                SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
                Log("LayeredOpaque");
            }
            if (_cloak == "rendering") Reveal();
        };
        CompositionTarget.Rendering += onRendering;

        var frames = 0;
        EventHandler<RenderedEventArgs>? onRendered = null;
        onRendered = async (_, _) =>
        {
            if (++frames > 3)
            {
                CompositionTarget.Rendered -= onRendered;
                return;
            }
            Log($"Rendered {frames}");
            if (frames > 1) return;
            Log(GetForegroundWindow() == hwnd ? "Foreground" : "NotForeground");
            switch (_cloak)
            {
                case "rendered":
                    Reveal();
                    break;
                case "commit" or "commitflush":
                    // Wait until the compositor has committed the frame XAML just rendered.
                    await window.Compositor.RequestCommitAsync();
                    Log("CommitCompleted");
                    if (_cloak == "commitflush")
                    {
                        DwmFlush();
                        Log("DwmFlushed");
                    }
                    Reveal();
                    break;
            }
        };
        CompositionTarget.Rendered += onRendered;

        // fix: the helper this lab led to.
        if (Has("fix"))
        {
            WinUiFirstFrameCloak.Attach(window);
            Log("CloakAttached");
        }

        void Show()
        {
            if (Has("noactivate"))
            {
                // Show without activation, as when a background process opens a window.
                Log("Show");
                window.AppWindow.Show(false);
                Log("Shown");
            }
            else
            {
                Log("Activate");
                window.Activate();
                Log("Activated");
            }
        }

        // post: the workaround from microsoft-ui-xaml#7892, show the window once the dispatcher is idle.
        if (Has("post")) queue.TryEnqueue(DispatcherQueuePriority.Low, Show);
        else Show();
    }

    // Thousands of stroked ellipses: a first frame that takes long to lay out and render.
    private static void AddHeavyContent(Microsoft.UI.Xaml.Controls.Canvas canvas)
    {
        var random = new Random(1);
        var stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x30, 0x30, 0x30));
        for (var i = 0; i < 6000; i++)
        {
            var size = 4 + random.NextDouble() * 8;
            var ellipse = new Ellipse { Width = size, Height = size, Stroke = stroke, StrokeThickness = 0.7 };
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(ellipse, random.NextDouble() * 720);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(ellipse, random.NextDouble() * 400);
            canvas.Children.Add(ellipse);
        }
    }

    private static nint OnMessage(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        // onactivate: cloak on the first activation after the window became visible, like FirstFrameCloak.
        if (Has("onactivate") && _cloak is not null && !_cloaked && msg is WM_NCACTIVATE or WM_ACTIVATE && wParam != 0 && IsWindowVisible(hwnd))
        {
            SetCloaked(hwnd, true);
            Log("CloakedOnActivate");
        }
        switch (msg)
        {
            case WM_ACTIVATE:
                Log((wParam & 0xFFFF) == 0 ? "WM_ACTIVATE inactive" : "WM_ACTIVATE active");
                break;
            case WM_NCACTIVATE:
                Log(wParam == 0 ? "WM_NCACTIVATE inactive" : "WM_NCACTIVATE active");
                break;
            case WM_WINDOWPOSCHANGED when (Marshal.ReadInt32(lParam, 2 * IntPtr.Size + 16) & SWP_SHOWWINDOW) != 0:
                Log("WindowPosShow");
                break;
            case WM_PAINT or WM_ERASEBKGND or WM_SHOWWINDOW or WM_NCPAINT:
                var seen = SeenMessages[msg] = SeenMessages.GetValueOrDefault(msg) + 1;
                if (seen <= 2)
                    Log(msg switch { WM_PAINT => "WM_PAINT", WM_ERASEBKGND => "WM_ERASEBKGND", WM_SHOWWINDOW => "WM_SHOWWINDOW", _ => "WM_NCPAINT" });
                break;
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private static void SetCloaked(nint hwnd, bool cloaked)
    {
        _cloaked = cloaked || _cloaked;
        SetAttribute(hwnd, DWMWA_CLOAK, cloaked ? 1 : 0);
    }

    private static void SetAttribute(nint hwnd, int attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    private static void Log(string name) => Console.WriteLine($"EVT {Stopwatch.GetTimestamp()} {name}");
}
