using DigiAhan.CDR.Receiver.Models;
using Microsoft.Data.SqlClient;
using System.Data;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class IntegrationSchedulerRepository
{
    private readonly string _connectionString;
    private readonly SqlQueryStore _queries;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    public IntegrationSchedulerRepository(IConfiguration configuration, SqlQueryStore queries)
    {
        _connectionString = configuration.GetConnectionString("DigiAhanCdr")
            ?? throw new InvalidOperationException("ConnectionStrings:DigiAhanCdr is missing.");
        _queries = queries;
    }

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_schemaReady) return;
        await _schemaGate.WaitAsync(ct);
        try
        {
            if (_schemaReady) return;
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(ct);
            await using var command = new SqlCommand(_queries.Get("SystemOperationsV42.sql"), connection)
            { CommandTimeout = 180 };
            await command.ExecuteNonQueryAsync(ct);
            _schemaReady = true;
        }
        finally { _schemaGate.Release(); }
    }

    public async Task<IReadOnlyList<IntegrationScheduleRow>> GetAllAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        const string sql = """
            SELECT JobKey,DisplayName,IntervalMinutes,IsEnabled,LastStartedAtUtc,
                   LastFinishedAtUtc,LastStatus,LastDurationMs,LastError,NextRunAtUtc,ConsecutiveFailures
            FROM dbo.IntegrationSchedules ORDER BY JobKey;
            """;
        var rows = new List<IntegrationScheduleRow>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(Read(reader));
        return rows;
    }

    public async Task<IReadOnlyList<string>> GetDueJobKeysAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        const string sql = """
            SELECT TOP(5) JobKey
            FROM dbo.IntegrationSchedules
            WHERE IsEnabled=1 AND NextRunAtUtc<=SYSUTCDATETIME() AND ISNULL(LastStatus,N'')<>N'RUNNING'
            ORDER BY NextRunAtUtc,JobKey;
            """;
        var result = new List<string>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0));
        return result;
    }

    public async Task<Guid?> TryStartAsync(string jobKey, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        var runId = Guid.NewGuid();
        const string sql = """
            DECLARE @changed int = 0;
            UPDATE dbo.IntegrationSchedules WITH(UPDLOCK,ROWLOCK)
            SET LastStartedAtUtc=SYSUTCDATETIME(),LastStatus=N'RUNNING',LastError=NULL,UpdatedAtUtc=SYSUTCDATETIME()
            WHERE JobKey=@job AND IsEnabled=1 AND NextRunAtUtc<=SYSUTCDATETIME() AND ISNULL(LastStatus,N'')<>N'RUNNING';
            SET @changed=@@ROWCOUNT;
            IF @changed=1
                INSERT dbo.IntegrationJobRuns(RunId,JobKey,StartedAtUtc,Status)
                VALUES(@run,@job,SYSUTCDATETIME(),N'RUNNING');
            SELECT @changed;
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@job", SqlDbType.NVarChar, 50).Value = jobKey;
        command.Parameters.Add("@run", SqlDbType.UniqueIdentifier).Value = runId;
        var changed = Convert.ToInt32(await command.ExecuteScalarAsync(ct) ?? 0);
        return changed > 0 ? runId : null;
    }

    public async Task FinishAsync(Guid runId, string jobKey, bool success, long durationMs, string? detail, CancellationToken ct)
    {
        const string sql = """
            DECLARE @status nvarchar(20)=CASE WHEN @success=1 THEN N'SUCCESS' ELSE N'FAILED' END;
            UPDATE dbo.IntegrationSchedules
            SET LastFinishedAtUtc=SYSUTCDATETIME(),LastStatus=@status,LastDurationMs=@duration,
                LastError=CASE WHEN @success=1 THEN NULL ELSE LEFT(@detail,2000) END,
                NextRunAtUtc=DATEADD(minute,IntervalMinutes,SYSUTCDATETIME()),
                ConsecutiveFailures=CASE WHEN @success=1 THEN 0 ELSE ConsecutiveFailures+1 END,
                UpdatedAtUtc=SYSUTCDATETIME()
            WHERE JobKey=@job;
            UPDATE dbo.IntegrationJobRuns
            SET FinishedAtUtc=SYSUTCDATETIME(),Status=@status,DurationMs=@duration,Detail=LEFT(@detail,2000)
            WHERE RunId=@run;
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@job", SqlDbType.NVarChar, 50).Value = jobKey;
        command.Parameters.Add("@run", SqlDbType.UniqueIdentifier).Value = runId;
        command.Parameters.Add("@success", SqlDbType.Bit).Value = success;
        command.Parameters.Add("@duration", SqlDbType.BigInt).Value = durationMs;
        command.Parameters.Add("@detail", SqlDbType.NVarChar, 2000).Value = string.IsNullOrWhiteSpace(detail) ? DBNull.Value : detail;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAsync(string jobKey, IntegrationScheduleUpdate update, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        var interval = Math.Clamp(update.IntervalMinutes, 1, 10080);
        const string sql = """
            UPDATE dbo.IntegrationSchedules
            SET IntervalMinutes=@interval,IsEnabled=@enabled,
                NextRunAtUtc=CASE WHEN @enabled=1 THEN SYSUTCDATETIME() ELSE NextRunAtUtc END,
                LastStatus=CASE WHEN @enabled=0 THEN N'DISABLED' ELSE LastStatus END,
                UpdatedAtUtc=SYSUTCDATETIME()
            WHERE JobKey=@job;
            IF @@ROWCOUNT=0 THROW 51042,N'Unknown integration job.',1;
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@job", SqlDbType.NVarChar, 50).Value = jobKey;
        command.Parameters.Add("@interval", SqlDbType.Int).Value = interval;
        command.Parameters.Add("@enabled", SqlDbType.Bit).Value = update.IsEnabled;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task ForceDueAsync(string jobKey, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        const string sql = "UPDATE dbo.IntegrationSchedules SET NextRunAtUtc=SYSUTCDATETIME(),LastStatus=NULL WHERE JobKey=@job AND IsEnabled=1;";
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@job", SqlDbType.NVarChar, 50).Value = jobKey;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static IntegrationScheduleRow Read(SqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetBoolean(3),
        reader.IsDBNull(4) ? null : reader.GetDateTime(4), reader.IsDBNull(5) ? null : reader.GetDateTime(5),
        reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetInt64(7),
        reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetDateTime(9), reader.GetInt32(10));
}
