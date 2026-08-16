using DigiAhan.CDR.Receiver.Models;
using DigiAhan.CDR.Receiver.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var masterConnectionString = Environment.GetEnvironmentVariable("DIGIAHAN_JOURNEY_TEST_SQL_MASTER_CONNECTION");
if (string.IsNullOrWhiteSpace(masterConnectionString))
{
    Console.WriteLine("Journey SQL integration skipped: set DIGIAHAN_JOURNEY_TEST_SQL_MASTER_CONNECTION to an isolated test SQL Server.");
    return;
}

var sourceRoot = FindSourceRoot();
var migrationPath = Path.Combine(sourceRoot, "Sql", "CustomerJourneyKernelV440.sql");
var migration = await File.ReadAllTextAsync(migrationPath);
var databaseName = $"DigiAhan_Journey_Test_{Environment.ProcessId}_{Guid.NewGuid():N}";
var masterBuilder = new SqlConnectionStringBuilder(masterConnectionString)
{
    InitialCatalog = "master",
    Pooling = false,
    Encrypt = false,
    TrustServerCertificate = true
};
var testBuilder = new SqlConnectionStringBuilder(masterBuilder.ConnectionString)
{
    InitialCatalog = databaseName,
    Pooling = false
};

await using var master = new SqlConnection(masterBuilder.ConnectionString);
await master.OpenAsync();
try
{
    await ExecuteAsync(master, $"CREATE DATABASE [{databaseName}];");
    await using var test = new SqlConnection(testBuilder.ConnectionString);
    await test.OpenAsync();
    await CreateLegacySchemaAsync(test);

    // Migration must be safe when the installer and the application both ensure the schema.
    await ExecuteAsync(test, migration);
    await ExecuteAsync(test, migration);
    await AssertScalarAsync(test,
        "SELECT COUNT(*) FROM dbo.JourneySchemaVersions WHERE Version=N'4.4.0';", 1,
        "Migration version marker is not idempotent.");

    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DigiAhanCdr"] = testBuilder.ConnectionString
        })
        .Build();
    var options = new CustomerJourneyOptions
    {
        Enabled = true,
        AutoCaptureSellerInteractions = true,
        DefaultLeadSlaMinutes = 5,
        DefaultFollowUpMinutes = 5,
        PilotSellerKeys = ["seller-test"]
    };
    var repository = new CustomerJourneyRepository(
        configuration,
        new SqlQueryStore(new TestWebHostEnvironment(sourceRoot)),
        new StaticOptionsMonitor<CustomerJourneyOptions>(options),
        NullLogger<CustomerJourneyRepository>.Instance);
    var seller = new SellerIdentity("seller-test", "Test Seller", ["201"], ["Steel"]);
    Assert(repository.IsEnabledFor(seller), "Pilot seller should be enabled.");
    Assert(!repository.IsEnabledFor(new SellerIdentity("seller-other", "Other", [], [])),
        "Non-pilot seller should be disabled.");

    var identityId = await InsertIdentityAsync(test, "Journey Test Customer", "09120000001");
    var leadKey = Guid.NewGuid();
    var createRequest = new JourneyCreateLeadRequest(
        leadKey.ToString(), identityId, "Test lead", "Rebar", 3, "CALL_BACK",
        DateTime.UtcNow.AddMinutes(30), "Integration test");
    var lead = await repository.CreateLeadAsync(seller, createRequest, CancellationToken.None);
    Assert(lead.LeadId > 0 && lead.WorkItemId > 0 && !lead.AlreadyExisted, "Lead was not created.");
    var duplicateLead = await repository.CreateLeadAsync(seller, createRequest, CancellationToken.None);
    Assert(duplicateLead.AlreadyExisted && duplicateLead.LeadId == lead.LeadId &&
           duplicateLead.WorkItemId == lead.WorkItemId, "Lead idempotency failed.");

    var workspace = await repository.GetWorkspaceAsync(seller, 20, CancellationToken.None);
    Assert(workspace.Leads.Any(row => row.LeadId == lead.LeadId), "Open lead is missing from workspace.");
    Assert(workspace.WorkItems.Any(row => row.WorkItemId == lead.WorkItemId), "Lead work item is missing.");
    Assert(workspace.Leads.Single(row => row.LeadId == lead.LeadId).PrimaryPhone == "09120000001",
        "Canonical identity phone was not returned.");

    var opportunityKey = Guid.NewGuid();
    var qualifyRequest = new JourneyQualifyLeadRequest(
        opportunityKey.ToString(), "Rebar opportunity", "Rebar", 10, "ton", 1_000_000,
        "SEND_QUOTE", DateTime.UtcNow.AddMinutes(40), DateTime.UtcNow.AddDays(7), "Qualified");
    var opportunity = await repository.QualifyLeadAsync(seller, lead.LeadId, qualifyRequest, CancellationToken.None);
    Assert(opportunity.OpportunityId > 0 && opportunity.WorkItemId > 0 && !opportunity.AlreadyExisted,
        "Opportunity was not created.");
    var duplicateOpportunity = await repository.QualifyLeadAsync(
        seller, lead.LeadId, qualifyRequest, CancellationToken.None);
    Assert(duplicateOpportunity.AlreadyExisted && duplicateOpportunity.OpportunityId == opportunity.OpportunityId,
        "Opportunity idempotency failed.");

    var quoteTransition = await repository.TransitionOpportunityAsync(
        seller,
        opportunity.OpportunityId,
        new JourneyTransitionOpportunityRequest(
            Guid.NewGuid().ToString(), "QUOTE_SENT", "FOLLOW_UP",
            DateTime.UtcNow.AddMinutes(45), null, "Quote sent"),
        CancellationToken.None);
    Assert(quoteTransition.Status == "QUOTE_SENT", "Opportunity transition failed.");

    workspace = await repository.GetWorkspaceAsync(seller, 20, CancellationToken.None);
    var quoteWork = workspace.WorkItems.SingleOrDefault(row =>
        row.OpportunityId == opportunity.OpportunityId && row.OpportunityStage == "QUOTE_SENT");
    Assert(quoteWork is not null, "Quote follow-up work item was not created.");
    await repository.CompleteWorkItemAsync(
        seller,
        quoteWork!.WorkItemId,
        new JourneyCompleteWorkItemRequest(Guid.NewGuid().ToString(), "DONE", "Customer contacted"),
        CancellationToken.None);
    workspace = await repository.GetWorkspaceAsync(seller, 20, CancellationToken.None);
    Assert(workspace.WorkItems.Any(row => row.OpportunityId == opportunity.OpportunityId),
        "Completing an active opportunity task did not create the required successor task.");

    var won = await repository.TransitionOpportunityAsync(
        seller,
        opportunity.OpportunityId,
        new JourneyTransitionOpportunityRequest(Guid.NewGuid().ToString(), "WON", "CLOSED", null, null, "Won"),
        CancellationToken.None);
    Assert(won.Status == "WON", "Won transition failed.");
    workspace = await repository.GetWorkspaceAsync(seller, 20, CancellationToken.None);
    Assert(workspace.Opportunities.All(row => row.OpportunityId != opportunity.OpportunityId),
        "Won opportunity is still active in workspace.");
    Assert(workspace.Leads.All(row => row.LeadId != lead.LeadId), "Converted lead is still active.");
    Assert(workspace.WorkItems.All(row => row.OpportunityId != opportunity.OpportunityId),
        "Closed opportunity still has an open work item.");

    var overdueIdentity = await InsertIdentityAsync(test, "Overdue Customer", "09120000002");
    var overdueLead = await repository.CreateLeadAsync(
        seller,
        new JourneyCreateLeadRequest(Guid.NewGuid().ToString(), overdueIdentity, "Overdue lead", null, 2,
            "CALL_BACK", DateTime.UtcNow.AddMinutes(20), null),
        CancellationToken.None);
    await ExecuteAsync(test,
        $"UPDATE dbo.JourneyWorkItems SET SlaDueAtUtc=DATEADD(minute,-10,SYSUTCDATETIME()) WHERE WorkItemId={overdueLead.WorkItemId};");
    var exceptions = await repository.GetManagerExceptionsAsync(100, CancellationToken.None);
    Assert(exceptions.Any(row => row.WorkItemId == overdueLead.WorkItemId && row.OverdueMinutes >= 9),
        "Overdue SLA exception was not detected.");

    var legacyIdentity = await InsertIdentityAsync(test, "Legacy Seller Customer", "09120000003");
    var interactionId = await InsertLegacyInteractionAsync(test, legacyIdentity, seller.Key);
    var capture = await repository.CaptureInteractionBestEffortAsync(seller, interactionId, CancellationToken.None);
    Assert(capture.Captured && capture.LeadId > 0 && capture.WorkItemId > 0 && capture.Reason == "CAPTURED",
        "Seller v2 interaction was not captured into Journey.");
    var duplicateCapture = await repository.CaptureInteractionBestEffortAsync(
        seller, interactionId, CancellationToken.None);
    Assert(!duplicateCapture.Captured && duplicateCapture.LeadId == capture.LeadId &&
           duplicateCapture.Reason == "ALREADY_CAPTURED", "Seller v2 bridge idempotency failed.");

    await AssertScalarAsync(test,
        "SELECT CASE WHEN (SELECT COUNT(*) FROM dbo.JourneyEvents)>0 AND (SELECT COUNT(*) FROM dbo.JourneyEvents)=(SELECT COUNT(*) FROM dbo.JourneyOutbox) THEN 1 ELSE 0 END;",
        1, "Event/outbox atomicity failed.");
    await AssertScalarAsync(test,
        "SELECT COUNT(*) FROM dbo.JourneyLeads WHERE SourceSystem=N'SELLER_V2' AND SourceInteractionId=" + interactionId + ";",
        1, "Legacy interaction was duplicated.");
    await VerifyOwnerInvariantAsync(test);

    Console.WriteLine("PASS: v4.4.0 migration executed twice on an isolated database.");
    Console.WriteLine("PASS: lead/opportunity/work-item/SLA/outbox workflow.");
    Console.WriteLine("PASS: Seller v2 compatibility bridge and idempotency.");
}
finally
{
    SqlConnection.ClearAllPools();
    await ExecuteAsync(master,
        $"IF DB_ID(N'{databaseName}') IS NOT NULL BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END;");
    await AssertScalarAsync(master, $"SELECT CASE WHEN DB_ID(N'{databaseName}') IS NULL THEN 1 ELSE 0 END;", 1,
        "Temporary integration database cleanup failed.");
    Console.WriteLine("PASS: isolated SQL test database removed.");
}

