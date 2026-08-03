using DigiAhan.CDR.Receiver.Logging;
using DigiAhan.CDR.Receiver.Models;
using DigiAhan.CDR.Receiver.Services;
using Microsoft.AspNetCore.Http.Json;
using System.Text.Json.Serialization;

const string AppVersion = "3.7.6";
const string BuildDate = "2026-08-03";

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Voip.local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile(
    "appsettings.Accounting.local.json",
    optional: true,
    reloadOnChange: true);

var configuredLogPath = builder.Configuration["Logging:FileDirectory"] ?? "Logs";
var logPath = Path.IsPathRooted(configuredLogPath)
    ? configuredLogPath
    : Path.Combine(builder.Environment.ContentRootPath, configuredLogPath);
builder.Logging.AddProvider(new DailyFileLoggerProvider(logPath));

builder.Host.UseWindowsService(options => options.ServiceName = "DigiAhan CDR Receiver");

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddSingleton<SqlQueryStore>();
builder.Services.AddSingleton<SqlCdrRepository>();
builder.Services.AddSingleton<DashboardRepository>();
builder.Services.AddSingleton<AgentEventStore>();
builder.Services.AddSingleton<CustomerIntelligenceRepository>();
builder.Services.AddSingleton<AgentPanelRepository>();
builder.Services.AddSingleton<SalesDashboardRepository>();
builder.Services.AddSingleton<AccountingSyncService>();

builder.WebHost.ConfigureKestrel(options =>
{
    var port = builder.Configuration.GetValue<int?>("Receiver:Port") ?? 5088;
    options.ListenAnyIP(port);
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GlobalException");
    var requestId = context.TraceIdentifier;
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        logger.LogError(ex,
            "Unhandled request error. Version={Version} RequestId={RequestId} Method={Method} Path={Path} Query={Query}",
            AppVersion, requestId, context.Request.Method, context.Request.Path, context.Request.QueryString.Value);

        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "خطای داخلی سرویس",
                requestId,
                version = AppVersion,
                utc = DateTime.UtcNow
            });
        }
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/", () => Results.Ok(new
{
    service = "DigiAhan CDR Receiver",
    status = "running",
    version = AppVersion,
    buildDate = BuildDate,
    utc = DateTime.UtcNow
}));

app.MapGet("/api/version", () => Results.Ok(new { version = AppVersion, buildDate = BuildDate }));

app.MapGet("/health", async (SqlCdrRepository repository, CancellationToken ct) =>
{
    var dbOk = await repository.CanConnectAsync(ct);
    return dbOk
        ? Results.Ok(new { status = "healthy", database = "connected", version = AppVersion, utc = DateTime.UtcNow })
        : Results.Json(new { status = "unhealthy", database = "disconnected", version = AppVersion, utc = DateTime.UtcNow },
            statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/dashboard", () => Results.Redirect("/dashboard/index.html"));
app.MapGet("/agent/{extension:int}", (int extension) =>
    Results.Redirect($"/agent/index.html?extension={extension}"));
app.MapPost("/api/voip/events", async (
    HttpRequest http,
    VoipRingEventRequest request,
    IConfiguration configuration,
    CustomerIntelligenceRepository repository,
    AgentEventStore store,
    AgentPanelRepository panelRepository,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var expected = configuration["Voip:ApiToken"];
    var supplied = http.Headers["X-Voip-Token"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(expected) || supplied != expected)
        return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(request.Extension) ||
        string.IsNullOrWhiteSpace(request.CallerNumber))
        return Results.BadRequest(new { error = "Extension and CallerNumber are required." });

    var logger = loggerFactory.CreateLogger("VoipEvent");
    AgentCustomerCard card;

    try
    {
        card = await repository.BuildCard(request, ct);
    }
    catch (Exception ex)
    {
        logger.LogError(
            ex,
            "VoIP BuildCard failed. Extension={Extension} Caller={Caller} LinkedId={LinkedId}",
            request.Extension,
            request.CallerNumber,
            request.LinkedId);

        return Results.Json(
            new
            {
                error = "خطا در ساخت کارت مشتری",
                stage = "BuildCard",
                requestId = http.HttpContext.TraceIdentifier,
                version = AppVersion,
                utc = DateTime.UtcNow
            },
            statusCode: StatusCodes.Status500InternalServerError);
    }

    // Availability first: publish the popup before writing call history.
    // A history-write failure must not block the live agent popup.
    var envelope = store.Put(request.Extension.Trim(), card);

    try
    {
        await panelRepository.RecordIncoming(card, ct);
        http.HttpContext.Response.Headers["X-Voip-Persisted"] = "true";
    }
    catch (Exception ex)
    {
        http.HttpContext.Response.Headers["X-Voip-Persisted"] = "false";
        logger.LogError(
            ex,
            "VoIP card was published but history persistence failed. Extension={Extension} Caller={Caller} LinkedId={LinkedId}",
            request.Extension,
            request.CallerNumber,
            request.LinkedId);
    }

    return Results.Ok(envelope);
});

app.MapGet("/api/agent/{extension}/current", (
    string extension,
    AgentEventStore store) =>
{
    var current = store.Get(extension.Trim());
    return current is null ? Results.NoContent() : Results.Ok(current);
});
app.MapPost("/api/agent/outcomes", async (
    AgentOutcomeRequest request,
    AgentPanelRepository repository,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Extension) ||
        string.IsNullOrWhiteSpace(request.CallerNumber) ||
        string.IsNullOrWhiteSpace(request.Outcome))
        return Results.BadRequest(new { error = "Extension, CallerNumber and Outcome are required." });

    return Results.Ok(await repository.SaveOutcome(request, ct));
});

