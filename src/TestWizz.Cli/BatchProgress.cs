using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TestWizz.Cli;

/// <summary>
/// One coherent, single-line terminal progress UI for batch commands. Never touches stdout.
/// When stderr is redirected (or CI/TERM=dumb), emit sparse, readable milestones instead
/// of carriage returns or escape codes. All user-controlled details are terminal-sanitized.
/// </summary>
internal sealed class BatchProgress : IDisposable
{
    private const string Reset = "\u001b[0m";
    private readonly TextWriter _writer;
    private readonly string _command;
    private readonly bool _interactive;
    private readonly bool _color;
    private readonly int _width;
    private readonly bool _quiet;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private string _stage = "prepare";
    private string _detail = "";
    private int _total;
    private int _completed;
    private int _failed;
    private int _skipped;
    private int _active;
    private int _lastBucket = -1;
    private int _lastReportedFailures;
    private TimeSpan _lastReport;
    private bool _drawn;
    private bool _finished;

    public static BatchProgress For(CommandContext context, string command, bool quiet = false)
    {
        var writer = context.Output.Stderr;
        var tty = ReferenceEquals(writer, Console.Error) && !Console.IsErrorRedirected &&
                  string.IsNullOrEmpty(context.GetEnvironmentVariable("CI")) &&
                  !string.Equals(context.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);
        var colors = tty && string.IsNullOrEmpty(context.GetEnvironmentVariable("NO_COLOR")) &&
                     context.GetEnvironmentVariable("CLICOLOR") != "0";
        var width = 100;
        if (tty)
        {
            try { width = Console.WindowWidth; }
            catch (IOException) { tty = false; colors = false; }
        }
        return new BatchProgress(writer, command, tty, width, colors, quiet);
    }

    // Explicit presentation settings make both terminal and redirected rendering testable.
    internal BatchProgress(TextWriter writer, string command, bool interactive,
        int width = 100, bool color = false, bool quiet = false)
    {
        _writer = writer;
        _command = Clean(command);
        _interactive = interactive && width >= 35;
        _width = Math.Max(35, width);
        _color = _interactive && color;
        _quiet = quiet;
    }

    public void Stage(string stage, int total, int completed = 0, string? detail = null)
    {
        _stage = Clean(stage);
        _total = Math.Max(0, total);
        _completed = Math.Clamp(completed, 0, Math.Max(_total, 0));
        _failed = 0;
        _skipped = 0;
        _active = 0;
        _detail = Clean(detail);
        _lastBucket = -1;
        Draw(force: true);
    }

    public void Update(int completed, string? detail = null, int failed = 0,
        int skipped = 0, int active = 0)
    {
        _completed = Math.Clamp(completed, 0, _total);
        _failed = Math.Max(0, failed);
        _skipped = Math.Max(0, skipped);
        _active = Math.Max(0, active);
        if (detail is not null) _detail = Clean(detail);
        Draw(force: false);
    }

    public void Pulse(string? detail = null, int active = 0)
    {
        if (detail is not null) _detail = Clean(detail);
        _active = Math.Max(0, active);
        // The caller supplies a bounded heartbeat, e.g. every 15 seconds.
        Draw(force: _interactive || _clock.Elapsed - _lastReport >= TimeSpan.FromSeconds(15));
    }

    public void Note(string message) => Line(Clean(message));

    /// <summary>
    /// A per-target failure line (code, message, proposal path). Longer than a note so the reason and path stay
    /// intact, but still bounded and terminal-sanitized.
    /// </summary>
    public void Failure(string message) => Line(Clean(message, 1200));

    private void Line(string text)
    {
        if (_quiet) return;
        ClearLine();
        _writer.WriteLine($"testwizz {_command}: {text}");
        if (_interactive && !_finished) Draw(force: true);
    }

    /// <summary>Preserves verbose diagnostics without allowing terminal control injection.</summary>
    public void Diagnostics(string details)
    {
        if (string.IsNullOrEmpty(details)) return;
        ClearLine();
        var safe = new StringBuilder(details.Length);
        foreach (var c in details)
            safe.Append(c is '\n' or '\t' || !char.IsControl(c) ? c : ' ');
        _writer.Write(safe);
        if (safe.Length == 0 || safe[^1] != '\n') _writer.WriteLine();
        if (_interactive && !_finished) Draw(force: true);
    }

