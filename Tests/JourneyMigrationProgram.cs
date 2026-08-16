using Microsoft.Data.SqlClient;

var masterConnectionString = Environment.GetEnvironmentVariable("DIGIAHAN_JOURNEY_TEST_SQL_MASTER_CONNECTION");
if (string.IsNullOrWhiteSpace(masterConnectionString))
{
    Console.WriteLine("Journey migration SQL execution skipped: set DIGIAHAN_JOURNEY_TEST_SQL_MASTER_CONNECTION to an isolated test SQL Server.");
    return;
}

var migrationPath = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "Source", "Sql", "CustomerJourneyKernelV440.sql"));
if (!File.Exists(migrationPath)) throw new FileNotFoundException("Journey migration was not found.", migrationPath);
var migration = await File.ReadAllTextAsync(migrationPath);
var databaseName = $"DigiAhan_Journey_Test_{Environment.ProcessId}_{Guid.NewGuid():N}";
var testBuilder = new SqlConnectionStringBuilder(masterConnectionString)
{
    InitialCatalog = databaseName,
    Pooling = false,
    TrustServerCertificate = true
};

await using var master = new SqlConnection(masterConnectionString);
await master.OpenAsync();
try
{
    await ExecuteAsync(master, $"CREATE DATABASE [{databaseName}];");
    await using var test = new SqlConnection(testBuilder.ConnectionString);
    await test.OpenAsync();
    await ExecuteAsync(test, """
        CREATE TABLE dbo.CustomerIdentities
        (
            IdentityId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
            DisplayName nvarchar(300) NULL,
            CompanyName nvarchar(300) NULL,
            OwnerName nvarchar(200) NULL,
            MasterSource nvarchar(30) NOT NULL DEFAULT(N'TEST'),
            IsActive bit NOT NULL DEFAULT(1),
            CreatedAtUtc datetime2(0) NOT NULL DEFAULT(SYSUTCDATETIME()),
            UpdatedAtUtc datetime2(0) NOT NULL DEFAULT(SYSUTCDATETIME())
        );
        """);

    await ExecuteAsync(test, migration);
    await ExecuteAsync(test, migration);

    await ExecuteAsync(test, """
        INSERT dbo.CustomerIdentities(DisplayName) VALUES(N'Test Customer');
        DECLARE @IdentityId bigint=SCOPE_IDENTITY();
        DECLARE @LeadKey uniqueidentifier=NEWID();
        INSERT dbo.JourneyLeads
          (IdempotencyKey,IdentityId,SourceSystem,OwnerSellerKey,Title,Status,Priority,
           NextActionType,NextActionAtUtc,SlaDueAtUtc)
        VALUES(@LeadKey,@IdentityId,N'TEST',N'seller-test',N'Test Lead',N'OPEN',2,
               N'CALL_BACK',DATEADD(hour,1,SYSUTCDATETIME()),DATEADD(minute,30,SYSUTCDATETIME()));
        DECLARE @LeadId bigint=SCOPE_IDENTITY();
        INSERT dbo.JourneyWorkItems
          (IdempotencyKey,IdentityId,LeadId,OwnerSellerKey,WorkType,Title,Priority,DueAtUtc,SlaDueAtUtc)
        VALUES(NEWID(),@IdentityId,@LeadId,N'seller-test',N'LEAD_NEXT_ACTION',N'Test Work',2,
               DATEADD(hour,1,SYSUTCDATETIME()),DATEADD(minute,30,SYSUTCDATETIME()));
        DECLARE @EventId bigint;
        INSERT dbo.JourneyEvents
          (EventKey,IdentityId,AggregateType,AggregateId,EventType,SourceSystem,ActorType,ActorKey,
           CorrelationId,OccurredAtUtc,PayloadJson)
        VALUES(NEWID(),@IdentityId,N'LEAD',@LeadId,N'LEAD_CREATED',N'TEST',N'SELLER',N'seller-test',
               NEWID(),SYSUTCDATETIME(),N'{"test":true}');
        SET @EventId=SCOPE_IDENTITY();
        INSERT dbo.JourneyOutbox(EventId,Destination) VALUES(@EventId,N'TEST');
        """);

    await using (var verify = new SqlCommand("""
        SELECT
          (SELECT COUNT(*) FROM dbo.JourneySchemaVersions WHERE Version=N'4.4.0'),
          (SELECT COUNT(*) FROM dbo.JourneyLeads),
          (SELECT COUNT(*) FROM dbo.JourneyWorkItems),
          (SELECT COUNT(*) FROM dbo.JourneyEvents),
          (SELECT COUNT(*) FROM dbo.JourneyOutbox);
        """, test))
    await using (var reader = await verify.ExecuteReaderAsync())
    {
        if (!await reader.ReadAsync() || Enumerable.Range(0, 5).Any(index => reader.GetInt32(index) != 1))
            throw new InvalidOperationException("Journey migration verification counts are invalid.");
    }

    var ownerInvariantRejected = false;
    try
    {
        await ExecuteAsync(test, """
            DECLARE @IdentityId bigint=(SELECT TOP(1) IdentityId FROM dbo.CustomerIdentities);
            INSERT dbo.JourneyLeads
              (IdempotencyKey,IdentityId,SourceSystem,OwnerSellerKey,Title,Status,Priority,
               NextActionType,NextActionAtUtc,SlaDueAtUtc)
            VALUES(NEWID(),@IdentityId,N'TEST',N'',N'Invalid Lead',N'OPEN',2,
                   N'CALL_BACK',DATEADD(hour,1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()));
            """);
    }
    catch (SqlException error) when (error.Number == 547)
    {
        ownerInvariantRejected = true;
    }
    if (!ownerInvariantRejected) throw new InvalidOperationException("Owner invariant was not enforced by SQL.");

    Console.WriteLine("v4.4.0 Journey migration executed twice and invariant checks passed on isolated SQL.");
}
finally
{
    SqlConnection.ClearAllPools();
    await ExecuteAsync(master,
        $"IF DB_ID(N'{databaseName}') IS NOT NULL BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END;");
}

static async Task ExecuteAsync(SqlConnection connection, string sql)
{
    await using var command = new SqlCommand(sql, connection) { CommandTimeout = 180 };
    await command.ExecuteNonQueryAsync();
}
