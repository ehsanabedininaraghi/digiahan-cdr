using DigiAhan.CDR.Receiver.Logging;
using DigiAhan.CDR.Receiver.Models;
using DigiAhan.CDR.Receiver.Services;
using DigiAhan.CDR.Receiver.Features.Journey;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Threading.RateLimiting;

const string AppVersion = "4.4.4";
const string BuildDate = "2026-08-25";

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Voip.local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile("appsettings.RecordingIngestion.local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile("appsettings.SellerWorkspace.local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile("appsettings.JourneyKernel.local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile("appsettings.Dashboard.local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile("appsettings.Didar.local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile(
    "appsettings.Accounting.local.json",
    optional: true,
    reloadOnChange: true);
builder.Configuration.AddJsonFile(
    "appsettings.DataGathering.local.json",
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
builder.Services.AddOutputCache(options =>
    options.AddBasePolicy(policy => policy
        .Expire(TimeSpan.FromSeconds(30))
        .SetVaryByQuery("*")));

builder.Services.AddSingleton<SqlQueryStore>();
builder.Services.AddSingleton<SqlCdrRepository>();
builder.Services.AddSingleton<DashboardRepository>();
builder.Services.AddSingleton<AgentEventStore>();
builder.Services.AddSingleton<AgentCallStateStore>();
builder.Services.AddSingleton<CustomerIntelligenceRepository>();
builder.Services.AddSingleton<AgentPanelRepository>();
builder.Services.AddSingleton<SellerWorkspaceAccessService>();
builder.Services.AddSingleton<SellerWorkspaceRepository>();
builder.Services.Configure<CustomerJourneyOptions>(builder.Configuration.GetSection("JourneyKernel"));
builder.Services.AddSingleton<CustomerJourneyRepository>();
builder.Services.AddHttpClient<LegacyAgentBridgeService>();
builder.Services.AddSingleton<SalesDashboardRepository>();
builder.Services.AddSingleton<AccountingSyncService>();
builder.Services.AddSingleton<VoipIncidentLogger>();
builder.Services.AddSingleton<ExcelMappingReader>();
builder.Services.AddSingleton<CustomerMappingService>();
builder.Services.AddSingleton<LegacyAccountingBridgeRunner>();
builder.Services.AddSingleton<CustomerIdentityReconcileService>();
builder.Services.AddSingleton<DidarPhoneRebuildService>();
builder.Services.AddSingleton<DidarApiSyncService>();
builder.Services.AddSingleton<DataGatheringCoordinator>();
builder.Services.AddSingleton<IntegrationSchedulerRepository>();
builder.Services.AddSingleton<SystemHealthService>();
builder.Services.AddSingleton<DatabaseMaintenanceService>();
builder.Services.AddSingleton<IntegrationSchedulerService>();
builder.Services.AddSingleton<InvoiceNotificationRepository>();
builder.Services.Configure<AiPipelineOptions>(builder.Configuration.GetSection("AiPipeline"));
builder.Services.AddSingleton<AiPipelineRepository>();
builder.Services.Configure<RecordingIngestionOptions>(builder.Configuration.GetSection("RecordingIngestion"));
builder.Services.AddSingleton<RecordingAssetRepository>();
builder.Services.AddSingleton<IssabelRecordingPathResolver>();
builder.Services.AddSingleton<IssabelSftpRecordingClient>();
builder.Services.AddSingleton<RecordingAudioValidator>();
builder.Services.AddSingleton<FasterWhisperTranscriber>();
builder.Services.AddSingleton<AiTranscriptAnalyzer>();
builder.Services.AddSingleton<AiAnalysisRepository>();
if (builder.Configuration.GetValue("IntegrationScheduler:Enabled", true))
    builder.Services.AddHostedService<IntegrationSchedulerWorker>();
builder.Services.AddHostedService<AiCallDiscoveryWorker>();
builder.Services.AddHostedService<DailyRecordingIngestionWorker>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("public-order", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("dashboard-login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("seller-login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

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

app.Use(async (context, next) =>
{
    if (!RequiresDashboardAuthentication(context.Request.Path))
    {
        await next();
        return;
    }

    var supplied = context.Request.Cookies["digiahan_dashboard_auth"];
    var expected = DashboardCookieValue(context.RequestServices.GetRequiredService<IConfiguration>());
    if (!string.IsNullOrWhiteSpace(expected)
        && !string.IsNullOrWhiteSpace(supplied)
        && TokenComparer.FixedTimeEquals(expected, supplied))
    {
        await next();
        return;
    }

    if (context.Request.Path.StartsWithSegments("/dashboard")
        || context.Request.Path.StartsWithSegments("/ai")
        || context.Request.Path.StartsWithSegments("/journey-control"))
    {
        context.Response.Redirect("/dashboard-login");
        return;
    }

    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    await context.Response.WriteAsJsonAsync(new { error = "ورود به داشبورد مدیریت لازم است." });
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseOutputCache();
app.UseRateLimiter();

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
app.MapGet("/dashboard-login", () => Results.Redirect("/dashboard-login/index.html"));
app.MapPost("/api/dashboard-auth/login", async (HttpContext context, IConfiguration configuration) =>
{
    var input = await context.Request.ReadFromJsonAsync<Dictionary<string, string>>();
    var password = input is not null && input.TryGetValue("password", out var value) ? value : null;
    var configuredHash = DashboardPasswordHash(configuration);
    if (string.IsNullOrWhiteSpace(configuredHash))
        return Results.Json(new { error = "رمز داشبورد هنوز تنظیم نشده است." }, statusCode: 503);
    var suppliedHash = HashDashboardSecret(password ?? string.Empty);
    if (!TokenComparer.FixedTimeEquals(configuredHash, suppliedHash))
        return Results.Json(new { error = "رمز واردشده صحیح نیست." }, statusCode: 401);

    context.Response.Cookies.Append("digiahan_dashboard_auth", DashboardCookieValue(configuration), new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = context.Request.IsHttps,
        IsEssential = true,
        Path = "/"
    });
    return Results.Ok(new { authenticated = true });
}).RequireRateLimiting("dashboard-login");
app.MapGet("/api/dashboard-auth/logout", (HttpContext context) =>
{
    context.Response.Cookies.Delete("digiahan_dashboard_auth", new CookieOptions { Path = "/" });
    return Results.Redirect("/dashboard-login");
});
app.MapGet("/ai", () => Results.Redirect("/ai/index.html"));
app.MapGet("/invoice-notifications", () => Results.Redirect("/invoice-notifications/index.html"));
app.MapGet("/sms-dashboard", () => Results.Redirect("/sms-dashboard/index.html"));
app.MapGet("/seller-v2", () => Results.Redirect("/seller-v2/index.html"));
app.MapGet("/seller-activity", () => Results.Redirect("/seller-activity/index.html"));
app.MapGet("/seller-mapping", () => Results.Redirect("/seller-mapping/index.html"));
app.MapGet("/seller-admin", () => Results.Redirect("/seller-admin/index.html"));
app.MapCustomerJourneyEndpoints();
app.MapGet("/order/{token}", (string token) =>
    PublicTokenService.IsWellFormed(token)
        ? Results.File(Path.Combine(app.Environment.WebRootPath, "order", "index.html"), "text/html; charset=utf-8")
        : Results.NotFound());
app.MapGet("/agent/{extension:int}", (int extension) =>
    Results.Redirect($"/agent/index.html?extension={extension}"));
app.MapPost("/api/voip/events", async (
    HttpContext context,
    IConfiguration configuration,
    CustomerIntelligenceRepository repository,
    AgentEventStore store,
    AgentCallStateStore callStates,
    AgentPanelRepository panelRepository,
    VoipIncidentLogger incidentLogger,
    ILoggerFactory loggerFactory) =>
{
    var requestId = context.TraceIdentifier;
    var logger = loggerFactory.CreateLogger("VoipEventV4");
    string rawBody;

    using (var reader = new StreamReader(context.Request.Body))
        rawBody = await reader.ReadToEndAsync();

    var runId = incidentLogger.Start(
        requestId,
        context.Request.Method,
        context.Request.Path,
        context.Connection.RemoteIpAddress?.ToString(),
        rawBody);

    try
    {
        var expected = configuration["Voip:ApiToken"];
        var supplied = context.Request.Headers["X-Voip-Token"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(expected) || supplied != expected)
        {
            incidentLogger.Write(runId, "AUTH_FAILED", new { tokenPresent = !string.IsNullOrWhiteSpace(supplied) });
            return Results.Unauthorized();
        }

        VoipRingEventRequest request;
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;

            static string? ReadString(JsonElement element, params string[] names)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                            return property.Value.GetString();
                        return property.Value.ToString();
                    }
                }
                return null;
            }

            var extension = ReadString(root, "extension", "ext", "internal")?.Trim();
            var caller = ReadString(root, "callerNumber", "caller", "phone", "src")?.Trim();
            var linkedId = ReadString(root, "linkedId", "linkedid", "uniqueId");
            var channel = ReadString(root, "channel");
            var eventText = ReadString(root, "eventTimeUtc", "eventTime", "utc");
            var eventTime = TehranClock.NormalizeIncomingEventUtc(eventText);

            if (string.IsNullOrWhiteSpace(extension) || string.IsNullOrWhiteSpace(caller))
            {
                incidentLogger.Write(runId, "VALIDATION_FAILED", new { extension, caller, rawBody });
                return Results.BadRequest(new
                {
                    error = "Extension and CallerNumber are required.",
                    runId,
                    version = AppVersion
                });
            }

            request = new VoipRingEventRequest(extension, caller, linkedId, channel, eventTime);
            callStates.RegisterRing(request.LinkedId, request.EventTimeUtc ?? DateTime.UtcNow);
            incidentLogger.Write(runId, "REQUEST_PARSED", new
            {
                request.Extension,
                request.CallerNumber,
                request.LinkedId,
                request.Channel,
                request.EventTimeUtc
            });
        }
        catch (Exception ex)
        {
            incidentLogger.Write(runId, "JSON_PARSE_FAILED", new { rawBody }, ex);
            return Results.BadRequest(new
            {
                error = "Invalid JSON body.",
                runId,
                version = AppVersion
            });
        }

        // Publish a minimal card before any database lookup. The legacy and V2
        // workspaces can show the ringing call immediately, then receive the
        // enriched card under the same LinkedId a moment later.
        var fastCard = new AgentCustomerCard(
            request.Extension.Trim(),
            request.CallerNumber.Trim(),
            request.EventTimeUtc ?? DateTime.UtcNow,
            request.LinkedId,
            null, null, null, null, false, null, 0, null, null,
            null, null, null, 0, 0m, "C", "COLD",
            "در حال دریافت اطلاعات مشتری…", null, "PENDING", "شناسایی سریع تماس", null, null);
        var fastEnvelope = store.Put(request.Extension.Trim(), fastCard);
        incidentLogger.Write(runId, "FAST_POPUP_PUBLISHED", new { fastEnvelope.Sequence, elapsedMs = 0 });

        AgentCustomerCard card;
        var mode = "FULL";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var fullTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            card = await repository.BuildCard(request, fullTimeout.Token);
            incidentLogger.Write(runId, "FULL_CARD_SUCCESS", new
            {
                card.CustomerName,
                card.AccountingCustomerCode,
                card.IsKnownCustomer,
                elapsedMs = stopwatch.ElapsedMilliseconds
            });
        }
        catch (Exception fullException)
        {
            mode = "FALLBACK";
            incidentLogger.Write(runId, "FULL_CARD_FAILED", new { elapsedMs = stopwatch.ElapsedMilliseconds }, fullException);

            try
            {
                using var fallbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                card = await repository.BuildFallbackCard(request, fallbackTimeout.Token);
                incidentLogger.Write(runId, "FALLBACK_CARD_SUCCESS", new
                {
                    card.CustomerName,
                    card.AccountingCustomerCode,
                    card.IsKnownCustomer,
                    elapsedMs = stopwatch.ElapsedMilliseconds
                });
            }
            catch (Exception fallbackException)
            {
                incidentLogger.Write(runId, "FALLBACK_CARD_FAILED", null, fallbackException);

                card = new AgentCustomerCard(
                    request.Extension,
                    request.CallerNumber,
                    request.EventTimeUtc ?? DateTime.UtcNow,
                    request.LinkedId,
                    null,
                    null,
                    null,
                    null,
                    false,
                    null,
                    0,
                    null,
                    null,
                    null,
                    null,
                    null,
                    0,
                    0m,
                    "C",
                    "COLD",
                    "مشتری جدید است؛ نام، شرکت و موضوع درخواست را ثبت کنید.",
                    null,
                    "EMERGENCY",
                    "کارت اضطراری به دلیل خطای پایگاه داده ساخته شد",
                    null,
                    null);
                mode = "EMERGENCY";
            }
        }

        var envelope = store.Put(request.Extension.Trim(), card);
        context.Response.Headers["X-Voip-Mode"] = mode;
        context.Response.Headers["X-Voip-RunId"] = runId;
        incidentLogger.Write(runId, "POPUP_PUBLISHED", new { mode, envelope.Sequence, card.AccountingCustomerCode });

        try
        {
            using var persistenceTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await panelRepository.RecordIncoming(card, persistenceTimeout.Token);
            context.Response.Headers["X-Voip-Persisted"] = "true";
            incidentLogger.Write(runId, "HISTORY_PERSISTED");
        }
        catch (Exception persistException)
        {
            context.Response.Headers["X-Voip-Persisted"] = "false";
            incidentLogger.Write(runId, "HISTORY_PERSIST_FAILED", null, persistException);
        }

        incidentLogger.Write(runId, "COMPLETED", new
        {
            mode,
            elapsedMs = stopwatch.ElapsedMilliseconds,
            card.CustomerName,
            card.AccountingCustomerCode
        });

        return Results.Ok(new
        {
            runId,
            mode,
            envelope.Sequence,
            envelope.Card,
            version = AppVersion
        });
    }
    catch (Exception ex)
    {
        incidentLogger.Write(runId, "UNEXPECTED_ENDPOINT_FAILURE", null, ex);
        logger.LogError(ex, "VoIP v4 endpoint failed. RunId={RunId} RequestId={RequestId}", runId, requestId);

        // The endpoint never returns 500 for an authenticated, structurally valid
        // VoIP event. A minimal response keeps the phone popup path alive while
        // preserving the complete exception in the incident file.
        return Results.Ok(new
        {
            runId,
            mode = "DEGRADED",
            accepted = true,
            errorLogged = true,
            version = AppVersion
        });
    }
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

app.MapPost("/api/seller-v2/login", async (
    SellerLoginRequest request,
    HttpContext context,
    SellerWorkspaceAccessService access,
    CancellationToken ct) =>
{
    var login = await access.LoginAsync(request.Username, request.Password, ct);
    if (login is null) return Results.Unauthorized();
    context.Response.Cookies.Append("digiahan_seller_session", login.AccessToken, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = context.Request.IsHttps,
        IsEssential = true,
        Path = "/",
        Expires = new DateTimeOffset(login.ExpiresAtUtc)
    });
    return Results.Ok(new SellerSessionResponse(login.Seller.Key, login.Seller.DisplayName,
        login.Seller.Extensions, login.Seller.ProductGroups));
}).RequireRateLimiting("seller-login");

app.MapPost("/api/seller-v2/logout", async (
    HttpContext context,
    SellerWorkspaceAccessService access,
    CancellationToken ct) =>
{
    await access.LogoutAsync(context, ct);
    context.Response.Cookies.Delete("digiahan_seller_session", new CookieOptions { Path = "/" });
    return Results.Ok(new { loggedOut = true });
});

app.MapPost("/api/voip/call-status", async (
    HttpContext context,
    IConfiguration configuration,
    AgentCallStateStore callStates,
    CancellationToken ct) =>
{
    var expected = configuration["Voip:ApiToken"];
    var supplied = context.Request.Headers["X-Voip-Token"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(expected) || supplied != expected)
        return Results.Unauthorized();

    var request = await context.Request.ReadFromJsonAsync<VoipCallStatusRequest>(cancellationToken: ct);
    if (request is null || string.IsNullOrWhiteSpace(request.LinkedId) || string.IsNullOrWhiteSpace(request.State))
        return Results.BadRequest(new { error = "LinkedId and State are required." });
    try
    {
        return Results.Ok(callStates.Update(request));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/seller-v2/session", async (
    HttpContext context,
    SellerWorkspaceAccessService access,
    CancellationToken ct) =>
{
    var seller = await access.AuthenticateAsync(context, ct);
    return seller is null
        ? Results.Unauthorized()
        : Results.Ok(new SellerSessionResponse(
            seller.Key, seller.DisplayName, seller.Extensions, seller.ProductGroups));
});

app.MapGet("/api/seller-admin/users", async (
    SellerWorkspaceAccessService access,
    CancellationToken ct) => Results.Ok(await access.ListUsersAsync(ct)));

app.MapPost("/api/seller-admin/users", async (
    SellerAdminUserSaveRequest request,
    SellerWorkspaceAccessService access,
    CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await access.CreateUserAsync(request, ct));
    }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException)
    {
        return SellerAdminError(error);
    }
});

