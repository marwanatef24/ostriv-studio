using System.Globalization;
using System.Text;

namespace OstrivStudio.Core;

public sealed record ResourceLedgerExportRow(string Scope, DateTime ObservedUtc, bool Complete, int SharedArrays,
    string Resource, int ResourceId, string Building, double Stored, double Reserved, double Awaited, double Other);

public static class ResourceLedgerCsv
{
    // Text from the scan/catalog can be opened in spreadsheet software, so prevent
    // a leading formula marker from becoming an executable spreadsheet formula.
    private static string TextCell(string value)
    {
        if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') ||
            value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static string Create(IEnumerable<ResourceLedgerExportRow> rows)
    {
        var csv = new StringBuilder("Scope,Scan time UTC,Complete scan,Shared arrays,Resource,Resource ID,Building candidate,Stored,Reserved,Awaited,Other\r\n");
        foreach (var row in rows)
        {
            csv.Append(TextCell(row.Scope)).Append(',').Append(TextCell(row.ObservedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)))
                .Append(',').Append(row.Complete ? "Yes" : "No").Append(',').Append(row.SharedArrays.ToString(CultureInfo.InvariantCulture))
                .Append(',').Append(TextCell(row.Resource)).Append(',').Append(row.ResourceId.ToString(CultureInfo.InvariantCulture))
                .Append(',').Append(TextCell(row.Building)).Append(',').Append(row.Stored.ToString("R", CultureInfo.InvariantCulture))
                .Append(',').Append(row.Reserved.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(row.Awaited.ToString("R", CultureInfo.InvariantCulture))
                .Append(',').Append(row.Other.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n");
        }
        return csv.ToString();
    }
}
