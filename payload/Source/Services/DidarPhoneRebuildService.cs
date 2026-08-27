using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace DigiAhan.CDR.Receiver.Services;

public sealed record DidarPhoneRebuildResult(int Contacts, int Phones);

public sealed class DidarPhoneRebuildService
{
    private static readonly Regex DigitRun = new(@"(?<!\d)\d{7,15}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex FormattedRun = new(@"(?<!\d)(?:\+?98|0098|0)?\d(?:[()\-._]*\d){6,14}(?!\d)", RegexOptions.Compiled);
    private readonly IConfiguration _configuration;
    private readonly ILogger<DidarPhoneRebuildService> _logger;

    public DidarPhoneRebuildService(IConfiguration configuration, ILogger<DidarPhoneRebuildService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<DidarPhoneRebuildResult> RebuildAsync(CancellationToken ct)
    {
        var cs = _configuration.GetConnectionString("DigiAhanCdr")
            ?? throw new InvalidOperationException("ConnectionStrings:DigiAhanCdr is missing.");
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync(ct);

        const string sourceSql = """
            SELECT DidarContactCode,MobilePhone,LandlinePhone,CompanyPhone,Fax,OtherPhones,Phones2
            FROM dbo.DidarContacts
            WHERE ISNULL(IsDeleted,0)=0 AND NULLIF(LTRIM(RTRIM(DidarContactCode)),N'') IS NOT NULL;
            """;
        var rows = new List<PhoneRow>();
        var contacts = 0;
        await using (var command = new SqlCommand(sourceSql, connection) { CommandTimeout = 180 })
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                contacts++;
                var code = reader.GetString(0);
                Add(rows, code, reader, 1, "Mobile", "MobilePhone", true);
                Add(rows, code, reader, 2, "Landline", "LandlinePhone", false);
                Add(rows, code, reader, 3, "Company", "CompanyPhone", false);
                Add(rows, code, reader, 4, "Fax", "Fax", false);
                Add(rows, code, reader, 5, "Other", "OtherPhones", false);
                Add(rows, code, reader, 6, "Other2", "Phones2", false);
            }
        }

        rows = rows
            .GroupBy(x => new { x.DidarContactCode, x.NormalizedPhone })
            .Select(g => g.OrderByDescending(x => x.IsPrimary).ThenBy(x => x.Priority).First())
            .ToList();

        var table = CreateTable(rows);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            await using (var clear = new SqlCommand("DELETE FROM dbo.DidarContactPhones;", connection, transaction)
                   { CommandTimeout = 180 })
                await clear.ExecuteNonQueryAsync(ct);

            using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, transaction)
            {
                DestinationTableName = "dbo.DidarContactPhones",
                BatchSize = 5000,
                BulkCopyTimeout = 300
            };
            foreach (DataColumn column in table.Columns)
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            await bulk.WriteToServerAsync(table, ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        _logger.LogInformation("All Didar phone fields rebuilt. Contacts={Contacts} Phones={Phones}", contacts, rows.Count);
        return new DidarPhoneRebuildResult(contacts, rows.Count);
    }

    public async Task<IReadOnlyList<object>> GetContactPhonesAsync(string didarContactCode, CancellationToken ct)
    {
        var cs = _configuration.GetConnectionString("DigiAhanCdr")
            ?? throw new InvalidOperationException("ConnectionStrings:DigiAhanCdr is missing.");
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync(ct);
        const string sql = """
            SELECT p.DidarContactCode,l.IdentityId,p.NormalizedPhone,p.OriginalPhone,p.PhoneType,p.SourceColumn,p.IsPrimary
            FROM dbo.DidarContactPhones p
            LEFT JOIN dbo.CustomerIdentityDidarLinks l ON l.DidarContactCode=p.DidarContactCode
            WHERE p.DidarContactCode=@code
            ORDER BY p.IsPrimary DESC,p.NormalizedPhone;
            """;
        await using var command = new SqlCommand(sql,connection);
        command.Parameters.Add("@code",SqlDbType.NVarChar,100).Value=didarContactCode;
        await using var reader=await command.ExecuteReaderAsync(ct);
        var result=new List<object>();
        while(await reader.ReadAsync(ct)) result.Add(new
        {
            didarContactCode=reader.GetString(0),
            identityId=reader.IsDBNull(1)?(long?)null:reader.GetInt64(1),
            normalizedPhone=reader.GetString(2),originalPhone=reader.GetString(3),
            phoneType=reader.GetString(4),sourceColumn=reader.IsDBNull(5)?null:reader.GetString(5),
            isPrimary=reader.GetBoolean(6)
        });
        return result;
    }

