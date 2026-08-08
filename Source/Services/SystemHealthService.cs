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
                LastCdrAt=CASE WHEN OBJECT_ID(N'dbo.RawCDR',N'U') IS NULL THEN NULL ELSE (SELECT MAX(Calldate) FROM dbo.RawCDR) END,
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
        DateTime? lastCdr = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
        DateTime? lastAccounting = reader.IsDBNull(3) ? null : reader.GetDateTime(3);
        var lastFactor = reader.IsDBNull(4) ? null : reader.GetString(4);
        var recovery = reader.IsDBNull(5) ? "UNKNOWN" : reader.GetString(5);
        var databaseMb = reader.IsDBNull(6) ? 0m : reader.GetDecimal(6);
        var logMb = reader.IsDBNull(7) ? 0m : reader.GetDecimal(7);

        var issabelStatus = lastCdr is null ? "NO_DATA" :
            DateTime.Now - lastCdr.Value <= TimeSpan.FromHours(24) ? "OK" : "STALE";
        var accountingJob = jobs.FirstOrDefault(x => x.JobKey == "ACCOUNTING");
        var accountingStatus = accountingJob?.LastStatus ?? (lastAccounting is null ? "NO_DATA" : "OK");
        var pending = jobs.Count(x => x.IsEnabled && x.NextRunAtUtc <= DateTime.UtcNow);

        return new SystemHealthSnapshot(
            "OK",
            didarContacts > 0 && didarPhones > 0 ? "OK" : "NO_DATA",
            didarContacts,
            didarPhones,
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
