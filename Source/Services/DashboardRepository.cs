using DigiAhan.CDR.Receiver.Models;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Diagnostics;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class DashboardRepository
{
    private readonly string _cs;
    private readonly SqlQueryStore _queries;
    private readonly ILogger<DashboardRepository> _logger;

    public DashboardRepository(
        IConfiguration configuration,
        SqlQueryStore queries,
        ILogger<DashboardRepository> logger)
    {
        _cs = configuration.GetConnectionString("DigiAhanCdr")
              ?? throw new InvalidOperationException("Missing connection string.");
        _queries = queries;
        _logger = logger;
    }

    public async Task<DashboardSummary> Summary(
        DateTime startDate, DateTime endDate, string? extension, CancellationToken ct)
    {
        await using var connection = await OpenConnection(ct);
        await using var command = CreateCommand(_queries.Get("DashboardSummary.sql"), connection);
        AddRangeParameters(command, startDate, endDate, extension);
        await using var reader = await ExecuteReader(command, "DashboardSummary", ct);
        await reader.ReadAsync(ct);

        return new DashboardSummary(
            startDate.Date,
            GetInt(reader, "T"), GetInt(reader, "A"), GetInt(reader, "M"),
            GetInt(reader, "I"), GetInt(reader, "O"), GetInt(reader, "B"),
            GetInt(reader, "Av"), GetInt(reader, "KnownCustomers"),
            GetInt(reader, "NewCustomers"), GetDateTime(reader, "L"), GetDateTime(reader, "R"));
    }

    public async Task<IReadOnlyList<HourlyPoint>> Hourly(
        DateTime startDate, DateTime endDate, string? extension, CancellationToken ct)
    {
        var items = new List<HourlyPoint>();
        await using var connection = await OpenConnection(ct);
        await using var command = CreateCommand(_queries.Get("DashboardHourly.sql"), connection);
        AddRangeParameters(command, startDate, endDate, extension);
        await using var reader = await ExecuteReader(command, "DashboardHourly", ct);

        while (await reader.ReadAsync(ct))
            items.Add(new HourlyPoint(GetInt(reader, "H"), GetInt(reader, "T"), GetInt(reader, "A"), GetInt(reader, "M")));

        return items;
    }

    public async Task<IReadOnlyList<DailyPoint>> Daily(
        DateTime startDate, DateTime endDate, string? extension, CancellationToken ct)
    {
        var items = new List<DailyPoint>();
        await using var connection = await OpenConnection(ct);
        await using var command = CreateCommand(_queries.Get("DashboardDaily.sql"), connection);
        AddRangeParameters(command, startDate, endDate, extension);
        await using var reader = await ExecuteReader(command, "DashboardDaily", ct);

        while (await reader.ReadAsync(ct))
        {
            items.Add(new DailyPoint(
                GetDateTime(reader, "D") ?? startDate.Date,
                GetInt(reader, "T"),
                GetInt(reader, "A"),
                GetInt(reader, "M"),
                GetInt(reader, "I"),
                GetInt(reader, "O"),
                GetInt(reader, "NewCustomers"),
                GetInt(reader, "KnownCustomers"),
                GetInt(reader, "B")));
        }

        return items;
    }

    public async Task<IReadOnlyList<ExtensionStat>> Extensions(
        DateTime startDate, DateTime endDate, CancellationToken ct)
    {
        var items = new List<ExtensionStat>();
        await using var connection = await OpenConnection(ct);
        await using var command = CreateCommand(_queries.Get("DashboardExtensions.sql"), connection);
        AddRangeParameters(command, startDate, endDate, null);
        await using var reader = await ExecuteReader(command, "DashboardExtensions", ct);

        while (await reader.ReadAsync(ct))
            items.Add(new ExtensionStat(
                GetString(reader, "E") ?? string.Empty,
                GetInt(reader, "T"), GetInt(reader, "I"), GetInt(reader, "O"),
                GetInt(reader, "A"), GetInt(reader, "M"),
                GetInt(reader, "B"), GetInt(reader, "Av")));

        return items;
    }

    public async Task<CallsPage> Calls(
        DateTime startDate, DateTime endDate, string? extension,
        string? search, string? status,
        int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 200);

        await using var connection = await OpenConnection(ct);

        int total;
        await using (var countCommand = CreateCommand(_queries.Get("DashboardCallsCount.sql"), connection))
        {
            AddCallsParameters(countCommand, startDate, endDate, extension, search, status);
            total = Convert.ToInt32(await ExecuteScalar(countCommand, "DashboardCallsCount", ct) ?? 0);
        }

        var rowStart = ((page - 1) * pageSize) + 1;
        var rowEnd = page * pageSize;
        var items = new List<CallRow>();

        await using (var pageCommand = CreateCommand(_queries.Get("DashboardCallsPage.sql"), connection))
        {
            AddCallsParameters(pageCommand, startDate, endDate, extension, search, status);
            pageCommand.Parameters.Add("@rowStart", SqlDbType.Int).Value = rowStart;
            pageCommand.Parameters.Add("@rowEnd", SqlDbType.Int).Value = rowEnd;

            await using var reader = await ExecuteReader(pageCommand, "DashboardCallsPage", ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(new CallRow(
                    GetLong(reader, "RawCDRId"),
                    GetDateTime(reader, "Calldate"),
                    GetString(reader, "Src"),
                    GetString(reader, "Dst"),
                    GetString(reader, "Direction") ?? "unknown",
                    GetString(reader, "Disposition"),
                    GetInt(reader, "Duration"),
                    GetInt(reader, "Billsec"),
                    GetString(reader, "RecordingFile"),
                    GetString(reader, "LinkedId"),
                    GetString(reader, "UniqueId"),
                    GetString(reader, "Did"),
                    GetString(reader, "Dcontext"),
                    GetString(reader, "CustomerPhone"),
                    GetString(reader, "CustomerName"),
                    GetString(reader, "CompanyName"),
                    GetString(reader, "OwnerName"),
                    GetString(reader, "DidarContactCode"),
                    GetBool(reader, "IsNewCustomer")));
            }
        }

        return new CallsPage(total, page, pageSize, items);
    }

    public async Task<SyncStatus> Sync(CancellationToken ct)
    {
        await using var connection = await OpenConnection(ct);
        await using var command = CreateCommand(_queries.Get("DashboardSync.sql"), connection);
        await using var reader = await ExecuteReader(command, "DashboardSync", ct);

        DateTime? started = null, finished = null, lastReceived = null, lastCdr = null;
        string? batchStatus = null;
        int inserted = 0, duplicates = 0, errors = 0, rowsLastHour = 0;

        if (await reader.ReadAsync(ct))
        {
            started = GetDateTime(reader, "StartedAtUtc");
            finished = GetDateTime(reader, "FinishedAtUtc");
            batchStatus = GetString(reader, "Status");
            inserted = GetInt(reader, "InsertedCount");
            duplicates = GetInt(reader, "DuplicateCount");
            errors = GetInt(reader, "ErrorCount");
        }

        await reader.NextResultAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            lastReceived = reader.IsDBNull(0) ? null : reader.GetDateTime(0);
            lastCdr = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
            rowsLastHour = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader[2]);
        }

        return new SyncStatus(started, finished, batchStatus, inserted, duplicates, errors, lastReceived, lastCdr, rowsLastHour);
    }

    public async Task<IReadOnlyList<SellerPerformanceRow>> SellerPerformance(
        DateTime startDate, DateTime endDate, string? extension, CancellationToken ct)
    {
        const string sql = """
            SELECT
                Extension,
                SUM(CASE WHEN Outcome=N'FOLLOW_UP' THEN 1 ELSE 0 END) AS FollowUps,
                SUM(CASE WHEN Outcome=N'QUOTED' THEN 1 ELSE 0 END) AS Quotes,
                SUM(CASE WHEN Outcome=N'ORDER' THEN 1 ELSE 0 END) AS Orders,
                SUM(CASE WHEN Outcome=N'NO_NEED' THEN 1 ELSE 0 END) AS NoNeed,
                SUM(CASE WHEN NULLIF(LTRIM(RTRIM(Note)),N'') IS NOT NULL THEN 1 ELSE 0 END) AS Notes,
                COUNT(*) AS TotalOutcomes
            FROM dbo.AgentCallOutcomes
            WHERE CreatedAtUtc>=@s AND CreatedAtUtc<@e
              AND (@ext=N'all' OR Extension=@ext)
            GROUP BY Extension
            ORDER BY Orders DESC,Quotes DESC,FollowUps DESC,Extension;
            """;
        var rows = new List<SellerPerformanceRow>();
        await using var connection = await OpenConnection(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        command.Parameters.Add("@s", SqlDbType.DateTime2).Value = startDate.Date;
        command.Parameters.Add("@e", SqlDbType.DateTime2).Value = endDate.Date.AddDays(1);
        command.Parameters.Add("@ext", SqlDbType.NVarChar, 20).Value =
            string.IsNullOrWhiteSpace(extension) ? "all" : extension.Trim();
        await using var reader = await ExecuteReader(command, "SellerPerformance", ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new SellerPerformanceRow(
                reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6)));
        return rows;
    }

    private async Task<SqlConnection> OpenConnection(CancellationToken ct)
    {
        var connection = new SqlConnection(_cs);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static SqlCommand CreateCommand(string sql, SqlConnection connection)
        => new(sql, connection) { CommandTimeout = 90 };

    private async Task<SqlDataReader> ExecuteReader(SqlCommand command, string operation, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var reader = await command.ExecuteReaderAsync(ct);
            _logger.LogInformation("SQL success. Operation={Operation} DurationMs={DurationMs}", operation, sw.ElapsedMilliseconds);
            return reader;
        }
        catch (Exception ex)
        {
            LogSqlFailure(ex, operation, command, sw.ElapsedMilliseconds);
            throw;
        }
    }

    private async Task<object?> ExecuteScalar(SqlCommand command, string operation, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await command.ExecuteScalarAsync(ct);
            _logger.LogInformation("SQL success. Operation={Operation} DurationMs={DurationMs}", operation, sw.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            LogSqlFailure(ex, operation, command, sw.ElapsedMilliseconds);
            throw;
        }
    }

    private void LogSqlFailure(Exception ex, string operation, SqlCommand command, long durationMs)
    {
        var parameters = string.Join(", ", command.Parameters.Cast<SqlParameter>()
            .Select(p => $"{p.ParameterName}={FormatParameter(p.Value)}"));

        _logger.LogError(ex,
            "SQL failed. Operation={Operation} DurationMs={DurationMs} Parameters=[{Parameters}] SQL={Sql}",
            operation, durationMs, parameters, command.CommandText);
    }

    private static string FormatParameter(object? value)
        => value is null or DBNull ? "NULL" : value is DateTime dt ? dt.ToString("O") : value.ToString() ?? string.Empty;

    private static void AddRangeParameters(
        SqlCommand command, DateTime startDate, DateTime endDate, string? extension)
    {
        var start = startDate.Date;
        var endExclusive = endDate.Date.AddDays(1);

        command.Parameters.Add("@s", SqlDbType.DateTime2).Value = start;
        command.Parameters.Add("@e", SqlDbType.DateTime2).Value = endExclusive;
        command.Parameters.Add("@ext", SqlDbType.NVarChar, 20).Value =
            string.IsNullOrWhiteSpace(extension) ? "all" : extension.Trim();
    }

    private static void AddCallsParameters(
        SqlCommand command, DateTime startDate, DateTime endDate, string? extension,
        string? search, string? status)
    {
        AddRangeParameters(command, startDate, endDate, extension);
        command.Parameters.Add("@q", SqlDbType.NVarChar, 200).Value = (search ?? string.Empty).Trim();
        command.Parameters.Add("@st", SqlDbType.NVarChar, 20).Value =
            string.IsNullOrWhiteSpace(status) ? "all" : status.Trim().ToLowerInvariant();
    }

    private static int GetInt(SqlDataReader reader, string name)
        => reader.IsDBNull(reader.GetOrdinal(name)) ? 0 : Convert.ToInt32(reader[name]);

    private static long GetLong(SqlDataReader reader, string name)
        => reader.IsDBNull(reader.GetOrdinal(name)) ? 0L : Convert.ToInt64(reader[name]);

    private static bool GetBool(SqlDataReader reader, string name)
        => !reader.IsDBNull(reader.GetOrdinal(name)) && Convert.ToBoolean(reader[name]);

    private static DateTime? GetDateTime(SqlDataReader reader, string name)
        => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetDateTime(reader.GetOrdinal(name));

    private static string? GetString(SqlDataReader reader, string name)
        => reader.IsDBNull(reader.GetOrdinal(name)) ? null : Convert.ToString(reader[name]);
}
