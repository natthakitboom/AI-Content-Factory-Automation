using System.Text;
using ContentFactory.Core.Exceptions;

namespace ContentFactory.Infrastructure.Analytics;

/// <summary>
/// Minimal streaming RFC 4180 reader. It returns text only: a cell that looks like a
/// spreadsheet formula is never evaluated, and size limits protect the process.
/// </summary>
public static class CsvParser
{
    public const int MaxCellLength = 4096;
    public const int MaxColumns = 64;
    public const int MaxRows = 100_000;

    public static IEnumerable<IReadOnlyList<string>> Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var wasQuoted = false;
        var rowCount = 0;
        var first = true;

        int code;
        while ((code = reader.Read()) >= 0)
        {
            var ch = (char)code;
            if (first)
            {
                first = false;
                if (ch == '﻿')
                {
                    continue;
                }
            }

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        Append(cell, '"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    Append(cell, ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"' when cell.Length == 0 && !wasQuoted:
                    inQuotes = true;
                    wasQuoted = true;
                    break;
                case ',':
                    EndCell(row, cell);
                    wasQuoted = false;
                    break;
                case '\r':
                    break;
                case '\n':
                    EndCell(row, cell);
                    wasQuoted = false;
                    if (++rowCount > MaxRows)
                    {
                        throw TooMany();
                    }

                    yield return row;
                    row = [];
                    break;
                default:
                    Append(cell, ch);
                    break;
            }
        }

        if (inQuotes)
        {
            throw new ValidationException("CSV_UNTERMINATED_QUOTE", "A quoted CSV value is not closed.");
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            EndCell(row, cell);
            yield return row;
        }
    }

    /// <summary>True for text a spreadsheet would execute; callers keep it as plain text.</summary>
    public static bool LooksLikeFormula(string value)
        => value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r';

    private static void Append(StringBuilder cell, char ch)
    {
        if (cell.Length >= MaxCellLength)
        {
            throw new ValidationException("CSV_CELL_TOO_LONG", "A CSV value is longer than the allowed limit.");
        }

        cell.Append(ch);
    }

    private static void EndCell(List<string> row, StringBuilder cell)
    {
        if (row.Count >= MaxColumns)
        {
            throw new ValidationException("CSV_TOO_MANY_COLUMNS", "The CSV has more columns than allowed.");
        }

        row.Add(cell.ToString());
        cell.Clear();
    }

    private static ValidationException TooMany()
        => new("CSV_TOO_MANY_ROWS", "The CSV has more rows than allowed.");
}