static string FindSourceRoot()
{
    var configured = Environment.GetEnvironmentVariable("DIGIAHAN_JOURNEY_TEST_SOURCE_ROOT");
    if (!string.IsNullOrWhiteSpace(configured) &&
        File.Exists(Path.Combine(configured, "Sql", "CustomerJourneyKernelV440.sql")))
        return Path.GetFullPath(configured);

    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        var candidate = Path.Combine(directory.FullName, "Source");
        if (File.Exists(Path.Combine(candidate, "Sql", "CustomerJourneyKernelV440.sql"))) return candidate;
        directory = directory.Parent;
    }
    throw new DirectoryNotFoundException("Repository Source directory was not found.");
}

static async Task CreateLegacySchemaAsync(SqlConnection connection)
{
    await ExecuteAsync(connection, """
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
        CREATE TABLE dbo.CustomerIdentityPhones
        (
            Id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
            IdentityId bigint NOT NULL,
            NormalizedPhone nvarchar(30) NOT NULL,
            RawPhone nvarchar(100) NULL,
            PhoneType nvarchar(30) NULL,
            SourceSystem nvarchar(30) NOT NULL DEFAULT(N'TEST'),
            IsPrimary bit NOT NULL DEFAULT(0),
            IsVerified bit NOT NULL DEFAULT(1),
            Priority int NOT NULL DEFAULT(10),
            CreatedAtUtc datetime2(0) NOT NULL DEFAULT(SYSUTCDATETIME()),
            CONSTRAINT FK_TestPhone_Identity FOREIGN KEY(IdentityId) REFERENCES dbo.CustomerIdentities(IdentityId)
        );
        CREATE TABLE dbo.SellerInteractions
        (
            Id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
            IdempotencyKey uniqueidentifier NOT NULL,
            SellerKey nvarchar(80) NOT NULL,
            CustomerIdentityId bigint NULL,
            Outcome nvarchar(40) NOT NULL,
            LossReason nvarchar(300) NULL,
            Note nvarchar(1500) NULL,
            OccurredAtUtc datetime2(0) NOT NULL
        );
        CREATE TABLE dbo.SellerInteractionProducts
        (
            Id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
            InteractionId bigint NOT NULL,
            ProductName nvarchar(200) NOT NULL
        );
        CREATE TABLE dbo.SellerFollowUps
        (
            Id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
            InteractionId bigint NOT NULL,
            Status nvarchar(20) NOT NULL,
            Subject nvarchar(300) NOT NULL,
            DueAtUtc datetime2(0) NOT NULL
        );
        """);
}

