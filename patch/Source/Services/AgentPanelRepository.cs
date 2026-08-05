using DigiAhan.CDR.Receiver.Models;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.RegularExpressions;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class AgentPanelRepository
{
    private readonly string _connectionString;

    public AgentPanelRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DigiAhanCdr")
            ?? throw new InvalidOperationException("ConnectionStrings:DigiAhanCdr is missing.");
    }

    public async Task EnsureSchema(CancellationToken ct)
    {
        const string sql = """
        IF OBJECT_ID(N'dbo.AgentIncomingEvents',N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.AgentIncomingEvents
            (
                Id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
                Extension nvarchar(10) NOT NULL,
                CallerNumber nvarchar(32) NOT NULL,
                LinkedId nvarchar(100) NULL,
                EventTimeUtc datetime2(0) NOT NULL,
                CustomerName nvarchar(300) NULL,
                CompanyName nvarchar(300) NULL,
                OwnerName nvarchar(200) NULL,
                IsKnownCustomer bit NOT NULL,
                CustomerRank nvarchar(10) NOT NULL,
                Temperature nvarchar(20) NOT NULL,
                LastInvoiceDate nvarchar(10) NULL,
                LastInvoiceAmount decimal(19,4) NULL,
                LastProduct nvarchar(400) NULL,
                Sales30Days decimal(19,4) NOT NULL,
                CreatedAtUtc datetime2(0) NOT NULL
                    CONSTRAINT DF_AgentIncomingEvents_CreatedAtUtc DEFAULT(SYSUTCDATETIME())
            );
            CREATE INDEX IX_AgentIncomingEvents_ExtensionCreated
                ON dbo.AgentIncomingEvents(Extension,CreatedAtUtc DESC);
            CREATE INDEX IX_AgentIncomingEvents_CallerCreated
                ON dbo.AgentIncomingEvents(CallerNumber,CreatedAtUtc DESC);
        END;

        IF OBJECT_ID(N'dbo.AgentCallOutcomes',N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.AgentCallOutcomes
            (
                Id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
                Extension nvarchar(10) NOT NULL,
                CallerNumber nvarchar(32) NOT NULL,
                Outcome nvarchar(30) NOT NULL,
                Note nvarchar(1000) NULL,
                FollowUpAt datetime2(0) NULL,
                LinkedId nvarchar(100) NULL,
                CreatedAtUtc datetime2(0) NOT NULL
                    CONSTRAINT DF_AgentCallOutcomes_CreatedAtUtc DEFAULT(SYSUTCDATETIME())
            );
            CREATE INDEX IX_AgentCallOutcomes_ExtensionCreated
                ON dbo.AgentCallOutcomes(Extension,CreatedAtUtc DESC);
            CREATE INDEX IX_AgentCallOutcomes_CallerCreated
                ON dbo.AgentCallOutcomes(CallerNumber,CreatedAtUtc DESC);
        END;
        """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordIncoming(AgentCustomerCard card, CancellationToken ct)
    {
        await EnsureSchema(ct);

        const string sql = """
        IF NOT EXISTS
        (
            SELECT 1
            FROM dbo.AgentIncomingEvents
            WHERE Extension=@extension
              AND CallerNumber=@caller
              AND
              (
                  (NULLIF(@linkedId,N'') IS NOT NULL AND LinkedId=@linkedId)
                  OR CreatedAtUtc>=DATEADD(second,-15,SYSUTCDATETIME())
              )
        )
        BEGIN
            INSERT INTO dbo.AgentIncomingEvents
            (
                Extension,CallerNumber,LinkedId,EventTimeUtc,
                CustomerName,CompanyName,OwnerName,IsKnownCustomer,
                CustomerRank,Temperature,LastInvoiceDate,LastInvoiceAmount,
                LastProduct,Sales30Days
            )
            VALUES
            (
                @extension,@caller,@linkedId,@eventTime,
                @customer,@company,@owner,@known,
                @rank,@temperature,@invoiceDate,@invoiceAmount,
                @product,@sales
            );
        END;
        """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);

        Add(command, "@extension", SqlDbType.NVarChar, 10, card.Extension);
        Add(command, "@caller", SqlDbType.NVarChar, 32, card.CallerNumber);
        Add(command, "@linkedId", SqlDbType.NVarChar, 100, card.LinkedId);
        command.Parameters.Add("@eventTime", SqlDbType.DateTime2).Value = card.EventTimeUtc;
        Add(command, "@customer", SqlDbType.NVarChar, 300, card.CustomerName);
        Add(command, "@company", SqlDbType.NVarChar, 300, card.CompanyName);
        Add(command, "@owner", SqlDbType.NVarChar, 200, card.OwnerName);
        command.Parameters.Add("@known", SqlDbType.Bit).Value = card.IsKnownCustomer;
        Add(command, "@rank", SqlDbType.NVarChar, 10, card.CustomerRank);
        Add(command, "@temperature", SqlDbType.NVarChar, 20, card.Temperature);
        Add(command, "@invoiceDate", SqlDbType.NVarChar, 10, card.LastInvoiceDate);
        command.Parameters.Add("@invoiceAmount", SqlDbType.Decimal).Value =
            card.LastInvoiceAmount.HasValue ? card.LastInvoiceAmount.Value : DBNull.Value;
        Add(command, "@product", SqlDbType.NVarChar, 400, card.LastProduct);
        command.Parameters.Add("@sales", SqlDbType.Decimal).Value = card.Sales30Days;

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<AgentOutcomeRow> SaveOutcome(AgentOutcomeRequest request, CancellationToken ct)
    {
        await EnsureSchema(ct);

        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "FOLLOW_UP", "QUOTED", "ORDER", "NO_NEED"
        };

        var outcome = request.Outcome.Trim().ToUpperInvariant();
        if (!valid.Contains(outcome))
            throw new ArgumentException("Outcome is invalid.");

        const string sql = """
        INSERT INTO dbo.AgentCallOutcomes
            (Extension,CallerNumber,Outcome,Note,FollowUpAt,LinkedId)
        OUTPUT
            inserted.Id,inserted.Extension,inserted.CallerNumber,inserted.Outcome,
            inserted.Note,inserted.FollowUpAt,inserted.LinkedId,inserted.CreatedAtUtc
        VALUES
            (@extension,@caller,@outcome,@note,@followUp,@linkedId);
        """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);

        Add(command, "@extension", SqlDbType.NVarChar, 10, request.Extension.Trim());
        Add(command, "@caller", SqlDbType.NVarChar, 32, request.CallerNumber.Trim());
        Add(command, "@outcome", SqlDbType.NVarChar, 30, outcome);
        Add(command, "@note", SqlDbType.NVarChar, 1000, request.Note?.Trim());
        command.Parameters.Add("@followUp", SqlDbType.DateTime2).Value =
            request.FollowUpAt.HasValue ? request.FollowUpAt.Value : DBNull.Value;
        Add(command, "@linkedId", SqlDbType.NVarChar, 100, request.LinkedId?.Trim());

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return ReadOutcome(reader);
    }

    public async Task<IReadOnlyList<AgentIncomingEventRow>> RecentIncoming(
        string extensionsCsv,
        int take,
        CancellationToken ct)
    {
        await EnsureSchema(ct);
        take = Math.Clamp(take, 1, 50);
        var extensions = ParseExtensions(extensionsCsv);
        var (inClause, parameters) = BuildInParameters(extensions);

        var sql = $"""
        SELECT TOP(@take)
            Id,Extension,CallerNumber,LinkedId,EventTimeUtc,
            CustomerName,CompanyName,OwnerName,IsKnownCustomer,
            CustomerRank,Temperature,LastInvoiceDate,LastInvoiceAmount,
            LastProduct,Sales30Days,CreatedAtUtc
        FROM dbo.AgentIncomingEvents
        WHERE Extension IN ({inClause})
        ORDER BY CreatedAtUtc DESC,Id DESC;
        """;

        var result = new List<AgentIncomingEventRow>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@take", SqlDbType.Int).Value = take;
        foreach (var item in parameters)
            command.Parameters.Add(item);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new AgentIncomingEventRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                GetString(reader, 3),
                reader.GetDateTime(4),
                GetString(reader, 5),
                GetString(reader, 6),
                GetString(reader, 7),
                reader.GetBoolean(8),
                reader.GetString(9),
                reader.GetString(10),
                GetString(reader, 11),
                reader.IsDBNull(12) ? null : reader.GetDecimal(12),
                GetString(reader, 13),
                reader.GetDecimal(14),
                reader.GetDateTime(15)));
        }

        return result;
    }

    public async Task<IReadOnlyList<AgentOutcomeRow>> RecentOutcomes(
        string extensionsCsv,
        int take,
        CancellationToken ct)
    {
        await EnsureSchema(ct);
        take = Math.Clamp(take, 1, 50);
        var extensions = ParseExtensions(extensionsCsv);
        var (inClause, parameters) = BuildInParameters(extensions);

        var sql = $"""
        SELECT TOP(@take)
            Id,Extension,CallerNumber,Outcome,Note,FollowUpAt,LinkedId,CreatedAtUtc
        FROM dbo.AgentCallOutcomes
        WHERE Extension IN ({inClause})
        ORDER BY CreatedAtUtc DESC,Id DESC;
        """;

        var result = new List<AgentOutcomeRow>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@take", SqlDbType.Int).Value = take;
        foreach (var item in parameters)
            command.Parameters.Add(item);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(ReadOutcome(reader));

        return result;
    }

    public async Task<AgentPanelStats> Stats(string extensionsCsv, CancellationToken ct)
    {
        await EnsureSchema(ct);
        var extensions = ParseExtensions(extensionsCsv);
        var (inClause, _) = BuildInParameters(extensions);
        var startUtc = StartOfIranDayUtc();

        var sql = $"""
        SELECT
            (SELECT COUNT(*) FROM dbo.AgentIncomingEvents
             WHERE Extension IN ({inClause}) AND CreatedAtUtc>=@startUtc) AS CallsToday,
            (SELECT COUNT(*) FROM dbo.AgentCallOutcomes
             WHERE Extension IN ({inClause}) AND CreatedAtUtc>=@startUtc) AS OutcomesToday,
            (SELECT COUNT(*) FROM dbo.AgentCallOutcomes
             WHERE Extension IN ({inClause}) AND CreatedAtUtc>=@startUtc AND Outcome=N'FOLLOW_UP') AS FollowUpsToday,
            (SELECT COUNT(*) FROM dbo.AgentCallOutcomes
             WHERE Extension IN ({inClause}) AND CreatedAtUtc>=@startUtc AND Outcome=N'QUOTED') AS QuotesToday,
            (SELECT COUNT(*) FROM dbo.AgentCallOutcomes
             WHERE Extension IN ({inClause}) AND CreatedAtUtc>=@startUtc AND Outcome=N'ORDER') AS OrdersToday,
            (SELECT COUNT(*) FROM dbo.AgentCallOutcomes
             WHERE Extension IN ({inClause}) AND Outcome=N'FOLLOW_UP'
               AND FollowUpAt IS NOT NULL AND FollowUpAt<=DATEADD(hour,24,SYSDATETIME())) AS PendingFollowUps;
        """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@startUtc", SqlDbType.DateTime2).Value = startUtc;

        for (var i = 0; i < extensions.Length; i++)
            command.Parameters.Add($"@e{i}", SqlDbType.NVarChar, 10).Value = extensions[i];

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return new AgentPanelStats(
            Convert.ToInt32(reader[0]),
            Convert.ToInt32(reader[1]),
            Convert.ToInt32(reader[2]),
            Convert.ToInt32(reader[3]),
            Convert.ToInt32(reader[4]),
            Convert.ToInt32(reader[5]));
    }

    private static string[] ParseExtensions(string? value)
    {
        var result = (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => Regex.IsMatch(x, @"^\d{3}$"))
            .Distinct()
            .Take(20)
            .ToArray();

        return result.Length > 0 ? result : new[] { "201" };
    }

    private static (string Sql, List<SqlParameter> Parameters) BuildInParameters(string[] extensions)
    {
        var parameters = new List<SqlParameter>();
        var names = new List<string>();

        for (var i = 0; i < extensions.Length; i++)
        {
            var name = $"@e{i}";
            names.Add(name);
            parameters.Add(new SqlParameter(name, SqlDbType.NVarChar, 10) { Value = extensions[i] });
        }

        return (string.Join(",", names), parameters);
    }

    private static DateTime StartOfIranDayUtc()
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Iran Standard Time");
            var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
            var localStart = DateTime.SpecifyKind(localNow.Date, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTimeToUtc(localStart, zone);
        }
        catch
        {
            return DateTime.UtcNow.Date;
        }
    }

    private static AgentOutcomeRow ReadOutcome(SqlDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            GetString(reader, 4),
            reader.IsDBNull(5) ? null : reader.GetDateTime(5),
            GetString(reader, 6),
            reader.GetDateTime(7));

    private static string? GetString(SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void Add(SqlCommand command, string name, SqlDbType type, int size, object? value)
    {
        var parameter = command.Parameters.Add(name, type, size);
        parameter.Value = value is null || string.IsNullOrWhiteSpace(Convert.ToString(value))
            ? DBNull.Value
            : value;
    }
}
