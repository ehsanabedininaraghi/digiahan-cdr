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

        var identity = await FindIdentity(connection, phone, ct);
        var didar = await FindDidar(connection, phone, identity?.DidarContactCode, ct);
        var accounting = await FindAccounting(
            connection,
            phone,
            identity?.SourceDatabase,
            identity?.FiscalYear,
            identity?.AccountingDetailCode,
            ct);
        var call = await FindCallHistory(connection, phone, ct);

        var known = identity is not null || didar is not null || accounting is not null;
        var rank = CalculateRank(accounting?.Sales30Days ?? 0m, accounting?.InvoiceCount ?? 0);
        var rankReason = CalculateRankReason(rank, accounting?.Sales30Days ?? 0m, accounting?.InvoiceCount ?? 0);
        var temperature = CalculateTemperature(
            call.LastCallAt,
            call.CallsLast30Days,
            accounting?.InvoiceCount ?? 0);

        var customerName =
            identity?.DisplayName ??
            didar?.FullName ??
            accounting?.CustomerName;

        var companyName =
            identity?.CompanyName ??
            didar?.CompanyName ??
            accounting?.CustomerName;

        var ownerName =
            identity?.OwnerName ??
            didar?.OwnerName;

        var identitySource =
            didar is not null && accounting is not null ? "DIDAR_ACCOUNTING" :
            didar is not null ? "DIDAR" :
            accounting is not null ? "ACCOUNTING" :
            identity is not null ? "IDENTITY" :
            "NEW";

        var lastInvoiceDaysAgo = PersianDateDaysAgo(accounting?.LastInvoiceDate);
        var suggestion = BuildSuggestion(
            customerName,
            companyName,
            accounting?.LastProduct,
            lastInvoiceDaysAgo,
            rank,
            known,
            call.CallsLast30Days,
            accounting?.InvoiceCount ?? 0);

        return new AgentCustomerCard(
            request.Extension.Trim(),
            phone,
            eventTime,
            request.LinkedId,
            customerName,
            companyName,
            ownerName,
            identity?.DidarContactCode ?? didar?.ContactCode,
            known,
            call.LastCallAt,
            call.CallsLast30Days,
            identity?.AccountingDetailCode ?? accounting?.DetailCode,
            accounting?.CustomerName,
            accounting?.LastInvoiceDate,
            accounting?.LastInvoiceAmount,
            accounting?.LastProduct,
            accounting?.InvoiceCount ?? 0,
            accounting?.Sales30Days ?? 0m,
            rank,
            temperature,
            suggestion,
            lastInvoiceDaysAgo,
            identitySource,
            rankReason,
            accounting?.AccountBalance,
            accounting?.CreditLimit);
    }


    public async Task<AgentCustomerCard> BuildFallbackCard(
        VoipRingEventRequest request,
        CancellationToken ct)
    {
        var phone = NormalizePhone(request.CallerNumber);
        var eventTime = request.EventTimeUtc ?? DateTime.UtcNow;

        string? displayName = null;
        string? companyName = null;
        string? ownerName = null;
        string? didarCode = null;
        string? accountingCode = null;
        string identitySource = "FALLBACK_NEW";
        var verified = false;

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            const string sql = """
            ;WITH MatchedPhone AS
            (
                SELECT TOP(1) IdentityId,SourceSystem,IsVerified
                FROM dbo.CustomerIdentityPhones
                WHERE NormalizedPhone=dbo.NormalizeIranPhone(@phone)
                ORDER BY IsVerified DESC,Priority,Id
            )
            SELECT
                COALESCE(NULLIF(i.DisplayName,N''),NULLIF(d.FullName,N''),NULLIF(a.CustomerName,N''),NULLIF(d.CompanyName,N'')),
                COALESCE(NULLIF(i.CompanyName,N''),NULLIF(d.CompanyName,N''),NULLIF(a.CustomerName,N'')),
                COALESCE(NULLIF(i.OwnerName,N''),NULLIF(d.OwnerName,N'')),
                d.DidarContactCode,a.DetailCode,p.SourceSystem,p.IsVerified
            FROM MatchedPhone p
            INNER JOIN dbo.CustomerIdentities i ON i.IdentityId=p.IdentityId
            OUTER APPLY
            (
                SELECT TOP(1) dc.DidarContactCode,dc.FullName,dc.CompanyName,dc.OwnerName
                FROM dbo.CustomerIdentityDidarLinks l
                INNER JOIN dbo.DidarContacts dc ON dc.DidarContactCode=l.DidarContactCode AND ISNULL(dc.IsDeleted,0)=0
                WHERE l.IdentityId=p.IdentityId
                ORDER BY l.IsVerified DESC,l.Id
            ) d
            OUTER APPLY
            (
                SELECT TOP(1) DetailCode,CustomerName
                FROM dbo.CustomerIdentityAccountingLinks
                WHERE IdentityId=p.IdentityId
                ORDER BY IsVerified DESC,FiscalYear DESC,Id
            ) a;
            """;

            await using var command = new SqlCommand(sql, connection)
            {
                CommandTimeout = 5
            };
            command.Parameters.Add("@phone", SqlDbType.NVarChar, 32).Value = phone;

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                displayName = GetString(reader, 0);
                companyName = GetString(reader, 1);
                ownerName = GetString(reader, 2);
                didarCode = GetString(reader, 3);
                accountingCode = GetString(reader, 4);
                identitySource = GetString(reader, 5) ?? "FALLBACK_DIRECTORY";
                verified = !reader.IsDBNull(6) && reader.GetBoolean(6);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Fallback customer lookup failed. Extension={Extension} Caller={Caller}",
                request.Extension,
                request.CallerNumber);
        }

        var known = !string.IsNullOrWhiteSpace(displayName)
            || !string.IsNullOrWhiteSpace(didarCode)
            || !string.IsNullOrWhiteSpace(accountingCode);

        var suggestion = known
            ? $"تماس ورودی از {displayName ?? companyName ?? phone}. اطلاعات پایه مشتری بازیابی شد."
            : "مشتری جدید است؛ نام، شرکت و موضوع درخواست را ثبت کنید.";

        return new AgentCustomerCard(
            request.Extension.Trim(),
            phone,
            eventTime,
            request.LinkedId,
            displayName,
            companyName,
            ownerName,
            didarCode,
            known,
            null,
            0,
            accountingCode,
            displayName,
            null,
            null,
            null,
            0,
            0m,
            known && verified ? "B" : "C",
            "COLD",
            suggestion,
            null,
            identitySource,
            known ? "بازیابی سریع از دفترچه یکپارچه مشتریان" : "شماره در دفترچه مشتریان پیدا نشد",
            null,
            null);
    }

    private static async Task<IdentityInfo?> FindIdentity(
        SqlConnection connection,
        string phone,
        CancellationToken ct)
    {
        const string sql = """
        ;WITH MatchedPhone AS
        (
            SELECT TOP(1) IdentityId,SourceSystem,IsVerified
            FROM dbo.CustomerIdentityPhones
            WHERE NormalizedPhone=dbo.NormalizeIranPhone(@phone)
            ORDER BY IsVerified DESC,Priority,Id
        )
        SELECT
            p.IdentityId,
            COALESCE(NULLIF(i.DisplayName,N''),NULLIF(d.FullName,N''),NULLIF(a.CustomerName,N''),NULLIF(d.CompanyName,N'')),
            COALESCE(NULLIF(i.CompanyName,N''),NULLIF(d.CompanyName,N''),NULLIF(a.CustomerName,N'')),
            COALESCE(NULLIF(i.OwnerName,N''),NULLIF(d.OwnerName,N'')),
            d.DidarContactCode,a.SourceDatabase,a.FiscalYear,a.DetailCode,a.ShortCode,
            p.SourceSystem,p.IsVerified
        FROM MatchedPhone p
        INNER JOIN dbo.CustomerIdentities i ON i.IdentityId=p.IdentityId
        OUTER APPLY
        (
            SELECT TOP(1) dc.DidarContactCode,dc.FullName,dc.CompanyName,dc.OwnerName
            FROM dbo.CustomerIdentityDidarLinks l
            INNER JOIN dbo.DidarContacts dc ON dc.DidarContactCode=l.DidarContactCode AND ISNULL(dc.IsDeleted,0)=0
            WHERE l.IdentityId=p.IdentityId
            ORDER BY l.IsVerified DESC,l.Id
        ) d
        OUTER APPLY
        (
            SELECT TOP(1) SourceDatabase,FiscalYear,DetailCode,ShortCode,CustomerName
            FROM dbo.CustomerIdentityAccountingLinks
            WHERE IdentityId=p.IdentityId
            ORDER BY IsVerified DESC,FiscalYear DESC,Id
        ) a;
        """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@phone", SqlDbType.NVarChar, 32).Value = phone;
        await using var reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
            return null;

        return new IdentityInfo(
            reader.GetInt64(0),
            GetString(reader, 1),
            GetString(reader, 2),
            GetString(reader, 3),
            GetString(reader, 4),
            GetString(reader, 5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            GetString(reader, 7),
            GetString(reader, 8),
            GetString(reader, 9),
            !reader.IsDBNull(10) && reader.GetBoolean(10));
    }

    private static async Task<DidarInfo?> FindDidar(
        SqlConnection connection,
        string phone,
        string? didarContactCode,
        CancellationToken ct)
    {
        const string sql = """
        DECLARE @resolvedCode nvarchar(100)=@code;
        IF @resolvedCode IS NULL
        BEGIN
            SELECT TOP(1) @resolvedCode=DidarContactCode
            FROM dbo.DidarContactPhones
            WHERE NormalizedPhone=dbo.NormalizeIranPhone(@phone)
            ORDER BY IsPrimary DESC,Id;
        END;

        SELECT TOP(1)
            dc.DidarContactCode,
            dc.FullName,
            dc.CompanyName,
            dc.OwnerName
        FROM dbo.DidarContacts dc
        WHERE dc.IsDeleted=0
          AND dc.DidarContactCode=@resolvedCode;
        """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@phone", SqlDbType.NVarChar, 32).Value = phone;
        command.Parameters.Add("@code", SqlDbType.NVarChar, 100).Value =
            string.IsNullOrWhiteSpace(didarContactCode) ? DBNull.Value : didarContactCode;

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
        ;WITH TargetIdentity AS
        (
            SELECT TOP(1) IdentityId
            FROM dbo.CustomerIdentityPhones
            WHERE NormalizedPhone=dbo.NormalizeIranPhone(@phone)
            ORDER BY IsVerified DESC,Priority,Id
        ),
        CustomerPhones AS
        (
            SELECT p.NormalizedPhone
            FROM dbo.CustomerIdentityPhones p
            WHERE p.IdentityId=(SELECT TOP(1) IdentityId FROM TargetIdentity)
            UNION SELECT dbo.NormalizeIranPhone(@phone)
        ),
        CustomerPhoneVariants AS
        (
            SELECT NormalizedPhone Phone FROM CustomerPhones WHERE NormalizedPhone IS NOT NULL
            UNION SELECT SUBSTRING(NormalizedPhone,2,32) FROM CustomerPhones WHERE NormalizedPhone LIKE N'0%'
            UNION SELECT N'98'+SUBSTRING(NormalizedPhone,2,32) FROM CustomerPhones WHERE NormalizedPhone LIKE N'0%'
            UNION SELECT N'0098'+SUBSTRING(NormalizedPhone,2,32) FROM CustomerPhones WHERE NormalizedPhone LIKE N'0%'
            UNION SELECT N'+98'+SUBSTRING(NormalizedPhone,2,32) FROM CustomerPhones WHERE NormalizedPhone LIKE N'0%'
        ),
        MatchingCalls AS
        (
            SELECT r.RawCDRId,r.Calldate,r.LinkedId,r.UniqueId
            FROM dbo.RawCDR r INNER JOIN CustomerPhoneVariants p ON p.Phone=r.Src
            WHERE r.Calldate>=DATEADD(day,-30,SYSDATETIME())
            UNION
            SELECT r.RawCDRId,r.Calldate,r.LinkedId,r.UniqueId
            FROM dbo.RawCDR r INNER JOIN CustomerPhoneVariants p ON p.Phone=r.Dst
            WHERE r.Calldate>=DATEADD(day,-30,SYSDATETIME())
        )
        SELECT
            MAX(r.Calldate) AS LastCallAt,
            COUNT(DISTINCT COALESCE(NULLIF(r.LinkedId,N''),NULLIF(r.UniqueId,N''),CONVERT(nvarchar(30),r.RawCDRId))) AS CallsLast30Days
        FROM MatchingCalls r;
        """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@phone", SqlDbType.NVarChar, 32).Value = phone;
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return new CallInfo(
            reader.IsDBNull(0) ? null : reader.GetDateTime(0),
            reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader[1]));
    }

    private static async Task<AccountingInfo?> FindAccounting(
        SqlConnection connection,
        string phone,
        string? sourceDatabase,
        int? fiscalYear,
        string? detailCode,
        CancellationToken ct)
    {
        AccountingKey? key = null;

        if (!string.IsNullOrWhiteSpace(detailCode))
        {
            const string identitySql = """
            SELECT TOP(1)
                SourceDatabase,FiscalYear,DetailCode,CustomerName,CustomerTel
            FROM dbo.AccountingCustomers
            WHERE DetailCode=@detail
              AND (@db IS NULL OR SourceDatabase=@db)
              AND (@fy IS NULL OR FiscalYear=@fy)
            ORDER BY FiscalYear DESC,ImportedAtUtc DESC;
            """;

            await using var command = new SqlCommand(identitySql, connection);
            command.Parameters.Add("@detail", SqlDbType.NVarChar, 30).Value = detailCode;
            command.Parameters.Add("@db", SqlDbType.NVarChar, 128).Value =
                string.IsNullOrWhiteSpace(sourceDatabase) ? DBNull.Value : sourceDatabase;
            command.Parameters.Add("@fy", SqlDbType.Int).Value =
                fiscalYear.HasValue ? fiscalYear.Value : DBNull.Value;

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                key = new AccountingKey(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    GetString(reader, 3),
                    GetString(reader, 4));
            }
        }

        if (key is null)
        {
            var tail = phone.Length >= 7 ? phone[^7..] : phone;

            const string phoneSql = """
            SELECT TOP(100)
                SourceDatabase,FiscalYear,DetailCode,CustomerName,CustomerTel
            FROM dbo.AccountingCustomers
            WHERE CustomerTel LIKE N'%'+@tail+N'%'
            ORDER BY FiscalYear DESC,ImportedAtUtc DESC;
            """;

            var candidates = new List<AccountingKey>();

            await using (var command = new SqlCommand(phoneSql, connection))
            {
                command.Parameters.Add("@tail", SqlDbType.NVarChar, 16).Value = tail;
                await using var reader = await command.ExecuteReaderAsync(ct);

                while (await reader.ReadAsync(ct))
                {
                    candidates.Add(new AccountingKey(
                        reader.GetString(0),
                        reader.GetInt32(1),
                        reader.GetString(2),
                        GetString(reader, 3),
                        GetString(reader, 4)));
                }
            }

            key = candidates.FirstOrDefault(x =>
                ExtractPhones(x.CustomerTel).Any(p => NormalizePhone(p) == phone));
        }

        if (key is null)
            return null;

        var calendar = new PersianCalendar();
        var cutoffDate = DateTime.Today.AddDays(-29);
        var cutoff = $"{calendar.GetYear(cutoffDate):0000}/{calendar.GetMonth(cutoffDate):00}/{calendar.GetDayOfMonth(cutoffDate):00}";

        const string summarySql = """
        SELECT
            COUNT(i.FactorCode) AS InvoiceCount,
            ISNULL(SUM(i.Amount),0) AS Sales30Days,
            c.AccountBalance,
            c.CreditLimit
        FROM dbo.AccountingCustomers c
        LEFT JOIN dbo.AccountingInvoices i
          ON i.SourceDatabase=c.SourceDatabase
         AND i.FiscalYear=c.FiscalYear
         AND i.CustomerDetailCode=c.DetailCode
         AND i.FactorDate>=@cutoff
        WHERE c.SourceDatabase=@db
          AND c.FiscalYear=@fy
          AND c.DetailCode=@detail
        GROUP BY c.AccountBalance,c.CreditLimit;
        """;

        int invoiceCount;
        decimal sales;
        decimal? accountBalance;
        decimal? creditLimit;

        await using (var command = new SqlCommand(summarySql, connection))
        {
            command.Parameters.Add("@db", SqlDbType.NVarChar, 128).Value = key.SourceDatabase;
            command.Parameters.Add("@fy", SqlDbType.Int).Value = key.FiscalYear;
            command.Parameters.Add("@detail", SqlDbType.NVarChar, 30).Value = key.DetailCode;
            command.Parameters.Add("@cutoff", SqlDbType.NVarChar, 10).Value = cutoff;

            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);

            invoiceCount = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader[0]);
            sales = reader.IsDBNull(1) ? 0m : reader.GetDecimal(1);
            accountBalance = reader.IsDBNull(2) ? null : reader.GetDecimal(2);
            creditLimit = reader.IsDBNull(3) ? null : reader.GetDecimal(3);
        }

        const string lastInvoiceSql = """
        SELECT TOP(1)
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

        string? lastDate = null;
        decimal? lastAmount = null;
        string? lastProduct = null;

        await using (var command = new SqlCommand(lastInvoiceSql, connection))
        {
            command.Parameters.Add("@db", SqlDbType.NVarChar, 128).Value = key.SourceDatabase;
            command.Parameters.Add("@fy", SqlDbType.Int).Value = key.FiscalYear;
            command.Parameters.Add("@detail", SqlDbType.NVarChar, 30).Value = key.DetailCode;

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                lastDate = GetString(reader, 0);
                lastAmount = reader.IsDBNull(1) ? null : reader.GetDecimal(1);
                lastProduct = GetString(reader, 2);
            }
        }

        return new AccountingInfo(
            key.DetailCode,
            key.CustomerName,
            lastDate,
            lastAmount,
            lastProduct,
            invoiceCount,
            sales,
            accountBalance,
            creditLimit);
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

    private static IEnumerable<string> ExtractPhones(string? value)
    {
        var text = value ?? string.Empty;
        text = Regex.Replace(
            text,
            @"(?<=\d{7})\s*-\s*(?=(?:0098|98|0)?\d{7,})",
            "|");

        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var part in Regex.Split(text, @"[,;/|\r\n]+"))
        {
            var direct = NormalizePhone(part);
            if (IsValidNormalizedPhone(direct))
                result.Add(direct);

            var patterns = new[]
            {
                @"(?<!\d)0098\d{10}(?!\d)",
                @"(?<!\d)98\d{10}(?!\d)",
                @"(?<!\d)09\d{9}(?!\d)",
                @"(?<!\d)0\d{10}(?!\d)",
                @"(?<!\d)\d{8}(?!\d)",
                @"(?<!\d)\d{7}(?!\d)"
            };

            foreach (var pattern in patterns)
            {
                foreach (Match match in Regex.Matches(part, pattern))
                {
                    var phone = NormalizePhone(match.Value);
                    if (IsValidNormalizedPhone(phone))
                        result.Add(phone);
                }
            }
        }

        return result;
    }

    private static bool IsValidNormalizedPhone(string phone)
        => Regex.IsMatch(phone, @"^09\d{9}$") ||
           Regex.IsMatch(phone, @"^0\d{10}$") ||
           Regex.IsMatch(phone, @"^\d{8}$") ||
           Regex.IsMatch(phone, @"^\d{7}$");

    private static string CalculateRank(decimal sales, int invoices)
    {
        if (sales >= 50_000_000_000m || invoices >= 10) return "A";
        if (sales >= 10_000_000_000m || invoices >= 5) return "B";
        if (sales > 0 || invoices > 0) return "C";
        return "NEW";
    }

    private static string CalculateRankReason(string rank, decimal sales, int invoices)
        => rank switch
        {
            "A" => $"خرید بالا یا تکرار خرید زیاد؛ {invoices} فاکتور در داده موجود",
            "B" => $"خرید متوسط و فعال؛ {invoices} فاکتور در داده موجود",
            "C" => "سابقه خرید دارد، اما حجم یا تکرار خرید پایین‌تر است",
            _ => "خریدی در داده حسابداری واردشده پیدا نشد"
        };

    private static string CalculateTemperature(DateTime? lastCallAt, int calls, int invoices)
    {
        if (calls >= 5 && invoices == 0) return "HOT";
        if (calls >= 3 || invoices > 0) return "WARM";
        if (lastCallAt.HasValue && lastCallAt.Value >= DateTime.Now.AddDays(-30))
            return "WARM";
        return "COLD";
    }

    private static int? PersianDateDaysAgo(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var parts = value.Trim().Replace("-", "/").Split('/');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var year) ||
            !int.TryParse(parts[1], out var month) ||
            !int.TryParse(parts[2], out var day))
            return null;

        try
        {
            var calendar = new PersianCalendar();
            var gregorian = calendar.ToDateTime(year, month, day, 0, 0, 0, 0);
            return Math.Max(0, (DateTime.Today - gregorian.Date).Days);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildSuggestion(
        string? name,
        string? company,
        string? product,
        int? lastInvoiceDaysAgo,
        string rank,
        bool known,
        int calls,
        int invoices)
    {
        if (!known)
            return "مشتری جدید است. نام، شرکت، کالای موردنیاز، تناژ، محل تحویل و زمان خرید را دقیق ثبت کنید.";

        var subject = !string.IsNullOrWhiteSpace(company) ? company :
                      !string.IsNullOrWhiteSpace(name) ? name : "این مشتری";

        if (calls >= 3 && invoices == 0)
            return $"{subject} در ۳۰ روز اخیر چند بار تماس داشته اما خریدی ثبت نشده است. ابتدا علت نهایی‌نشدن خرید قبلی را مشخص کنید.";

        if (!string.IsNullOrWhiteSpace(product))
        {
            var when = lastInvoiceDaysAgo.HasValue
                ? $"{lastInvoiceDaysAgo.Value} روز قبل"
                : "در آخرین خرید";

            return $"{subject} {when} «{product}» خریده است. مکالمه را با پیگیری همان کالا و نیاز فعلی شروع کنید.";
        }

        if (rank == "A")
            return $"{subject} مشتری رده A است. پاسخ را سریع، شخصی و با پیگیری دقیق ادامه دهید.";

        return $"{subject} سابقه تماس یا خرید دارد. پیش از اعلام قیمت، کالای دقیق، تناژ، محل تحویل و زمان تصمیم را مشخص کنید.";
    }

    private static string? GetString(SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader[ordinal]);

    private sealed record IdentityInfo(
        long IdentityId,
        string? DisplayName,
        string? CompanyName,
        string? OwnerName,
        string? DidarContactCode,
        string? SourceDatabase,
        int? FiscalYear,
        string? AccountingDetailCode,
        string? AccountingShortCode,
        string? MatchSource,
        bool IsVerified);

    private sealed record DidarInfo(
        string? ContactCode,
        string? FullName,
        string? CompanyName,
        string? OwnerName);

    private sealed record CallInfo(
        DateTime? LastCallAt,
        int CallsLast30Days);

    private sealed record AccountingKey(
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
        int InvoiceCount,
        decimal Sales30Days,
        decimal? AccountBalance,
        decimal? CreditLimit);
}
