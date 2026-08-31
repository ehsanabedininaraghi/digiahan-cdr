using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigiAhan.CDR.Receiver.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class CustomerJourneyRepository
{
    private readonly string _connectionString;
    private readonly SqlQueryStore _queries;
    private readonly IOptionsMonitor<CustomerJourneyOptions> _options;
    private readonly ILogger<CustomerJourneyRepository> _logger;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    public CustomerJourneyRepository(
        IConfiguration configuration,
        SqlQueryStore queries,
        IOptionsMonitor<CustomerJourneyOptions> options,
        ILogger<CustomerJourneyRepository> logger)
    {
        _connectionString = configuration.GetConnectionString("DigiAhanCdr")
            ?? throw new InvalidOperationException("ConnectionStrings:DigiAhanCdr is missing.");
        _queries = queries;
        _options = options;
        _logger = logger;
    }

    public bool IsEnabledFor(SellerIdentity seller)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled) return false;
        var pilots = options.PilotSellerKeys ?? Array.Empty<string>();
        return pilots.Length == 0 || pilots.Contains(seller.Key, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsEnabled => _options.CurrentValue.Enabled;

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_schemaReady) return;
        await _schemaGate.WaitAsync(ct);
        try
        {
            if (_schemaReady) return;
            await using var connection = await OpenAsync(ct);
            await using var command = new SqlCommand(_queries.Get("CustomerJourneyKernelV440.sql"), connection)
            { CommandTimeout = 180 };
            await command.ExecuteNonQueryAsync(ct);
            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    public async Task<JourneyWorkspaceResponse> GetWorkspaceAsync(
        SellerIdentity seller, int take, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        take = Math.Clamp(take, 10, 100);
        var statsTask = GetStatsAsync(seller.Key, ct);
        var workTask = GetWorkItemsAsync(seller.Key, take, ct);
        var leadsTask = GetLeadsAsync(seller.Key, Math.Min(take, 50), ct);
        var opportunitiesTask = GetOpportunitiesAsync(seller.Key, Math.Min(take, 50), ct);
        await Task.WhenAll(statsTask, workTask, leadsTask, opportunitiesTask);
        return new JourneyWorkspaceResponse(
            true,
            new SellerSessionResponse(seller.Key, seller.DisplayName, seller.Extensions, seller.ProductGroups),
            await statsTask,
            await workTask,
            await leadsTask,
            await opportunitiesTask,
            DateTime.UtcNow);
    }

    public async Task<JourneyLeadCreatedResponse> CreateLeadAsync(
        SellerIdentity seller, JourneyCreateLeadRequest request, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        var key = CustomerJourneyRules.RequireIdempotencyKey(request.IdempotencyKey);
        if (request.IdentityId <= 0) throw new ArgumentException("IDENTITY_REQUIRED");
        var title = CustomerJourneyRules.RequireText(request.Title, 300, "LEAD_TITLE_INVALID");
        var action = CustomerJourneyRules.RequireText(request.NextActionType, 60, "NEXT_ACTION_INVALID").ToUpperInvariant();
        var now = DateTime.UtcNow;
        var nextAt = CustomerJourneyRules.RequireFutureUtc(request.NextActionAtUtc, now, "NEXT_ACTION_TIME_INVALID");
        var priority = CustomerJourneyRules.NormalizePriority(request.Priority);
        var slaAt = now.AddMinutes(Math.Clamp(_options.CurrentValue.DefaultLeadSlaMinutes, 5, 10_080));

        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var existing = await FindLeadByKeyAsync(connection, transaction, key, ct);
            if (existing is not null)
            {
                await transaction.CommitAsync(ct);
                return existing with { AlreadyExisted = true };
            }

            await RequireActiveIdentityAsync(connection, transaction, request.IdentityId, ct);
            const string insertLead = """
                INSERT dbo.JourneyLeads
                  (IdempotencyKey,IdentityId,SourceSystem,OwnerSellerKey,Title,Status,Priority,
                   NextActionType,NextActionAtUtc,SlaDueAtUtc,ProductSummary,Note)
                OUTPUT inserted.LeadId,inserted.CreatedAtUtc
                VALUES(@key,@identity,N'SELLER_V3',@owner,@title,N'OPEN',@priority,
                       @action,@next,@sla,@product,@note);
                """;
            long leadId;
            DateTime created;
            await using (var command = new SqlCommand(insertLead, connection, transaction))
            {
                Add(command, "@key", SqlDbType.UniqueIdentifier, key);
                Add(command, "@identity", SqlDbType.BigInt, request.IdentityId);
                Add(command, "@owner", SqlDbType.NVarChar, seller.Key, 80);
                Add(command, "@title", SqlDbType.NVarChar, title, 300);
                Add(command, "@priority", SqlDbType.TinyInt, priority);
                Add(command, "@action", SqlDbType.NVarChar, action, 60);
                Add(command, "@next", SqlDbType.DateTime2, nextAt);
                Add(command, "@sla", SqlDbType.DateTime2, slaAt);
                Add(command, "@product", SqlDbType.NVarChar, CustomerJourneyRules.Clean(request.ProductSummary, 500), 500);
                Add(command, "@note", SqlDbType.NVarChar, CustomerJourneyRules.Clean(request.Note, 1500), 1500);
                await using var reader = await command.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                leadId = reader.GetInt64(0);
                created = reader.GetDateTime(1);
            }

            var workId = await InsertWorkItemAsync(
                connection, transaction, key, request.IdentityId, leadId, null, null, seller.Key,
                "LEAD_NEXT_ACTION", title, request.Note, priority, nextAt, slaAt, ct);
            await InsertEventAsync(connection, transaction, DeterministicGuid(key, "lead-created"),
                request.IdentityId, "LEAD", leadId, "LEAD_CREATED", "SELLER_V3", null,
                seller.Key, key, now, new { title, action, nextAt, priority }, ct);
            await transaction.CommitAsync(ct);
            return new JourneyLeadCreatedResponse(leadId, workId, false, TehranClock.AsUtc(created));
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<JourneyOpportunityCreatedResponse> QualifyLeadAsync(
        SellerIdentity seller, long leadId, JourneyQualifyLeadRequest request, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        var key = CustomerJourneyRules.RequireIdempotencyKey(request.IdempotencyKey);
        var title = CustomerJourneyRules.RequireText(request.Title, 300, "OPPORTUNITY_TITLE_INVALID");
        var action = CustomerJourneyRules.RequireText(request.NextActionType, 60, "NEXT_ACTION_INVALID").ToUpperInvariant();
        var now = DateTime.UtcNow;
        var nextAt = CustomerJourneyRules.RequireFutureUtc(request.NextActionAtUtc, now, "NEXT_ACTION_TIME_INVALID");
        if (request.EstimatedAmount < 0 || request.Quantity < 0) throw new ArgumentException("OPPORTUNITY_VALUE_INVALID");
        var slaAt = now.AddMinutes(Math.Clamp(_options.CurrentValue.DefaultFollowUpMinutes, 5, 43_200));

        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var existing = await FindOpportunityByKeyAsync(connection, transaction, key, ct);
            if (existing is not null)
            {
                await transaction.CommitAsync(ct);
                return existing with { AlreadyExisted = true };
            }

            long identityId;
            byte priority;
            const string leadSql = """
                SELECT IdentityId,Priority,Status FROM dbo.JourneyLeads WITH(UPDLOCK,HOLDLOCK)
                WHERE LeadId=@lead AND OwnerSellerKey=@owner;
                """;
            await using (var command = new SqlCommand(leadSql, connection, transaction))
            {
                Add(command, "@lead", SqlDbType.BigInt, leadId);
                Add(command, "@owner", SqlDbType.NVarChar, seller.Key, 80);
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("LEAD_NOT_FOUND");
                identityId = reader.GetInt64(0);
                priority = reader.GetByte(1);
                if (reader.GetString(2) != "OPEN")
                    throw new InvalidOperationException("LEAD_NOT_OPEN");
            }

            await CancelOpenWorkItemsAsync(connection, transaction, seller.Key, leadId, null, "QUALIFIED", ct);
            const string insertOpportunity = """
                INSERT dbo.JourneyOpportunities
                  (IdempotencyKey,IdentityId,LeadId,OwnerSellerKey,Title,Stage,NextActionType,
                   NextActionAtUtc,SlaDueAtUtc,ExpectedCloseAtUtc,EstimatedAmount,ProductSummary,Note)
                OUTPUT inserted.OpportunityId,inserted.CreatedAtUtc
                VALUES(@key,@identity,@lead,@owner,@title,N'DISCOVERY',@action,
                       @next,@sla,@expected,@amount,@product,@note);
                """;
            long opportunityId;
            DateTime created;
            await using (var command = new SqlCommand(insertOpportunity, connection, transaction))
            {
                Add(command, "@key", SqlDbType.UniqueIdentifier, key);
                Add(command, "@identity", SqlDbType.BigInt, identityId);
                Add(command, "@lead", SqlDbType.BigInt, leadId);
                Add(command, "@owner", SqlDbType.NVarChar, seller.Key, 80);
                Add(command, "@title", SqlDbType.NVarChar, title, 300);
                Add(command, "@action", SqlDbType.NVarChar, action, 60);
                Add(command, "@next", SqlDbType.DateTime2, nextAt);
                Add(command, "@sla", SqlDbType.DateTime2, slaAt);
                Add(command, "@expected", SqlDbType.DateTime2, request.ExpectedCloseAtUtc?.ToUniversalTime());
                AddDecimal(command, "@amount", request.EstimatedAmount, 19, 4);
                Add(command, "@product", SqlDbType.NVarChar, CustomerJourneyRules.Clean(request.ProductSummary, 500), 500);
                Add(command, "@note", SqlDbType.NVarChar, CustomerJourneyRules.Clean(request.Note, 1500), 1500);
                await using var reader = await command.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                opportunityId = reader.GetInt64(0);
                created = reader.GetDateTime(1);
            }

            if (!string.IsNullOrWhiteSpace(request.ProductSummary))
            {
                const string productSql = """
                    INSERT dbo.JourneyOpportunityProducts(OpportunityId,ProductName,Quantity,QuantityUnit)
                    VALUES(@opportunity,@name,@quantity,@unit);
                    """;
                await using var product = new SqlCommand(productSql, connection, transaction);
                Add(product, "@opportunity", SqlDbType.BigInt, opportunityId);
                Add(product, "@name", SqlDbType.NVarChar, CustomerJourneyRules.Clean(request.ProductSummary, 200), 200);
                AddDecimal(product, "@quantity", request.Quantity, 18, 3);
                Add(product, "@unit", SqlDbType.NVarChar, CustomerJourneyRules.Clean(request.QuantityUnit, 30), 30);
                await product.ExecuteNonQueryAsync(ct);
            }

            await using (var updateLead = new SqlCommand(
                "UPDATE dbo.JourneyLeads SET Status=N'QUALIFIED',NextActionType=@action,NextActionAtUtc=@next,SlaDueAtUtc=@sla,UpdatedAtUtc=SYSUTCDATETIME() WHERE LeadId=@lead;",
                connection, transaction))
            {
                Add(updateLead, "@lead", SqlDbType.BigInt, leadId);
                Add(updateLead, "@action", SqlDbType.NVarChar, action, 60);
                Add(updateLead, "@next", SqlDbType.DateTime2, nextAt);
                Add(updateLead, "@sla", SqlDbType.DateTime2, slaAt);
                await updateLead.ExecuteNonQueryAsync(ct);
            }

            await InsertStageHistoryAsync(connection, transaction, opportunityId, null, "DISCOVERY", seller.Key,
                request.Note, DeterministicGuid(key, "initial-stage"), ct);
            var workId = await InsertWorkItemAsync(connection, transaction, key, identityId, leadId, opportunityId,
                null, seller.Key, "OPPORTUNITY_NEXT_ACTION", title, request.Note, priority, nextAt, slaAt, ct);
            await InsertEventAsync(connection, transaction, DeterministicGuid(key, "opportunity-created"),
                identityId, "OPPORTUNITY", opportunityId, "OPPORTUNITY_CREATED", "SELLER_V3", null,
                seller.Key, key, now, new { leadId, title, stage = "DISCOVERY", action, nextAt }, ct);
            await transaction.CommitAsync(ct);
            return new JourneyOpportunityCreatedResponse(opportunityId, workId, false, TehranClock.AsUtc(created));
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<JourneyMutationResponse> TransitionOpportunityAsync(
        SellerIdentity seller, long opportunityId, JourneyTransitionOpportunityRequest request, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        var key = CustomerJourneyRules.RequireIdempotencyKey(request.IdempotencyKey);
        var stage = CustomerJourneyRules.RequireStage(request.Stage);
        var closed = CustomerJourneyRules.IsClosedStage(stage);
        var now = DateTime.UtcNow;
        var action = closed
            ? "CLOSED"
            : CustomerJourneyRules.RequireText(request.NextActionType, 60, "NEXT_ACTION_INVALID").ToUpperInvariant();
        var nextAt = closed
            ? now
            : CustomerJourneyRules.RequireFutureUtc(request.NextActionAtUtc ?? default, now, "NEXT_ACTION_TIME_INVALID");
        var lostReason = CustomerJourneyRules.Clean(request.LostReason, 300);
        if (stage == "LOST" && lostReason is null) throw new ArgumentException("LOST_REASON_REQUIRED");
        if (stage != "LOST") lostReason = null;
        var slaAt = closed ? now : now.AddMinutes(Math.Clamp(_options.CurrentValue.DefaultFollowUpMinutes, 5, 43_200));

        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var historyKey = DeterministicGuid(key, "stage-transition");
            if (await EventExistsAsync(connection, transaction, DeterministicGuid(key, "opportunity-transitioned"), ct))
            {
                await transaction.CommitAsync(ct);
                return new JourneyMutationResponse(opportunityId, stage, now);
            }

            long identityId;
            long? leadId;
            string fromStage;
            const string select = """
                SELECT IdentityId,LeadId,Stage FROM dbo.JourneyOpportunities WITH(UPDLOCK,HOLDLOCK)
                WHERE OpportunityId=@id AND OwnerSellerKey=@owner;
                """;
            await using (var command = new SqlCommand(select, connection, transaction))
            {
                Add(command, "@id", SqlDbType.BigInt, opportunityId);
                Add(command, "@owner", SqlDbType.NVarChar, seller.Key, 80);
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("OPPORTUNITY_NOT_FOUND");
                identityId = reader.GetInt64(0);
                leadId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                fromStage = reader.GetString(2);
            }

            await CancelOpenWorkItemsAsync(connection, transaction, seller.Key, null, opportunityId,
                closed ? stage : "RESCHEDULED", ct);
            const string update = """
                UPDATE dbo.JourneyOpportunities
                SET Stage=@stage,NextActionType=@action,NextActionAtUtc=@next,SlaDueAtUtc=@sla,
                    WonAtUtc=CASE WHEN @stage=N'WON' THEN @now ELSE NULL END,
                    LostAtUtc=CASE WHEN @stage=N'LOST' THEN @now ELSE NULL END,
                    LostReason=@lost,Note=COALESCE(@note,Note),UpdatedAtUtc=@now
                WHERE OpportunityId=@id;
                """;
            await using (var command = new SqlCommand(update, connection, transaction))
            {
                Add(command, "@id", SqlDbType.BigInt, opportunityId);
                Add(command, "@stage", SqlDbType.NVarChar, stage, 30);
                Add(command, "@action", SqlDbType.NVarChar, action, 60);
                Add(command, "@next", SqlDbType.DateTime2, nextAt);
                Add(command, "@sla", SqlDbType.DateTime2, slaAt);
                Add(command, "@now", SqlDbType.DateTime2, now);
                Add(command, "@lost", SqlDbType.NVarChar, lostReason, 300);
                Add(command, "@note", SqlDbType.NVarChar, CustomerJourneyRules.Clean(request.Note, 1500), 1500);
                await command.ExecuteNonQueryAsync(ct);
            }

            await InsertStageHistoryAsync(connection, transaction, opportunityId, fromStage, stage, seller.Key,
                request.Note, historyKey, ct);
            if (!closed)
            {
                await InsertWorkItemAsync(connection, transaction, key, identityId, leadId, opportunityId, null,
                    seller.Key, "OPPORTUNITY_NEXT_ACTION", action, request.Note, 2, nextAt, slaAt, ct);
            }
            else if (leadId.HasValue)
            {
                await using var lead = new SqlCommand(
                    "UPDATE dbo.JourneyLeads SET Status=@status,ClosedReason=@reason,UpdatedAtUtc=@now WHERE LeadId=@lead;",
                    connection, transaction);
                Add(lead, "@status", SqlDbType.NVarChar, stage == "WON" ? "CONVERTED" : "DISQUALIFIED", 30);
                Add(lead, "@reason", SqlDbType.NVarChar, lostReason, 300);
                Add(lead, "@now", SqlDbType.DateTime2, now);
                Add(lead, "@lead", SqlDbType.BigInt, leadId.Value);
                await lead.ExecuteNonQueryAsync(ct);
            }

            await InsertEventAsync(connection, transaction, DeterministicGuid(key, "opportunity-transitioned"),
                identityId, "OPPORTUNITY", opportunityId, "OPPORTUNITY_STAGE_CHANGED", "SELLER_V3", null,
                seller.Key, key, now, new { fromStage, toStage = stage, action, nextAt, lostReason }, ct);
            await transaction.CommitAsync(ct);
            return new JourneyMutationResponse(opportunityId, stage, now);
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<JourneyMutationResponse> CompleteWorkItemAsync(
        SellerIdentity seller, long workItemId, JourneyCompleteWorkItemRequest request, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        var key = CustomerJourneyRules.RequireIdempotencyKey(request.IdempotencyKey);
        var outcome = CustomerJourneyRules.RequireWorkItemOutcome(request.Outcome);
        var eventKey = DeterministicGuid(key, "work-item-completed");
        var now = DateTime.UtcNow;

        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            if (await EventExistsAsync(connection, transaction, eventKey, ct))
            {
                await transaction.CommitAsync(ct);
                return new JourneyMutationResponse(workItemId, "COMPLETED", now);
            }

            long identityId;
            long? leadId;
            long? opportunityId;
            byte priority;
            const string update = """
                UPDATE dbo.JourneyWorkItems WITH(UPDLOCK,ROWLOCK)
                SET Status=N'COMPLETED',CompletedAtUtc=@now,CompletedBySellerKey=@owner,
                    Outcome=@outcome,CompletionNote=@note,UpdatedAtUtc=@now
                OUTPUT inserted.IdentityId,inserted.LeadId,inserted.OpportunityId,inserted.Priority
                WHERE WorkItemId=@id AND OwnerSellerKey=@owner AND Status IN (N'OPEN',N'IN_PROGRESS');
                """;
            await using (var command = new SqlCommand(update, connection, transaction))
            {
                Add(command, "@id", SqlDbType.BigInt, workItemId);
                Add(command, "@owner", SqlDbType.NVarChar, seller.Key, 80);
                Add(command, "@outcome", SqlDbType.NVarChar, outcome, 40);
                Add(command, "@note", SqlDbType.NVarChar, CustomerJourneyRules.Clean(request.Note, 1000), 1000);
                Add(command, "@now", SqlDbType.DateTime2, now);
                var value = await command.ExecuteScalarAsync(ct);
                if (value is null || value is DBNull) throw new KeyNotFoundException("WORK_ITEM_NOT_FOUND");
                identityId = Convert.ToInt64(value);
            }

            await using (var details = new SqlCommand(
                "SELECT LeadId,OpportunityId,Priority FROM dbo.JourneyWorkItems WHERE WorkItemId=@id;",
                connection, transaction))
            {
                Add(details, "@id", SqlDbType.BigInt, workItemId);
                await using var reader = await details.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                leadId = GetInt64(reader, 0);
                opportunityId = GetInt64(reader, 1);
                priority = reader.GetByte(2);
            }

            if (outcome is "CUSTOMER_DECLINED" or "NOT_RELEVANT")
            {
                if (opportunityId.HasValue)
                {
                    await using var closeOpportunity = new SqlCommand(
                        "UPDATE dbo.JourneyOpportunities SET Stage=N'LOST',NextActionType=N'CLOSED',NextActionAtUtc=@now,SlaDueAtUtc=@now,LostAtUtc=@now,LostReason=@reason,UpdatedAtUtc=@now WHERE OpportunityId=@id AND Stage NOT IN(N'WON',N'LOST');",
                        connection, transaction);
                    Add(closeOpportunity, "@id", SqlDbType.BigInt, opportunityId.Value);
                    Add(closeOpportunity, "@now", SqlDbType.DateTime2, now);
                    Add(closeOpportunity, "@reason", SqlDbType.NVarChar,
                        outcome == "CUSTOMER_DECLINED" ? "CUSTOMER_DECLINED" : "NOT_RELEVANT", 300);
                    await closeOpportunity.ExecuteNonQueryAsync(ct);
                }
                if (leadId.HasValue)
                {
                    await using var closeLead = new SqlCommand(
                        "UPDATE dbo.JourneyLeads SET Status=N'DISQUALIFIED',ClosedReason=@reason,UpdatedAtUtc=@now WHERE LeadId=@id AND Status IN(N'OPEN',N'QUALIFIED');",
                        connection, transaction);
                    Add(closeLead, "@id", SqlDbType.BigInt, leadId.Value);
                    Add(closeLead, "@now", SqlDbType.DateTime2, now);
                    Add(closeLead, "@reason", SqlDbType.NVarChar,
                        outcome == "CUSTOMER_DECLINED" ? "CUSTOMER_DECLINED" : "NOT_RELEVANT", 300);
                    await closeLead.ExecuteNonQueryAsync(ct);
                }
            }
            else if (leadId.HasValue || opportunityId.HasValue)
            {
                var successorAt = now.AddMinutes(Math.Clamp(_options.CurrentValue.DefaultFollowUpMinutes, 5, 43_200));
                await InsertWorkItemAsync(connection, transaction, DeterministicGuid(key, "successor-work"),
                    identityId, leadId, opportunityId, null, seller.Key, "NEXT_ACTION_REVIEW",
                    outcome == "NO_ANSWER" ? "تماس مجدد پس از عدم پاسخ" : "تعیین اقدام بعدی مشتری",
                    request.Note, priority, successorAt, successorAt, ct);
            }

            await InsertEventAsync(connection, transaction, eventKey, identityId, "WORK_ITEM", workItemId,
                "WORK_ITEM_COMPLETED", "SELLER_V3", null, seller.Key, key, now,
                new { outcome, note = CustomerJourneyRules.Clean(request.Note, 1000) }, ct);
            await transaction.CommitAsync(ct);
            return new JourneyMutationResponse(workItemId, "COMPLETED", now);
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<JourneyManagerExceptionRow>> GetManagerExceptionsAsync(
        int take, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        const string sql = """
            SELECT TOP(@take) w.WorkItemId,w.IdentityId,
              COALESCE(NULLIF(i.DisplayName,N''),NULLIF(i.CompanyName,N''),N'مشتری بدون نام'),
              phone.NormalizedPhone,w.OwnerSellerKey,w.WorkType,w.Title,w.DueAtUtc,w.SlaDueAtUtc,
              DATEDIFF(minute,w.SlaDueAtUtc,SYSUTCDATETIME()),l.Status,o.Stage
            FROM dbo.JourneyWorkItems w
            INNER JOIN dbo.CustomerIdentities i ON i.IdentityId=w.IdentityId
            LEFT JOIN dbo.JourneyLeads l ON l.LeadId=w.LeadId
            LEFT JOIN dbo.JourneyOpportunities o ON o.OpportunityId=w.OpportunityId
            OUTER APPLY
            (
              SELECT TOP(1) p.NormalizedPhone FROM dbo.CustomerIdentityPhones p
              WHERE p.IdentityId=w.IdentityId ORDER BY p.IsPrimary DESC,p.IsVerified DESC,p.Priority,p.Id
            ) phone
            WHERE w.Status IN (N'OPEN',N'IN_PROGRESS') AND w.SlaDueAtUtc<SYSUTCDATETIME()
            ORDER BY w.SlaDueAtUtc,w.Priority DESC,w.WorkItemId;
            """;
        var rows = new List<JourneyManagerExceptionRow>();
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 20 };
        Add(command, "@take", SqlDbType.Int, Math.Clamp(take, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new JourneyManagerExceptionRow(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), GetString(reader, 3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6),
                TehranClock.AsUtc(reader.GetDateTime(7)), TehranClock.AsUtc(reader.GetDateTime(8)),
                reader.GetInt32(9), GetString(reader, 10), GetString(reader, 11)));
        }
        return rows;
    }

    public async Task<JourneyCaptureResult> CaptureInteractionBestEffortAsync(
        SellerIdentity seller, long interactionId, CancellationToken ct)
    {
        if (!IsEnabledFor(seller) || !_options.CurrentValue.AutoCaptureSellerInteractions)
            return new JourneyCaptureResult(false, null, null, null, "FEATURE_DISABLED");
        try
        {
            return await CaptureInteractionAsync(seller, interactionId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Journey capture failed without affecting Seller v2. Seller={SellerKey} Interaction={InteractionId}",
                seller.Key, interactionId);
            return new JourneyCaptureResult(false, null, null, null, "CAPTURE_FAILED");
        }
    }

    private async Task<JourneyCaptureResult> CaptureInteractionAsync(
        SellerIdentity seller, long interactionId, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            const string select = """
                SELECT i.IdempotencyKey,i.CustomerIdentityId,i.Outcome,i.LossReason,i.Note,i.OccurredAtUtc,
                       product.ProductName,followup.Subject,followup.DueAtUtc
                FROM dbo.SellerInteractions i WITH(UPDLOCK,HOLDLOCK)
                OUTER APPLY(SELECT TOP(1) ProductName FROM dbo.SellerInteractionProducts WHERE InteractionId=i.Id ORDER BY Id) product
                OUTER APPLY(SELECT TOP(1) Subject,DueAtUtc FROM dbo.SellerFollowUps WHERE InteractionId=i.Id AND Status=N'OPEN' ORDER BY Id DESC) followup
                WHERE i.Id=@id AND i.SellerKey=@seller;
                """;
            Guid sourceKey;
            long identityId;
            string outcome;
            string? lossReason;
            string? note;
            DateTime occurred;
            string? product;
            string? followUpSubject;
            DateTime? followUpAt;
            await using (var command = new SqlCommand(select, connection, transaction))
            {
                Add(command, "@id", SqlDbType.BigInt, interactionId);
                Add(command, "@seller", SqlDbType.NVarChar, seller.Key, 80);
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    return new JourneyCaptureResult(false, null, null, null, "INTERACTION_NOT_FOUND");
                }
                if (reader.IsDBNull(1))
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    return new JourneyCaptureResult(false, null, null, null, "IDENTITY_NOT_RESOLVED");
                }
                sourceKey = reader.GetGuid(0);
                identityId = reader.GetInt64(1);
                outcome = reader.GetString(2);
                lossReason = GetString(reader, 3);
                note = GetString(reader, 4);
                occurred = TehranClock.AsUtc(reader.GetDateTime(5));
                product = GetString(reader, 6);
                followUpSubject = GetString(reader, 7);
                followUpAt = reader.IsDBNull(8) ? null : TehranClock.AsUtc(reader.GetDateTime(8));
            }

            if (outcome == "NON_SALES")
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new JourneyCaptureResult(false, null, null, null, "NON_SALES_INTERACTION");
            }

            await using (var existing = new SqlCommand(
                "SELECT TOP(1) LeadId FROM dbo.JourneyLeads WHERE SourceInteractionId=@id;", connection, transaction))
            {
                Add(existing, "@id", SqlDbType.BigInt, interactionId);
                var value = await existing.ExecuteScalarAsync(ct);
                if (value is not null && value is not DBNull)
                {
                    await transaction.CommitAsync(ct);
                    return new JourneyCaptureResult(false, Convert.ToInt64(value), null, null, "ALREADY_CAPTURED");
                }
            }

            var now = DateTime.UtcNow;
            var active = outcome is "FOLLOW_UP" or "DECIDING";
            var leadStatus = outcome switch
            {
                "ORDER" => "CONVERTED",
                "LOST" => "DISQUALIFIED",
                _ => "OPEN"
            };
            var nextAt = followUpAt is { } due && due > now ? due : now.AddMinutes(
                Math.Clamp(_options.CurrentValue.DefaultFollowUpMinutes, 5, 43_200));
            var nextAction = followUpSubject is null ? (active ? "QUALIFY_LEAD" : "CLOSED") : "FOLLOW_UP";
            var title = product is null ? "پیگیری تماس فروش" : $"پیگیری خرید {product}";
            const string insertLead = """
                INSERT dbo.JourneyLeads
                  (IdempotencyKey,IdentityId,SourceSystem,SourceReference,SourceInteractionId,OwnerSellerKey,
                   Title,Status,Priority,NextActionType,NextActionAtUtc,SlaDueAtUtc,ProductSummary,Note,ClosedReason)
                OUTPUT inserted.LeadId
                VALUES(@key,@identity,N'SELLER_V2',@reference,@interaction,@owner,
                       @title,@status,2,@action,@next,@sla,@product,@note,@closed);
                """;
            long leadId;
            await using (var command = new SqlCommand(insertLead, connection, transaction))
            {
                Add(command, "@key", SqlDbType.UniqueIdentifier, sourceKey);
                Add(command, "@identity", SqlDbType.BigInt, identityId);
                Add(command, "@reference", SqlDbType.NVarChar, interactionId.ToString(), 120);
                Add(command, "@interaction", SqlDbType.BigInt, interactionId);
                Add(command, "@owner", SqlDbType.NVarChar, seller.Key, 80);
                Add(command, "@title", SqlDbType.NVarChar, title, 300);
                Add(command, "@status", SqlDbType.NVarChar, leadStatus, 30);
                Add(command, "@action", SqlDbType.NVarChar, nextAction, 60);
                Add(command, "@next", SqlDbType.DateTime2, nextAt);
                Add(command, "@sla", SqlDbType.DateTime2, active ? nextAt : now);
                Add(command, "@product", SqlDbType.NVarChar, product, 500);
                Add(command, "@note", SqlDbType.NVarChar, note, 1500);
                Add(command, "@closed", SqlDbType.NVarChar, outcome == "LOST" ? lossReason : null, 300);
                leadId = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
            }

            long? workItemId = null;
            if (active)
            {
                workItemId = await InsertWorkItemAsync(connection, transaction,
                    DeterministicGuid(sourceKey, "captured-work"), identityId, leadId, null, interactionId,
                    seller.Key, "LEAD_NEXT_ACTION", title, followUpSubject ?? note, 2, nextAt, nextAt, ct);
            }

            long? opportunityId = null;
            if (outcome == "ORDER")
            {
                var opportunityKey = DeterministicGuid(sourceKey, "captured-order");
                const string insertOpportunity = """
                    INSERT dbo.JourneyOpportunities
                      (IdempotencyKey,IdentityId,LeadId,SourceInteractionId,OwnerSellerKey,Title,Stage,
                       NextActionType,NextActionAtUtc,SlaDueAtUtc,ProductSummary,WonAtUtc,Note)
                    OUTPUT inserted.OpportunityId
                    VALUES(@key,@identity,@lead,@interaction,@owner,@title,N'WON',N'CLOSED',@now,@now,@product,@now,@note);
                    """;
                await using var opportunity = new SqlCommand(insertOpportunity, connection, transaction);
                Add(opportunity, "@key", SqlDbType.UniqueIdentifier, opportunityKey);
                Add(opportunity, "@identity", SqlDbType.BigInt, identityId);
                Add(opportunity, "@lead", SqlDbType.BigInt, leadId);
                Add(opportunity, "@interaction", SqlDbType.BigInt, interactionId);
                Add(opportunity, "@owner", SqlDbType.NVarChar, seller.Key, 80);
                Add(opportunity, "@title", SqlDbType.NVarChar, title, 300);
                Add(opportunity, "@now", SqlDbType.DateTime2, now);
                Add(opportunity, "@product", SqlDbType.NVarChar, product, 500);
                Add(opportunity, "@note", SqlDbType.NVarChar, note, 1500);
                opportunityId = Convert.ToInt64(await opportunity.ExecuteScalarAsync(ct));
                await InsertStageHistoryAsync(connection, transaction, opportunityId.Value, null, "WON", seller.Key,
                    "Captured from Seller v2 order", DeterministicGuid(sourceKey, "captured-order-stage"), ct);
            }

            await InsertEventAsync(connection, transaction, DeterministicGuid(sourceKey, "interaction-captured"),
                identityId, "LEAD", leadId, "SELLER_INTERACTION_CAPTURED", "SELLER_V2", interactionId.ToString(),
                seller.Key, sourceKey, occurred, new { interactionId, outcome, product, followUpAt }, ct);
            await transaction.CommitAsync(ct);
            return new JourneyCaptureResult(true, leadId, opportunityId, workItemId, "CAPTURED");
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<JourneyStats> GetStatsAsync(string sellerKey, CancellationToken ct)
    {
        const string sql = """
            DECLARE @today datetime2(0)=DATEADD(day,DATEDIFF(day,0,DATEADD(minute,210,SYSUTCDATETIME())),0);
            DECLARE @tomorrow datetime2(0)=DATEADD(day,1,@today);
            SELECT
              (SELECT COUNT(*) FROM dbo.JourneyLeads WHERE OwnerSellerKey=@seller AND Status=N'OPEN'),
              (SELECT COUNT(*) FROM dbo.JourneyOpportunities WHERE OwnerSellerKey=@seller AND Stage NOT IN(N'WON',N'LOST')),
              (SELECT COUNT(*) FROM dbo.JourneyWorkItems WHERE OwnerSellerKey=@seller AND Status IN(N'OPEN',N'IN_PROGRESS') AND DueAtUtc>=DATEADD(minute,-210,@today) AND DueAtUtc<DATEADD(minute,-210,@tomorrow)),
              (SELECT COUNT(*) FROM dbo.JourneyWorkItems WHERE OwnerSellerKey=@seller AND Status IN(N'OPEN',N'IN_PROGRESS') AND DueAtUtc<SYSUTCDATETIME()),
              (SELECT COUNT(*) FROM dbo.JourneyWorkItems WHERE Status IN(N'OPEN',N'IN_PROGRESS') AND SlaDueAtUtc<SYSUTCDATETIME());
            """;
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 15 };
        Add(command, "@seller", SqlDbType.NVarChar, sellerKey, 80);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new JourneyStats(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));
    }

    private async Task<IReadOnlyList<JourneyWorkItemRow>> GetWorkItemsAsync(
        string sellerKey, int take, CancellationToken ct)
    {
        const string sql = """
            SELECT TOP(@take) w.WorkItemId,w.IdentityId,w.LeadId,w.OpportunityId,w.WorkType,w.Title,
              w.Status,w.Priority,w.DueAtUtc,w.SlaDueAtUtc,
              CONVERT(bit,CASE WHEN w.DueAtUtc<SYSUTCDATETIME() THEN 1 ELSE 0 END),
              COALESCE(NULLIF(i.DisplayName,N''),NULLIF(i.CompanyName,N''),N'مشتری بدون نام'),
              phone.NormalizedPhone,l.Status,o.Stage,w.OwnerSellerKey
            FROM dbo.JourneyWorkItems w
            INNER JOIN dbo.CustomerIdentities i ON i.IdentityId=w.IdentityId
            LEFT JOIN dbo.JourneyLeads l ON l.LeadId=w.LeadId
            LEFT JOIN dbo.JourneyOpportunities o ON o.OpportunityId=w.OpportunityId
            OUTER APPLY
            (
              SELECT TOP(1) p.NormalizedPhone FROM dbo.CustomerIdentityPhones p
              WHERE p.IdentityId=w.IdentityId ORDER BY p.IsPrimary DESC,p.IsVerified DESC,p.Priority,p.Id
            ) phone
            WHERE w.OwnerSellerKey=@seller AND w.Status IN(N'OPEN',N'IN_PROGRESS')
            ORDER BY CASE WHEN w.DueAtUtc<SYSUTCDATETIME() THEN 0 ELSE 1 END,w.DueAtUtc,w.Priority DESC,w.WorkItemId;
            """;
        var rows = new List<JourneyWorkItemRow>();
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 15 };
        Add(command, "@take", SqlDbType.Int, take);
        Add(command, "@seller", SqlDbType.NVarChar, sellerKey, 80);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new JourneyWorkItemRow(
                reader.GetInt64(0), reader.GetInt64(1), GetInt64(reader, 2), GetInt64(reader, 3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetByte(7),
                TehranClock.AsUtc(reader.GetDateTime(8)), TehranClock.AsUtc(reader.GetDateTime(9)), reader.GetBoolean(10),
                reader.GetString(11), GetString(reader, 12), GetString(reader, 13), GetString(reader, 14), reader.GetString(15)));
        }
        return rows;
    }

    private async Task<IReadOnlyList<JourneyLeadRow>> GetLeadsAsync(string sellerKey, int take, CancellationToken ct)
    {
        const string sql = """
            SELECT TOP(@take) l.LeadId,l.IdentityId,
              COALESCE(NULLIF(i.DisplayName,N''),NULLIF(i.CompanyName,N''),N'مشتری بدون نام'),phone.NormalizedPhone,
              l.Title,l.Status,l.Priority,l.OwnerSellerKey,l.NextActionType,l.NextActionAtUtc,l.SlaDueAtUtc,
              l.ProductSummary,l.UpdatedAtUtc
            FROM dbo.JourneyLeads l
            INNER JOIN dbo.CustomerIdentities i ON i.IdentityId=l.IdentityId
            OUTER APPLY(SELECT TOP(1) p.NormalizedPhone FROM dbo.CustomerIdentityPhones p WHERE p.IdentityId=l.IdentityId ORDER BY p.IsPrimary DESC,p.IsVerified DESC,p.Priority,p.Id) phone
            WHERE l.OwnerSellerKey=@seller AND l.Status IN(N'OPEN',N'QUALIFIED')
            ORDER BY l.NextActionAtUtc,l.Priority DESC,l.LeadId;
            """;
        var rows = new List<JourneyLeadRow>();
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 15 };
        Add(command, "@take", SqlDbType.Int, take);
        Add(command, "@seller", SqlDbType.NVarChar, sellerKey, 80);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new JourneyLeadRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), GetString(reader, 3),
                reader.GetString(4), reader.GetString(5), reader.GetByte(6), reader.GetString(7), reader.GetString(8),
                TehranClock.AsUtc(reader.GetDateTime(9)), TehranClock.AsUtc(reader.GetDateTime(10)), GetString(reader, 11),
                TehranClock.AsUtc(reader.GetDateTime(12))));
        return rows;
    }

    private async Task<IReadOnlyList<JourneyOpportunityRow>> GetOpportunitiesAsync(
        string sellerKey, int take, CancellationToken ct)
    {
        const string sql = """
            SELECT TOP(@take) o.OpportunityId,o.IdentityId,o.LeadId,
              COALESCE(NULLIF(i.DisplayName,N''),NULLIF(i.CompanyName,N''),N'مشتری بدون نام'),phone.NormalizedPhone,
              o.Title,o.Stage,o.OwnerSellerKey,o.NextActionType,o.NextActionAtUtc,o.SlaDueAtUtc,
              o.EstimatedAmount,o.ProductSummary,o.UpdatedAtUtc
            FROM dbo.JourneyOpportunities o
            INNER JOIN dbo.CustomerIdentities i ON i.IdentityId=o.IdentityId
            OUTER APPLY(SELECT TOP(1) p.NormalizedPhone FROM dbo.CustomerIdentityPhones p WHERE p.IdentityId=o.IdentityId ORDER BY p.IsPrimary DESC,p.IsVerified DESC,p.Priority,p.Id) phone
            WHERE o.OwnerSellerKey=@seller AND o.Stage NOT IN(N'WON',N'LOST')
            ORDER BY o.NextActionAtUtc,o.OpportunityId;
            """;
        var rows = new List<JourneyOpportunityRow>();
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 15 };
        Add(command, "@take", SqlDbType.Int, take);
        Add(command, "@seller", SqlDbType.NVarChar, sellerKey, 80);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new JourneyOpportunityRow(reader.GetInt64(0), reader.GetInt64(1), GetInt64(reader, 2),
                reader.GetString(3), GetString(reader, 4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetString(8), TehranClock.AsUtc(reader.GetDateTime(9)), TehranClock.AsUtc(reader.GetDateTime(10)),
                GetDecimal(reader, 11), GetString(reader, 12), TehranClock.AsUtc(reader.GetDateTime(13))));
        return rows;
    }

    private static async Task<JourneyLeadCreatedResponse?> FindLeadByKeyAsync(
        SqlConnection connection, SqlTransaction transaction, Guid key, CancellationToken ct)
    {
        const string sql = """
            SELECT l.LeadId,l.CreatedAtUtc,ISNULL((SELECT TOP(1) WorkItemId FROM dbo.JourneyWorkItems WHERE LeadId=l.LeadId ORDER BY WorkItemId),0)
            FROM dbo.JourneyLeads l WITH(UPDLOCK,HOLDLOCK) WHERE l.IdempotencyKey=@key;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        Add(command, "@key", SqlDbType.UniqueIdentifier, key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new JourneyLeadCreatedResponse(reader.GetInt64(0), reader.GetInt64(2), true, TehranClock.AsUtc(reader.GetDateTime(1)))
            : null;
    }

    private static async Task<JourneyOpportunityCreatedResponse?> FindOpportunityByKeyAsync(
        SqlConnection connection, SqlTransaction transaction, Guid key, CancellationToken ct)
    {
        const string sql = """
            SELECT o.OpportunityId,o.CreatedAtUtc,ISNULL((SELECT TOP(1) WorkItemId FROM dbo.JourneyWorkItems WHERE OpportunityId=o.OpportunityId ORDER BY WorkItemId),0)
            FROM dbo.JourneyOpportunities o WITH(UPDLOCK,HOLDLOCK) WHERE o.IdempotencyKey=@key;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        Add(command, "@key", SqlDbType.UniqueIdentifier, key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new JourneyOpportunityCreatedResponse(reader.GetInt64(0), reader.GetInt64(2), true, TehranClock.AsUtc(reader.GetDateTime(1)))
            : null;
    }

    private static async Task RequireActiveIdentityAsync(
        SqlConnection connection, SqlTransaction transaction, long identityId, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.CustomerIdentities WITH(UPDLOCK,HOLDLOCK) WHERE IdentityId=@identity AND ISNULL(IsActive,1)=1;",
            connection, transaction);
        Add(command, "@identity", SqlDbType.BigInt, identityId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == 0)
            throw new KeyNotFoundException("IDENTITY_NOT_FOUND");
    }

    private static async Task<long> InsertWorkItemAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid key,
        long identityId,
        long? leadId,
        long? opportunityId,
        long? sourceInteractionId,
        string owner,
        string workType,
        string title,
        string? description,
        byte priority,
        DateTime dueAt,
        DateTime slaAt,
        CancellationToken ct)
    {
        const string sql = """
            INSERT dbo.JourneyWorkItems
              (IdempotencyKey,IdentityId,LeadId,OpportunityId,SourceInteractionId,OwnerSellerKey,
               WorkType,Title,Description,Priority,DueAtUtc,SlaDueAtUtc)
            OUTPUT inserted.WorkItemId
            VALUES(@key,@identity,@lead,@opportunity,@interaction,@owner,@type,@title,@description,@priority,@due,@sla);
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        Add(command, "@key", SqlDbType.UniqueIdentifier, key);
        Add(command, "@identity", SqlDbType.BigInt, identityId);
        Add(command, "@lead", SqlDbType.BigInt, leadId);
        Add(command, "@opportunity", SqlDbType.BigInt, opportunityId);
        Add(command, "@interaction", SqlDbType.BigInt, sourceInteractionId);
        Add(command, "@owner", SqlDbType.NVarChar, owner, 80);
        Add(command, "@type", SqlDbType.NVarChar, workType, 60);
        Add(command, "@title", SqlDbType.NVarChar, CustomerJourneyRules.Clean(title, 300), 300);
        Add(command, "@description", SqlDbType.NVarChar, CustomerJourneyRules.Clean(description, 1000), 1000);
        Add(command, "@priority", SqlDbType.TinyInt, priority);
        Add(command, "@due", SqlDbType.DateTime2, dueAt);
        Add(command, "@sla", SqlDbType.DateTime2, slaAt);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private static async Task InsertStageHistoryAsync(
        SqlConnection connection, SqlTransaction transaction, long opportunityId, string? fromStage,
        string toStage, string sellerKey, string? reason, Guid key, CancellationToken ct)
    {
        const string sql = """
            INSERT dbo.JourneyOpportunityStageHistory
              (OpportunityId,FromStage,ToStage,ChangedBySellerKey,Reason,IdempotencyKey)
            VALUES(@opportunity,@from,@to,@seller,@reason,@key);
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        Add(command, "@opportunity", SqlDbType.BigInt, opportunityId);
        Add(command, "@from", SqlDbType.NVarChar, fromStage, 30);
        Add(command, "@to", SqlDbType.NVarChar, toStage, 30);
        Add(command, "@seller", SqlDbType.NVarChar, sellerKey, 80);
        Add(command, "@reason", SqlDbType.NVarChar, CustomerJourneyRules.Clean(reason, 500), 500);
        Add(command, "@key", SqlDbType.UniqueIdentifier, key);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertEventAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid eventKey,
        long identityId,
        string aggregateType,
        long? aggregateId,
        string eventType,
        string sourceSystem,
        string? sourceReference,
        string? actorKey,
        Guid correlationId,
        DateTime occurredAtUtc,
        object? payload,
        CancellationToken ct)
    {
        const string sql = """
            INSERT dbo.JourneyEvents
              (EventKey,IdentityId,AggregateType,AggregateId,EventType,SourceSystem,SourceReference,
               ActorType,ActorKey,CorrelationId,OccurredAtUtc,PayloadJson)
            OUTPUT inserted.EventId
            VALUES(@key,@identity,@aggregateType,@aggregateId,@eventType,@source,@reference,
                   N'SELLER',@actor,@correlation,@occurred,@payload);
            """;
        long eventId;
        await using (var command = new SqlCommand(sql, connection, transaction))
        {
            Add(command, "@key", SqlDbType.UniqueIdentifier, eventKey);
            Add(command, "@identity", SqlDbType.BigInt, identityId);
            Add(command, "@aggregateType", SqlDbType.NVarChar, aggregateType, 40);
            Add(command, "@aggregateId", SqlDbType.BigInt, aggregateId);
            Add(command, "@eventType", SqlDbType.NVarChar, eventType, 60);
            Add(command, "@source", SqlDbType.NVarChar, sourceSystem, 30);
            Add(command, "@reference", SqlDbType.NVarChar, sourceReference, 120);
            Add(command, "@actor", SqlDbType.NVarChar, actorKey, 80);
            Add(command, "@correlation", SqlDbType.UniqueIdentifier, correlationId);
            Add(command, "@occurred", SqlDbType.DateTime2, occurredAtUtc);
            Add(command, "@payload", SqlDbType.NVarChar, payload is null ? null : JsonSerializer.Serialize(payload), -1);
            eventId = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        }
        await using var outbox = new SqlCommand(
            "INSERT dbo.JourneyOutbox(EventId,Destination) VALUES(@event,N'JOURNEY_ANALYTICS');",
            connection, transaction);
        Add(outbox, "@event", SqlDbType.BigInt, eventId);
        await outbox.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> EventExistsAsync(
        SqlConnection connection, SqlTransaction transaction, Guid eventKey, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.JourneyEvents WITH(UPDLOCK,HOLDLOCK) WHERE EventKey=@key;",
            connection, transaction);
        Add(command, "@key", SqlDbType.UniqueIdentifier, eventKey);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task CancelOpenWorkItemsAsync(
        SqlConnection connection, SqlTransaction transaction, string sellerKey, long? leadId,
        long? opportunityId, string outcome, CancellationToken ct)
    {
        const string sql = """
            UPDATE dbo.JourneyWorkItems
            SET Status=N'COMPLETED',CompletedAtUtc=SYSUTCDATETIME(),CompletedBySellerKey=@seller,
                Outcome=@outcome,UpdatedAtUtc=SYSUTCDATETIME()
            WHERE OwnerSellerKey=@seller AND Status IN(N'OPEN',N'IN_PROGRESS')
              AND ((@lead IS NOT NULL AND LeadId=@lead) OR (@opportunity IS NOT NULL AND OpportunityId=@opportunity));
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        Add(command, "@seller", SqlDbType.NVarChar, sellerKey, 80);
        Add(command, "@lead", SqlDbType.BigInt, leadId);
        Add(command, "@opportunity", SqlDbType.BigInt, opportunityId);
        Add(command, "@outcome", SqlDbType.NVarChar, outcome, 40);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static Guid DeterministicGuid(Guid source, string purpose)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{source:N}|{purpose}"));
        Span<byte> guid = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guid);
        guid[7] = (byte)((guid[7] & 0x0F) | 0x40);
        guid[8] = (byte)((guid[8] & 0x3F) | 0x80);
        return new Guid(guid);
    }

    private static void Add(SqlCommand command, string name, SqlDbType type, object? value, int size = 0)
    {
        var parameter = size == 0 ? command.Parameters.Add(name, type) : command.Parameters.Add(name, type, size);
        parameter.Value = value ?? DBNull.Value;
    }

    private static void AddDecimal(SqlCommand command, string name, decimal? value, byte precision, byte scale)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = precision;
        parameter.Scale = scale;
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
    }

    private static string? GetString(SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? GetInt64(SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static decimal? GetDecimal(SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
}
