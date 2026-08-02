using DigiAhan.CDR.Receiver.Models;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class CustomerIntelligenceRepository
{
    private readonly string _connectionString;
    private readonly ILogger<CustomerIntelligenceRepository> _logger;

    public CustomerIntelligenceRepository(
        IConfiguration configuration,
        ILogger<CustomerIntelligenceRepository> logger)
    {
        _connectionString = configuration.GetConnectionString("DigiAhanCdr")
            ?? throw new InvalidOperationException("ConnectionStrings:DigiAhanCdr is missing.");
        _logger = logger;
    }

    public async Task<AgentCustomerCard> BuildCard(
        VoipRingEventRequest request,
        CancellationToken ct)
    {
        var phone = NormalizePhone(request.CallerNumber);
        var eventTime = request.EventTimeUtc ?? DateTime.UtcNow;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var didar = await FindDidar(connection, phone, ct);
        var call = await FindCallHistory(connection, phone, ct);
        var accounting = await FindAccounting(connection, phone, ct);

        var known = didar is not null || accounting is not null;
        var rank = CalculateRank(accounting?.Sales30Days ?? 0m, accounting?.InvoiceCount30Days ?? 0);
        var temperature = CalculateTemperature(
            call.LastCallAt,
            accounting?.LastInvoiceDate,
            call.CallsLast30Days,
            accounting?.InvoiceCount30Days ?? 0);

        var displayName =
            didar?.FullName ??
            didar?.CompanyName ??
            accounting?.CustomerName;

        var companyName =
            didar?.CompanyName ??
            accounting?.CustomerName;

        var suggestion = BuildSuggestion(
            displayName,
            companyName,
            didar?.OwnerName,
            accounting?.LastProduct,
            accounting?.LastInvoiceDate,
            rank,
            known);

        return new AgentCustomerCard(
            request.Extension.Trim(),
            phone,
            eventTime,
            request.LinkedId,
            displayName,
            companyName,
            didar?.OwnerName,
            didar?.ContactCode,
            known,
            call.LastCallAt,
            call.CallsLast30Days,
            accounting?.DetailCode,
            accounting?.CustomerName,
            accounting?.LastInvoiceDate,
            accounting?.LastInvoiceAmount,
            accounting?.LastProduct,
            accounting?.InvoiceCount30Days ?? 0,
            accounting?.Sales30Days ?? 0m,
            rank,
            temperature,
            suggestion);
    }

    private static async Task<DidarInfo?> FindDidar(
        SqlConnection connection,
        string phone,
        CancellationToken ct)
    {
        const string sql = """
            SELECT TOP(1)
                dc.DidarContactCode,
                dc.FullName,
                dc.CompanyName,
                dc.OwnerName
            FROM dbo.DidarContactPhones p
            INNER JOIN dbo.DidarContacts dc
                ON dc.DidarContactCode=p.DidarContactCode
               AND dc.IsDeleted=0
            WHERE p.NormalizedPhone=dbo.NormalizeIranPhone(@phone)
            ORDER BY p.IsPrimary DESC,p.Id ASC;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@phone", SqlDbType.NVarChar, 32).Value = phone;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new DidarInfo(
            GetString(reader, 0),
            GetString(reader, 1),
            GetString(reader, 2),
            GetString(reader, 3));
    }

    private static async Task<CallInfo> FindCallHistory(
        SqlConnection connection,
        string phone,
        CancellationToken ct)
    {
        const string sql = """
            SELECT
                MAX(r.Calldate) AS LastCallAt,
                COUNT(DISTINCT COALESCE(NULLIF(r.LinkedId,N''),NULLIF(r.UniqueId,N''),CONVERT(nvarchar(30),r.RawCDRId))) AS CallsLast30Days
            FROM dbo.RawCDR r
            WHERE r.Calldate>=DATEADD(day,-30,SYSDATETIME())
              AND (
                    dbo.NormalizeIranPhone(r.Src)=dbo.NormalizeIranPhone(@phone)
                 OR dbo.NormalizeIranPhone(r.Dst)=dbo.NormalizeIranPhone(@phone)
              );
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@phone", SqlDbType.NVarChar, 32).Value = phone;
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return new CallInfo(
            reader.IsDBNull(0) ? null : reader.GetDateTime(0),
            reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader[1]));
    }

    private async Task<AccountingInfo?> FindAccounting(
        SqlConnection connection,
        string phone,
        CancellationToken ct)
    {
        var tail = phone.Length >= 7 ? phone[^7..] : phone;

        const string customersSql = """
            SELECT TOP(30)
                SourceDatabase,FiscalYear,DetailCode,CustomerName,CustomerTel
            FROM dbo.AccountingCustomers
            WHERE CustomerTel LIKE N'%'+@tail+N'%'
            ORDER BY ImportedAtUtc DESC;
            """;

        var candidates = new List<AccountingCustomerCandidate>();
        await using (var command = new SqlCommand(customersSql, connection))
        {
            command.Parameters.Add("@tail", SqlDbType.NVarChar, 16).Value = tail;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                candidates.Add(new AccountingCustomerCandidate(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    GetString(reader, 3),
                    GetString(reader, 4)));
            }
        }

        var match = candidates.FirstOrDefault(x =>
            SplitPhones(x.CustomerTel).Any(p => NormalizePhone(p) == phone));

        if (match is null)
            return null;

        const string invoiceSql = """
            SELECT
                COUNT(*) AS InvoiceCount30Days,
                ISNULL(SUM(i.Amount),0) AS Sales30Days,
                MAX(i.FactorDate) AS LastInvoiceDate
            FROM dbo.AccountingInvoices i
            WHERE i.SourceDatabase=@db
              AND i.FiscalYear=@fy
              AND i.CustomerDetailCode=@detail;
            """;

        int invoiceCount;
        decimal sales;
        string? lastDate;

        await using (var command = new SqlCommand(invoiceSql, connection))
        {
            command.Parameters.Add("@db", SqlDbType.NVarChar, 128).Value = match.SourceDatabase;
            command.Parameters.Add("@fy", SqlDbType.Int).Value = match.FiscalYear;
            command.Parameters.Add("@detail", SqlDbType.NVarChar, 18).Value = match.DetailCode;
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            invoiceCount = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            sales = reader.IsDBNull(1) ? 0 : reader.GetDecimal(1);
            lastDate = GetString(reader, 2);
        }

        const string lastInvoiceSql = """
            SELECT TOP(1)
                i.FactorCode,
                i.FactorDate,
                i.Amount,
                COALESCE(NULLIF(ii.ProductName,N''),NULLIF(ii.Description,N'')) AS ProductName
            FROM dbo.AccountingInvoices i
            LEFT JOIN dbo.AccountingInvoiceItems ii
              ON ii.SourceDatabase=i.SourceDatabase
             AND ii.FiscalYear=i.FiscalYear
             AND ii.FactorCode=i.FactorCode
            WHERE i.SourceDatabase=@db
              AND i.FiscalYear=@fy
              AND i.CustomerDetailCode=@detail
            ORDER BY i.FactorDate DESC,i.FactorCode DESC,ii.ItemRow;
            """;

        decimal? lastAmount = null;
        string? lastProduct = null;
        await using (var command = new SqlCommand(lastInvoiceSql, connection))
        {
            command.Parameters.Add("@db", SqlDbType.NVarChar, 128).Value = match.SourceDatabase;
            command.Parameters.Add("@fy", SqlDbType.Int).Value = match.FiscalYear;
            command.Parameters.Add("@detail", SqlDbType.NVarChar, 18).Value = match.DetailCode;
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                lastDate = GetString(reader, 1) ?? lastDate;
                lastAmount = reader.IsDBNull(2) ? null : reader.GetDecimal(2);
                lastProduct = GetString(reader, 3);
            }
        }

        return new AccountingInfo(
            match.DetailCode,
            match.CustomerName,
            lastDate,
            lastAmount,
            lastProduct,
            invoiceCount,
            sales);
    }

    public static string NormalizePhone(string? value)
    {
        var digits = Regex.Replace(value ?? string.Empty, @"\D+", "");
        if (digits.StartsWith("0098") && digits.Length > 4)
            digits = "0" + digits[4..];
        else if (digits.StartsWith("98") && digits.Length >= 12)
            digits = "0" + digits[2..];
        else if (digits.Length == 10 && !digits.StartsWith("0"))
            digits = "0" + digits;
        return digits;
    }

    private static IEnumerable<string> SplitPhones(string? value)
        => Regex.Split(value ?? string.Empty, @"[,;/|\s]+")
            .Where(x => !string.IsNullOrWhiteSpace(x));

    private static string CalculateRank(decimal sales, int invoices)
    {
        if (sales >= 50_000_000_000m || invoices >= 10) return "A";
        if (sales >= 10_000_000_000m || invoices >= 5) return "B";
        if (sales > 0 || invoices > 0) return "C";
        return "NEW";
    }

    private static string CalculateTemperature(
        DateTime? lastCallAt,
        string? lastInvoiceDate,
        int calls,
        int invoices)
    {
        if (calls >= 5 && invoices == 0) return "HOT";
        if (calls >= 2 || invoices > 0) return "WARM";
        if (lastCallAt.HasValue && lastCallAt.Value >= DateTime.Now.AddDays(-30))
            return "WARM";
        return "COLD";
    }

    private static string BuildSuggestion(
        string? name,
        string? company,
        string? owner,
        string? product,
        string? lastInvoiceDate,
        string rank,
        bool known)
    {
        if (!known)
            return "مشتری جدید است؛ ابتدا نام، شرکت، کالای موردنیاز و محل تحویل را دقیق ثبت کنید.";

        var subject = !string.IsNullOrWhiteSpace(company) ? company :
                      !string.IsNullOrWhiteSpace(name) ? name : "این مشتری";

        if (!string.IsNullOrWhiteSpace(product))
            return $"{subject} قبلاً «{product}» خریده است. ابتدا درباره همان نیاز و تجربه خرید قبلی سؤال کنید.";

        if (rank == "A")
            return $"{subject} مشتری باارزش است. مکالمه را شخصی، سریع و با پیگیری دقیق پیش ببرید.";

        return $"{subject} سابقه تماس یا خرید دارد. قبل از اعلام قیمت، نیاز فعلی و زمان خرید را روشن کنید.";
    }

    private static string? GetString(SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader[ordinal]);

    private sealed record DidarInfo(
        string? ContactCode,
        string? FullName,
        string? CompanyName,
        string? OwnerName);

    private sealed record CallInfo(
        DateTime? LastCallAt,
        int CallsLast30Days);

    private sealed record AccountingCustomerCandidate(
        string SourceDatabase,
        int FiscalYear,
        string DetailCode,
        string? CustomerName,
        string? CustomerTel);

    private sealed record AccountingInfo(
        string DetailCode,
        string? CustomerName,
        string? LastInvoiceDate,
        decimal? LastInvoiceAmount,
        string? LastProduct,
        int InvoiceCount30Days,
        decimal Sales30Days);
}