    private static void Add(List<PhoneRow> result, string code, SqlDataReader reader, int ordinal,
        string type, string source, bool primary)
    {
        if (reader.IsDBNull(ordinal)) return;
        var index = 0;
        foreach (var phone in Extract(reader.GetString(ordinal)))
        {
            result.Add(new PhoneRow(code, phone.Raw, phone.Normalized, type, source, primary && index == 0,
                primary ? 0 : SourcePriority(type)));
            index++;
        }
    }

    public static IReadOnlyList<(string Raw, string Normalized)> Extract(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<(string,string)>();
        var ascii = ToAsciiDigits(value);
        var formatted = FormattedRun.Matches(ascii).ToArray();
        var candidates = formatted.Select(x => x.Value).ToList();
        candidates.AddRange(DigitRun.Matches(ascii)
            .Where(run => !formatted.Any(f => run.Index >= f.Index && run.Index + run.Length <= f.Index + f.Length))
            .Select(x => x.Value));
        return candidates.Select(x => (Raw: x.Trim(), Normalized: Normalize(x)))
            .Where(x => x.Normalized.Length is >= 7 and <= 15)
            .DistinctBy(x => x.Normalized)
            .ToList();
    }

    private static string Normalize(string value)
    {
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        if (digits.StartsWith("0098")) digits = digits[4..];
        else if (digits.StartsWith("98")) digits = digits[2..];
        if (digits.Length == 10 && (digits[0] == '9' || digits[0] == '2')) digits = "0" + digits;
        return digits;
    }

    private static string ToAsciiDigits(string value)
    {
        var output = new StringBuilder(value.Length);
        foreach (var c in value)
            output.Append(c switch
            {
                >= '\u06F0' and <= '\u06F9' => (char)('0' + c - '\u06F0'),
                >= '\u0660' and <= '\u0669' => (char)('0' + c - '\u0660'),
                _ => c
            });
        return output.ToString();
    }

    private static int SourcePriority(string type) => type switch
    { "Company" => 10, "Landline" => 20, "Other" => 30, "Other2" => 40, "Fax" => 50, _ => 60 };

    private static DataTable CreateTable(IEnumerable<PhoneRow> rows)
    {
        var table=new DataTable();
        table.Columns.Add("DidarContactCode",typeof(string));table.Columns.Add("OriginalPhone",typeof(string));
        table.Columns.Add("NormalizedPhone",typeof(string));table.Columns.Add("PhoneType",typeof(string));
        table.Columns.Add("IsPrimary",typeof(bool));table.Columns.Add("CreatedAt",typeof(DateTime));
        table.Columns.Add("SourceColumn",typeof(string));table.Columns.Add("LastSyncedAtUtc",typeof(DateTime));
        var now=DateTime.UtcNow;
        foreach(var row in rows) table.Rows.Add(row.DidarContactCode,row.RawPhone,row.NormalizedPhone,row.PhoneType,row.IsPrimary,now,row.SourceColumn,now);
        return table;
    }

    private sealed record PhoneRow(string DidarContactCode,string RawPhone,string NormalizedPhone,
        string PhoneType,string SourceColumn,bool IsPrimary,int Priority);
}
