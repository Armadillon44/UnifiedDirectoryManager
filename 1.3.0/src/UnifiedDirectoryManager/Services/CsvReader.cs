namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Minimal RFC-4180 CSV reader: handles quoted fields, embedded commas/quotes (doubled <c>""</c>),
/// and CR/LF line endings inside quotes. Complements <see cref="CsvText"/> (the writer side).
/// </summary>
public static class CsvReader
{
    /// <summary>Parses CSV text into a header row plus the remaining (non-blank) data rows.</summary>
    public static (IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows) Parse(string text)
    {
        var records = ParseRecords(text);
        if (records.Count == 0) return (Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());
        var headers = records[0];
        var rows = records.Skip(1)
            .Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))) // drop fully-blank lines
            .Cast<IReadOnlyList<string>>()
            .ToList();
        return (headers, rows);
    }

    private static List<List<string>> ParseRecords(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new System.Text.StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;

        void EndField() { record.Add(field.ToString()); field.Clear(); fieldStarted = false; }
        void EndRecord() { EndField(); records.Add(record); record = new List<string>(); }

        for (var idx = 0; idx < text.Length; idx++)
        {
            var c = text[idx];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (idx + 1 < text.Length && text[idx + 1] == '"') { field.Append('"'); idx++; } // escaped quote
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0 && !fieldStarted:
                    inQuotes = true; fieldStarted = true; break;
                case ',':
                    EndField(); break;
                case '\r':
                    if (idx + 1 < text.Length && text[idx + 1] == '\n') idx++; // CRLF
                    EndRecord(); break;
                case '\n':
                    EndRecord(); break;
                default:
                    field.Append(c); fieldStarted = true; break;
            }
        }

        // Flush the last field/record if the file didn't end with a newline.
        if (field.Length > 0 || record.Count > 0) EndRecord();
        return records;
    }
}