static async Task<long> InsertIdentityAsync(SqlConnection connection, string displayName, string phone)
{
    await using var command = new SqlCommand("""
        INSERT dbo.CustomerIdentities(DisplayName) OUTPUT inserted.IdentityId VALUES(@name);
        """, connection);
    command.Parameters.AddWithValue("@name", displayName);
    var identityId = Convert.ToInt64(await command.ExecuteScalarAsync());
    await using var phoneCommand = new SqlCommand("""
        INSERT dbo.CustomerIdentityPhones(IdentityId,NormalizedPhone,RawPhone,PhoneType,IsPrimary)
        VALUES(@identity,@phone,@phone,N'Mobile',1);
        """, connection);
    phoneCommand.Parameters.AddWithValue("@identity", identityId);
    phoneCommand.Parameters.AddWithValue("@phone", phone);
    await phoneCommand.ExecuteNonQueryAsync();
    return identityId;
}

static async Task<long> InsertLegacyInteractionAsync(SqlConnection connection, long identityId, string sellerKey)
{
    await using var command = new SqlCommand("""
        INSERT dbo.SellerInteractions
          (IdempotencyKey,SellerKey,CustomerIdentityId,Outcome,Note,OccurredAtUtc)
        OUTPUT inserted.Id
        VALUES(NEWID(),@seller,@identity,N'FOLLOW_UP',N'Legacy bridge test',SYSUTCDATETIME());
        """, connection);
    command.Parameters.AddWithValue("@seller", sellerKey);
    command.Parameters.AddWithValue("@identity", identityId);
    var interactionId = Convert.ToInt64(await command.ExecuteScalarAsync());
    await using var related = new SqlCommand("""
        INSERT dbo.SellerInteractionProducts(InteractionId,ProductName) VALUES(@id,N'Sheet');
        INSERT dbo.SellerFollowUps(InteractionId,Status,Subject,DueAtUtc)
        VALUES(@id,N'OPEN',N'Call customer',DATEADD(minute,30,SYSUTCDATETIME()));
        """, connection);
    related.Parameters.AddWithValue("@id", interactionId);
    await related.ExecuteNonQueryAsync();
    return interactionId;
}