app.MapGet("/api/agent/history", async (
    string? extensions,
    int? take,
    AgentPanelRepository repository,
    CancellationToken ct) =>
    Results.Ok(await repository.RecentIncoming(extensions ?? "201", take ?? 20, ct)));

app.MapGet("/api/agent/outcomes", async (
    string? extensions,
    int? take,
    AgentPanelRepository repository,
    CancellationToken ct) =>
    Results.Ok(await repository.RecentOutcomes(extensions ?? "201", take ?? 15, ct)));

app.MapGet("/api/agent/stats", async (
    string? extensions,
    AgentPanelRepository repository,
    CancellationToken ct) =>
    Results.Ok(await repository.Stats(extensions ?? "201", ct)));
app.MapGet("/api/dashboard/summary", async (DateTime? startDate, DateTime? endDate, string? extension, DashboardRepository repo, CancellationToken ct) =>
{
    var (start, end) = ResolveRange(startDate, endDate);
    return Results.Ok(await repo.Summary(start, end, extension, ct));
});
app.MapGet("/api/dashboard/hourly", async (DateTime? startDate, DateTime? endDate, string? extension, DashboardRepository repo, CancellationToken ct) =>
{
    var (start, end) = ResolveRange(startDate, endDate);
    return Results.Ok(await repo.Hourly(start, end, extension, ct));
});
app.MapGet("/api/dashboard/daily", async (DateTime? startDate, DateTime? endDate, string? extension, DashboardRepository repo, CancellationToken ct) =>
{
    var (start, end) = ResolveRange(startDate, endDate);
    return Results.Ok(await repo.Daily(start, end, extension, ct));
});
app.MapGet("/api/dashboard/extensions", async (DateTime? startDate, DateTime? endDate, DashboardRepository repo, CancellationToken ct) =>
{
    var (start, end) = ResolveRange(startDate, endDate);
    return Results.Ok(await repo.Extensions(start, end, ct));
});
app.MapGet("/api/dashboard/calls", async (DateTime? startDate, DateTime? endDate, string? extension, string? search, string? status, int? page, int? pageSize, DashboardRepository repo, CancellationToken ct) =>
{
    var (start, end) = ResolveRange(startDate, endDate);
    return Results.Ok(await repo.Calls(start, end, extension, search, status, page ?? 1, pageSize ?? 50, ct));
});
app.MapGet("/api/dashboard/sync", async (DashboardRepository repo, CancellationToken ct) => Results.Ok(await repo.Sync(ct)));

app.MapGet("/api/sales/summary", async (
    SalesDashboardRepository repo,
    CancellationToken ct) =>
    Results.Ok(await repo.Summary(ct)));

