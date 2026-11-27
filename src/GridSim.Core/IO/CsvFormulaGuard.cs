namespace GridSim.Core.IO;

/// <summary>
/// CSV formula-injection guard (CWE-1236). A spreadsheet opening a CSV file runs a cell whose text starts with =, +, -
/// or @ as a formula, and some also treat a leading tab or carriage return that way. A STRING cell that starts with one
/// of those characters gets a leading apostrophe, which the spreadsheet shows as text. Apply it to string cells only,
/// chosen by the cell's type (a name, a label, a message), never to a number: a negative number starts with '-' and stays
/// exactly as it is. The usual CSV quoting is applied afterwards, as before.
/// </summary>
public static class CsvFormulaGuard
{
    /// <summary>The characters that make a leading position dangerous.</summary>
    public const string TriggerCharacters = "=+-@\t\r";

    /// <summary><paramref name="text"/> with a leading apostrophe when its first character is one of
    /// <see cref="TriggerCharacters"/>; otherwise unchanged (the same string instance).</summary>
    public static string Neutralise(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length > 0 && TriggerCharacters.Contains(text[0], StringComparison.Ordinal) ? "'" + text : text;
    }
}
