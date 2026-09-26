using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

#pragma warning disable WPF0001 // ThemeMode is experimental in .NET 10.

namespace FlashLab;

// The DarkStartupMinimal window with switchable startup variants. FlashProbe starts it and records every
// frame DWM composes. The mode is a list of flags joined by '+', for example "cloak=cr" or
// "software+cloak=frames+inval"; the flags are listed in ../README.md. Each step is written to stdout as
// "EVT <Stopwatch.GetTimestamp()> <name>" so that FlashProbe can place it on its frame timeline.
internal static class Program
{
    private const int WM_ACTIVATE = 0x0006, WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_SHOWWINDOW = 0x0018;
    private const int WM_WINDOWPOSCHANGED = 0x0047, WM_NCPAINT = 0x0085, WM_NCACTIVATE = 0x0086;
    private const int SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    private const int SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040;
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3, DWMWA_CLOAK = 13;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, int flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int colorref);

    private static readonly IntPtr DarkBrush = CreateSolidBrush(0x202020);
    private static readonly Dictionary<int, int> SeenMessages = [];
    private static string[] _flags = [];

    private static bool Has(string flag) => _flags.Contains(flag);

    [STAThread]
    private static void Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "baseline";
        _flags = mode.Split('+');
        // cloak=<when>: hide the window before its first ShowWindow (with onshow/onactivate: once it is shown or
        // activated) and reveal it at <when>.
        var cloak = _flags.FirstOrDefault(f => f.StartsWith("cloak=", StringComparison.Ordinal))?["cloak=".Length..];
        Log("Main " + mode);

        if (Has("mica")) AppContext.SetSwitch("Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop", false);
        if (Has("software")) RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        if (!Has("plain")) application.ThemeMode = ThemeMode.Dark;

        var window = new MainWindow();
        if (Has("plain"))
        {
            window.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
            window.Foreground = Brushes.White;
        }
        if (Has("heavy")) window.Heavy.Visibility = Visibility.Visible;
        // noactivate: show without activation, as when a background process opens a window.
        if (Has("noactivate")) window.ShowActivated = false;
        // noime: no Text Services Framework for this window (what does activation spend its time on?).
        if (Has("noime")) System.Windows.Input.InputMethod.SetIsInputMethodEnabled(window, false);

        IntPtr hwnd = IntPtr.Zero;
        var revealed = false;
        var loaded = false;

        void Reveal()
        {
            if (revealed) return;
            revealed = true;
            if (Has("rgn"))
            {
                SetWindowRgn(hwnd, IntPtr.Zero, true);
                Log("RegionReset");
            }
            else
            {
                SetAttribute(hwnd, DWMWA_CLOAK, 0);
                Log("Uncloaked");
            }
            if (Has("inval"))
            {
                // WM_PAINT makes HwndTarget invalidate the render target, so WPF renders and presents again.
                InvalidateRect(hwnd, IntPtr.Zero, false);
                Log("Invalidated");
            }
            if (Has("rm"))
            {
                // The public RenderMode switch recreates the render target.
                var target = HwndSource.FromHwnd(hwnd).CompositionTarget;
                target.RenderMode = RenderMode.SoftwareOnly;
                target.RenderMode = RenderMode.Default;
                Log("RenderModeToggled");
            }
            if (Has("framechanged"))
            {
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                Log("FrameChanged");
            }
        }

        void AfterRenderingTicks(int count, Action action)
        {
            var ticks = 0;
            EventHandler? onRendering = null;
            onRendering = (_, _) =>
            {
                Log($"Rendering {++ticks}");
                if (ticks < count) return;
                CompositionTarget.Rendering -= onRendering;
                action();
            };
            CompositionTarget.Rendering += onRendering;
        }

        void After(int milliseconds, Action action)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                action();
            };
            timer.Start();
        }

        window.SourceInitialized += (_, _) =>
        {
            Log("SourceInitialized");
            hwnd = new WindowInteropHelper(window).Handle;
            var source = HwndSource.FromHwnd(hwnd);
            source.AddHook(LogMessages);
            if (Has("erase")) source.AddHook(PaintBackground);
            if (Has("nofade")) SetAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, 1);
            if (cloak is null) return;

            if (Has("rgn"))
            {
                // Hide with an empty window region instead of DWMWA_CLOAK.
                SetWindowRgn(hwnd, CreateRectRgn(0, 0, 0, 0), false);
                Log("EmptyRegion");
            }
            else if (!Has("onshow") && !Has("onactivate"))
            {
                SetAttribute(hwnd, DWMWA_CLOAK, 1);
                Log("Cloaked");
            }

            var cloakedLater = false;
            // Hooks added later run first, so this one sees WM_ERASEBKGND before PaintBackground handles it.
            source.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                // WM_WINDOWPOSCHANGED with SWP_SHOWWINDOW: ShowWindow has made the window visible.
                var shown = msg == WM_WINDOWPOSCHANGED && (Marshal.ReadInt32(lParam, 2 * IntPtr.Size + 16) & SWP_SHOWWINDOW) != 0;
                if (Has("onshow") && !cloakedLater && shown)
                {
                    cloakedLater = true;
                    SetAttribute(h, DWMWA_CLOAK, 1);
                    Log("CloakedOnShow");
                }
                // onactivate: cloak on the first activation after the window became visible, like FirstFrameCloak.
                if (Has("onactivate") && !cloakedLater && msg is WM_NCACTIVATE or WM_ACTIVATE && wParam != IntPtr.Zero && IsWindowVisible(h))
                {
                    cloakedLater = true;
                    SetAttribute(h, DWMWA_CLOAK, 1);
                    Log("CloakedOnActivate");
                }
                if (cloak == "erase" && msg == WM_ERASEBKGND) Reveal();
                // cloak=showpos: uncloak as soon as the window is visible, unless it became active.
                if (cloak == "showpos" && shown)
                {
                    var active = GetForegroundWindow() == h;
                    Log(active ? "ShownActive" : "ShownInactive");
                    if (!active) Reveal();
                }
                return IntPtr.Zero;
            });

            if (cloak == "tick")
            {
                // The first Rendering tick precedes the first commit of the frame.
                EventHandler? onRendering = null;
                onRendering = (_, _) =>
                {
                    CompositionTarget.Rendering -= onRendering;
                    Log("FirstTick");
                    Reveal();
                };
                CompositionTarget.Rendering += onRendering;
            }
            if (cloak == "opdone")
            {
                // Right after the first Render-priority dispatcher operation after Loaded: the first commit.
                DispatcherHookEventHandler? onCompleted = null;
                onCompleted = (_, e) =>
                {
                    if (!loaded || e.Operation.Priority != DispatcherPriority.Render) return;
                    window.Dispatcher.Hooks.OperationCompleted -= onCompleted;
                    Log("RenderOperationCompleted");
                    Reveal();
                };
                window.Dispatcher.Hooks.OperationCompleted += onCompleted;
            }
        };

        window.Loaded += (_, _) =>
        {
            loaded = true;
            Log("Loaded");
            if (cloak == "loaded") Reveal();
            if (cloak == "active")
            {
                // An inactive window gets its first frame before DWM draws it; only an active one needs to wait.
                Log(window.IsActive ? "Active" : "NotActive");
                if (!window.IsActive) Reveal();
            }
            if (Has("forcefast"))
            {
                // Render the first frame now and wait until it is presented.
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                CompleteRender(window.Dispatcher);
                Log("ForcedPresent");
            }
            if (Has("delayrender"))
            {
                Thread.Sleep(300);
                Log("DelayedRender");
            }
        };

        var contentRendered = false;
        window.ContentRendered += (_, _) =>
        {
            if (contentRendered) return;
            contentRendered = true;
            Log("ContentRendered");
            Log(GetForegroundWindow() == hwnd ? "Foreground" : "NotForeground");
            switch (cloak)
            {
                case "cr" or "active" or "showpos":
                    Reveal();
                    break;
                case "complete":
                    CompleteRender(window.Dispatcher);
                    Log("CompleteRender");
                    Reveal();
                    break;
                case "late":
                    After(300, Reveal);
                    break;
                case "frames":
                    AfterRenderingTicks(3, Reveal);
                    break;
                case "frames-post":
                    AfterRenderingTicks(3, () => window.Dispatcher.BeginInvoke(DispatcherPriority.Background, Reveal));
                    break;
            }
            if (Has("reshow"))
                After(600, () =>
                {
                    window.Hide();
                    Log("Hidden");
                    After(600, () =>
                    {
                        window.Show();
                        Log("ShownAgain");
                    });
                });
        };

        Log("Run");
        if (Has("sync"))
        {
            // Show, then run layout and render synchronously before the message loop starts.
            window.Show();
            Log("Shown");
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            Log("SyncRendered");
            application.Run();
        }
        else
        {
            application.Run(window);
        }
    }

    private static void SetAttribute(IntPtr hwnd, int attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    // Internal MediaContext.CompleteRender: "issue a sync flush, which will only return after the last frame
    // is presented". Reflection is fine for an experiment; do not ship this.
    private static void CompleteRender(Dispatcher dispatcher)
    {
        var type = typeof(Visual).Assembly.GetType("System.Windows.Media.MediaContext")!;
        var context = type.GetMethod("From", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Dispatcher)])!.Invoke(null, [dispatcher]);
        type.GetMethod("CompleteRender", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)!.Invoke(context, null);
    }

    // erase: WPF answers WM_ERASEBKGND without painting; paint the dark background instead.
    private static IntPtr PaintBackground(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_ERASEBKGND) return IntPtr.Zero;
        GetClientRect(hwnd, out var rect);
        FillRect(wParam, ref rect, DarkBrush);
        handled = true;
        return 1;
    }

    private static IntPtr LogMessages(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_ACTIVATE) Log(wParam == 0 ? "WM_ACTIVATE inactive" : "WM_ACTIVATE active");
        if (msg is WM_PAINT or WM_ERASEBKGND or WM_SHOWWINDOW or WM_NCPAINT)
        {
            var seen = SeenMessages[msg] = SeenMessages.GetValueOrDefault(msg) + 1;
            if (seen <= (msg == WM_PAINT ? 50 : 2))
                Log(msg switch { WM_PAINT => "WM_PAINT", WM_ERASEBKGND => "WM_ERASEBKGND", WM_SHOWWINDOW => "WM_SHOWWINDOW", _ => "WM_NCPAINT" });
        }
        return IntPtr.Zero;
    }

    private static void Log(string name) => Console.WriteLine($"EVT {Stopwatch.GetTimestamp()} {name}");
}
