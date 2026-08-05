using DigiAhan.CDR.Receiver.Models;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class AccountingSyncService
{
    private readonly IConfiguration _configuration;
    private readonly SqlQueryStore _queries;
    private readonly ILogger<AccountingSyncService> _logger;

    public AccountingSyncService(
        IConfiguration configuration,
        SqlQueryStore queries,
        ILogger<AccountingSyncService> logger)
    {
        _configuration = configuration;
        _queries = queries;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_configuration.GetConnectionString("AccountingLegacy")) &&
        !string.IsNullOrWhiteSpace(_configuration.GetConnectionString("DigiAhanCdr"));

    public async Task<AccountingSyncResult> SyncAsync(int days, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "Accounting connection is not configured. Run scripts/configure-accounting.ps1 first.");

        days = Math.Clamp(days, 1, 90);

        var sourceConnectionString = _configuration.GetConnectionString("AccountingLegacy")!;
        var destinationConnectionString = _configuration.GetConnectionString("DigiAhanCdr")!;
        var sourceServer = _configuration["Accounting:Server"]?.Trim() ?? "COREI5";
        var sourceDatabase = _configuration["Accounting:Database"]?.Trim() ?? "daftar1405";
        var fiscalYear = _configuration.GetValue<int?>("Accounting:FiscalYear") ?? 1405;
        var cutoff = ToPersianDate(DateTime.Today.AddDays(-(days - 1)));

        var runId = Guid.NewGuid();
        var started = DateTime.UtcNow;

        await using var destination = new SqlConnection(destinationConnectionString);
        await destination.OpenAsync(ct);
        await EnsureSchemaAsync(destination, ct);
        await StartRunAsync(destination, runId, started, sourceServer, sourceDatabase, fiscalYear, cutoff, ct);

        try
        {
            await using var source = new SqlConnection(sourceConnectionString);
            await source.OpenAsync(ct);

            var visitors = await ReadVisitorsAsync(source, ct);
            var invoices = await ReadInvoicesAsync(source, cutoff, ct);
            var factorCodes = invoices.Select(x => x.FactorCode).ToHashSet();
            var items = await ReadItemsAsync(source, cutoff, factorCodes, ct);

            var customerCodes = invoices
                .Where(x => !string.IsNullOrWhiteSpace(x.CustomerShortCode))
                .Select(x => x.CustomerShortCode!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var customers = await ReadCustomersAsync(source, customerCodes, ct);

            await using var transaction = (SqlTransaction)await destination.BeginTransactionAsync(ct);
            try
            {
                await ReplaceSnapshotAsync(
                    destination, transaction, sourceDatabase, fiscalYear, cutoff,
                    visitors, customers, invoices, items, ct);

                await transaction.CommitAsync(ct);
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }

            var finished = DateTime.UtcNow;
            await FinishRunAsync(destination, runId, finished, "SUCCESS",
                visitors.Count, customers.Count, invoices.Count, items.Count, null, ct);

            return new AccountingSyncResult(
                runId, started, finished, sourceServer, sourceDatabase, fiscalYear, cutoff,
                visitors.Count, customers.Count, invoices.Count, items.Count, "SUCCESS", null);
        }
        catch (Exception ex)
        {
            var finished = DateTime.UtcNow;
            _logger.LogError(ex,
                "Accounting sync failed. RunId={RunId} Source={SourceServer}/{SourceDatabase}",
                runId, sourceServer, sourceDatabase);

            await FinishRunAsync(destination, runId, finished, "FAILED", 0, 0, 0, 0, ex.Message, ct);

            return new AccountingSyncResult(
                runId, started, finished, sourceServer, sourceDatabase, fiscalYear, cutoff,
                0, 0, 0, 0, "FAILED", ex.Message);
        }
    }

    public async Task<AccountingSyncStatus> GetStatusAsync(CancellationToken ct)
    {
        if (!IsConfigured)
            return new AccountingSyncStatus(false, null, null, null, 0, 0, 0, null, null, null, null);

        await using var connection = new SqlConnection(_configuration.GetConnectionString("DigiAhanCdr"));
        await connection.OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);

        const string sql = """
            SELECT TOP(1)
                StartedAtUtc, FinishedAtUtc, Status, CustomerCount, InvoiceCount,
                InvoiceItemCount, SourceServer, SourceDatabase, FiscalYear, ErrorMessage
            FROM dbo.AccountingSyncRuns
            ORDER BY StartedAtUtc DESC;
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
            return new AccountingSyncStatus(true, null, null, null, 0, 0, 0, null, null, null, null);

        return new AccountingSyncStatus(
            true,
            reader.GetDateTime(0),
            reader.IsDBNull(1) ? null : reader.GetDateTime(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    private async Task EnsureSchemaAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = new SqlCommand(_queries.Get("AccountingSchema.sql"), connection)
        {
            CommandTimeout = 120
        };
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<VisitorRow>> ReadVisitorsAsync(SqlConnection source, CancellationToken ct)
    {
        const string sql = """
            SELECT visitorid, visitorname
            FROM visitor
            ORDER BY visitorid
            """;

        var result = new List<VisitorRow>();
        await using var command = new SqlCommand(sql, source) { CommandTimeout = 90 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = Convert.ToInt32(reader["visitorid"]);
            result.Add(new VisitorRow(
                id,
                Convert.ToString(reader["visitorname"]),
                id switch
                {
                    6 => "SHARED",
                    7 => "COLLECTIONS",
                    _ => "SALES"
                },
                id != 8));
        }
        return result;
    }

    private static async Task<List<InvoiceRow>> ReadInvoicesAsync(
        SqlConnection source, string cutoff, CancellationToken ct)
    {
        const string sql = """
            SELECT
                f.Code AS FactorCode,
                f.dnumber,
                f.fnumber,
                f.fdate,
                f.typeindex,
                f.type,
                f.customercode,
                f.customername,
                f.amount,
                f.visitorid,
                v.visitorname,
                c.detailcode
            FROM factor f
            LEFT JOIN visitor v ON v.visitorid = f.visitorid
            LEFT JOIN customer c ON c.shortcode = f.customercode
            WHERE f.typeindex = 1
              AND f.fdate >= @cutoff
            ORDER BY f.Code
            """;

        var result = new List<InvoiceRow>();
        await using var command = new SqlCommand(sql, source) { CommandTimeout = 120 };
        command.Parameters.Add("@cutoff", SqlDbType.NVarChar, 10).Value = cutoff;

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new InvoiceRow(
                Convert.ToInt32(reader["FactorCode"]),
                NullableDecimal(reader["dnumber"]),
                NullableDecimal(reader["fnumber"]),
                Convert.ToString(reader["fdate"]),
                NullableInt(reader["typeindex"]),
                Convert.ToString(reader["type"]),
                Convert.ToString(reader["customercode"]),
                Convert.ToString(reader["detailcode"]),
                Convert.ToString(reader["customername"]),
                NullableDecimal(reader["amount"]),
                NullableInt(reader["visitorid"]),
                Convert.ToString(reader["visitorname"])));
        }
        return result;
    }

    private static async Task<List<CustomerRow>> ReadCustomersAsync(
        SqlConnection source, IReadOnlyCollection<string> shortCodes, CancellationToken ct)
    {
        if (shortCodes.Count == 0)
            return new List<CustomerRow>();

        var result = new List<CustomerRow>();
        var codes = shortCodes.ToArray();

        for (var offset = 0; offset < codes.Length; offset += 200)
        {
            var batch = codes.Skip(offset).Take(200).ToArray();
            var names = batch.Select((_, i) => "@p" + i).ToArray();
            var sql = $"""
                SELECT
                    detailcode, shortcode, customername, managername,
                    economiccode, customertel, customeraddress
                FROM customer
                WHERE shortcode IN ({string.Join(",", names)})
                """;

            await using var command = new SqlCommand(sql, source) { CommandTimeout = 120 };
            for (var i = 0; i < batch.Length; i++)
                command.Parameters.Add(names[i], SqlDbType.NVarChar, 12).Value = batch[i];

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(new CustomerRow(
                    Convert.ToString(reader["detailcode"]) ?? string.Empty,
                    Convert.ToString(reader["shortcode"]),
                    Convert.ToString(reader["customername"]),
                    Convert.ToString(reader["managername"]),
                    Convert.ToString(reader["economiccode"]),
                    Convert.ToString(reader["customertel"]),
                    Convert.ToString(reader["customeraddress"])));
            }
        }

        return result
            .GroupBy(x => x.DetailCode, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
    }

    private static async Task<List<ItemRow>> ReadItemsAsync(
        SqlConnection source, string cutoff, HashSet<int> factorCodes, CancellationToken ct)
    {
        if (factorCodes.Count == 0)
            return new List<ItemRow>();

        const string sql = """
            SELECT
                i.Code AS ItemCode,
                f.Code AS FactorCode,
                f.fdate,
                i.row AS ItemRow,
                i.scode,
                i.name,
                i.des,
                i.count1,
                i.facprice,
                i.factprice
            FROM factor f
            INNER JOIN facitem i
                ON i.docno = f.dnumber
               AND i.facno = f.fnumber
            WHERE f.typeindex = 1
              AND f.fdate >= @cutoff
            ORDER BY i.Code
            """;

        var result = new List<ItemRow>();
        await using var command = new SqlCommand(sql, source) { CommandTimeout = 180 };
        command.Parameters.Add("@cutoff", SqlDbType.NVarChar, 10).Value = cutoff;

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var factorCode = Convert.ToInt32(reader["FactorCode"]);
            if (!factorCodes.Contains(factorCode))
                continue;

            result.Add(new ItemRow(
                Convert.ToInt32(reader["ItemCode"]),
                factorCode,
                Convert.ToString(reader["fdate"]),
                NullableInt(reader["ItemRow"]),
                Convert.ToString(reader["scode"]),
                Convert.ToString(reader["name"]),
                Convert.ToString(reader["des"]),
                NullableDouble(reader["count1"]),
                NullableDecimal(reader["facprice"]),
                NullableDecimal(reader["factprice"])));
        }
        return result;
    }

    private static async Task ReplaceSnapshotAsync(
        SqlConnection destination,
        SqlTransaction transaction,
        string sourceDatabase,
        int fiscalYear,
        string cutoff,
        IReadOnlyList<VisitorRow> visitors,
        IReadOnlyList<CustomerRow> customers,
        IReadOnlyList<InvoiceRow> invoices,
        IReadOnlyList<ItemRow> items,
        CancellationToken ct)
    {
        await ExecuteAsync(destination, transaction,
            "DELETE FROM dbo.AccountingInvoiceItems WHERE SourceDatabase=@db AND FiscalYear=@fy AND FactorDate>=@cutoff;",
            ct, ("@db", sourceDatabase), ("@fy", fiscalYear), ("@cutoff", cutoff));

        await ExecuteAsync(destination, transaction,
            "DELETE FROM dbo.AccountingInvoices WHERE SourceDatabase=@db AND FiscalYear=@fy AND FactorDate>=@cutoff;",
            ct, ("@db", sourceDatabase), ("@fy", fiscalYear), ("@cutoff", cutoff));

        await ExecuteAsync(destination, transaction,
            "DELETE FROM dbo.AccountingVisitors WHERE SourceDatabase=@db AND FiscalYear=@fy;",
            ct, ("@db", sourceDatabase), ("@fy", fiscalYear));

        foreach (var visitor in visitors)
        {
            const string sql = """
                INSERT INTO dbo.AccountingVisitors
                    (SourceDatabase,FiscalYear,VisitorId,VisitorName,RoleType,IsActive,ImportedAtUtc)
                VALUES
                    (@db,@fy,@id,@name,@role,@active,SYSUTCDATETIME());
                """;
            await ExecuteAsync(destination, transaction, sql, ct,
                ("@db", sourceDatabase), ("@fy", fiscalYear), ("@id", visitor.VisitorId),
                ("@name", visitor.VisitorName), ("@role", visitor.RoleType), ("@active", visitor.IsActive));
        }

        foreach (var customer in customers)
        {
            const string sql = """
                UPDATE dbo.AccountingCustomers
                SET ShortCode=@short,CustomerName=@name,ManagerName=@manager,EconomicCode=@economic,
                    CustomerTel=@tel,CustomerAddress=@address,ImportedAtUtc=SYSUTCDATETIME()
                WHERE SourceDatabase=@db AND FiscalYear=@fy AND DetailCode=@detail;

                IF @@ROWCOUNT=0
                INSERT INTO dbo.AccountingCustomers
                    (SourceDatabase,FiscalYear,DetailCode,ShortCode,CustomerName,ManagerName,
                     EconomicCode,CustomerTel,CustomerAddress,ImportedAtUtc)
                VALUES
                    (@db,@fy,@detail,@short,@name,@manager,@economic,@tel,@address,SYSUTCDATETIME());
                """;
            await ExecuteAsync(destination, transaction, sql, ct,
                ("@db", sourceDatabase), ("@fy", fiscalYear), ("@detail", customer.DetailCode),
                ("@short", customer.ShortCode), ("@name", customer.CustomerName),
                ("@manager", customer.ManagerName), ("@economic", customer.EconomicCode),
                ("@tel", customer.CustomerTel), ("@address", customer.CustomerAddress));
        }

        foreach (var invoice in invoices)
        {
            const string sql = """
                INSERT INTO dbo.AccountingInvoices
                    (SourceDatabase,FiscalYear,FactorCode,DocumentNumber,FactorNumber,FactorDate,
                     TypeIndex,TypeDescription,CustomerShortCode,CustomerDetailCode,CustomerName,
                     Amount,VisitorId,VisitorName,ImportedAtUtc)
                VALUES
                    (@db,@fy,@code,@doc,@factor,@date,@typeIndex,@type,@short,@detail,@customer,
                     @amount,@visitorId,@visitorName,SYSUTCDATETIME());
                """;
            await ExecuteAsync(destination, transaction, sql, ct,
                ("@db", sourceDatabase), ("@fy", fiscalYear), ("@code", invoice.FactorCode),
                ("@doc", invoice.DocumentNumber), ("@factor", invoice.FactorNumber),
                ("@date", invoice.FactorDate), ("@typeIndex", invoice.TypeIndex),
                ("@type", invoice.TypeDescription), ("@short", invoice.CustomerShortCode),
                ("@detail", invoice.CustomerDetailCode), ("@customer", invoice.CustomerName),
                ("@amount", invoice.Amount), ("@visitorId", invoice.VisitorId),
                ("@visitorName", invoice.VisitorName));
        }

        foreach (var item in items)
        {
            const string sql = """
                INSERT INTO dbo.AccountingInvoiceItems
                    (SourceDatabase,FiscalYear,ItemCode,FactorCode,FactorDate,ItemRow,
                     ProductCode,ProductName,Description,Quantity,UnitPrice,TotalPrice,ImportedAtUtc)
                VALUES
                    (@db,@fy,@itemCode,@factorCode,@date,@row,@productCode,@productName,
                     @description,@quantity,@unitPrice,@totalPrice,SYSUTCDATETIME());
                """;
            await ExecuteAsync(destination, transaction, sql, ct,
                ("@db", sourceDatabase), ("@fy", fiscalYear), ("@itemCode", item.ItemCode),
                ("@factorCode", item.FactorCode), ("@date", item.FactorDate), ("@row", item.ItemRow),
                ("@productCode", item.ProductCode), ("@productName", item.ProductName),
                ("@description", item.Description), ("@quantity", item.Quantity),
                ("@unitPrice", item.UnitPrice), ("@totalPrice", item.TotalPrice));
        }
    }

    private static async Task StartRunAsync(
        SqlConnection connection, Guid runId, DateTime started, string server,
        string database, int fiscalYear, string cutoff, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO dbo.AccountingSyncRuns
                (RunId,StartedAtUtc,SourceServer,SourceDatabase,FiscalYear,CutoffPersianDate,Status)
            VALUES
                (@id,@started,@server,@database,@year,@cutoff,N'RUNNING');
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", runId);
        command.Parameters.AddWithValue("@started", started);
        command.Parameters.AddWithValue("@server", server);
        command.Parameters.AddWithValue("@database", database);
        command.Parameters.AddWithValue("@year", fiscalYear);
        command.Parameters.AddWithValue("@cutoff", cutoff);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task FinishRunAsync(
        SqlConnection connection, Guid runId, DateTime finished, string status,
        int visitors, int customers, int invoices, int items, string? error, CancellationToken ct)
    {
        const string sql = """
            UPDATE dbo.AccountingSyncRuns
            SET FinishedAtUtc=@finished,Status=@status,VisitorCount=@visitors,
                CustomerCount=@customers,InvoiceCount=@invoices,
                InvoiceItemCount=@items,ErrorMessage=@error
            WHERE RunId=@id;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@finished", finished);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@visitors", visitors);
        command.Parameters.AddWithValue("@customers", customers);
        command.Parameters.AddWithValue("@invoices", invoices);
        command.Parameters.AddWithValue("@items", items);
        command.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("@id", runId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, string sql,
        CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 120 };
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string ToPersianDate(DateTime date)
    {
        var calendar = new PersianCalendar();
        return $"{calendar.GetYear(date):0000}/{calendar.GetMonth(date):00}/{calendar.GetDayOfMonth(date):00}";
    }

    private static int? NullableInt(object value) =>
        value is DBNull ? null : Convert.ToInt32(value);

    private static decimal? NullableDecimal(object value) =>
        value is DBNull ? null : Convert.ToDecimal(value);

    private static double? NullableDouble(object value) =>
        value is DBNull ? null : Convert.ToDouble(value);

    private sealed record VisitorRow(int VisitorId, string? VisitorName, string RoleType, bool IsActive);
    private sealed record CustomerRow(
        string DetailCode, string? ShortCode, string? CustomerName, string? ManagerName,
        string? EconomicCode, string? CustomerTel, string? CustomerAddress);
    private sealed record InvoiceRow(
        int FactorCode, decimal? DocumentNumber, decimal? FactorNumber, string? FactorDate,
        int? TypeIndex, string? TypeDescription, string? CustomerShortCode,
        string? CustomerDetailCode, string? CustomerName, decimal? Amount,
        int? VisitorId, string? VisitorName);
    private sealed record ItemRow(
        int ItemCode, int FactorCode, string? FactorDate, int? ItemRow,
        string? ProductCode, string? ProductName, string? Description,
        double? Quantity, decimal? UnitPrice, decimal? TotalPrice);
}
