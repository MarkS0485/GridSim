using GridSim.SchemaEditor.Ui;

namespace GridSim.SchemaEditor;

/// <summary>
/// The ONLY code that touches <see cref="Console"/> (Mark's decision D2: a full-screen console UI for a local app).
/// It refuses redirected input or output (a script should use --check), treats Ctrl+C as a key so it goes through
/// the unsaved-changes question, hides the cursor except at the edit caret, reads one key at a time, redraws only
/// the rows that changed (all rows after a resize, checked before every frame), and restores the console in a
/// finally block whatever happens.
/// </summary>
internal static class ConsoleHost
{
    /// <summary>Run the editor until it finishes. Returns the process exit code (0, or 2 when the console is unusable).</summary>
    public static int Run(Editor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("The case editor needs an interactive console (input and output not redirected). For scripts use --check.");
            return 2;
        }

        bool oldCtrlC = Console.TreatControlCAsInput;
        try
        {
            Console.TreatControlCAsInput = true;
            Console.CursorVisible = false;
            Console.Clear();
            ScreenBuffer? previous = null;
            while (!editor.Finished)
            {
                // Resize check before every frame: a new size means a full redraw.
                int w = Math.Max(1, Console.WindowWidth), h = Math.Max(1, Console.WindowHeight);
                var buffer = new ScreenBuffer(w, h);
                Renderer.Render(editor, buffer);
                if (previous is null || previous.Width != w || previous.Height != h) { Console.Clear(); previous = null; }
                Draw(buffer, previous);
                previous = buffer;

                // Wait for a key, but notice a resize while waiting: the screen used to stay garbled after a resize
                // until the next key press, because the size was only read after ReadKey returned.
                if (WaitForKeyOrResize(w, h) is not ConsoleKeyInfo key) continue;   // resized: redraw at the new size
                ProcessKey(editor, key, Renderer.IsTooSmall(w, h));
            }
            return 0;
        }
        finally
        {
            Console.ResetColor();
            Console.Clear();
            Console.CursorVisible = true;
            Console.TreatControlCAsInput = oldCtrlC;
        }
    }

    /// <summary>How often (ms) the wait loop checks for a key or a window resize.</summary>
    private const int KeyPollMs = 50;

    /// <summary>
    /// Block until a key is pressed (returned) or the window size differs from <paramref name="width"/> x
    /// <paramref name="height"/> (null, so the caller redraws).
    /// </summary>
    private static ConsoleKeyInfo? WaitForKeyOrResize(int width, int height)
    {
        while (!Console.KeyAvailable)
        {
            if (Math.Max(1, Console.WindowWidth) != width || Math.Max(1, Console.WindowHeight) != height) return null;
            Thread.Sleep(KeyPollMs);
        }
        return Console.ReadKey(intercept: true);
    }

    /// <summary>
    /// Handle one key press and run the effect it asks for. Two guards (pure apart from the editor, so the tests drive
    /// it directly):
    /// <list type="bullet">
    /// <item>While the window is too small to draw (<paramref name="windowTooSmall"/>), only the quit keys (Q, F12,
    /// Ctrl+C) reach the editor. Every other key used to be handled with nothing on screen, so D duplicated a record,
    /// Del reset a field and Y answered a question invisibly. A refused key is noted on the message line for when the
    /// window is enlarged.</item>
    /// <item>An unexpected exception from the editor or an effect (an I/O error while staging, a defect in an
    /// operation) is caught HERE and shown on the message line as an error; the session, with every unsaved edit,
    /// stays open so the user can save or quit through the unsaved-changes question. It used to end the process and
    /// discard every edit. Nothing is swallowed: the message names the exception type and text.</item>
    /// </list>
    /// Returns true when the key was handled, false when it was refused or failed.
    /// </summary>
    internal static bool ProcessKey(Editor editor, ConsoleKeyInfo key, bool windowTooSmall)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (windowTooSmall && KeyMap.Lookup(key) != Command.Quit)
        {
            editor.Say("A key pressed while the window was too small was ignored. Only Q / F12 / Ctrl+C work until it is enlarged.", error: true);
            return false;
        }
        try
        {
            EditorEffect effect = editor.Handle(key);
            EffectRunner.Run(editor, effect);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            editor.Say($"Internal error ({ex.GetType().Name}): {ex.Message}. Your unsaved edits are still open: save (F10) or quit (Q).", error: true);
            return false;
        }
    }

    /// <summary>Copy the rows that differ from the previous frame to the console, in runs of one style.</summary>
    private static void Draw(ScreenBuffer b, ScreenBuffer? previous)
    {
        Console.CursorVisible = false;
        for (int y = 0; y < b.Height; y++)
        {
            if (previous is not null && b.RowEquals(previous, y)) continue;
            Console.SetCursorPosition(0, y);
            string text = b.RowText(y);
            // Never write the very last cell of the screen: on most consoles that scrolls the whole window by a line.
            int width = y == b.Height - 1 ? b.Width - 1 : b.Width;
            int x = 0;
            while (x < width)
            {
                CellStyle style = b.StyleAt(x, y);
                int end = x + 1;
                while (end < width && b.StyleAt(end, y) == style) end++;
                Apply(style);
                Console.Write(text.AsSpan(x, end - x));
                x = end;
            }
            Console.ResetColor();
        }
        if (b.Caret is (int cx, int cy))
        {
            Console.SetCursorPosition(Math.Min(cx, b.Width - 1), cy);
            Console.CursorVisible = true;
        }
    }

    private static void Apply(CellStyle style)
    {
        Console.ResetColor();
        switch (style)
        {
            case CellStyle.Reverse:
                Console.BackgroundColor = ConsoleColor.Gray;
                Console.ForegroundColor = ConsoleColor.Black;
                break;
            case CellStyle.Bold:
                Console.ForegroundColor = ConsoleColor.White;
                break;
            case CellStyle.Dim:
                Console.ForegroundColor = ConsoleColor.DarkGray;
                break;
        }
    }
}
