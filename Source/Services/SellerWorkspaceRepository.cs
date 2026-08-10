using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigiAhan.CDR.Receiver.Models;
using Microsoft.Data.SqlClient;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class SellerWorkspaceRepository
{
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);
    private static bool _schemaReady;
    private static readonly HashSet<string> Outcomes = new(StringComparer.OrdinalIgnoreCase)
        { "ORDER", "DECIDING", "FOLLOW_UP", "LOST", "NON_SALES" };
    private static readonly HashSet<string> LossReasons = new(StringComparer.OrdinalIgnoreCase)
        { "PRICE", "NO_STOCK", "PAYMENT", "DELIVERY", "COMPETITOR", "NO_NEED", "OTHER" };
    private static readonly HashSet<string> ActionCodes = new(StringComparer.OrdinalIgnoreCase)
        { "PRICE_QUOTED", "STOCK_CHECKED", "PAYMENT_EXPLAINED", "ALTERNATIVE_OFFERED" };

    private readonly string _connectionString;
    private readonly IWebHostEnvironment _environment;

    public SellerWorkspaceRepository(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _connectionString = configuration.GetConnectionString("DigiAhanCdr")
            ?? throw new InvalidOperationException("ConnectionStrings:DigiAhanCdr is missing.");
        _environment = environment;
    }

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_schemaReady) return;
        await SchemaGate.WaitAsync(ct);
        try
        {
            if (_schemaReady) return;
            var path = Path.Combine(_environment.ContentRootPath, "Sql", "SellerWorkspaceV1.sql");
            if (!File.Exists(path)) throw new FileNotFoundException("Seller Workspace schema is missing.", path);
            var sql = await File.ReadAllTextAsync(path, ct);
            await using var connection = await OpenAsync(ct);
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync(ct);
            _schemaReady = true;
        }
        finally { SchemaGate.Release(); }
    }

    public async Task<SellerTodayStats> GetStatsAsync(SellerIdentity seller, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        var start = StartOfIranDayUtc();
        var end = start.AddDays(1);
        var extensions = BuildExtensions(seller.Extensions);
        var sql = $"""
        SELECT
          (SELECT COUNT(*) FROM dbo.AgentIncomingEvents WHERE Extension IN ({extensions.Sql}) AND CreatedAtUtc>=@start AND CreatedAtUtc<@end),
          (SELECT COUNT(*) FROM dbo.SellerInteractions WHERE SellerKey=@seller AND OccurredAtUtc>=@start AND OccurredAtUtc<@end),
          (SELECT COUNT(*) FROM dbo.SellerInteractionActions a JOIN dbo.SellerInteractions i ON i.Id=a.InteractionId WHERE i.SellerKey=@seller AND i.OccurredAtUtc>=@start AND i.OccurredAtUtc<@end AND a.ActionCode=N'PRICE_QUOTED'),
          (SELECT COUNT(*) FROM dbo.SellerInteractions WHERE SellerKey=@seller AND OccurredAtUtc>=@start AND OccurredAtUtc<@end AND Outcome=N'ORDER'),
          (SELECT COUNT(*) FROM dbo.SellerInteractions WHERE SellerKey=@seller AND OccurredAtUtc>=@start AND OccurredAtUtc<@end AND Outcome=N'LOST'),
          (SELECT COUNT(*) FROM dbo.SellerFollowUps WHERE SellerKey=@seller AND Status=N'OPEN' AND DueAtUtc>=@start AND DueAtUtc<@end),
          (SELECT COUNT(*) FROM dbo.SellerFollowUps WHERE SellerKey=@seller AND Status=N'OPEN' AND DueAtUtc<@now);
        """;
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 15 };
        command.Parameters.Add("@seller", SqlDbType.NVarChar, 80).Value = seller.Key;
        command.Parameters.Add("@start", SqlDbType.DateTime2).Value = start;
        command.Parameters.Add("@end", SqlDbType.DateTime2).Value = end;
        command.Parameters.Add("@now", SqlDbType.DateTime2).Value = DateTime.UtcNow;
        AddExtensionParameters(command, extensions.Values);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var calls = reader.GetInt32(0);
        var results = reader.GetInt32(1);
        return new SellerTodayStats(calls, reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
            Math.Max(0, calls - results), reader.GetInt32(5), reader.GetInt32(6));
    }

    public async Task<IReadOnlyList<SellerFollowUpRow>> GetFollowUpsAsync(SellerIdentity seller, int take, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        const string sql = """
        SELECT TOP(@take) f.Id,f.CustomerPhone,
          COALESCE(NULLIF(d.DisplayName,N''),NULLIF(d.CompanyName,N''),f.CustomerPhone),
          f.Subject,f.DueAtUtc,f.Status,f.InteractionId
        FROM dbo.SellerFollowUps f
        OUTER APPLY
        (
          SELECT TOP(1) p.DisplayName,p.CompanyName
          FROM dbo.CustomerPhoneDirectory p
          WHERE p.NormalizedPhone=dbo.NormalizeIranPhone(f.CustomerPhone)
          ORDER BY p.IsVerified DESC,p.IdentityId
        ) d
        WHERE f.SellerKey=@seller AND f.Status=N'OPEN'
        ORDER BY CASE WHEN f.DueAtUtc<SYSUTCDATETIME() THEN 0 ELSE 1 END,f.DueAtUtc;
        """;
        var rows = new List<SellerFollowUpRow>();
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 15 };
        command.Parameters.Add("@take", SqlDbType.Int).Value = Math.Clamp(take, 1, 50);
        command.Parameters.Add("@seller", SqlDbType.NVarChar, 80).Value = seller.Key;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetDateTime(4), reader.GetString(5), reader.GetInt64(6)));
        return rows;
    }

    public async Task<IReadOnlyList<SellerTimelineRow>> GetTimelineAsync(
        SellerIdentity seller, string phone, int take, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        phone = NormalizePhone(phone);
        var extensions = BuildExtensions(seller.Extensions);
        var sql = $"""
        ;WITH TargetIdentity AS
        (
          SELECT TOP(1) IdentityId FROM dbo.CustomerIdentityPhones
          WHERE NormalizedPhone=dbo.NormalizeIranPhone(@phone)
          ORDER BY IsVerified DESC,Priority,Id
        ),
        CustomerPhones AS
        (
          SELECT p.NormalizedPhone FROM dbo.CustomerIdentityPhones p
          WHERE p.IdentityId=(SELECT TOP(1) IdentityId FROM TargetIdentity)
          UNION SELECT dbo.NormalizeIranPhone(@phone)
        ),
        CustomerPhoneVariants AS
        (
          SELECT NormalizedPhone Phone FROM CustomerPhones WHERE NormalizedPhone IS NOT NULL
          UNION SELECT SUBSTRING(NormalizedPhone,2,32) FROM CustomerPhones WHERE NormalizedPhone LIKE N'0%'
          UNION SELECT N'98'+SUBSTRING(NormalizedPhone,2,32) FROM CustomerPhones WHERE NormalizedPhone LIKE N'0%'
          UNION SELECT N'0098'+SUBSTRING(NormalizedPhone,2,32) FROM CustomerPhones WHERE NormalizedPhone LIKE N'0%'
          UNION SELECT N'+98'+SUBSTRING(NormalizedPhone,2,32) FROM CustomerPhones WHERE NormalizedPhone LIKE N'0%'
        ),
        MatchingCalls AS
        (
          SELECT r.* FROM dbo.RawCDR r
          INNER JOIN CustomerPhoneVariants p ON p.Phone=r.Src
          WHERE r.Calldate>=DATEADD(month,-6,SYSDATETIME())
          UNION
          SELECT r.* FROM dbo.RawCDR r
          INNER JOIN CustomerPhoneVariants p ON p.Phone=r.Dst
          WHERE r.Calldate>=DATEADD(month,-6,SYSDATETIME())
        ),
        Events AS
        (
          SELECT i.Id,N'INTERACTION' EventType,i.OccurredAtUtc EventAtUtc,i.SellerDisplayName,
            CASE i.Outcome WHEN N'ORDER' THEN N'سفارش ثبت شد' WHEN N'DECIDING' THEN N'در حال تصمیم‌گیری'
              WHEN N'FOLLOW_UP' THEN N'نیاز به پیگیری' WHEN N'LOST' THEN N'خرید انجام نشد' ELSE N'تماس غیر فروش' END Title,
            i.Note Description,p.ProductName,p.ProductSize,p.Quantity,p.QuantityUnit,i.Outcome,i.LossReason,
            CAST(CASE WHEN i.SellerKey=@seller THEN 1 ELSE 0 END AS bit) IsMine
          FROM dbo.SellerInteractions i
          OUTER APPLY(SELECT TOP(1) * FROM dbo.SellerInteractionProducts p0 WHERE p0.InteractionId=i.Id ORDER BY p0.Id) p
          WHERE i.CustomerIdentityId=(SELECT TOP(1) IdentityId FROM TargetIdentity)
             OR dbo.NormalizeIranPhone(i.CustomerPhone) IN(SELECT NormalizedPhone FROM CustomerPhones)
          UNION ALL
          SELECT -MIN(r.RawCDRId),N'CALL',MIN(r.Calldate),
            CASE
              WHEN MAX(CASE WHEN r.Src IN ({extensions.Sql}) THEN 1 ELSE 0 END)=1
                THEN N'تماس خروجی از داخلی '+MAX(CASE WHEN r.Src IN ({extensions.Sql}) THEN r.Src END)
              WHEN MAX(CASE WHEN r.Dst IN ({extensions.Sql}) THEN 1 ELSE 0 END)=1
                THEN N'تماس ورودی به داخلی '+MAX(CASE WHEN r.Dst IN ({extensions.Sql}) THEN r.Dst END)
              ELSE N'سیستم تلفنی'
            END,
            CASE
              WHEN MAX(CASE WHEN r.Src IN ({extensions.Sql}) THEN 1 ELSE 0 END)=1 THEN N'تماس خروجی'
              ELSE N'تماس ورودی'
            END,
            N'وضعیت: '+COALESCE(MAX(NULLIF(r.Disposition,N'')),N'نامشخص')+
              N' · مدت مکالمه: '+CONVERT(nvarchar(20),MAX(ISNULL(r.Billsec,0)))+N' ثانیه',
            NULL,NULL,NULL,NULL,NULL,NULL,
            CAST(MAX(CASE WHEN r.Src IN ({extensions.Sql}) OR r.Dst IN ({extensions.Sql}) THEN 1 ELSE 0 END) AS bit)
          FROM MatchingCalls r
          GROUP BY COALESCE(NULLIF(r.LinkedId,N''),NULLIF(r.UniqueId,N''),CONVERT(nvarchar(30),r.RawCDRId))
        )
        SELECT TOP(@take) Id,EventType,EventAtUtc,SellerDisplayName,Title,Description,
          ProductName,ProductSize,Quantity,QuantityUnit,Outcome,LossReason,IsMine
        FROM Events ORDER BY EventAtUtc DESC,Id DESC;
        """;
        var rows = new List<SellerTimelineRow>();
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 15 };
        command.Parameters.Add("@take", SqlDbType.Int).Value = Math.Clamp(take, 1, 100);
        command.Parameters.Add("@seller", SqlDbType.NVarChar, 80).Value = seller.Key;
        command.Parameters.Add("@phone", SqlDbType.NVarChar, 32).Value = phone;
        AddExtensionParameters(command, extensions.Values);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetDateTime(2), reader.GetString(3),
                reader.GetString(4), GetString(reader, 5), GetString(reader, 6), GetString(reader, 7),
                GetDecimal(reader, 8), GetString(reader, 9), GetString(reader, 10), GetString(reader, 11), reader.GetBoolean(12)));
        return rows;
    }

    public async Task<SellerInteractionResult> SaveInteractionAsync(
        SellerIdentity seller, SellerInteractionRequest request, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        var normalized = Validate(request);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            const string existingSql = "SELECT Id,CreatedAtUtc FROM dbo.SellerInteractions WITH(UPDLOCK,HOLDLOCK) WHERE IdempotencyKey=@key;";
            SellerInteractionResult? existingResult = null;
            await using (var existing = new SqlCommand(existingSql, connection, transaction))
            {
                existing.Parameters.Add("@key", SqlDbType.UniqueIdentifier).Value = normalized.Key;
                await using var reader = await existing.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                    existingResult = new SellerInteractionResult(
                        reader.GetInt64(0), normalized.Key.ToString(), true, reader.GetDateTime(1));
            }
            if (existingResult is not null)
            {
                await transaction.CommitAsync(ct);
                return existingResult;
            }

            var identityId = await ResolveIdentityIdAsync(connection, transaction, normalized.Phone, ct);
            const string insertSql = """
            INSERT dbo.SellerInteractions
              (IdempotencyKey,SellerKey,SellerDisplayName,SellerExtension,CustomerIdentityId,CustomerPhone,
               CallLinkedId,Outcome,LossReason,CompetitorName,CompetitorPrice,Note,OccurredAtUtc)
            OUTPUT inserted.Id,inserted.CreatedAtUtc
            VALUES(@key,@seller,@name,@extension,@identity,@phone,@linked,@outcome,@loss,@competitor,@competitorPrice,@note,@occurred);
            """;
            long interactionId; DateTime created;
            await using (var command = new SqlCommand(insertSql, connection, transaction))
            {
                Add(command, "@key", SqlDbType.UniqueIdentifier, normalized.Key);
                Add(command, "@seller", SqlDbType.NVarChar, seller.Key, 80);
                Add(command, "@name", SqlDbType.NVarChar, seller.DisplayName, 200);
                Add(command, "@extension", SqlDbType.NVarChar, seller.Extensions[0], 10);
                Add(command, "@identity", SqlDbType.BigInt, identityId);
                Add(command, "@phone", SqlDbType.NVarChar, normalized.Phone, 32);
                Add(command, "@linked", SqlDbType.NVarChar, Clean(request.CallLinkedId, 100), 100);
                Add(command, "@outcome", SqlDbType.NVarChar, normalized.Outcome, 30);
                Add(command, "@loss", SqlDbType.NVarChar, normalized.LossReason, 30);
                Add(command, "@competitor", SqlDbType.NVarChar, Clean(request.CompetitorName, 200), 200);
                AddDecimal(command, "@competitorPrice", request.CompetitorPrice);
                Add(command, "@note", SqlDbType.NVarChar, Clean(request.Note, 1000), 1000);
                Add(command, "@occurred", SqlDbType.DateTime2, request.OccurredAtUtc?.ToUniversalTime() ?? DateTime.UtcNow);
                await using var reader = await command.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct); interactionId = reader.GetInt64(0); created = reader.GetDateTime(1);
            }

            if (!string.IsNullOrWhiteSpace(request.ProductName))
                await InsertProductAsync(connection, transaction, interactionId, request, ct);
            foreach (var action in normalized.Actions)
                await InsertActionAsync(connection, transaction, interactionId, action, ct);
            if (normalized.Outcome == "FOLLOW_UP")
                await InsertFollowUpAsync(connection, transaction, interactionId, seller, normalized.Phone, request, ct);
            await InsertAuditAsync(connection, transaction, seller.Key, "CREATE", "INTERACTION", interactionId.ToString(),
                normalized.Key, new { normalized.Outcome, normalized.LossReason, normalized.Phone }, ct);
            await transaction.CommitAsync(ct);
            return new(interactionId, normalized.Key.ToString(), false, created);
        }
        catch { await transaction.RollbackAsync(CancellationToken.None); throw; }
    }

    public async Task<bool> CompleteFollowUpAsync(SellerIdentity seller, long id, Guid key, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        const string sql = """
        UPDATE dbo.SellerFollowUps SET Status=N'COMPLETED',CompletedAtUtc=SYSUTCDATETIME(),
          CompletedBySellerKey=@seller,UpdatedAtUtc=SYSUTCDATETIME()
        WHERE Id=@id AND SellerKey=@seller AND Status=N'OPEN';
        """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        command.Parameters.Add("@seller", SqlDbType.NVarChar, 80).Value = seller.Key;
        var changed = await command.ExecuteNonQueryAsync(ct) > 0;
        if (changed) await InsertAuditAsync(connection, transaction, seller.Key, "COMPLETE", "FOLLOW_UP", id.ToString(), key, null, ct);
        await transaction.CommitAsync(ct);
        return changed;
    }

    private static async Task InsertProductAsync(SqlConnection c, SqlTransaction t, long id, SellerInteractionRequest r, CancellationToken ct)
    {
        const string sql = "INSERT dbo.SellerInteractionProducts(InteractionId,ProductName,ProductSize,ProductBrand,Quantity,QuantityUnit) VALUES(@id,@name,@size,@brand,@quantity,@unit);";
        await using var command = new SqlCommand(sql, c, t);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        Add(command, "@name", SqlDbType.NVarChar, Clean(r.ProductName, 120), 120);
        Add(command, "@size", SqlDbType.NVarChar, Clean(r.ProductSize, 60), 60);
        Add(command, "@brand", SqlDbType.NVarChar, Clean(r.ProductBrand, 120), 120);
        AddDecimal(command, "@quantity", r.Quantity, 18, 3);
        Add(command, "@unit", SqlDbType.NVarChar, Clean(r.QuantityUnit, 30), 30);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertActionAsync(SqlConnection c, SqlTransaction t, long id, string action, CancellationToken ct)
    {
        await using var command = new SqlCommand("INSERT dbo.SellerInteractionActions(InteractionId,ActionCode) VALUES(@id,@action);", c, t);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        command.Parameters.Add("@action", SqlDbType.NVarChar, 40).Value = action;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertFollowUpAsync(SqlConnection c, SqlTransaction t, long id, SellerIdentity seller, string phone, SellerInteractionRequest r, CancellationToken ct)
    {
        const string sql = "INSERT dbo.SellerFollowUps(InteractionId,SellerKey,CustomerPhone,Subject,DueAtUtc) VALUES(@id,@seller,@phone,@subject,@due);";
        await using var command = new SqlCommand(sql, c, t);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        command.Parameters.Add("@seller", SqlDbType.NVarChar, 80).Value = seller.Key;
        command.Parameters.Add("@phone", SqlDbType.NVarChar, 32).Value = phone;
        command.Parameters.Add("@subject", SqlDbType.NVarChar, 300).Value = Clean(r.FollowUpSubject, 300)!;
        command.Parameters.Add("@due", SqlDbType.DateTime2).Value = r.FollowUpAtUtc!.Value.ToUniversalTime();
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertAuditAsync(SqlConnection c, SqlTransaction t, string seller, string action, string entity, string id, Guid? key, object? details, CancellationToken ct)
    {
        const string sql = "INSERT dbo.SellerWorkspaceAudit(SellerKey,ActionType,EntityType,EntityId,IdempotencyKey,DetailsJson) VALUES(@seller,@action,@entity,@id,@key,@details);";
        await using var command = new SqlCommand(sql, c, t);
        command.Parameters.Add("@seller", SqlDbType.NVarChar, 80).Value = seller;
        command.Parameters.Add("@action", SqlDbType.NVarChar, 60).Value = action;
        command.Parameters.Add("@entity", SqlDbType.NVarChar, 60).Value = entity;
        command.Parameters.Add("@id", SqlDbType.NVarChar, 100).Value = id;
        command.Parameters.Add("@key", SqlDbType.UniqueIdentifier).Value = key.HasValue ? key.Value : DBNull.Value;
        command.Parameters.Add("@details", SqlDbType.NVarChar).Value = details is null ? DBNull.Value : JsonSerializer.Serialize(details);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long?> ResolveIdentityIdAsync(SqlConnection c, SqlTransaction t, string phone, CancellationToken ct)
    {
        const string sql = "SELECT TOP(1) IdentityId FROM dbo.CustomerIdentityPhones WHERE NormalizedPhone=dbo.NormalizeIranPhone(@phone) ORDER BY IsVerified DESC,Priority,Id;";
        await using var command = new SqlCommand(sql, c, t);
        command.Parameters.Add("@phone", SqlDbType.NVarChar, 32).Value = phone;
        var value = await command.ExecuteScalarAsync(ct);
        return value is null || value is DBNull ? null : Convert.ToInt64(value);
    }

    private static (Guid Key, string Phone, string Outcome, string? LossReason, string[] Actions) Validate(SellerInteractionRequest r)
    {
        if (!Guid.TryParse(r.IdempotencyKey, out var key)) throw new ArgumentException("IdempotencyKey is invalid.");
        var phone = NormalizePhone(r.CustomerPhone);
        if (phone.Length < 7) throw new ArgumentException("CustomerPhone is invalid.");
        var outcome = (r.Outcome ?? string.Empty).Trim().ToUpperInvariant();
        if (!Outcomes.Contains(outcome)) throw new ArgumentException("Outcome is invalid.");
        var loss = string.IsNullOrWhiteSpace(r.LossReason) ? null : r.LossReason.Trim().ToUpperInvariant();
        if (outcome == "LOST" && (loss is null || !LossReasons.Contains(loss))) throw new ArgumentException("LossReason is required for LOST.");
        if (outcome != "LOST" && loss is not null) throw new ArgumentException("LossReason is only valid for LOST.");
        if (outcome == "FOLLOW_UP" && (!r.FollowUpAtUtc.HasValue || string.IsNullOrWhiteSpace(r.FollowUpSubject)))
            throw new ArgumentException("FollowUpAtUtc and FollowUpSubject are required for FOLLOW_UP.");
        if (r.Quantity < 0 || r.CompetitorPrice < 0) throw new ArgumentException("Numeric values cannot be negative.");
        var actions = (r.Actions ?? Array.Empty<string>()).Select(x => (x ?? string.Empty).Trim().ToUpperInvariant()).Where(ActionCodes.Contains).Distinct().ToArray();
        return (key, phone, outcome, loss, actions);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct) { var c = new SqlConnection(_connectionString); await c.OpenAsync(ct); return c; }
    private static string NormalizePhone(string? value) { var digits = Regex.Replace(value ?? string.Empty, @"\D", ""); if (digits.StartsWith("0098")) digits = "0" + digits[4..]; else if (digits.StartsWith("98") && digits.Length >= 12) digits = "0" + digits[2..]; return digits; }
    private static string? Clean(string? value, int max) { value = value?.Trim(); return string.IsNullOrWhiteSpace(value) ? null : value[..Math.Min(value.Length, max)]; }
    private static string? GetString(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static decimal? GetDecimal(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);
    private static void Add(SqlCommand c, string n, SqlDbType t, object? v, int size = 0) { var p = size > 0 ? c.Parameters.Add(n, t, size) : c.Parameters.Add(n, t); p.Value = v ?? DBNull.Value; }
    private static void AddDecimal(SqlCommand c, string n, decimal? v, byte precision = 19, byte scale = 4) { var p = c.Parameters.Add(n, SqlDbType.Decimal); p.Precision = precision; p.Scale = scale; p.Value = v.HasValue ? v.Value : DBNull.Value; }
    private static (string Sql, string[] Values) BuildExtensions(string[] values) => (string.Join(",", values.Select((_, i) => $"@e{i}")), values);
    private static void AddExtensionParameters(SqlCommand c, string[] values) { for (var i = 0; i < values.Length; i++) c.Parameters.Add($"@e{i}", SqlDbType.NVarChar, 10).Value = values[i]; }
    private static DateTime StartOfIranDayUtc() { var zone = TimeZoneInfo.FindSystemTimeZoneById("Iran Standard Time"); var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone); return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(now.Date, DateTimeKind.Unspecified), zone); }
}
