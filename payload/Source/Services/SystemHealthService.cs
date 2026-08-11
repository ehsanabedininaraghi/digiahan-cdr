using DigiAhan.CDR.Receiver.Models;
using Microsoft.Data.SqlClient;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class SystemHealthService
{
    private readonly string _connectionString;
    private readonly IntegrationSchedulerRepository _schedules;

    public SystemHealthService(IConfiguration configuration, IntegrationSchedulerRepository schedules)
    {
        _connectionString = configuration.GetConnectionString("DigiAhanCdr")
            ?? throw new InvalidOperationException("ConnectionStrings:DigiAhanCdr is missing.");
        _schedules = schedules;
    }

    public async Task<SystemHealthSnapshot> GetAsync(CancellationToken ct)
    {
        var jobs = await _schedules.GetAllAsync(ct);
        const string sql = """
            SELECT
                DidarContacts=CASE WHEN OBJECT_ID(N'dbo.DidarContacts',N'U') IS NULL THEN 0 ELSE (SELECT COUNT_BIG(*) FROM dbo.DidarContacts WHERE ISNULL(IsDeleted,0)=0) END,
                DidarPhones=CASE WHEN OBJECT_ID(N'dbo.DidarContactPhones',N'U') IS NULL THEN 0 ELSE (SELECT COUNT_BIG(*) FROM dbo.DidarContactPhones) END,
                LastDidarSourceSyncAtUtc=CASE WHEN OBJECT_ID(N'dbo.DidarContacts',N'U') IS NULL THEN NULL ELSE (SELECT MAX(LastSyncedAt) FROM dbo.DidarContacts) END,
                LastCdrAt=CASE WHEN OBJECT_ID(N'dbo.RawCDR',N'U') IS NULL THEN NULL ELSE (SELECT MAX(ReceivedAtUtc) FROM dbo.RawCDR) END,
                LastAccountingSyncAtUtc=CASE WHEN OBJECT_ID(N'dbo.AccountingSyncRuns',N'U') IS NULL THEN NULL ELSE (SELECT MAX(FinishedAtUtc) FROM dbo.AccountingSyncRuns WHERE Status=N'SUCCESS') END,
                LastAccountingFactorDate=CASE WHEN OBJECT_ID(N'dbo.AccountingInvoices',N'U') IS NULL THEN NULL ELSE (SELECT MAX(FactorDate) FROM dbo.AccountingInvoices) END,
                RecoveryModel=(SELECT recovery_model_desc FROM sys.databases WHERE database_id=DB_ID()),
                DatabaseSizeMb=CAST((SELECT SUM(size)*8.0/1024 FROM sys.database_files) AS decimal(18,1)),
                LogSizeMb=CAST((SELECT SUM(size)*8.0/1024 FROM sys.database_files WHERE type_desc=N'LOG') AS decimal(18,1));
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        var didarContacts = reader.GetInt64(0);
        var didarPhones = reader.GetInt64(1);
        DateTime? lastDidar = reader.IsDBNull(2) ? null : TehranClock.AsUtc(reader.GetDateTime(2));
        DateTime? lastCdr = reader.IsDBNull(3) ? null : TehranClock.AsUtc(reader.GetDateTime(3));
        DateTime? lastAccounting = reader.IsDBNull(4) ? null : TehranClock.AsUtc(reader.GetDateTime(4));
        var lastFactor = reader.IsDBNull(5) ? null : reader.GetString(5);
        var recovery = reader.IsDBNull(6) ? "UNKNOWN" : reader.GetString(6);
        var databaseMb = reader.IsDBNull(7) ? 0m : reader.GetDecimal(7);
        var logMb = reader.IsDBNull(8) ? 0m : reader.GetDecimal(8);

        var issabelStatus = lastCdr is null ? "NO_DATA" :
            DateTime.UtcNow - lastCdr.Value <= TimeSpan.FromMinutes(15) ? "OK" : "STALE";
        var didarStatus = didarContacts == 0 || didarPhones == 0 ? "NO_DATA" :
            lastDidar.HasValue && DateTime.UtcNow - lastDidar.Value <= TimeSpan.FromMinutes(30) ? "OK" : "STALE";
        var accountingJob = jobs.FirstOrDefault(x => x.JobKey == "ACCOUNTING");
        var accountingStatus = accountingJob?.LastStatus ?? (lastAccounting is null ? "NO_DATA" : "OK");
        var pending = jobs.Count(x => x.IsEnabled && x.NextRunAtUtc <= DateTime.UtcNow);

        return new SystemHealthSnapshot(
            "OK",
            didarStatus,
            didarContacts,
            didarPhones,
            lastDidar,
            issabelStatus,
            lastCdr,
            accountingStatus,
            lastAccounting,
            lastFactor,
            recovery,
            databaseMb,
            logMb,
            pending,
            jobs,
            DateTime.UtcNow);
    }

    public async Task ProbeIssabelAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand("SELECT TOP(1) RawCDRId FROM dbo.RawCDR ORDER BY ReceivedAtUtc DESC;", connection)
        { CommandTimeout = 15 };
        await command.ExecuteScalarAsync(ct);
    }
}