app.MapGet("/api/sales/by-visitor", async (
    SalesDashboardRepository repo,
    CancellationToken ct) =>
    Results.Ok(await repo.ByVisitor(ct)));

app.MapGet("/api/sales/recent-invoices", async (
    int? take,
    SalesDashboardRepository repo,
    CancellationToken ct) =>
    Results.Ok(await repo.RecentInvoices(take ?? 25, ct)));

app.MapGet("/api/accounting/status", async (
    AccountingSyncService service,
    CancellationToken ct) =>
    Results.Ok(await service.GetStatusAsync(ct)));

app.MapPost("/api/accounting/sync", async (
    int? days,
    AccountingSyncService service,
    CancellationToken ct) =>
{
    var result = await service.SyncAsync(days ?? 30, ct);
    return result.Status == "SUCCESS"
        ? Results.Ok(result)
        : Results.Json(result, statusCode: StatusCodes.Status500InternalServerError);
});

app.MapPost("/api/cdr", async (
    HttpRequest httpRequest,
    CdrBatchRequest request,
    IConfiguration configuration,
    SqlCdrRepository repository,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var expectedToken = configuration["Receiver:ApiToken"];
    var suppliedToken = httpRequest.Headers["X-Api-Token"].FirstOrDefault() ?? request.Token;

    if (string.IsNullOrWhiteSpace(expectedToken) || string.IsNullOrWhiteSpace(suppliedToken) ||
        !TokenComparer.FixedTimeEquals(expectedToken, suppliedToken))
        return Results.Unauthorized();

    if (request.Records is null || request.Records.Count == 0)
        return Results.BadRequest(new { error = "Records is required and cannot be empty." });

    if (request.Records.Count > 1000)
        return Results.BadRequest(new { error = "Maximum batch size is 1000 records." });

    var sourceServer = string.IsNullOrWhiteSpace(request.SourceServer) ? "Issabel" : request.SourceServer.Trim();
    var batchId = request.BatchId ?? Guid.NewGuid();
    var response = new CdrBatchResponse { BatchId = batchId, Received = request.Records.Count };

    await repository.StartBatchAsync(batchId, sourceServer, request.Records.Count, ct);
    try
    {
        foreach (var record in request.Records)
        {
            try
            {
                record.Fingerprint = FingerprintBuilder.Build(record, sourceServer);
                var result = await repository.InsertAsync(sourceServer, batchId, record, ct);
                if (result.Inserted) response.Inserted++; else response.Duplicates++;
            }
            catch (Exception ex)
            {
                response.Errors++;
                response.ErrorMessages.Add(ex.Message);
                logger.LogError(ex, "Failed CDR. UniqueId={UniqueId} LinkedId={LinkedId}", record.UniqueId, record.LinkedId);
            }
        }

        await repository.FinishBatchAsync(batchId, response.Inserted, response.Duplicates, response.Errors,
            response.Errors == 0 ? "SUCCESS" : "PARTIAL",
            response.ErrorMessages.Count == 0 ? null : string.Join(Environment.NewLine, response.ErrorMessages.Take(20)), ct);

        return response.Errors == 0 ? Results.Ok(response) : Results.Json(response, statusCode: StatusCodes.Status207MultiStatus);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "CDR batch failed. BatchId={BatchId}", batchId);
        await repository.FinishBatchAsync(batchId, response.Inserted, response.Duplicates, response.Errors + 1,
            "FAILED", ex.Message, ct);
        return Results.Problem(title: "Batch processing failed", detail: ex.Message, statusCode: 500);
    }
});

static (DateTime Start, DateTime End) ResolveRange(DateTime? startDate, DateTime? endDate)
{
    var start = (startDate ?? DateTime.Today).Date;
    var end = (endDate ?? start).Date;

    if (end < start)
        (start, end) = (end, start);

    if ((end - start).TotalDays > 366)
        end = start.AddDays(366);

    return (start, end);
}


try
{
    using var startupScope = app.Services.CreateScope();
    var agentPanelRepository = startupScope.ServiceProvider
        .GetRequiredService<AgentPanelRepository>();
    await agentPanelRepository.EnsureSchema(CancellationToken.None);
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Agent panel schema startup check failed. The application will continue and retry on request.");
}

app.Run();
