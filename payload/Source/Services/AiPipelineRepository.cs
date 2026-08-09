using DigiAhan.CDR.Receiver.Models;
using Microsoft.Data.SqlClient;
using System.Data;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class AiPipelineRepository
{
    private readonly string _connectionString;

    public AiPipelineRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DigiAhanCdr")
            ?? throw new InvalidOperationException("ConnectionStrings:DigiAhanCdr is missing.");
    }

    public async Task<AiDiscoveryResult> DiscoverAndQueueAsync(
        int stabilizationSeconds,
        int batchSize,
        CancellationToken ct)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand("dbo.usp_AiDiscoverLogicalCalls", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 60
        };
        command.Parameters.Add("@StabilizationSeconds", SqlDbType.Int).Value =
            Math.Clamp(stabilizationSeconds, 60, 86400);
        command.Parameters.Add("@BatchSize", SqlDbType.Int).Value =
            Math.Clamp(batchSize, 1, 5000);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new AiDiscoveryResult(0, 0, 0, DateTime.UtcNow);

        return new AiDiscoveryResult(
            reader.GetInt32(reader.GetOrdinal("CallsDiscovered")),
            reader.GetInt32(reader.GetOrdinal("CallsFinalized")),
            reader.GetInt32(reader.GetOrdinal("RunsQueued")),
            reader.GetDateTime(reader.GetOrdinal("ExecutedAtUtc")));
    }
}
