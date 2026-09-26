using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace FlashProbe;

// FlashProbe starts a GUI process and records, for every frame DWM composes, how the process's first
// captioned window looks on screen: the brightness of its client area and title bar. A light flash before
// the first WPF frame shows up as frames that are brighter than both the desktop behind the window and the
// finished dark window. The child may write "EVT <Stopwatch.GetTimestamp()> <name>" lines to stdout; they
// are merged into the timeline. All times are milliseconds after the window became WS_VISIBLE.
internal static class Program
{
    private sealed record FrameRecord(double At, double Present, uint Accumulated, bool Cloaked, bool Covered, Measurement M);

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static int Main(string[] args)
    {
        Win32.UsePhysicalPixels();
        Options options;
        try { options = Options.Parse(args); }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }
        Directory.CreateDirectory(options.OutDir);

        using var backdrop = options.Backdrop ? Backdrop.Show() : null;
        if (backdrop != null) Thread.Sleep(150); // let DWM compose it before it becomes the "behind" reference
        var duplication = new DesktopDuplication();
        duplication.Settle();

        var t0 = Stopwatch.GetTimestamp();
        using var process = Process.Start(new ProcessStartInfo(options.Exe, options.ChildArgs)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(options.Exe)!
        }) ?? throw new InvalidOperationException("Could not start " + options.Exe);
        var events = new List<(double At, string Name)>();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not { } line) return;
            var parts = line.Split(' ', 3);
            long ticks = 0;
            var isEvent = parts.Length == 3 && parts[0] == "EVT" && long.TryParse(parts[1], NumberStyles.Integer, Invariant, out ticks);
            var at = isEvent ? Ms(ticks - t0) : Ms(Stopwatch.GetTimestamp() - t0);
            lock (events) events.Add((at, isEvent ? parts[2] : line));
        };
        process.BeginOutputReadLine();

        nint hwnd = 0;
        double visibleAt = -1;
        var behind = new Measurement();
        var frames = new List<FrameRecord>();
        while (true)
        {
            var now = Ms(Stopwatch.GetTimestamp() - t0);
            if (process.HasExited || now > 20000 || (visibleAt >= 0 && now - visibleAt > options.AfterMs)) break;
            if (hwnd == 0) hwnd = Win32.FindCaptionedWindow(process.Id);
            var cloaked = hwnd != 0 && Win32.IsCloaked(hwnd);
            RECT frame = default, client = default;
            var located = hwnd != 0 && Win32.IsWindowVisible(hwnd) && Win32.TryGetGeometry(hwnd, out frame, out client);
            var covered = located && !Win32.IsOnTop(hwnd, client);
            var owner = located ? duplication.Outputs.FirstOrDefault(o => o.Bounds.ContainsCentreOf(frame)) : null;
            if (owner != null && visibleAt < 0)
            {
                // The last frame composed before WS_VISIBLE shows what is behind the window.
                visibleAt = now;
                behind = duplication.Measure(owner, owner.Last, frame, client, null);
            }
            foreach (var o in duplication.Outputs)
                duplication.Poll(o, 0, (texture, info) =>
                {
                    if (o != owner) return;
                    var at = Ms(Stopwatch.GetTimestamp() - t0) - visibleAt;
                    var measurement = duplication.Measure(o, texture, frame, client, at <= options.SaveMs ? 3 : null);
                    frames.Add(new FrameRecord(at, Ms(info.LastPresentTime - t0) - visibleAt, info.AccumulatedFrames, cloaked, covered, measurement));
                });
            Thread.Sleep(0);
        }

        if (options.Snapshot)
        {
            if (hwnd != 0) Console.WriteLine("  window: " + Win32.Describe(hwnd));
            foreach (var o in duplication.Outputs)
            {
                duplication.Poll(o, 50, null);
                duplication.SaveLast(o, Path.Combine(options.OutDir, $"{options.Label}-screen-{o.Name}.png"));
            }
        }
        try
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(3000)) process.Kill();
            }
        }
        catch (InvalidOperationException) { }
        process.WaitForExit(3000);

        List<(double At, string Name)> timeline;
        lock (events) timeline = events.Select(e => (e.At - Math.Max(visibleAt, 0), e.Name)).OrderBy(e => e.Item1).ToList();
        Report(options, behind, frames, timeline);
        return 0;
    }

    private static void Report(Options options, Measurement behind, List<FrameRecord> frames, List<(double At, string Name)> events)
    {
        var csv = new StringBuilder("at_ms,present_ms,accumulated,cloaked,client_avg,client_light_pct,client_dark_pct,title_avg\n");
        foreach (var f in frames)
            csv.AppendLine(string.Create(Invariant,
                $"{f.At:F1},{f.Present:F1},{f.Accumulated},{f.Cloaked},{f.Covered},{f.M.Client.Avg:F1},{f.M.Client.LightPct:F1},{f.M.Client.DarkPct:F1},{f.M.Title.Avg:F1}"));
        File.WriteAllText(Path.Combine(options.OutDir, $"{options.Label}-frames.csv"), csv.ToString());
        File.WriteAllLines(Path.Combine(options.OutDir, $"{options.Label}-events.txt"),
            events.Select(e => string.Create(Invariant, $"{e.At:F1} {e.Name}")));
        var index = 0;
        foreach (var f in frames.Where(f => f.M.Rgb != null))
            Png.Save(Path.Combine(options.OutDir, string.Create(Invariant, $"{options.Label}-{index++:D3}-{f.At:F0}ms.png")),
                f.M.ImageWidth, f.M.ImageHeight, f.M.Rgb!);

        // While the window opens, its client area is a blend of whatever is behind it and its own surface. A blend
        // of "behind" and the finished dark window is never brighter than both, so a frame brighter than both
        // (plus a margin) shows the light surface: a flash frame. "Shown" is the first frame that differs from
        // what was behind the window; a window that never differs never appeared on screen.
        // Frames in which another window covered the target show that window, not the target.
        var onScreen = frames.Where(f => !f.Cloaked && !f.Covered).ToList();
        var coveredFrames = frames.Count(f => f.Covered && !f.Cloaked);
        var final = frames.Count > 0 ? frames[^1].M.Client.Avg : 0;
        var limit = Math.Max(behind.Client.Avg, final) + 8;
        var flashes = onScreen.Where(f => f.M.Client.Avg > limit).ToList();
        var peak = onScreen.MaxBy(f => f.M.Client.Avg);
        var shown = onScreen.FirstOrDefault(f =>
            Math.Abs(f.M.Client.Avg - behind.Client.Avg) > 2 ||
            Math.Abs(f.M.Client.DarkPct - behind.Client.DarkPct) > 4 ||
            Math.Abs(f.M.Title.Avg - behind.Title.Avg) > 4);

        string Round(double? value) => value is { } v ? v.ToString("F0", Invariant) : "";
        var flashSpan = flashes.Count > 0 ? $" ({Round(flashes[0].At)}..{Round(flashes[^1].At)} ms)" : "";
        var shownText = shown is null ? "never" : Round(shown.At) + " ms";
        Console.WriteLine(string.Create(Invariant,
            $"[{options.Label}] frames={frames.Count} behind={behind.Client.Avg:F0} final={final:F0} peak={peak?.M.Client.Avg ?? 0:F0}@{Round(peak?.At)}ms flash={flashes.Count} frames{flashSpan} shown={shownText}{(coveredFrames > 0 ? $" COVERED={coveredFrames} frames" : "")}"));
        var eventText = string.Join("; ", events.Select(e => string.Create(Invariant, $"{e.Name}@{e.At:F0}")));
        Console.WriteLine("    events: " + eventText);
        if (options.Verbose)
            foreach (var f in frames)
                Console.WriteLine(string.Create(Invariant,
                    $"    {f.At,7:F1} ms  cloaked={(f.Cloaked ? 1 : 0)}  client avg={f.M.Client.Avg,5:F1} light={f.M.Client.LightPct,5:F1}% dark={f.M.Client.DarkPct,5:F1}%  title={f.M.Title.Avg,5:F1}"));

        if (options.SummaryCsv is not { } summaryPath) return;
        Directory.CreateDirectory(Path.GetDirectoryName(summaryPath)!);
        var header = File.Exists(summaryPath) ? "" :
            "label,target,args,frames,behind,final,peak,peak_ms,flash_frames,flash_from_ms,flash_to_ms,shown_ms,covered_frames,events" + Environment.NewLine;
        string[] row =
        [
            Quote(options.Label), Quote(Path.GetFileNameWithoutExtension(options.Exe)), Quote(options.ChildArgs),
            frames.Count.ToString(Invariant), Round(behind.Client.Avg), Round(final), Round(peak?.M.Client.Avg), Round(peak?.At),
            flashes.Count.ToString(Invariant), Round(flashes.FirstOrDefault()?.At), Round(flashes.LastOrDefault()?.At),
            Round(shown?.At), coveredFrames.ToString(Invariant), Quote(eventText)
        ];
        File.AppendAllText(summaryPath, header + string.Join(',', row) + Environment.NewLine);
    }

    private static string Quote(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
}
