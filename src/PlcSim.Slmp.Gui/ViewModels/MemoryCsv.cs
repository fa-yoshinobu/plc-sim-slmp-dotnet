using System.IO;
using System.Text;

namespace PlcSim.Slmp.Gui.ViewModels;

public static class MemoryCsv
{
    public static string Format(IReadOnlyCollection<string> headers, IEnumerable<MemoryRow> rows)
    {
        var result = new StringBuilder();
        result.AppendLine("番地," + string.Join(",", headers.Select(Escape)));
        foreach (var row in rows.Where(static row => row.IsVisible))
            result.AppendLine(Escape(row.Address) + "," + string.Join(",", row.Cells.Take(headers.Count).Select(static cell => Escape(cell.Text))));
        return result.ToString();
    }

    private static string Escape(string value)
        => value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    public static void Write(string path, IReadOnlyCollection<string> headers, IEnumerable<MemoryRow> rows)
        => File.WriteAllText(path, Format(headers, rows), new UTF8Encoding(true));
}
