using DigiAhan.CDR.Receiver.Models;
using Microsoft.Data.SqlClient;
using System.Data;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class SqlCdrRepository
{
    private readonly string _connectionString;

    public SqlCdrRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DigiAhanCdr")
            ?? throw new InvalidOperationException(
                "Connection string 'DigiAhanCdr' was not found in appsettings.json.");
    }

    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqlCommand("SELECT 1", connection);
            _ = await command.ExecuteScalarAsync(cancellationToken);

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<CdrInsertResult> InsertAsync(
        string sourceServer,
        Guid batchId,
        CdrRecord record,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("dbo.usp_InsertRawCDR", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 30
        };

        Add(command, "@SourceServer", SqlDbType.NVarChar, 100, sourceServer);
        Add(command, "@Calldate", SqlDbType.DateTime2, null, record.Calldate);
        Add(command, "@Clid", SqlDbType.NVarChar, 255, record.Clid);
        Add(command, "@Src", SqlDbType.NVarChar, 80, record.Src);
        Add(command, "@Dst", SqlDbType.NVarChar, 80, record.Dst);
        Add(command, "@Dcontext", SqlDbType.NVarChar, 80, record.Dcontext);
        Add(command, "@Channel", SqlDbType.NVarChar, 255, record.Channel);
        Add(command, "@DstChannel", SqlDbType.NVarChar, 255, record.DstChannel);
        Add(command, "@LastApp", SqlDbType.NVarChar, 80, record.LastApp);
        Add(command, "@LastData", SqlDbType.NVarChar, 500, record.LastData);
        Add(command, "@Duration", SqlDbType.Int, null, record.Duration);
        Add(command, "@Billsec", SqlDbType.Int, null, record.Billsec);
        Add(command, "@Disposition", SqlDbType.NVarChar, 45, record.Disposition);
        Add(command, "@Amaflags", SqlDbType.Int, null, record.Amaflags);
        Add(command, "@AccountCode", SqlDbType.NVarChar, 80, record.AccountCode);
        Add(command, "@UniqueId", SqlDbType.NVarChar, 150, record.UniqueId);
        Add(command, "@UserField", SqlDbType.NVarChar, 500, record.UserField);
        Add(command, "@RecordingFile", SqlDbType.NVarChar, 1000, record.RecordingFile);
        Add(command, "@Cnum", SqlDbType.NVarChar, 80, record.Cnum);
        Add(command, "@Cnam", SqlDbType.NVarChar, 255, record.Cnam);
        Add(command, "@OutboundCnum", SqlDbType.NVarChar, 80, record.OutboundCnum);
        Add(command, "@OutboundCnam", SqlDbType.NVarChar, 255, record.OutboundCnam);
        Add(command, "@DstCnam", SqlDbType.NVarChar, 255, record.DstCnam);
        Add(command, "@Did", SqlDbType.NVarChar, 80, record.Did);
        Add(command, "@LinkedId", SqlDbType.NVarChar, 150, record.LinkedId);
        Add(command, "@PeerAccount", SqlDbType.NVarChar, 80, record.PeerAccount);
        Add(command, "@SequenceNo", SqlDbType.Int, null, record.SequenceNo);
        Add(command, "@SourceRowKey", SqlDbType.NVarChar, 255, record.SourceRowKey);
        Add(command, "@Fingerprint", SqlDbType.Char, 64, record.Fingerprint);
        Add(command, "@BatchId", SqlDbType.UniqueIdentifier, null, batchId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Stored procedure returned no result.");

        return new CdrInsertResult(
            reader.GetBoolean(reader.GetOrdinal("Inserted")),
            reader.GetInt64(reader.GetOrdinal("RawCDRId")));
    }

    public async Task StartBatchAsync(
        Guid batchId,
        string sourceServer,
        int sentCount,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.SyncBatch
            (
                BatchId,
                SourceServer,
                SentCount,
                Status
            )
            VALUES
            (
                @BatchId,
                @SourceServer,
                @SentCount,
                N'RUNNING'
            );
            """;

        await ExecuteBatchCommandAsync(
            sql,
            batchId,
            sourceServer,
            sentCount,
            0,
            0,
            0,
            "RUNNING",
            null,
            cancellationToken);
    }

    public async Task FinishBatchAsync(
        Guid batchId,
        int insertedCount,
        int duplicateCount,
        int errorCount,
        string status,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.SyncBatch
            SET
                FinishedAtUtc = SYSUTCDATETIME(),
                InsertedCount = @InsertedCount,
                DuplicateCount = @DuplicateCount,
                ErrorCount = @ErrorCount,
                Status = @Status,
                ErrorMessage = @ErrorMessage
            WHERE BatchId = @BatchId;
            """;

        await ExecuteBatchCommandAsync(
            sql,
            batchId,
            "",
            0,
            insertedCount,
            duplicateCount,
            errorCount,
            status,
            errorMessage,
            cancellationToken);
    }

    private async Task ExecuteBatchCommandAsync(
        string sql,
        Guid batchId,
        string sourceServer,
        int sentCount,
        int insertedCount,
        int duplicateCount,
        int errorCount,
        string status,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = 30
        };

        command.Parameters.Add("@BatchId", SqlDbType.UniqueIdentifier).Value = batchId;
        command.Parameters.Add("@SourceServer", SqlDbType.NVarChar, 100).Value =
            string.IsNullOrWhiteSpace(sourceServer) ? DBNull.Value : sourceServer;
        command.Parameters.Add("@SentCount", SqlDbType.Int).Value = sentCount;
        command.Parameters.Add("@InsertedCount", SqlDbType.Int).Value = insertedCount;
        command.Parameters.Add("@DuplicateCount", SqlDbType.Int).Value = duplicateCount;
        command.Parameters.Add("@ErrorCount", SqlDbType.Int).Value = errorCount;
        command.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value = status;
        command.Parameters.Add("@ErrorMessage", SqlDbType.NVarChar, -1).Value =
            errorMessage is null ? DBNull.Value : errorMessage;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Add(
        SqlCommand command,
        string name,
        SqlDbType type,
        int? size,
        object? value)
    {
        var parameter = size.HasValue
            ? command.Parameters.Add(name, type, size.Value)
            : command.Parameters.Add(name, type);

        parameter.Value = value ?? DBNull.Value;
    }
}
