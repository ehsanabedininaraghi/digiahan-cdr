using DigiAhan.CDR.Receiver.Models;
using Microsoft.Data.SqlClient;
using System.Data;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class RecordingAssetRepository
{
    private readonly string _connectionString;

    public RecordingAssetRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DigiAhanCdr")
            ?? throw new InvalidOperationException("ConnectionStrings:DigiAhanCdr is missing.");
    }

    public async Task<RecordingDiscoveryResult> DiscoverAsync(
        DateOnly targetDate,
        string sourceServer,
        int batchSize,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand("dbo.usp_AiDiscoverRecordingAssets", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 60
        };
        command.Parameters.Add("@TargetDate", SqlDbType.Date).Value = targetDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("@SourceServer", SqlDbType.NVarChar, 100).Value = sourceServer;
        command.Parameters.Add("@BatchSize", SqlDbType.Int).Value = Math.Clamp(batchSize, 1, 1000);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new(0, 0, DateTime.UtcNow);
        return new(reader.GetInt32(0), reader.GetInt32(1), reader.GetDateTime(2));
    }

    public async Task<RecordingAssetLease?> ClaimNextAsync(
        string owner,
        DateOnly targetDate,
        int leaseSeconds,
        int maxAttempts,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            await using var claim = new SqlCommand("""
                DECLARE @Id bigint;
                SELECT TOP(1) @Id=a.RecordingAssetId
                FROM dbo.AiRecordingAssets a WITH(UPDLOCK,READPAST,ROWLOCK)
                WHERE a.ProcessingStatus IN
                    (N'DISCOVERED',N'SOURCE_LOOKUP_PENDING',N'SOURCE_MISSING',N'WAITING_FOR_STABLE_FILE',
                     N'FETCHING',N'READY_FOR_AI',N'TRANSCRIBING',N'ANALYZING',N'RETRY',N'VALIDATION_FAILED')
                  AND a.AttemptCount<@maxAttempts
                  AND a.CallDate=@targetDate
                  AND (a.NextRetryAtUtc IS NULL OR a.NextRetryAtUtc<=SYSUTCDATETIME())
                  AND (a.LeaseExpiresAtUtc IS NULL OR a.LeaseExpiresAtUtc<SYSUTCDATETIME())
                ORDER BY CASE WHEN a.ProcessingStatus=N'READY_FOR_AI' THEN 0 ELSE 1 END,
                         a.CallDate,a.RecordingAssetId;
                IF @Id IS NOT NULL
                    UPDATE dbo.AiRecordingAssets
                    SET LeaseOwner=@owner,
                        LeaseExpiresAtUtc=DATEADD(second,@leaseSeconds,SYSUTCDATETIME()),
                        LastAttemptAtUtc=SYSUTCDATETIME(),
                        AttemptCount=AttemptCount+1,
                        UpdatedAtUtc=SYSUTCDATETIME()
                    WHERE RecordingAssetId=@Id;
                SELECT @Id;
                """, connection, transaction);
            claim.Parameters.Add("@owner", SqlDbType.NVarChar, 200).Value = owner;
            claim.Parameters.Add("@targetDate", SqlDbType.Date).Value = targetDate.ToDateTime(TimeOnly.MinValue);
            claim.Parameters.Add("@leaseSeconds", SqlDbType.Int).Value = Math.Clamp(leaseSeconds, 60, 14400);
            claim.Parameters.Add("@maxAttempts", SqlDbType.Int).Value = Math.Clamp(maxAttempts, 1, 100);
            var value = await claim.ExecuteScalarAsync(ct);
            if (value is null or DBNull)
            {
                await transaction.CommitAsync(ct);
                return null;
            }

            var id = Convert.ToInt64(value);
            await using var read = new SqlCommand("""
                SELECT a.RecordingAssetId,l.LogicalCallId,l.RunId,a.SourceServer,a.OriginalFileName,
                       a.SourceRelativePath,a.StorageKey,a.ProcessingStatus,a.AttemptCount,a.CallDate,
                       a.LeaseExpiresAtUtc
                FROM dbo.AiRecordingAssets a
                JOIN dbo.AiLogicalCallRecordings l ON l.RecordingAssetId=a.RecordingAssetId
                WHERE a.RecordingAssetId=@id;
                """, connection, transaction);
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("Claimed recording asset has no logical-call link.");
            var result = new RecordingAssetLease(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3),
                reader.GetString(4), GetString(reader, 5), GetString(reader, 6), reader.GetString(7),
                reader.GetInt32(8), reader.GetDateTime(9), reader.GetDateTime(10));
            await reader.CloseAsync();
            await transaction.CommitAsync(ct);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public Task MarkWaitingAsync(long id, string owner, RemoteRecordingInfo remote, DateTime retryAtUtc, CancellationToken ct) =>
        UpdateAsync(id, owner, "WAITING_FOR_STABLE_FILE", remote, null, null, null, retryAtUtc,
            "Remote recording is still inside the stability window.", ct);

    public Task MarkFetchingAsync(long id, string owner, RemoteRecordingInfo remote, string storageKey, CancellationToken ct) =>
        UpdateAsync(id, owner, "FETCHING", remote, storageKey, null, null, null, null, ct);

    public Task MarkReadyForAiAsync(long id, string owner, ValidatedRecording recording, CancellationToken ct) =>
        UpdateAsync(id, owner, "READY_FOR_AI", null, null, recording, null, null, null, ct);

    public Task MarkPhaseAsync(long id, string owner, string phase, CancellationToken ct) =>
        UpdateAsync(id, owner, phase, null, null, null, null, null, null, ct);

    public Task MarkCompletedAsync(long id, string owner, decimal durationSeconds, CancellationToken ct) =>
        UpdateAsync(id, owner, "COMPLETED", null, null, null, durationSeconds, null, null, ct);

    public async Task MarkSourceMissingAsync(
        long id, string owner, DateTime retryAtUtc, string error, CancellationToken ct) =>
        await UpdateAsync(id, owner, "SOURCE_MISSING", null, null, null, null, retryAtUtc, error, ct);

    public async Task MarkRetryAsync(
        long id, string owner, int attemptCount, int maxAttempts, string error, CancellationToken ct)
    {
        var terminal = attemptCount >= maxAttempts;
        var delayMinutes = Math.Min(360, Math.Pow(2, Math.Min(attemptCount, 8)) * 5);
        await UpdateAsync(
            id, owner, terminal ? "QUARANTINED" : "RETRY",
            null, null, null, null,
            terminal ? null : DateTime.UtcNow.AddMinutes(delayMinutes),
            error, ct);
    }

    public async Task MarkPurgedAsync(long id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand("""
            UPDATE dbo.AiRecordingAssets
            SET ProcessingStatus=N'LOCAL_PURGED',StorageKey=NULL,PurgedAtUtc=SYSUTCDATETIME(),
                UpdatedAtUtc=SYSUTCDATETIME()
            WHERE RecordingAssetId=@id AND ProcessingStatus=N'COMPLETED';
            """, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<RecordingAssetView?> GetForCallAsync(long logicalCallId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand("""
            SELECT TOP(1) a.RecordingAssetId,a.OriginalFileName,a.ProcessingStatus,a.FileSizeBytes,
                   a.CompletedAtUtc,a.PurgedAtUtc,a.LastError
            FROM dbo.AiLogicalCallRecordings l
            JOIN dbo.AiRecordingAssets a ON a.RecordingAssetId=l.RecordingAssetId
            WHERE l.LogicalCallId=@id
            ORDER BY l.IsPrimary DESC,a.RecordingAssetId;
            """, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = logicalCallId;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            GetInt64(reader, 3), GetDateTime(reader, 4), GetDateTime(reader, 5), GetString(reader, 6));
    }

    private async Task UpdateAsync(
        long id,
        string owner,
        string status,
        RemoteRecordingInfo? remote,
        string? storageKey,
        ValidatedRecording? validated,
        decimal? duration,
        DateTime? retryAtUtc,
        string? error,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand("""
            UPDATE dbo.AiRecordingAssets
            SET ProcessingStatus=@status,
                SourceRelativePath=COALESCE(@relative,SourceRelativePath),
                RemoteFileSizeBytes=COALESCE(@remoteSize,RemoteFileSizeBytes),
                RemoteObservedAtUtc=COALESCE(@remoteObserved,RemoteObservedAtUtc),
                RemoteIsStable=COALESCE(@remoteStable,RemoteIsStable),
                StorageKey=COALESCE(@storageKey,StorageKey),
                FileSizeBytes=COALESCE(@fileSize,FileSizeBytes),
                Sha256=COALESCE(@sha,Sha256),
                DurationSeconds=COALESCE(@duration,DurationSeconds),
                DownloadedAtUtc=CASE WHEN @status=N'READY_FOR_AI' THEN SYSUTCDATETIME() ELSE DownloadedAtUtc END,
                CompletedAtUtc=CASE WHEN @status=N'COMPLETED' THEN SYSUTCDATETIME() ELSE CompletedAtUtc END,
                NextRetryAtUtc=@retry,
                LastError=@error,
                AttemptCount=CASE WHEN @preserveAttempt=1 AND AttemptCount>0 THEN AttemptCount-1 ELSE AttemptCount END,
                LeaseOwner=CASE WHEN @release=1 THEN NULL ELSE LeaseOwner END,
                LeaseExpiresAtUtc=CASE WHEN @release=1 THEN NULL ELSE LeaseExpiresAtUtc END,
                UpdatedAtUtc=SYSUTCDATETIME()
            WHERE RecordingAssetId=@id AND LeaseOwner=@owner;
            IF @@ROWCOUNT=0 THROW 50001,N'Recording asset lease was lost.',1;
            """, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        command.Parameters.Add("@owner", SqlDbType.NVarChar, 200).Value = owner;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 40).Value = status;
        AddNullable(command, "@relative", SqlDbType.NVarChar, 1000, remote?.RelativePath);
        AddNullable(command, "@remoteSize", SqlDbType.BigInt, null, remote?.SizeBytes);
        AddNullable(command, "@remoteObserved", SqlDbType.DateTime2, null, remote?.ObservedAtUtc);
        AddNullable(command, "@remoteStable", SqlDbType.Bit, null, remote?.IsStable);
        AddNullable(command, "@storageKey", SqlDbType.NVarChar, 1000, storageKey);
        AddNullable(command, "@fileSize", SqlDbType.BigInt, null, validated?.SizeBytes);
        AddNullable(command, "@sha", SqlDbType.Char, 64, validated?.Sha256);
        AddNullable(command, "@duration", SqlDbType.Decimal, null, duration, precision: 12, scale: 3);
        AddNullable(command, "@retry", SqlDbType.DateTime2, null, retryAtUtc);
        AddNullable(command, "@error", SqlDbType.NVarChar, 2000, error);
        command.Parameters.Add("@release", SqlDbType.Bit).Value =
            status is "WAITING_FOR_STABLE_FILE" or "SOURCE_MISSING" or "RETRY" or "QUARANTINED" or "COMPLETED";
        command.Parameters.Add("@preserveAttempt", SqlDbType.Bit).Value =
            status is "WAITING_FOR_STABLE_FILE" or "SOURCE_MISSING";
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static string? GetString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static long? GetInt64(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static DateTime? GetDateTime(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);

    private static void AddNullable(
        SqlCommand command,
        string name,
        SqlDbType type,
        int? size,
        object? value,
        byte? precision = null,
        byte? scale = null)
    {
        var parameter = size.HasValue ? command.Parameters.Add(name, type, size.Value) : command.Parameters.Add(name, type);
        if (precision.HasValue) parameter.Precision = precision.Value;
        if (scale.HasValue) parameter.Scale = scale.Value;
        parameter.Value = value ?? DBNull.Value;
    }
}
