using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.RegularExpressions;
using DigiAhan.CDR.Receiver.Models;
using DigiAhan.CDR.Receiver.Services;

var migrationPaths = args.Length > 0
    ? args
    : new[]
    {
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Source", "Sql", "AiPipelineVNext.sql")),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Source", "Sql", "AiAnalysisVNext.sql")),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Source", "Sql", "AiRecordingSyncVNext.sql"))
    };
foreach (var migrationPath in migrationPaths)
{
    if (!File.Exists(migrationPath))
        throw new FileNotFoundException("Migration was not found.", migrationPath);
}

var databaseName = $"DigiAhan_AiPipeline_Test_{Environment.ProcessId}";
var testSqlServer = Environment.GetEnvironmentVariable("DIGIAHAN_TEST_SQL_SERVER") ?? "localhost";
var masterConnectionString = Environment.GetEnvironmentVariable("DIGIAHAN_TEST_SQL_MASTER_CONNECTION")
    ?? $"Server={testSqlServer};Database=master;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Pooling=False;";
var testBuilder = new SqlConnectionStringBuilder(masterConnectionString)
{
    InitialCatalog = databaseName,
    Pooling = false,
    TrustServerCertificate = true
};
var testConnectionString = testBuilder.ConnectionString;

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
            SourceServer nvarchar(100) NULL,
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

    foreach (var migrationPath in migrationPaths)
    {
        var migration = await File.ReadAllTextAsync(migrationPath);
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var batch in Regex.Split(migration, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(batch))
                    await ExecuteAsync(test, batch);
            }
        }
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
    await reader.CloseAsync();

    var recordingFirst = await DiscoverRecordingsAsync(test);
    Assert(recordingFirst == (1, 1), $"Unexpected recording discovery: {recordingFirst}");
    var recordingSecond = await DiscoverRecordingsAsync(test);
    Assert(recordingSecond == (0, 0), $"Recording discovery idempotency failed: {recordingSecond}");

    await ExecuteAsync(test, """
        DECLARE @RunId bigint=(SELECT TOP(1) RunId FROM dbo.AiPipelineRuns ORDER BY RunId);
        INSERT dbo.AiCallAssessments
            (RunId,AudioClass,HasHumanSpeech,IsBusinessRelevant,Direction,InternalExtension,
             QueueName,Confidence,SpeechSeconds,Summary,StructuredJson,AnalyzerVersion)
        VALUES
            (@RunId,N'BUSINESS_CONVERSATION',1,1,N'INBOUND',N'201',N'400',0.82,91.0,
             N'test',N'{"schema_version":"1.0"}',N'integration-test');
        DECLARE @AssessmentId bigint=SCOPE_IDENTITY();
        INSERT dbo.AiExtractedFacts
            (AssessmentId,FactType,RawValue,NormalizedValue,Unit,StartSeconds,EndSeconds,
             Confidence,ReviewStatus,ExtractionVersion)
        VALUES(@AssessmentId,N'QUANTITY',N'fifteen',N'15',N'BRANCH',1,2,0.75,N'REVIEW',N'integration-test');
        INSERT dbo.AiReviewItems
            (AssessmentId,Category,Priority,ReasonCode,RawText,StartSeconds,EndSeconds,ReviewStatus)
        VALUES(@AssessmentId,N'NUMERIC',N'HIGH',N'UNIT_UNKNOWN',N'fifteen',1,2,N'OPEN');
        """);

    await using var analysisVerification = new SqlCommand("""
        SELECT
            (SELECT COUNT(*) FROM dbo.AiCallAssessments),
            (SELECT COUNT(*) FROM dbo.AiExtractedFacts),
            (SELECT COUNT(*) FROM dbo.AiReviewItems);
        """, test);
    await using var analysisReader = await analysisVerification.ExecuteReaderAsync();
    Assert(await analysisReader.ReadAsync(), "Analysis verification returned no result.");
    Assert(analysisReader.GetInt32(0) == 1, "Assessment was not stored exactly once.");
    Assert(analysisReader.GetInt32(1) == 1, "Fact was not stored exactly once.");
    Assert(analysisReader.GetInt32(2) == 1, "Review item was not stored exactly once.");

    var resolver = new IssabelRecordingPathResolver();
    var resolved = resolver.ResolveRelativePath(
        "exten-220-88451277-20260809-001218-1786221734.927.wav");
    Assert(
        resolved == "2026/08/09/exten-220-88451277-20260809-001218-1786221734.927.wav",
        $"Issabel recording path resolution failed: {resolved}");
    var traversalRejected = false;
    try { resolver.ResolveRelativePath("../../etc/passwd.wav", DateTime.Today); }
    catch (InvalidOperationException) { traversalRejected = true; }
    Assert(traversalRejected, "Recording path traversal was not rejected.");

    var analyzer = new AiTranscriptAnalyzer();
    var analyzed = analyzer.Analyze(new AiAnalyzeRunRequest(
        "سلام قیمت بالاست و فعلا خرید نمی‌کنم. پول را به حساب شخصی واریز کنم؟",
        "[{\"start\":1.0,\"end\":8.0,\"text\":\"قیمت بالاست و فعلا خرید نمی‌کنم\"},{\"start\":9.0,\"end\":14.0,\"text\":\"پول را به حساب شخصی واریز کنم\"}]",
        "fa", 14, 13, 2, "test", "test", null, null, null, null));
    Assert(analyzed.Facts.Any(f => f.FactType == "NON_PURCHASE_REASON"), "Non-purchase reason was not extracted.");
    Assert(analyzed.ReviewItems.Any(r => r.Category == "BRIBERY_OR_PERSONAL_PAYMENT"), "Sensitive payment signal was not queued for human review.");

    var audioTestDirectory = Path.Combine(Path.GetTempPath(), $"digiahan-wav-test-{Environment.ProcessId}");
    Directory.CreateDirectory(audioTestDirectory);
    try
    {
        var validWav = Path.Combine(audioTestDirectory, "valid.wav.part-test");
        await File.WriteAllBytesAsync(validWav,
        [
            (byte)'R',(byte)'I',(byte)'F',(byte)'F', 8,0,0,0,
            (byte)'W',(byte)'A',(byte)'V',(byte)'E', 0,0,0,0
        ]);
        var validation = await new RecordingAudioValidator().ValidateWavAsync(
            validWav, new FileInfo(validWav).Length, CancellationToken.None);
        Assert(validation.SizeBytes == 16 && validation.Sha256.Length == 64, "WAV validation or SHA-256 failed.");

        var invalidWav = Path.Combine(audioTestDirectory, "invalid.wav.part-test");
        await File.WriteAllBytesAsync(invalidWav, "not-a-wave-file"u8.ToArray());
        var invalidRejected = false;
        try
        {
            await new RecordingAudioValidator().ValidateWavAsync(
                invalidWav, new FileInfo(invalidWav).Length, CancellationToken.None);
        }
        catch (InvalidDataException) { invalidRejected = true; }
        Assert(invalidRejected, "Invalid WAV header was not rejected.");
    }
    finally
    {
        if (Directory.Exists(audioTestDirectory)) Directory.Delete(audioTestDirectory, recursive: true);
    }

    Console.WriteLine("AI pipeline, daily recording ingestion and analysis integration test passed.");
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

static async Task<(int Assets, int Links)> DiscoverRecordingsAsync(SqlConnection connection)
{
    await using var dateCommand = new SqlCommand(
        "SELECT CONVERT(date,StartedAt) FROM dbo.AiLogicalCalls WHERE CallKey=N'linked:l1';",
        connection);
    var targetDate = (DateTime)(await dateCommand.ExecuteScalarAsync()
        ?? throw new InvalidOperationException("Logical call date was not found."));
    await using var command = new SqlCommand("dbo.usp_AiDiscoverRecordingAssets", connection)
    {
        CommandType = CommandType.StoredProcedure,
        CommandTimeout = 60
    };
    command.Parameters.Add("@TargetDate", SqlDbType.Date).Value = targetDate;
    command.Parameters.Add("@SourceServer", SqlDbType.NVarChar, 100).Value = "issabel-primary";
    command.Parameters.Add("@BatchSize", SqlDbType.Int).Value = 100;
    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        throw new InvalidOperationException("Recording discovery returned no result.");
    return (reader.GetInt32(0), reader.GetInt32(1));
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
