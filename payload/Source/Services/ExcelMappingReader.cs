using DigiAhan.CDR.Receiver.Models;
using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class ExcelMappingReader
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    public IReadOnlyList<CustomerMappingInputRow> Read(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var shared = ReadSharedStrings(archive);
        var sheetPath = ResolveFirstSheetPath(archive);
        var entry = archive.GetEntry(sheetPath)
            ?? throw new InvalidDataException($"Worksheet not found in workbook: {sheetPath}");

        using var sheetStream = entry.Open();
        var sheet = XDocument.Load(sheetStream);
        var rows = sheet.Descendants(Main + "row").ToArray();
        if (rows.Length == 0) return Array.Empty<CustomerMappingInputRow>();

        var header = ReadCells(rows[0], shared)
            .ToDictionary(x => NormalizeHeader(x.Value), x => ColumnIndex(x.Reference), StringComparer.OrdinalIgnoreCase);
        var codeColumn = FindColumn(header, "customercode", "accountingcode", "کدحسابداری", "کدمشتری");
        var nameColumn = FindColumn(header, "customername", "name", "ناممشتری", "نام");
        var phoneColumn = FindColumn(header, "telephone", "phone", "mobile", "تلفن", "شمارهتلفن");

        var result = new List<CustomerMappingInputRow>();
        foreach (var row in rows.Skip(1))
        {
            var cells = ReadCells(row, shared).ToDictionary(x => ColumnIndex(x.Reference), x => x.Value);
            cells.TryGetValue(codeColumn, out var code);
            cells.TryGetValue(nameColumn, out var name);
            cells.TryGetValue(phoneColumn, out var phone);
            if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(phone))
                continue;
            var rowNumber = int.TryParse(row.Attribute("r")?.Value, out var parsed) ? parsed : result.Count + 2;
            result.Add(new CustomerMappingInputRow(rowNumber, code ?? string.Empty, name, phone));
        }
        return result;
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return new List<string>();
        using var stream = entry.Open();
        return XDocument.Load(stream).Descendants(Main + "si")
            .Select(x => string.Concat(x.Descendants(Main + "t").Select(t => t.Value)))
            .ToList();
    }

    private static string ResolveFirstSheetPath(ZipArchive archive)
    {
        using var workbookStream = archive.GetEntry("xl/workbook.xml")?.Open()
            ?? throw new InvalidDataException("xl/workbook.xml is missing.");
        var workbook = XDocument.Load(workbookStream);
        var relationshipId = workbook.Descendants(Main + "sheet").FirstOrDefault()?.Attribute(Rel + "id")?.Value
            ?? throw new InvalidDataException("Workbook has no worksheet.");

        using var relsStream = archive.GetEntry("xl/_rels/workbook.xml.rels")?.Open()
            ?? throw new InvalidDataException("Workbook relationships are missing.");
        var rels = XDocument.Load(relsStream);
        var target = rels.Descendants(PackageRel + "Relationship")
            .FirstOrDefault(x => x.Attribute("Id")?.Value == relationshipId)?.Attribute("Target")?.Value
            ?? throw new InvalidDataException("Worksheet relationship was not found.");
        target = target.Replace('\\', '/').TrimStart('/');
        return target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) ? target : "xl/" + target;
    }

    private static IEnumerable<(string Reference, string Value)> ReadCells(XElement row, IReadOnlyList<string> shared)
    {
        foreach (var cell in row.Elements(Main + "c"))
        {
            var reference = cell.Attribute("r")?.Value ?? string.Empty;
            var type = cell.Attribute("t")?.Value;
            var value = type == "inlineStr"
                ? string.Concat(cell.Descendants(Main + "t").Select(x => x.Value))
                : cell.Element(Main + "v")?.Value ?? string.Empty;
            if (type == "s" && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index >= 0 && index < shared.Count)
                value = shared[index];
            yield return (reference, value.Trim());
        }
    }

    private static int FindColumn(IReadOnlyDictionary<string, int> header, params string[] names)
    {
        foreach (var name in names)
            if (header.TryGetValue(NormalizeHeader(name), out var index)) return index;
        throw new InvalidDataException($"Required column was not found. Accepted names: {string.Join(", ", names)}");
    }

    private static string NormalizeHeader(string value) =>
        new(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static int ColumnIndex(string reference)
    {
        var result = 0;
        foreach (var ch in reference.TakeWhile(char.IsLetter))
            result = result * 26 + char.ToUpperInvariant(ch) - 'A' + 1;
        return result;
    }
}
