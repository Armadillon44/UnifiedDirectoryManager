namespace UnifiedDirectoryManager.Services;

/// <summary>
/// CSV cell encoding shared by every CSV export in the app. Quotes cells that contain commas, quotes,
/// or newlines (doubling embedded quotes), and neutralizes spreadsheet formula-injection by prefixing
/// a leading <c>= + - @</c> or tab with an apostrophe so Excel/Sheets treats the value as text.
/// </summary>
public static class CsvText
{
    public static string Field(string? value)
    {
        value ??= string.Empty;
        if (value.Length > 0 && (value[0] is '=' or '+' or '-' or '@' || value[0] == '\t'))
            value = "'" + value;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    /// <summary>Joins a row of already-raw cells into a single CSV line (each cell encoded via <see cref="Field"/>).</summary>
    public static string Row(IEnumerable<string> cells) => string.Join(",", cells.Select(Field));
}
