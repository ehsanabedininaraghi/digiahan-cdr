using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.RegularExpressions;

var migrationPath = args.FirstOrDefault()
    ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Source", "Sql", "AiPipelineVNext.sql"));
if (!File.Exists(migrationPath))
    throw new FileNotFoundException("Migration was not found.", migrationPath);

var databaseName = $"DigiAhan_AiPipeline_Test_{Environment.ProcessId}";
var masterConnectionString = "Server=lpc:localhost;Database=master;Integrated Security=True;TrustServerCertificate=True;Pooling=False;";
var testConnectionString = $"Server=lpc:localhost;Database={databaseName};Integrated Security=True;TrustServerCertificate=True;Pooling=False;";

await using var master = new SqlConnection(masterConnectionString);
await master.OpenAsync();
try
{
    await ExecuteAsync(master, $"CREATE DATABASE [{databaseName}];");
    await using var test = new SqlConnection(testConnectionString);
    await test.OpenAsync();

    await ExecuteAsync(test, """
        CREATE TABLE dbo.RawCDR
        (
            RawCDRId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
            Calldate datetime2(3) NULL,
            Duration int NULL,
            Billsec int NULL,
            UniqueId nvarchar(150) NULL,
            LinkedId nvarchar(150) NULL,
            RecordingFile nvarchar(500) NULL,
            ReceivedAtUtc datetime2(3) NOT NULL
        );
        CREATE INDEX IX_RawCDR_LinkedId ON dbo.RawCDR(LinkedId);
        """);

    var migration = await File.ReadAllTextAsync(migrationPath);
    foreach (var batch in Regex.Split(migration, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
    {
        if (!string.IsNullOrWhiteSpace(batch))
            await ExecuteAsync(test, batch);
    }
    foreach (var batch in Regex.Split(migration, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
    {
        if (!string.IsNullOrWhiteSpace(batch))
            await ExecuteAsync(test, batch);
    }

    await ExecuteAsync(test, """
        DECLARE @Old datetime2(3)=DATEADD(minute,-20,SYSUTCDATETIME());
        INSERT dbo.RawCDR(Calldate,Duration,Billsec,UniqueId,LinkedId,RecordingFile,ReceivedAtUtc) VALUES
          (DATEADD(second,0,@Old),100,95,N'u1',N'l1',N'one.wav',@Old),
          (DATEADD(second,2,@Old),98,92,N'u2',N'l1',N'one.wav',DATEADD(second,2,@Old)),
          (DATEADD(second,0,@Old),50,45,N'u3',N'l2',N'a.wav',@Old),
          (DATEADD(second,1,@Old),49,44,N'u4',N'l2',N'b.wav',DATEADD(second,1,@Old)),
          (DATEADD(second,0,@Old),30,20,N'u5',N'l3',NULL,@Old);
        """);

    var first = await DiscoverAsync(test);
    Assert(first == (3, 3, 1), $"Unexpected first discovery: {first}");
    var second = await DiscoverAsync(test);
    Assert(second == (0, 0, 0), $"Idempotency failed: {second}");

    await ExecuteAsync(test, """
        DECLARE @Old datetime2(3)=DATEADD(minute,-10,SYSUTCDATETIME());
        INSERT dbo.RawCDR(Calldate,Duration,Billsec,UniqueId,LinkedId,RecordingFile,ReceivedAtUtc)
        VALUES(@Old,105,99,N'u6',N'l1',N'one.wav',@Old);
        """);
    var late = await DiscoverAsync(test);
    Assert(late == (1, 1, 1), $"Late-leg reconciliation failed: {late}");

    await using var verification = new SqlCommand("""
        SELECT c.PipelineState,COUNT(r.RunId)
        FROM dbo.AiLogicalCalls c
        LEFT JOIN dbo.AiPipelineRuns r ON r.LogicalCallId=c.LogicalCallId
        WHERE c.CallKey=N'linked:l1'
        GROUP BY c.PipelineState;
        """, test);
    await using var reader = await verification.ExecuteReaderAsync();
    Assert(await reader.ReadAsync(), "Validated call was not found.");
    Assert(reader.GetString(0) == "FINALIZED", "Late-leg call did not return to FINALIZED.");
    Assert(reader.GetInt32(1) == 2, "Late-leg call did not create exactly two runs.");

    Console.WriteLine("AI migration integration test passed.");
}
finally
{
    SqlConnection.ClearAllPools();
    await ExecuteAsync(master, $"IF DB_ID(N'{databaseName}') IS NOT NULL BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END;");
}

static async Task ExecuteAsync(SqlConnection connection, string sql)
{
    await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
    await command.ExecuteNonQueryAsync();
}

static async Task<(int Discovered, int Finalized, int Queued)> DiscoverAsync(SqlConnection connection)
{
    await using var command = new SqlCommand("dbo.usp_AiDiscoverLogicalCalls", connection)
    {
        CommandType = CommandType.StoredProcedure,
        CommandTimeout = 60
    };
    command.Parameters.Add("@StabilizationSeconds", SqlDbType.Int).Value = 300;
    command.Parameters.Add("@BatchSize", SqlDbType.Int).Value = 500;
    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        throw new InvalidOperationException("Discovery returned no result.");
    return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