    public void Finish(int completed, int failed = 0, int skipped = 0, string? detail = null)
    {
        if (_finished) return;
        _stage = failed > 0 ? "issues" : "done";
        _completed = Math.Clamp(completed, 0, _total);
        _failed = Math.Max(0, failed);
        _skipped = Math.Max(0, skipped);
        _active = 0;
        if (detail is not null) _detail = Clean(detail);
        Draw(force: true);
        if (_interactive && _drawn)
        {
            _writer.WriteLine();
            _drawn = false;
        }
        _finished = true;
    }

    public void Dispose()
    {
        // Avoid leaving a half-painted line when an operation is cancelled or throws.
        if (_interactive && _drawn && !_finished)
        {
            ClearLine();
            _writer.WriteLine($"testwizz {_command}: interrupted at {_completed}/{_total}");
            _drawn = false;
        }
    }

    private void Draw(bool force)
    {
        if (_quiet || _finished) return;
        if (_interactive)
        {
            var plain = TerminalLine(out var coloredBar, out var uncoloredBar);
            var line = _color ? plain.Replace(uncoloredBar, coloredBar, StringComparison.Ordinal) : plain;
            _writer.Write("\r\u001b[2K");
            _writer.Write(line);
            _writer.Flush();
            _drawn = true;
            return;
        }

        var bucket = _total <= 0 ? 0 : _completed * 10 / _total;
        if (!force && bucket == _lastBucket &&
            _failed == _lastReportedFailures &&
            _clock.Elapsed - _lastReport < TimeSpan.FromSeconds(15))
            return;
        _lastBucket = bucket;
        _lastReportedFailures = _failed;
        _lastReport = _clock.Elapsed;
        var units = _total == 0 ? "?" : _total.ToString(CultureInfo.InvariantCulture);
        var percent = _total == 0 ? "" : $" ({_completed * 100 / _total}%)";
        var counts = _failed > 0 ? $", {_failed} failed" : "";
        if (_skipped > 0) counts += $", {_skipped} skipped";
        var detail = _detail.Length == 0 ? "" : $" | {_detail}";
        _writer.WriteLine($"testwizz {_command}: {_stage} {_completed}/{units}{percent}{counts} " +
                          $"[{Duration()}]{detail}");
    }

    private string TerminalLine(out string coloredBar, out string uncoloredBar)
    {
        var narrow = _width < 76;
        var barWidth = _width >= 115 ? 20 : _width >= 90 ? 14 : 9;
        var filled = _total == 0 ? 0 : _completed * barWidth / _total;
        uncoloredBar = "[" + new string('━', filled) + new string('─', barWidth - filled) + "]";
        var tint = _failed > 0 ? "\u001b[33m" :
                   _total > 0 && _completed == _total ? "\u001b[32m" : "\u001b[36m";
        coloredBar = "[" + tint + new string('━', filled) + Reset +
                     "\u001b[90m" + new string('─', barWidth - filled) + Reset + "]";
        var units = _total == 0 ? "?" : _total.ToString(CultureInfo.InvariantCulture);
        var percent = _total == 0 ? " -- " : $"{_completed * 100 / _total,3}%";
        var elapsed = Duration();
        var baseLine = narrow
            ? $" {_command} {uncoloredBar} {_completed}/{units} {_stage} {elapsed}"
            : $" {_command,-13} {_stage,-9} {uncoloredBar} {percent}  {_completed}/{units}  {elapsed}";
        var extras = new List<string>();
        if (_failed > 0) extras.Add($"{_failed} failed");
        if (_skipped > 0) extras.Add($"{_skipped} skipped");
        if (_active > 0) extras.Add($"{_active} active");
        if (extras.Count > 0)
        {
            var addition = " · " + string.Join(" · ", extras);
            if (baseLine.Length + addition.Length <= _width - 2) baseLine += addition;
        }

        var available = _width - 1 - baseLine.Length - 3;
        if (!string.IsNullOrWhiteSpace(_detail) && available >= 8)
            baseLine += " · " + Shorten(_detail, available);
        return Shorten(baseLine, _width - 1);
    }

    private void ClearLine()
    {
        if (_interactive && _drawn)
        {
            _writer.Write("\r\u001b[2K");
            _drawn = false;
        }
    }

    private string Duration()
    {
        var elapsed = _clock.Elapsed;
        return elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static string Shorten(string value, int max)
    {
        if (value.Length <= max) return value;
        return max <= 1 ? "…" : value[..(max - 1)] + "…";
    }

    private static string Clean(string? value, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = new StringBuilder(Math.Min(value.Length, max));
        foreach (var c in value)
        {
            if (text.Length >= max) break;
            if (char.IsControl(c)) text.Append(' ');
            else text.Append(c);
        }
        return text.ToString().Trim();
    }
}