app.MapPut("/api/seller-admin/users/{id:long}", async (
    long id,
    SellerAdminUserSaveRequest request,
    SellerWorkspaceAccessService access,
    CancellationToken ct) =>
{
    try
    {
        var result = await access.UpdateUserAsync(id, request, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException)
    {
        return SellerAdminError(error);
    }
});

app.MapPost("/api/seller-admin/users/{id:long}/reset-password", async (
    long id,
    SellerAdminPasswordResetRequest request,
    SellerWorkspaceAccessService access,
    CancellationToken ct) =>
{
    try
    {
        return await access.ResetPasswordAsync(id, request.NewPassword, ct)
            ? Results.Ok(new { id, passwordReset = true })
            : Results.NotFound();
    }
    catch (ArgumentException error)
    {
        return SellerAdminError(error);
    }
});

app.MapGet("/api/seller-v2/current-call", async (
    HttpContext context,
    SellerWorkspaceAccessService access,
    AgentEventStore events,
    AgentCallStateStore callStates,
    LegacyAgentBridgeService legacyBridge,
    AgentPanelRepository panelRepository,
    SellerWorkspaceRepository workspace,
    CancellationToken ct) =>
{
    var seller = await access.AuthenticateAsync(context, ct);
    if (seller is null) return Results.Unauthorized();
    var envelope = seller.Extensions
        .Select(events.Get)
        .Where(envelope => envelope is not null)
        .Select(value => value!)
        .Where(value => value.PublishedAtUtc >= DateTime.UtcNow.AddMinutes(-10))
        .OrderByDescending(value => value.PublishedAtUtc)
        .FirstOrDefault();
    var card = envelope?.Card ?? await legacyBridge.GetCurrentAsync(seller, ct);
    AgentIncomingEventRow? persisted = null;
    if (card is null)
    {
        persisted = (await panelRepository.RecentIncoming(string.Join(',', seller.Extensions), 1, ct))
            .FirstOrDefault(row => TehranClock.AsUtc(row.CreatedAtUtc) >= DateTime.UtcNow.AddMinutes(-10));
        card = persisted is null ? null : CardFromIncoming(persisted);
    }
    if (card is null) return Results.NoContent();
    var publishedAt = envelope?.PublishedAtUtc ?? TehranClock.AsUtc(persisted?.CreatedAtUtc ?? card.EventTimeUtc);
    var liveState = callStates.Get(card.LinkedId);
    var cdrState = await workspace.GetCallLifecycleAsync(card.LinkedId, card.CallerNumber, card.EventTimeUtc, ct);
    var lifecycle = cdrState?.State == "ENDED"
        ? cdrState
        : liveState ?? cdrState ?? new SellerCallLifecycle("RINGING", null, null, null, 0);
    var answererName = await workspace.GetSellerDisplayNameAsync(lifecycle.AnsweredExtension, ct);
    var requiresInteraction = lifecycle.AnsweredExtension is not null
        && seller.Extensions.Contains(lifecycle.AnsweredExtension, StringComparer.OrdinalIgnoreCase);
    return Results.Ok(new SellerCurrentCallResponse(
        card, publishedAt, DateTime.UtcNow, lifecycle.State, lifecycle.AnsweredExtension,
        answererName, lifecycle.AnsweredAtUtc, lifecycle.EndedAtUtc, lifecycle.TalkSeconds,
        requiresInteraction));
}).CacheOutput(policy => policy.NoCache());

app.MapGet("/api/seller-v2/live-events", async (
    HttpContext context,
    SellerWorkspaceAccessService access,
    AgentEventStore events,
    AgentCallStateStore callStates,
    SellerWorkspaceRepository workspace,
    CancellationToken ct) =>
{
    var seller = await access.AuthenticateAsync(context, ct);
    if (seller is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    context.Response.Headers.CacheControl = "no-cache, no-store";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream; charset=utf-8";
    long lastSequence = -1;
    var lastHeartbeat = DateTime.UtcNow;

    while (!ct.IsCancellationRequested)
    {
        var envelope = seller.Extensions
            .Select(events.Get)
            .Where(value => value is not null)
            .Select(value => value!)
            .OrderByDescending(value => value.Sequence)
            .FirstOrDefault();

        if (envelope is not null && envelope.Sequence != lastSequence &&
            envelope.PublishedAtUtc >= DateTime.UtcNow.AddMinutes(-10))
        {
            lastSequence = envelope.Sequence;
            var lifecycle = callStates.Get(envelope.Card.LinkedId)
                ?? new SellerCallLifecycle("RINGING", null, null, null, 0);
            var answererName = await workspace.GetSellerDisplayNameAsync(lifecycle.AnsweredExtension, ct);
            var payload = JsonSerializer.Serialize(new SellerCurrentCallResponse(
                envelope.Card, envelope.PublishedAtUtc, DateTime.UtcNow,
                lifecycle.State, lifecycle.AnsweredExtension, answererName,
                lifecycle.AnsweredAtUtc, lifecycle.EndedAtUtc, lifecycle.TalkSeconds,
                lifecycle.AnsweredExtension is not null
                    && seller.Extensions.Contains(lifecycle.AnsweredExtension, StringComparer.OrdinalIgnoreCase)));
            await context.Response.WriteAsync($"event: call\ndata: {payload}\n\n", ct);
            await context.Response.Body.FlushAsync(ct);
        }
        else if (DateTime.UtcNow - lastHeartbeat >= TimeSpan.FromSeconds(15))
        {
            lastHeartbeat = DateTime.UtcNow;
            await context.Response.WriteAsync(": heartbeat\n\n", ct);
            await context.Response.Body.FlushAsync(ct);
        }

        try { await Task.Delay(500, ct); }
        catch (OperationCanceledException) { break; }
    }
});

app.MapGet("/api/seller-v2/workspace", async (
    HttpContext context,
    string? phone,
    bool? readOnly,
    SellerWorkspaceAccessService access,
    SellerWorkspaceRepository workspace,
    CustomerIntelligenceRepository customers,
    AgentEventStore events,
    LegacyAgentBridgeService legacyBridge,
    AgentPanelRepository panelRepository,
    CancellationToken ct) =>
{
    var seller = await access.AuthenticateAsync(context, ct);
    if (seller is null) return Results.Unauthorized();

    var readOnlyCustomer = readOnly == true;

    AgentCustomerCard? card;
    if (!string.IsNullOrWhiteSpace(phone))
    {
        card = await customers.BuildCard(
            new VoipRingEventRequest(seller.Extensions[0], phone, null, null, DateTime.UtcNow), ct);
    }
    else
    {
        card = seller.Extensions
            .Select(events.Get)
            .Where(envelope => envelope is not null)
            .Select(envelope => envelope!.Card)
            .OrderByDescending(value => value.EventTimeUtc)
            .FirstOrDefault();
        card ??= await legacyBridge.GetCurrentAsync(seller, ct);
        if (card is null)
        {
            var persisted = (await panelRepository.RecentIncoming(string.Join(',', seller.Extensions), 1, ct))
                .FirstOrDefault(row => TehranClock.AsUtc(row.CreatedAtUtc) >= DateTime.UtcNow.AddMinutes(-10));
            card = persisted is null ? null : CardFromIncoming(persisted);
        }
    }

    var statsTask = workspace.GetStatsAsync(seller, ct);
    var yesterdayStatsTask = workspace.GetYesterdayStatsAsync(seller, ct);
    var performanceTask = workspace.GetPerformanceAsync(seller, ct);
    var followUpsTask = workspace.GetFollowUpsAsync(seller, 20, ct);
    var timelineTask = card is null
        ? Task.FromResult<IReadOnlyList<SellerTimelineRow>>(Array.Empty<SellerTimelineRow>())
        : workspace.GetTimelineAsync(seller, card.CallerNumber, 50, ct);
    await Task.WhenAll(statsTask, yesterdayStatsTask, performanceTask, followUpsTask, timelineTask);

    return Results.Ok(new SellerWorkspaceResponse(
        new SellerSessionResponse(seller.Key, seller.DisplayName, seller.Extensions, seller.ProductGroups),
        card,
        await statsTask,
        await yesterdayStatsTask,
        await performanceTask,
        await followUpsTask,
        await timelineTask,
        readOnlyCustomer,
        DateTime.UtcNow));
}).CacheOutput(policy => policy.NoCache());

app.MapGet("/api/seller-v2/customers/search", async (
    HttpContext context,
    string? q,
    int? take,
    SellerWorkspaceAccessService access,
    SellerWorkspaceRepository workspace,
    CancellationToken ct) =>
{
    var seller = await access.AuthenticateAsync(context, ct);
    if (seller is null) return Results.Unauthorized();
    return Results.Ok(await workspace.SearchCustomersAsync(q, take ?? 20, ct));
}).CacheOutput(policy => policy.NoCache());

app.MapGet("/api/seller-v2/customers/profile", async (
    HttpContext context,
    string? phone,
    SellerWorkspaceAccessService access,
    SellerWorkspaceRepository workspace,
    CancellationToken ct) =>
{
    var seller = await access.AuthenticateAsync(context, ct);
    if (seller is null) return Results.Unauthorized();
    return Results.Ok(await workspace.GetCustomerProfileAsync(phone, ct));
}).CacheOutput(policy => policy.NoCache());

app.MapPost("/api/seller-v2/customers", async (
    HttpContext context,
    SellerCustomerSaveRequest request,
    SellerWorkspaceAccessService access,
    SellerWorkspaceRepository workspace,
    CancellationToken ct) =>
{
    var seller = await access.AuthenticateAsync(context, ct);
    if (seller is null) return Results.Unauthorized();
    try { return Results.Ok(await workspace.SaveCustomerAsync(seller, null, request, true, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPut("/api/seller-v2/customers/by-phone/{phone}", async (
    string phone,
    HttpContext context,
    SellerCustomerSaveRequest request,
    SellerWorkspaceAccessService access,
    SellerWorkspaceRepository workspace,
    CancellationToken ct) =>
{
    var seller = await access.AuthenticateAsync(context, ct);
    if (seller is null) return Results.Unauthorized();
    try { return Results.Ok(await workspace.SaveCustomerAsync(seller, phone, request, false, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapDelete("/api/seller-v2/customers/by-phone/{phone}", async (
    string phone,
    HttpContext context,
    SellerWorkspaceAccessService access,
    SellerWorkspaceRepository workspace,
    CancellationToken ct) =>
{
    var seller = await access.AuthenticateAsync(context, ct);
    if (seller is null) return Results.Unauthorized();
    return await workspace.ArchiveCustomerAsync(seller, phone, ct) ? Results.NoContent() : Results.NotFound();
});

app.MapGet("/api/seller-v2/missing-results", async (
    HttpContext context,
    int? take,
    SellerWorkspaceAccessService access,
    SellerWorkspaceRepository workspace,
    CancellationToken ct) =>
{
    var seller = await access.AuthenticateAsync(context, ct);
    if (seller is null) return Results.Unauthorized();
    return Results.Ok(await workspace.GetMissingResultsAsync(seller, take ?? 30, ct));
}).CacheOutput(policy => policy.NoCache());

app.MapPost("/api/seller-v2/interactions", async (
    HttpContext context,
    SellerInteractionRequest request,
    SellerWorkspaceAccessService access,
    SellerWorkspaceRepository workspace,
    CustomerJourneyRepository journey,
    CancellationToken ct) =>
{
    var seller = await access.AuthenticateAsync(context, ct);
    if (seller is null) return Results.Unauthorized();
    try
    {
        var result = await workspace.SaveInteractionAsync(seller, request, ct);
        await journey.CaptureInteractionBestEffortAsync(seller, result.Id, ct);
        return Results.Ok(result);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/seller-v2/interactions/{id:long}", async (
    long id,
    HttpContext context,
    SellerWorkspaceAccessService access,
    SellerWorkspaceRepository workspace,
    CancellationToken ct) =>
{
    var seller = await access.AuthenticateAsync(context, ct);
    if (seller is null) return Results.Unauthorized();
    var result = await workspace.GetInteractionAsync(seller, id, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
}).CacheOutput(policy => policy.NoCache());

app.MapPut("/api/seller-v2/interactions/{id:long}", async (
    long id,
    HttpContext context,
    SellerInteractionRequest request,
    SellerWorkspaceAccessService access,
    SellerWorkspaceRepository workspace,
    CancellationToken ct) =>
{
    var seller = await access.AuthenticateAsync(context, ct);
    if (seller is null) return Results.Unauthorized();
    try
    {
        var result = await workspace.UpdateInteractionAsync(seller, id, request, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/seller-v2/follow-ups/{id:long}/complete", async (
    HttpContext context,
    long id,
    SellerFollowUpCompleteRequest request,
    SellerWorkspaceAccessService access,
    SellerWorkspaceRepository workspace,
    CancellationToken ct) =>
{
    var seller = await access.AuthenticateAsync(context, ct);
    if (seller is null) return Results.Unauthorized();
    if (!Guid.TryParse(request.IdempotencyKey, out var key))
        return Results.BadRequest(new { error = "IdempotencyKey is invalid." });
    return await workspace.CompleteFollowUpAsync(seller, id, key, ct)
        ? Results.Ok(new { id, status = "COMPLETED" })
        : Results.NotFound();
});
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

app.MapGet("/api/dashboard/seller-performance", async (
    DateTime? startDate, DateTime? endDate, string? extension,
    DashboardRepository repo, SellerWorkspaceRepository workspace, CancellationToken ct) =>
{
    var (start, end) = ResolveRange(startDate, endDate);
    await workspace.EnsureSchemaAsync(ct);
    return Results.Ok(await repo.SellerPerformance(start, end, extension, ct));
});
app.MapGet("/api/dashboard/seller-activity/daily", async (
    DateTime? startDate, DateTime? endDate, DashboardRepository repo, CancellationToken ct) =>
{
    var (start, end) = ResolveRange(startDate, endDate);
    return Results.Ok(await repo.SellerDailyActivity(start, end, ct));
});
app.MapGet("/api/dashboard/seller-activity/raw", async (
    DateTime? startDate, DateTime? endDate, string? sellerKey, int? page, int? pageSize,
    DashboardRepository repo, CancellationToken ct) =>
{
    var (start, end) = ResolveRange(startDate, endDate);
    return Results.Ok(await repo.SellerActivities(start, end, sellerKey, page ?? 1, pageSize ?? 100, ct));
});

app.MapGet("/api/sales/summary", async (
    DateTime? startDate,
    DateTime? endDate,
    SalesDashboardRepository repo,
    CancellationToken ct) =>
{
    var (start, end) = ResolveRange(startDate, endDate);
    return Results.Ok(await repo.Summary(start, end, ct));
});

app.MapGet("/api/sales/by-visitor", async (
    DateTime? startDate,
    DateTime? endDate,
    SalesDashboardRepository repo,
    CancellationToken ct) =>
{
    var (start, end) = ResolveRange(startDate, endDate);
    return Results.Ok(await repo.ByVisitor(start, end, ct));
});

app.MapGet("/api/sales/recent-invoices", async (
    DateTime? startDate,
    DateTime? endDate,
    int? take,
    SalesDashboardRepository repo,
    CancellationToken ct) =>
{
    var (start, end) = ResolveRange(startDate, endDate);
    return Results.Ok(await repo.RecentInvoices(start, end, take ?? 25, ct));
});

app.MapGet("/api/system/health", async (
    SystemHealthService service,
    CancellationToken ct) => Results.Ok(await service.GetAsync(ct)));

app.MapGet("/api/system/schedules", async (
    IntegrationSchedulerRepository repository,
    CancellationToken ct) => Results.Ok(await repository.GetAllAsync(ct)));

app.MapPut("/api/system/schedules/{jobKey}", async (
    string jobKey,
    IntegrationScheduleUpdate update,
    HttpContext context,
    IConfiguration configuration,
    IntegrationSchedulerRepository repository,
    CancellationToken ct) =>
{
    if (!CanWriteInternalData(context, configuration)) return Results.Unauthorized();
    await repository.UpdateAsync(jobKey.Trim().ToUpperInvariant(), update, ct);
    return Results.Ok(await repository.GetAllAsync(ct));
});

app.MapPost("/api/system/schedules/{jobKey}/run", async (
    string jobKey,
    HttpContext context,
    IConfiguration configuration,
    IntegrationSchedulerService scheduler,
    CancellationToken ct) =>
{
    if (!CanWriteInternalData(context, configuration)) return Results.Unauthorized();
    return Results.Ok(new { started = await scheduler.RunAsync(jobKey, true, ct) });
});

app.MapGet("/api/accounting/status", async (
    AccountingSyncService service,
    CancellationToken ct) =>
    Results.Ok(await service.GetStatusAsync(ct)));

app.MapPost("/api/accounting/sync", async (
    int? days,
    HttpContext context,
    IConfiguration configuration,
    AccountingSyncService service,
    InvoiceNotificationRepository notifications,
    CancellationToken ct) =>
{
    if (!CanWriteInternalData(context, configuration)) return Results.Unauthorized();
    var result = await service.SyncAsync(days ?? 30, ct);
    if (result.Status == "SUCCESS") await notifications.DiscoverAsync(ct);
    return result.Status == "SUCCESS"
        ? Results.Ok(result)
        : Results.Json(result, statusCode: StatusCodes.Status500InternalServerError);
});

app.MapGet("/api/invoice-notifications", async (
    string? status,
    int? take,
    HttpContext context,
    IConfiguration configuration,
    InvoiceNotificationRepository repository,
    CancellationToken ct) =>
{
    if (!CanReadInternalData(context, configuration)) return Results.Unauthorized();
    return Results.Ok(await repository.ListAsync(status, take ?? 200, ct));
});

app.MapPost("/api/invoice-notifications/discover", async (
    HttpContext context,
    IConfiguration configuration,
    InvoiceNotificationRepository repository,
    CancellationToken ct) =>
{
    if (!CanWriteInternalData(context, configuration)) return Results.Unauthorized();
    return Results.Ok(await repository.DiscoverAsync(ct));
});

app.MapPost("/api/invoice-notifications/{id:long}/primary-mobile", async (
    long id,
    SetPrimaryMobileRequest request,
    HttpContext context,
    IConfiguration configuration,
    InvoiceNotificationRepository repository,
    CancellationToken ct) =>
{
    if (!CanWriteInternalData(context, configuration)) return Results.Unauthorized();
    try
    {
        var phone = await repository.SetPrimaryMobileAsync(id, request.Phone, request.Actor, ct);
        return Results.Ok(new { phone });
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/invoice-notifications/prepare", async (
    PrepareInvoiceNotificationsRequest request,
    HttpContext context,
    IConfiguration configuration,
    InvoiceNotificationRepository repository,
    CancellationToken ct) =>
{
    if (!CanWriteInternalData(context, configuration)) return Results.Unauthorized();
    try
    {
        return Results.Ok(await repository.PrepareAsync(request.NotificationIds, request.Actor, ct));
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/invoice-notifications/{id:long}/manual-sent", async (
    long id,
    MarkManualSentRequest request,
    HttpContext context,
    IConfiguration configuration,
    InvoiceNotificationRepository repository,
    CancellationToken ct) =>
{
    if (!CanWriteInternalData(context, configuration)) return Results.Unauthorized();
    try
    {
        await repository.MarkManualSentAsync(id, request.Actor, request.Note, ct);
        return Results.Ok(new { status = "MANUALLY_SENT" });
    }
    catch (SqlException ex) when (ex.Number == 51000)
    {
        return Results.BadRequest(new { error = "ابتدا باید متن و لینک پیامک آماده شود." });
    }
});

// Restricted operator workflow for trusted private-LAN clients. Management
// actions remain protected by the management token.
app.MapPost("/api/sms-operator/prepare", async (
    PrepareInvoiceNotificationsRequest request,
    HttpContext context,
    IConfiguration configuration,
    InvoiceNotificationRepository repository,
    CancellationToken ct) =>
{
    if (!CanReadInternalData(context, configuration)) return Results.Unauthorized();
    try
    {
        return Results.Ok(await repository.PrepareAsync(request.NotificationIds, request.Actor, ct));
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/sms-operator/{id:long}/manual-sent", async (
    long id,
    MarkManualSentRequest request,
    HttpContext context,
    IConfiguration configuration,
    InvoiceNotificationRepository repository,
    CancellationToken ct) =>
{
    if (!CanReadInternalData(context, configuration)) return Results.Unauthorized();
    try
    {
        await repository.MarkManualSentAsync(id, request.Actor, request.Note, ct);
        return Results.Ok(new { status = "MANUALLY_SENT" });
    }
    catch (SqlException ex) when (ex.Number == 51000)
    {
        return Results.BadRequest(new { error = "این حواله در وضعیت قابل ارسال نیست." });
    }
});

app.MapGet("/api/public/orders/{token}", async (
    string token,
    InvoiceNotificationRepository repository,
    CancellationToken ct) =>
{
    var order = await repository.FindPublicOrderAsync(token, ct);
    return order is null ? Results.NotFound(new { error = "لینک معتبر نیست یا منقضی شده است." }) : Results.Ok(order);
}).RequireRateLimiting("public-order");

app.MapGet("/api/data-gathering/status", async (
    DataGatheringCoordinator coordinator,
    CancellationToken ct) =>
    Results.Ok(await coordinator.GetStatusAsync(ct)));

app.MapGet("/api/identities/status", async (
    CustomerIdentityReconcileService identities,
    CancellationToken ct) =>
    Results.Ok(await identities.GetStatusAsync(ct)));

app.MapPost("/api/identities/reconcile", async (
    HttpContext context,
    IConfiguration configuration,
    CustomerIdentityReconcileService identities,
    CancellationToken ct) =>
{
    if (!CanWriteInternalData(context,configuration)) return Results.Unauthorized();
    return Results.Ok(await identities.ReconcileAsync(ct));
});

app.MapGet("/api/didar/{didarContactCode}/phones", async (
    string didarContactCode,
    DidarPhoneRebuildService phones,
    CancellationToken ct) =>
    Results.Ok(await phones.GetContactPhonesAsync(didarContactCode,ct)));

app.MapPost("/api/data-gathering/run", async (
    HttpContext context,
    IConfiguration configuration,
    DataGatheringCoordinator coordinator,
    CancellationToken ct) =>
{
    if (!CanWriteInternalData(context, configuration)) return Results.Unauthorized();
    var result = await coordinator.RunAsync(ct);
    return result.Status == "FAILED"
        ? Results.Json(result, statusCode: StatusCodes.Status500InternalServerError)
        : Results.Ok(result);
});

app.MapGet("/api/mappings/summary", async (
    CustomerMappingService service,
    CancellationToken ct) =>
    Results.Ok(await service.GetSummaryAsync(ct)));

app.MapGet("/api/mappings/unmapped", async (
    int? take,
    CustomerMappingService service,
    CancellationToken ct) =>
    Results.Ok(await service.GetUnmappedAsync(take ?? 500, ct)));

app.MapPost("/api/mappings/import", async (
    HttpContext context,
    HttpRequest request,
    IConfiguration configuration,
    CustomerMappingService service,
    CancellationToken ct) =>
{
    if (!CanWriteInternalData(context, configuration)) return Results.Unauthorized();
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Send mappingfile.xlsx as multipart/form-data field 'file'." });
    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "Excel file is required." });
    if (!string.Equals(Path.GetExtension(file.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Only .xlsx files are supported." });
    await using var input = file.OpenReadStream();
    return Results.Ok(await service.ImportExcelAsync(input, Path.GetFileName(file.FileName), ct));
}).DisableAntiforgery();

app.MapGet("/api/seller-v2/accounting-mapping/pending", async (
    int? take,
    HttpContext context,
    SellerWorkspaceAccessService access,
    CustomerMappingService service,
    CancellationToken ct) =>
{
    var seller = await access.AuthenticateAsync(context, ct);
    if (seller is null) return Results.Unauthorized();
    if (!seller.Extensions.Any(x => x is "201" or "202" or "215" or "216")) return Results.Forbid();
    return Results.Ok(await service.GetPendingInvoiceMappingsAsync(take ?? 200, ct));
});

app.MapPost("/api/seller-v2/accounting-mapping/link", async (
    ManualAccountingMappingRequest request,
    HttpContext context,
    SellerWorkspaceAccessService access,
    CustomerMappingService service,
    CancellationToken ct) =>
{
    var seller = await access.AuthenticateAsync(context, ct);
    if (seller is null) return Results.Unauthorized();
    if (!seller.Extensions.Any(x => x is "201" or "202" or "215" or "216")) return Results.Forbid();
    try { return Results.Ok(await service.LinkManuallyAsync(request.AccountingCode, request.Phone, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/ai/status", async (
    HttpContext context,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    AiAnalysisRepository repository,
    IOptionsMonitor<RecordingIngestionOptions> recordingOptions,
    CancellationToken ct) =>
{
    if (!CanReadInternalData(context, configuration)) return Results.Unauthorized();
    var ingestion = recordingOptions.CurrentValue;
    return Results.Ok(new
    {
        installed = await repository.IsInstalledAsync(ct),
        analyzerVersion = AiTranscriptAnalyzer.Version,
        recordingIngestion = new
        {
            enabled = ingestion.Enabled,
            configurationFilePresent = File.Exists(Path.Combine(environment.ContentRootPath, "appsettings.RecordingIngestion.local.json")),
            scope = "TODAY_ONLY",
            ingestion.SourceName,
            ingestion.RemoteRoot,
            credentialsConfigured =
                !string.IsNullOrWhiteSpace(ingestion.Username) &&
                File.Exists(ingestion.PrivateKeyPath) &&
                File.Exists(ingestion.KnownHostsPath)
        }
    });
});

app.MapPost("/api/ai/runs/{runId:long}/analyze", async (
    long runId,
    AiAnalyzeRunRequest request,
    HttpContext context,
    IConfiguration configuration,
    AiTranscriptAnalyzer analyzer,
    AiAnalysisRepository repository,
    CancellationToken ct) =>
{
    if (!CanWriteInternalData(context, configuration)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.TranscriptText) &&
        !string.Equals(request.AudioClassHint, "NON_SPEECH_OR_UNSUPPORTED", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "TranscriptText is required unless the audio is explicitly non-speech." });
    if (request.TranscriptText?.Length > 2_000_000)
        return Results.BadRequest(new { error = "TranscriptText exceeds the 2,000,000 character limit." });
    if (!string.IsNullOrWhiteSpace(request.SegmentsJson))
    {
        try { using var _ = JsonDocument.Parse(request.SegmentsJson); }
        catch (JsonException) { return Results.BadRequest(new { error = "SegmentsJson is not valid JSON." }); }
    }
    try
    {
        var result = analyzer.Analyze(request);
        await repository.SaveAnalysisAsync(runId, request, result, ct);
        return Results.Ok(result);
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

app.MapGet("/api/ai/calls", async (
    string? search,
    string? audioClass,
    string? reviewStatus,
    int? page,
    int? pageSize,
    HttpContext context,
    IConfiguration configuration,
    AiAnalysisRepository repository,
    CancellationToken ct) =>
{
    if (!CanReadInternalData(context, configuration)) return Results.Unauthorized();
    if (!await repository.IsInstalledAsync(ct)) return Results.Ok(Array.Empty<AiCallListItem>());
    return Results.Ok(await repository.ListCallsAsync(search, audioClass, reviewStatus, page ?? 1, pageSize ?? 50, ct));
});

app.MapGet("/api/ai/calls/{logicalCallId:long}", async (
    long logicalCallId,
    HttpContext context,
    IConfiguration configuration,
    AiAnalysisRepository repository,
    CancellationToken ct) =>
{
    if (!CanReadInternalData(context, configuration)) return Results.Unauthorized();
    if (!await repository.IsInstalledAsync(ct))
        return Results.NotFound(new { error = "AI analysis module is not installed." });
    var result = await repository.GetCallAsync(logicalCallId, ct);
    return result is null ? Results.NotFound(new { error = "AI call was not found." }) : Results.Ok(result);
});

app.MapGet("/api/ai/reviews", async (
    string? status,
    int? take,
    HttpContext context,
    IConfiguration configuration,
    AiAnalysisRepository repository,
    CancellationToken ct) =>
{
    if (!CanReadInternalData(context, configuration)) return Results.Unauthorized();
    if (!await repository.IsInstalledAsync(ct)) return Results.Ok(Array.Empty<AiReviewView>());
    return Results.Ok(await repository.ListReviewsAsync(status, take ?? 200, ct));
});

app.MapPost("/api/ai/reviews/{reviewItemId:long}/resolve", async (
    long reviewItemId,
    AiReviewResolutionRequest request,
    HttpContext context,
    IConfiguration configuration,
    AiAnalysisRepository repository,
    CancellationToken ct) =>
{
    if (!CanWriteInternalData(context, configuration)) return Results.Unauthorized();
    try
    {
        var updated = await repository.ResolveReviewAsync(reviewItemId, request, ct);
        return updated ? Results.Ok(new { updated = true }) : Results.NotFound(new { error = "Review item was not found." });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
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

static bool CanWriteInternalData(HttpContext context, IConfiguration configuration)
{
    var remote = context.Connection.RemoteIpAddress;
    if (remote is not null && System.Net.IPAddress.IsLoopback(remote)) return true;
    var expected = configuration["Receiver:ApiToken"];
    var supplied = context.Request.Headers["X-Api-Token"].FirstOrDefault();
    return !string.IsNullOrWhiteSpace(expected)
        && !string.IsNullOrWhiteSpace(supplied)
        && TokenComparer.FixedTimeEquals(expected, supplied);
}

static AgentCustomerCard CardFromIncoming(AgentIncomingEventRow row)
    => new(
        row.Extension,
        row.CallerNumber,
        TehranClock.AsUtc(row.EventTimeUtc),
        row.LinkedId,
        row.CustomerName,
        row.CompanyName,
        row.OwnerName,
        null,
        row.IsKnownCustomer,
        null,
        0,
        null,
        null,
        row.LastInvoiceDate,
        row.LastInvoiceAmount,
        row.LastProduct,
        0,
        row.Sales30Days,
        row.CustomerRank,
        row.Temperature,
        row.IsKnownCustomer ? "تماس ورودی مشتری ثبت‌شده" : "تماس ورودی مشتری جدید",
        null,
        "PERSISTED_AGENT_EVENT",
        "بازیابی‌شده از آخرین تماس ثبت‌شده",
        null,
        null);

static bool CanReadInternalData(HttpContext context, IConfiguration configuration)
{
    if (CanWriteInternalData(context, configuration)) return true;
    var remote = context.Connection.RemoteIpAddress;
    if (remote is null) return false;
    if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
    var bytes = remote.GetAddressBytes();
    if (bytes.Length != 4) return false;
    return bytes[0] == 10
           || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
           || (bytes[0] == 192 && bytes[1] == 168);
}

static bool RequiresDashboardAuthentication(PathString path)
    => path.StartsWithSegments("/dashboard")
       || path.StartsWithSegments("/ai")
       || path.StartsWithSegments("/seller-admin")
       || path.StartsWithSegments("/seller-activity")
       || path.StartsWithSegments("/journey-control")
       || path.StartsWithSegments("/api/dashboard")
       || path.StartsWithSegments("/api/ai")
       || path.StartsWithSegments("/api/seller-admin")
       || path.StartsWithSegments("/api/journey-control")
       || path.StartsWithSegments("/api/sales");

static IResult SellerAdminError(Exception exception)
{
    var code = exception.Message;
    var message = code switch
    {
        "USERNAME_INVALID" => "نام کاربری باید ۳ تا ۸۰ کاراکتر و بدون فاصله باشد.",
        "SELLER_KEY_INVALID" => "شناسه فروشنده باید لاتین و بدون فاصله باشد.",
        "DISPLAY_NAME_INVALID" => "نام فروشنده معتبر نیست.",
        "PASSWORD_REQUIRED" => "برای کاربر جدید رمز عبور لازم است.",
        "PASSWORD_INVALID" => "رمز عبور باید حداقل ۸ کاراکتر باشد.",
        "EXTENSION_REQUIRED" => "حداقل یک داخلی معتبر لازم است.",
        "USERNAME_EXISTS" => "این نام کاربری قبلاً استفاده شده است.",
        "SELLER_KEY_EXISTS" => "این شناسه فروشنده قبلاً استفاده شده است.",
        _ => "اطلاعات کاربر فروش معتبر نیست."
    };
    return code is "USERNAME_EXISTS" or "SELLER_KEY_EXISTS"
        ? Results.Conflict(new { error = message, code })
        : Results.BadRequest(new { error = message, code });
}

static string? DashboardPasswordHash(IConfiguration configuration)
{
    var configuredHash = configuration["DashboardAccess:PasswordHash"]?.Trim();
    if (!string.IsNullOrWhiteSpace(configuredHash)
        && !configuredHash.StartsWith("CHANGE_", StringComparison.OrdinalIgnoreCase))
        return configuredHash.ToUpperInvariant();
    var fallback = configuration["Receiver:ApiToken"];
    return string.IsNullOrWhiteSpace(fallback) ? null : HashDashboardSecret(fallback);
}

static string DashboardCookieValue(IConfiguration configuration)
{
    var passwordHash = DashboardPasswordHash(configuration);
    return string.IsNullOrWhiteSpace(passwordHash)
        ? string.Empty
        : HashDashboardSecret($"DigiAhan-Dashboard-Session|{passwordHash}");
}

static string HashDashboardSecret(string value)
    => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));


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

try
{
    using var startupScope = app.Services.CreateScope();
    var notifications = startupScope.ServiceProvider.GetRequiredService<InvoiceNotificationRepository>();
    await notifications.EnsureSchemaAsync(CancellationToken.None);
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Invoice notification schema startup check failed. The application will continue and retry on request.");
}

if (app.Services.GetRequiredService<IOptions<CustomerJourneyOptions>>().Value.Enabled)
{
    try
    {
        using var startupScope = app.Services.CreateScope();
        await startupScope.ServiceProvider.GetRequiredService<SellerWorkspaceRepository>()
            .EnsureSchemaAsync(CancellationToken.None);
        await startupScope.ServiceProvider.GetRequiredService<CustomerJourneyRepository>()
            .EnsureSchemaAsync(CancellationToken.None);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex,
            "Journey Kernel schema startup check failed. Seller v2 remains available and Journey v3 will retry on request.");
    }
}

app.Run();

