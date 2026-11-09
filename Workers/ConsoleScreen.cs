using System.Text;

namespace MsfsTelemetry.Workers;

/// <summary>One piece of a footer line, in its own colour (null = the console's default).</summary>
public readonly record struct FooterText(string Text, ConsoleColor? Color = null);

/// <summary>
/// Keeps a few status lines (the "footer") pinned below everything else written to the console.
/// <see cref="Install"/> replaces <see cref="Console.Out"/> with a writer that rubs the footer out
/// before any output (log lines, prompts) and draws it again after each completed line, so the logger
/// and the console commands need no changes to share the window with it.
///
/// <see cref="Pause"/> hides the footer while a command asks a question, so prompts and the text typed
/// in reply aren't disturbed. Without a real console (output or input redirected) it does nothing.
/// </summary>
public sealed class ConsoleScreen
{
    private readonly object _lock = new();
    private readonly TextWriter? _out;   // the real console writer; null when disabled
    private Func<IReadOnlyList<FooterText[]>>? _footer;
    private bool _shown;
    private int _top, _lines;
    private int _paused;
    private bool _atLineStart = true;

    private ConsoleScreen(TextWriter? realOut) => _out = realOut;

    /// <summary>False when there's no console window to draw in; then nothing is pinned.</summary>
    public bool Enabled => _out is not null;

    /// <summary>
    /// Takes over <see cref="Console.Out"/> when there's a real console. Call before the host is built,
    /// because the console logger keeps the writer it finds when it's created.
    /// </summary>
    public static ConsoleScreen Install()
    {
        if (!HasConsole())
            return new ConsoleScreen(null);

        // UTF-8 for the status symbols. Set first: changing the encoding replaces Console.Out.
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch (IOException) { }
        var screen = new ConsoleScreen(Console.Out);
        Console.SetOut(TextWriter.Synchronized(new PinnedWriter(screen)));
        return screen;
    }

    /// <summary>Starts showing the footer. <paramref name="footer"/> is asked for the lines on every redraw.</summary>
    public void Show(Func<IReadOnlyList<FooterText[]>> footer)
    {
        if (_out is null)
            return;
        lock (_lock)
        {
            _footer = footer;
            TryConsole(() => Console.CursorVisible = false);
            DrawIfReady();
        }
    }

    /// <summary>Removes the footer for good (at shutdown), leaving the cursor where it was drawn.</summary>
    public void Close()
    {
        if (_out is null)
            return;
        lock (_lock)
        {
            Erase();
            _footer = null;
            TryConsole(() => Console.CursorVisible = true);
        }
    }

    /// <summary>Draws the footer again with fresh values (e.g. once a second for the flight timer).</summary>
    public void Refresh()
    {
        if (_out is null)
            return;
        lock (_lock)
        {
            if (!_shown)
                return;
            Erase();
            DrawIfReady();
        }
    }

    /// <summary>
    /// Hides the footer until the returned object is disposed, for a command that prompts and reads
    /// input. Nested pauses are fine.
    /// </summary>
    public IDisposable Pause()
    {
        if (_out is null)
            return NoPause.Instance;
        lock (_lock)
        {
            if (_paused++ == 0)
            {
                Erase();
                TryConsole(() => Console.CursorVisible = true);
            }
        }
        return new Resume(this);
    }

    private void EndPause()
    {
        lock (_lock)
        {
            if (--_paused > 0 || _footer is null)
                return;
            TryConsole(() => Console.CursorVisible = false);
            if (!_atLineStart)
                Raw("\n");   // a prompt left the cursor mid-line; the footer goes below it
            DrawIfReady();
        }
    }

    /// <summary>Everything written to Console.Out comes through here.</summary>
    private void Write(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return;
        lock (_lock)
        {
            Erase();
            _out!.Write(text);
            _out.Flush();
            _atLineStart = text[^1] == '\n';
            DrawIfReady();
        }
    }

    private void Raw(string text)
    {
        _out!.Write(text);
        _out.Flush();
        _atLineStart = text.EndsWith('\n');
    }

    /// <summary>Draws the footer under the last complete line, unless paused or mid-line.</summary>
    private void DrawIfReady()
    {
        if (_shown || _footer is null || _paused > 0 || !_atLineStart)
            return;

        IReadOnlyList<FooterText[]> lines;
        try { lines = _footer(); }
        catch { return; }   // a status getter failing must not break console output
        if (lines.Count == 0)
            return;

        TryConsole(() =>
        {
            var width = Math.Max(20, Console.WindowWidth - 1);   // never fill the last column: that wraps
            var (fg, bg) = (Console.ForegroundColor, Console.BackgroundColor);
            Console.ResetColor();
            for (var i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                    _out!.Write('\n');
                var used = 0;
                foreach (var part in lines[i])
                {
                    if (used >= width)
                        break;
                    var text = part.Text.Length > width - used ? part.Text[..(width - used)] : part.Text;
                    if (part.Color is { } c)
                        Console.ForegroundColor = c;
                    _out!.Write(text);
                    _out.Flush();
                    if (part.Color is not null)
                        Console.ResetColor();
                    used += text.Length;
                }
                _out!.Write(new string(' ', width - used));
            }
            _out!.Flush();
            (Console.ForegroundColor, Console.BackgroundColor) = (fg, bg);

            // Measured after drawing, so it's right even when drawing scrolled the window.
            _lines = lines.Count;
            _top = Console.CursorTop - (_lines - 1);
            _shown = true;
        });
    }

    /// <summary>Blanks the footer and puts the cursor where its first line was, ready for new output.</summary>
    private void Erase()
    {
        if (!_shown)
            return;
        _shown = false;
        TryConsole(() =>
        {
            var blank = new string(' ', Math.Max(20, Console.WindowWidth - 1));
            var (fg, bg) = (Console.ForegroundColor, Console.BackgroundColor);
            Console.ResetColor();
            for (var i = 0; i < _lines; i++)
            {
                Console.SetCursorPosition(0, _top + i);
                _out!.Write(blank);
                _out.Flush();
            }
            Console.SetCursorPosition(0, _top);
            (Console.ForegroundColor, Console.BackgroundColor) = (fg, bg);
        });
        _atLineStart = true;
    }

    /// <summary>Console calls throw if the window goes away or is resized mid-call; the footer just skips a beat.</summary>
    private static void TryConsole(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException or InvalidOperationException) { }
    }

    private static bool HasConsole()
    {
        if (Console.IsOutputRedirected || Console.IsInputRedirected)
            return false;
        try
        {
            _ = Console.WindowWidth;
            _ = Console.KeyAvailable;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private sealed class Resume(ConsoleScreen screen) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                screen.EndPause();
        }
    }

    private sealed class NoPause : IDisposable
    {
        public static readonly NoPause Instance = new();
        public void Dispose() { }
    }

    /// <summary>Routes every write to <see cref="ConsoleScreen.Write"/>.</summary>
    private sealed class PinnedWriter(ConsoleScreen screen) : TextWriter
    {
        public override Encoding Encoding => screen._out!.Encoding;
        public override void Write(char value) => screen.Write([value]);
        public override void Write(string? value) => screen.Write(value);
        public override void Write(char[] buffer, int index, int count) => screen.Write(buffer.AsSpan(index, count));
        public override void Write(ReadOnlySpan<char> buffer) => screen.Write(buffer);
        public override void WriteLine(string? value) => screen.Write((value ?? "") + NewLine);
        public override void WriteLine() => screen.Write(NewLine);
        public override void Flush() { }
    }
}