static async Task VerifyOwnerInvariantAsync(SqlConnection connection)
{
    var rejected = false;
    try
    {
        await ExecuteAsync(connection, """
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
        rejected = true;
    }
    Assert(rejected, "SQL owner invariant was not enforced.");
}

static async Task ExecuteAsync(SqlConnection connection, string sql)
{
    await using var command = new SqlCommand(sql, connection) { CommandTimeout = 180 };
    await command.ExecuteNonQueryAsync();
}

static async Task AssertScalarAsync(SqlConnection connection, string sql, int expected, string message)
{
    await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
    var actual = Convert.ToInt32(await command.ExecuteScalarAsync());
    if (actual != expected) throw new InvalidOperationException($"{message} Expected={expected}, Actual={actual}.");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => NullDisposable.Instance;

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}

sealed class TestWebHostEnvironment : IWebHostEnvironment
{
    public TestWebHostEnvironment(string contentRootPath)
    {
        ContentRootPath = contentRootPath;
        WebRootPath = Path.Combine(contentRootPath, "wwwroot");
        ContentRootFileProvider = new PhysicalFileProvider(contentRootPath);
        WebRootFileProvider = Directory.Exists(WebRootPath)
            ? new PhysicalFileProvider(WebRootPath)
            : new NullFileProvider();
    }

    public string ApplicationName { get; set; } = "DigiAhan.CDR.JourneyMigrationTests";
    public IFileProvider WebRootFileProvider { get; set; }
    public string WebRootPath { get; set; }
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }
}
