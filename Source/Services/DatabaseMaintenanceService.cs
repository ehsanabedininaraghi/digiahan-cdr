using Microsoft.Data.SqlClient;

namespace DigiAhan.CDR.Receiver.Services;

public sealed record DatabaseMaintenanceResult(int DeletedOperationalRows, string RecoveryModel, decimal LogSizeMb, bool ShrinkAttempted);

public sealed class DatabaseMaintenanceService
{
    private readonly string _connectionString;
    private readonly IConfiguration _configuration;

    public DatabaseMaintenanceService(IConfiguration configuration)
    {
        _configuration = configuration;
        _connectionString = configuration.GetConnectionString("DigiAhanCdr")
            ?? throw new InvalidOperationException("ConnectionStrings:DigiAhanCdr is missing.");
    }

    public async Task<DatabaseMaintenanceResult> RunAsync(CancellationToken ct)
    {
        var retentionDays = Math.Clamp(_configuration.GetValue("DatabaseMaintenance:OperationalHistoryDays", 90), 14, 730);
        var targetLogMb = Math.Clamp(_configuration.GetValue("DatabaseMaintenance:TargetLogMb", 512), 128, 8192);
        var allowShrink = _configuration.GetValue("DatabaseMaintenance:ShrinkWhenSimple", true);

        const string cleanup = """
            DECLARE @deleted int=0;
            IF OBJECT_ID(N'dbo.IntegrationJobRuns',N'U') IS NOT NULL
            BEGIN
                DELETE dbo.IntegrationJobRuns WHERE StartedAtUtc<DATEADD(day,-@days,SYSUTCDATETIME());
                SET @deleted+=@@ROWCOUNT;
            END;
            IF OBJECT_ID(N'dbo.DataGatheringRuns',N'U') IS NOT NULL
            BEGIN
                DELETE dbo.DataGatheringRuns WHERE StartedAtUtc<DATEADD(day,-@days,SYSUTCDATETIME());
                SET @deleted+=@@ROWCOUNT;
            END;
            IF OBJECT_ID(N'dbo.AccountingSyncRuns',N'U') IS NOT NULL
            BEGIN
                DELETE dbo.AccountingSyncRuns WHERE StartedAtUtc<DATEADD(day,-@days,SYSUTCDATETIME());
                SET @deleted+=@@ROWCOUNT;
            END;
            SELECT @deleted,
                   (SELECT recovery_model_desc FROM sys.databases WHERE database_id=DB_ID()),
                   CAST((SELECT SUM(size)*8.0/1024 FROM sys.database_files WHERE type_desc=N'LOG') AS decimal(18,1));
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(cleanup, connection) { CommandTimeout = 120 };
        command.Parameters.AddWithValue("@days", retentionDays);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var deleted = reader.GetInt32(0);
        var recovery = reader.GetString(1);
        var logMb = reader.GetDecimal(2);
        await reader.CloseAsync();

        var shrink = allowShrink && string.Equals(recovery, "SIMPLE", StringComparison.OrdinalIgnoreCase)
            && logMb > targetLogMb * 1.5m;
        if (shrink)
        {
            const string shrinkSql = """
                DECLARE @log sysname=(SELECT TOP(1) name FROM sys.database_files WHERE type_desc=N'LOG');
                DECLARE @sql nvarchar(1000)=N'DBCC SHRINKFILE ('+QUOTENAME(@log)+N','+CONVERT(nvarchar(20),@target)+N') WITH NO_INFOMSGS;';
                EXEC sys.sp_executesql @sql;
                """;
            await using var shrinkCommand = new SqlCommand(shrinkSql, connection) { CommandTimeout = 300 };
            shrinkCommand.Parameters.AddWithValue("@target", targetLogMb);
            await shrinkCommand.ExecuteNonQueryAsync(ct);
        }

        return new DatabaseMaintenanceResult(deleted, recovery, logMb, shrink);
    }
}
