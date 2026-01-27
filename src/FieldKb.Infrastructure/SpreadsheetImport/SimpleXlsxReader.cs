using System.IO.Compression;
using System.Xml.Linq;

namespace FieldKb.Infrastructure.SpreadsheetImport;

public static class SimpleXlsxReader
{
    private static readonly XNamespace NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace NsRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace NsDocRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public sealed record Sheet(string Name, IReadOnlyList<IReadOnlyList<string>> Rows);

    public static IReadOnlyDictionary<string, Sheet> ReadSheets(string xlsxPath)
    {
        using var zip = ZipFile.OpenRead(xlsxPath);
        var workbookXml = ReadXml(zip, "xl/workbook.xml");
        var workbookRelsXml = ReadXml(zip, "xl/_rels/workbook.xml.rels");

        var relMap = workbookRelsXml
            .Root?
            .Elements(NsRel + "Relationship")
            .Where(e => e.Attribute("Id") is not null)
            .ToDictionary(
                e => e.Attribute("Id")!.Value,
                e => e.Attribute("Target")?.Value ?? string.Empty,
                StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var sheets = new Dictionary<string, Sheet>(StringComparer.OrdinalIgnoreCase);
        var sheetEls = workbookXml
            .Root?
            .Element(NsMain + "sheets")
            ?.Elements(NsMain + "sheet")
            ?? Enumerable.Empty<XElement>();

        foreach (var sheetEl in sheetEls)
        {
            var name = sheetEl.Attribute("name")?.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var relId = sheetEl.Attribute(NsDocRel + "id")?.Value;
            if (string.IsNullOrWhiteSpace(relId) || !relMap.TryGetValue(relId, out var target) || string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            var path = target.Replace('\\', '/');
            if (!path.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
            {
                path = $"xl/{path}";
            }

            var worksheetXml = ReadXml(zip, path);
            var rows = ReadRows(worksheetXml);
            sheets[name] = new Sheet(name, rows);
        }

        return sheets;
    }

    private static XDocument ReadXml(ZipArchive zip, string entryPath)
    {
        var entry = zip.GetEntry(entryPath.Replace('\\', '/'))
            ?? throw new FileNotFoundException($"Missing entry: {entryPath}", entryPath);
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static IReadOnlyList<IReadOnlyList<string>> ReadRows(XDocument worksheetXml)
    {
        var sheetData = worksheetXml.Root?.Element(NsMain + "sheetData");
        if (sheetData is null)
        {
            return Array.Empty<IReadOnlyList<string>>();
        }

        var rows = new List<IReadOnlyList<string>>();
        foreach (var rowEl in sheetData.Elements(NsMain + "row"))
        {
            var cellMap = new Dictionary<int, string>();
            var maxCol = 0;

            foreach (var cellEl in rowEl.Elements(NsMain + "c"))
            {
                var cellRef = cellEl.Attribute("r")?.Value;
                if (string.IsNullOrWhiteSpace(cellRef))
                {
                    continue;
                }

                var col = ParseColumnIndex(cellRef);
                if (col <= 0)
                {
                    continue;
                }

                var value = ReadCellText(cellEl);
                cellMap[col] = value;
                if (col > maxCol)
                {
                    maxCol = col;
                }
            }

            if (maxCol <= 0)
            {
                rows.Add(Array.Empty<string>());
                continue;
            }

            var row = new string[maxCol];
            for (var i = 1; i <= maxCol; i++)
            {
                row[i - 1] = cellMap.TryGetValue(i, out var v) ? v : string.Empty;
            }

            rows.Add(row);
        }

        return rows;
    }

    private static string ReadCellText(XElement cellEl)
    {
        var t = cellEl.Attribute("t")?.Value;
        if (string.Equals(t, "inlineStr", StringComparison.OrdinalIgnoreCase))
        {
            return cellEl.Element(NsMain + "is")?.Element(NsMain + "t")?.Value ?? string.Empty;
        }

        if (string.Equals(t, "str", StringComparison.OrdinalIgnoreCase))
        {
            return cellEl.Element(NsMain + "v")?.Value ?? string.Empty;
        }

        return cellEl.Element(NsMain + "v")?.Value
            ?? cellEl.Element(NsMain + "is")?.Element(NsMain + "t")?.Value
            ?? string.Empty;
    }

    private static int ParseColumnIndex(string cellRef)
    {
        var col = 0;
        foreach (var ch in cellRef)
        {
            if (ch is >= 'A' and <= 'Z')
            {
                col = col * 26 + (ch - 'A' + 1);
                continue;
            }

            if (ch is >= 'a' and <= 'z')
            {
                col = col * 26 + (ch - 'a' + 1);
                continue;
            }

            break;
        }

        return col;
    }
}
