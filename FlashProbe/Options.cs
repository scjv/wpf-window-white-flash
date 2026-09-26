using System.Globalization;

namespace FlashProbe;

/// <summary>Command line of one recording.</summary>
internal sealed record Options(
    string Exe,
    string ChildArgs,
    string OutDir,
    string Label,
    double AfterMs,
    double SaveMs,
    string? SummaryCsv,
    bool Backdrop,
    bool Snapshot,
    bool Verbose)
{
    public const string Usage = """
        Usage: FlashProbe <exe> [options] [-- <child args>]
          --out <dir>       folder for <label>-frames.csv, <label>-events.txt and saved frames
          --label <name>    name of the run (default: exe name)
          --args "<text>"   arguments for the child process (alternative to "-- ...")
          --after <ms>      how long to record after the window becomes visible (default 1500)
          --save <ms>       save every composed frame of the first <ms> as PNG (1/3 scale)
          --summary <csv>   append a one-line summary of the run to this CSV file
          --backdrop        show a plain grey window behind the area where the window opens
          --snapshot        save the last composed image of every monitor when recording ends
          --verbose         print every recorded frame
        """;

    public static Options Parse(string[] args)
    {
        var split = Array.IndexOf(args, "--");
        var own = split < 0 ? args : args[..split];
        if (own.Length == 0 || own[0].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Missing target executable.");

        string? Value(string name)
        {
            var index = Array.IndexOf(own, name);
            return index >= 0 && index + 1 < own.Length ? own[index + 1] : null;
        }

        double Number(string name, double fallback) =>
            Value(name) is { } text ? double.Parse(text, CultureInfo.InvariantCulture) : fallback;

        var exe = Path.GetFullPath(own[0]);
        var childArgs = split >= 0 ? string.Join(' ', args[(split + 1)..].Select(Quote)) : Value("--args") ?? "";
        return new Options(
            exe,
            childArgs,
            Path.GetFullPath(Value("--out") ?? Path.Combine(Path.GetTempPath(), "FlashProbe")),
            Value("--label") ?? Path.GetFileNameWithoutExtension(exe),
            Number("--after", 1500),
            Number("--save", 0),
            Value("--summary") is { } summary ? Path.GetFullPath(summary) : null,
            own.Contains("--backdrop"),
            own.Contains("--snapshot"),
            own.Contains("--verbose"));
    }

    private static string Quote(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;
}
