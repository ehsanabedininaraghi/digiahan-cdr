using System.Data;
using System.Globalization;
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
        var start = TehranClock.StartOfTodayUtc();
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
        var missing = Math.Max(0, calls - results);
        var quality = calls == 0 ? 100 : Math.Clamp((int)Math.Round(results * 100d / calls), 0, 100);
        return new SellerTodayStats(calls, reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
            missing, reader.GetInt32(5), reader.GetInt32(6), quality);
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
                TehranClock.AsUtc(reader.GetDateTime(4)), reader.GetString(5), reader.GetInt64(6)));
        return rows;
    }

    public async Task<IReadOnlyList<SellerCustomerSearchRow>> SearchCustomersAsync(
        string? query, int take, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        query = NormalizeSearchText(query);
        if (query.Length < 1) return Array.Empty<SellerCustomerSearchRow>();
        take = Math.Clamp(take, 1, 30);
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        var tokenWhere = string.Join(" AND ", tokens.Select((_, index) =>
            $"REPLACE(REPLACE(CONCAT(ISNULL(i.DisplayName,N''),N' ',ISNULL(i.CompanyName,N'')),N'ي',N'ی'),N'ك',N'ک') LIKE N'%'+@token{index}+N'%'"));
        var sql = $"""
        SELECT TOP(@take)
          i.IdentityId,
          COALESCE(primaryPhone.NormalizedPhone,anyPhone.NormalizedPhone),
          COALESCE(NULLIF(i.DisplayName,N''),NULLIF(i.CompanyName,N''),N'مشتری بدون نام'),
          NULLIF(i.CompanyName,N''),mobiles.MobilePhones,NULLIF(i.OwnerName,N''),
          CASE WHEN EXISTS(SELECT 1 FROM dbo.CustomerIdentityDidarLinks d WHERE d.IdentityId=i.IdentityId)
               THEN N'DIDAR' WHEN EXISTS(SELECT 1 FROM dbo.CustomerIdentityAccountingLinks a WHERE a.IdentityId=i.IdentityId)
               THEN N'ACCOUNTING' ELSE N'DIGIAHAN' END
        FROM dbo.CustomerIdentities i
        OUTER APPLY
        (
          SELECT TOP(1) p.NormalizedPhone FROM dbo.CustomerIdentityPhones p
          WHERE p.IdentityId=i.IdentityId AND p.NormalizedPhone LIKE N'09%'
          ORDER BY p.IsPrimary DESC,p.IsVerified DESC,p.Priority,p.Id
        ) primaryPhone
        OUTER APPLY
        (
          SELECT TOP(1) p.NormalizedPhone FROM dbo.CustomerIdentityPhones p
          WHERE p.IdentityId=i.IdentityId
          ORDER BY p.IsPrimary DESC,p.IsVerified DESC,p.Priority,p.Id
        ) anyPhone
        OUTER APPLY
        (
          SELECT STRING_AGG(x.NormalizedPhone,N'، ') WITHIN GROUP(ORDER BY x.NormalizedPhone) MobilePhones
          FROM (SELECT DISTINCT p.NormalizedPhone FROM dbo.CustomerIdentityPhones p
                WHERE p.IdentityId=i.IdentityId AND p.NormalizedPhone LIKE N'09%') x
        ) mobiles
        WHERE ISNULL(i.IsActive,1)=1
          AND
          (
            ({tokenWhere})
            OR (@phone<>N'' AND EXISTS
            (
              SELECT 1 FROM dbo.CustomerIdentityPhones p
              WHERE p.IdentityId=i.IdentityId
                AND (p.NormalizedPhone LIKE N'%'+@phone+N'%'
                     OR p.RawPhone LIKE N'%'+@query+N'%')
            ))
          )
        ORDER BY
          CASE WHEN i.DisplayName=@query OR i.CompanyName=@query THEN 0
               WHEN i.DisplayName LIKE @query+N'%' OR i.CompanyName LIKE @query+N'%' THEN 1 ELSE 2 END,
          i.DisplayName,i.IdentityId;
        """;
        var rows = new List<SellerCustomerSearchRow>();
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 15 };
        command.Parameters.Add("@take", SqlDbType.Int).Value = take;
        command.Parameters.Add("@query", SqlDbType.NVarChar, 200).Value = query;
        command.Parameters.Add("@phone", SqlDbType.NVarChar, 32).Value = Regex.IsMatch(query, @"\d") ? NormalizePhone(query) : string.Empty;
        for (var index = 0; index < tokens.Length; index++)
            command.Parameters.Add($"@token{index}", SqlDbType.NVarChar, 100).Value = tokens[index];
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(1)) continue;
            rows.Add(new SellerCustomerSearchRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), GetString(reader, 3),
                GetString(reader, 4), GetString(reader, 5), reader.GetString(6)));
        }
        return rows;
    }

    public async Task<SellerCustomerProfile> GetCustomerProfileAsync(string? phone, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        phone = NormalizePhone(phone);
        if (phone.Length < 7)
            return new SellerCustomerProfile(null, null, null, null, Array.Empty<string>(), false, "NEW");
        const string sql = """
        ;WITH Target AS
        (
          SELECT TOP(1) IdentityId
          FROM dbo.CustomerIdentityPhones
          WHERE NormalizedPhone=dbo.NormalizeIranPhone(@phone)
          ORDER BY IsVerified DESC,Priority,Id
        )
        SELECT i.IdentityId,i.DisplayName,i.CompanyName,i.OwnerName,
          phones.AllPhones,i.IsActive,
          CASE WHEN i.MasterSource=N'DIGIAHAN' THEN N'DIGIAHAN'
               WHEN EXISTS(SELECT 1 FROM dbo.CustomerIdentityDidarLinks d WHERE d.IdentityId=i.IdentityId) THEN N'DIDAR'
               WHEN EXISTS(SELECT 1 FROM dbo.CustomerIdentityAccountingLinks a WHERE a.IdentityId=i.IdentityId) THEN N'ACCOUNTING'
               ELSE ISNULL(i.MasterSource,N'LEGACY') END
        FROM dbo.CustomerIdentities i
        OUTER APPLY
        (
          SELECT STRING_AGG(x.NormalizedPhone,N'|') WITHIN GROUP(ORDER BY x.NormalizedPhone) AllPhones
          FROM (SELECT DISTINCT p.NormalizedPhone FROM dbo.CustomerIdentityPhones p WHERE p.IdentityId=i.IdentityId) x
        ) phones
        WHERE i.IdentityId=(SELECT TOP(1) IdentityId FROM Target);
        """;
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 15 };
        command.Parameters.Add("@phone", SqlDbType.NVarChar, 32).Value = phone;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new SellerCustomerProfile(null, null, null, null, new[] { phone }, true, "NEW");
        var phones = (GetString(reader, 4) ?? phone).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new SellerCustomerProfile(reader.GetInt64(0), GetString(reader, 1), GetString(reader, 2), GetString(reader, 3),
            phones, reader.GetBoolean(5), reader.GetString(6));
    }

    public async Task<SellerCustomerSaveResult> SaveCustomerAsync(
        SellerIdentity seller, string? anchorPhone, SellerCustomerSaveRequest request, bool create, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        var displayName = Clean(request.DisplayName, 300);
        var companyName = Clean(request.CompanyName, 300);
        var ownerName = Clean(request.OwnerName, 200);
        if (displayName is null && companyName is null)
            throw new ArgumentException("نام شخص یا شرکت الزامی است.");
        var phones = (request.Phones ?? Array.Empty<string>())
            .Select(NormalizePhone).Where(value => value.Length >= 7)
            .Distinct(StringComparer.Ordinal).Take(30).ToList();
        var anchor = NormalizePhone(anchorPhone);
        if (!create && anchor.Length >= 7 && !phones.Contains(anchor, StringComparer.Ordinal)) phones.Insert(0, anchor);
        if (phones.Count == 0) throw new ArgumentException("حداقل یک شماره تلفن معتبر الزامی است.");

        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            long? identityId = create ? null : await ResolveIdentityIdAsync(connection, transaction, anchor, ct);
            if (!create && identityId is null) throw new ArgumentException("مشتری برای ویرایش پیدا نشد.");
            foreach (var candidate in phones)
            {
                var linked = await ResolveIdentityIdAsync(connection, transaction, candidate, ct);
                if (linked.HasValue && (!identityId.HasValue || linked.Value != identityId.Value))
                    throw new ArgumentException($"شماره {candidate} قبلاً به مشتری دیگری متصل است.");
            }

            if (create)
            {
                const string insert = """
                INSERT dbo.CustomerIdentities(DisplayName,CompanyName,OwnerName,MasterSource,IsActive)
                OUTPUT inserted.IdentityId
                VALUES(@display,@company,@owner,N'DIGIAHAN',1);
                """;
                await using var command = new SqlCommand(insert, connection, transaction);
                Add(command, "@display", SqlDbType.NVarChar, displayName, 300);
                Add(command, "@company", SqlDbType.NVarChar, companyName, 300);
                Add(command, "@owner", SqlDbType.NVarChar, ownerName, 200);
                identityId = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
            }
            else
            {
                const string update = """
                UPDATE dbo.CustomerIdentities
                SET DisplayName=@display,CompanyName=@company,OwnerName=@owner,
                    MasterSource=N'DIGIAHAN',IsActive=1,UpdatedAtUtc=SYSUTCDATETIME()
                WHERE IdentityId=@identity;
                """;
                await using var command = new SqlCommand(update, connection, transaction);
                command.Parameters.Add("@identity", SqlDbType.BigInt).Value = identityId!.Value;
                Add(command, "@display", SqlDbType.NVarChar, displayName, 300);
                Add(command, "@company", SqlDbType.NVarChar, companyName, 300);
                Add(command, "@owner", SqlDbType.NVarChar, ownerName, 200);
                await command.ExecuteNonQueryAsync(ct);
            }

            for (var index = 0; index < phones.Count; index++)
            {
                const string upsertPhone = """
                IF NOT EXISTS
                (
                  SELECT 1 FROM dbo.CustomerIdentityPhones
                  WHERE IdentityId=@identity AND NormalizedPhone=@phone AND SourceSystem=N'DIGIAHAN'
                )
                  INSERT dbo.CustomerIdentityPhones
                    (IdentityId,NormalizedPhone,RawPhone,PhoneType,SourceSystem,IsPrimary,IsVerified,Priority)
                  VALUES(@identity,@phone,@phone,N'MANUAL',N'DIGIAHAN',@primary,1,5);
                ELSE
                  UPDATE dbo.CustomerIdentityPhones
                  SET RawPhone=@phone,IsPrimary=@primary,IsVerified=1,Priority=5
                  WHERE IdentityId=@identity AND NormalizedPhone=@phone AND SourceSystem=N'DIGIAHAN';
                """;
                await using var command = new SqlCommand(upsertPhone, connection, transaction);
                command.Parameters.Add("@identity", SqlDbType.BigInt).Value = identityId!.Value;
                command.Parameters.Add("@phone", SqlDbType.NVarChar, 32).Value = phones[index];
                command.Parameters.Add("@primary", SqlDbType.Bit).Value = index == 0;
                await command.ExecuteNonQueryAsync(ct);
            }

            await InsertAuditAsync(connection, transaction, seller.Key, create ? "CREATE" : "UPDATE", "CUSTOMER",
                identityId!.Value.ToString(), null, new { displayName, companyName, ownerName, phones }, ct);
            await transaction.CommitAsync(ct);
            return new SellerCustomerSaveResult(identityId.Value, phones[0], create, DateTime.UtcNow);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<bool> ArchiveCustomerAsync(SellerIdentity seller, string? phone, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        phone = NormalizePhone(phone);
        if (phone.Length < 7) return false;
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var identityId = await ResolveIdentityIdAsync(connection, transaction, phone, ct);
        if (!identityId.HasValue) { await transaction.RollbackAsync(ct); return false; }
        await using (var command = new SqlCommand(
            "UPDATE dbo.CustomerIdentities SET IsActive=0,MasterSource=N'DIGIAHAN',UpdatedAtUtc=SYSUTCDATETIME() WHERE IdentityId=@id;",
            connection, transaction))
        {
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = identityId.Value;
            await command.ExecuteNonQueryAsync(ct);
        }
        await InsertAuditAsync(connection, transaction, seller.Key, "ARCHIVE", "CUSTOMER", identityId.Value.ToString(), null,
            new { phone }, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<SellerMissingResultRow>> GetMissingResultsAsync(
        SellerIdentity seller, int take, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        var start = TehranClock.StartOfTodayUtc();
        var extensions = BuildExtensions(seller.Extensions);
        var sql = $"""
        ;WITH Incoming AS
        (
          SELECT e.*,
            rn=ROW_NUMBER() OVER(PARTITION BY COALESCE(NULLIF(e.LinkedId,N''),e.CallerNumber)
                                 ORDER BY e.CreatedAtUtc DESC,e.Id DESC)
          FROM dbo.AgentIncomingEvents e
          WHERE e.Extension IN ({extensions.Sql}) AND e.CreatedAtUtc>=@start
        )
        SELECT TOP(@take) e.Id,e.CallerNumber,
          COALESCE(NULLIF(e.CustomerName,N''),NULLIF(e.CompanyName,N''),e.CallerNumber),
          e.EventTimeUtc,e.LinkedId
        FROM Incoming e
        WHERE e.rn=1
          AND NOT EXISTS
          (
            SELECT 1 FROM dbo.SellerInteractions i
            WHERE i.SellerKey=@seller
              AND ((NULLIF(e.LinkedId,N'') IS NOT NULL AND i.CallLinkedId=e.LinkedId)
                   OR (dbo.NormalizeIranPhone(i.CustomerPhone)=dbo.NormalizeIranPhone(e.CallerNumber)
                       AND i.OccurredAtUtc BETWEEN DATEADD(minute,-15,e.CreatedAtUtc) AND DATEADD(hour,4,e.CreatedAtUtc)))
          )
          AND NOT EXISTS
          (
            SELECT 1 FROM dbo.AgentCallOutcomes o
            WHERE o.Extension=e.Extension
              AND ((NULLIF(e.LinkedId,N'') IS NOT NULL AND o.LinkedId=e.LinkedId)
                   OR (dbo.NormalizeIranPhone(o.CallerNumber)=dbo.NormalizeIranPhone(e.CallerNumber)
                       AND o.CreatedAtUtc BETWEEN DATEADD(minute,-15,e.CreatedAtUtc) AND DATEADD(hour,4,e.CreatedAtUtc)))
          )
        ORDER BY e.CreatedAtUtc DESC;
        """;
        var rows = new List<SellerMissingResultRow>();
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 15 };
        command.Parameters.Add("@take", SqlDbType.Int).Value = Math.Clamp(take, 1, 50);
        command.Parameters.Add("@seller", SqlDbType.NVarChar, 80).Value = seller.Key;
        command.Parameters.Add("@start", SqlDbType.DateTime2).Value = start;
        AddExtensionParameters(command, extensions.Values);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new SellerMissingResultRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                TehranClock.AsUtc(reader.GetDateTime(3)), GetString(reader, 4)));
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
          UNION
          SELECT r.* FROM dbo.RawCDR r
          INNER JOIN CustomerPhoneVariants p ON p.Phone=r.Dst
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
          SELECT -MIN(r.RawCDRId),N'CALL',MIN(r.ReceivedAtUtc),
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
          UNION ALL
          SELECT -1000000000-o.Id,N'LEGACY_OUTCOME',o.CreatedAtUtc,
            N'داخلی '+o.Extension,
            CASE o.Outcome WHEN N'ORDER' THEN N'سفارش ثبت شد' WHEN N'QUOTED' THEN N'قیمت اعلام شد'
              WHEN N'FOLLOW_UP' THEN N'پیگیری ثبت شد' ELSE N'عدم نیاز' END,
            o.Note,NULL,NULL,NULL,NULL,o.Outcome,NULL,
            CAST(CASE WHEN o.Extension IN ({extensions.Sql}) THEN 1 ELSE 0 END AS bit)
          FROM dbo.AgentCallOutcomes o
          WHERE dbo.NormalizeIranPhone(o.CallerNumber) IN(SELECT NormalizedPhone FROM CustomerPhones)
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
            rows.Add(new(reader.GetInt64(0), reader.GetString(1), TehranClock.AsUtc(reader.GetDateTime(2)), reader.GetString(3),
                reader.GetString(4), GetString(reader, 5), GetString(reader, 6), GetString(reader, 7),
                GetDecimal(reader, 8), GetString(reader, 9), GetString(reader, 10), GetString(reader, 11), reader.GetBoolean(12)));
        await reader.DisposeAsync();

        const string invoiceSql = """
        SELECT TOP(@take) i.FactorCode,i.FactorDate,i.FactorNumber,i.TypeDescription,
          i.FactorDescription,i.Amount,i.VisitorName,i.ImportedAtUtc,products.ProductNames
        FROM dbo.AccountingInvoices i
        INNER JOIN dbo.CustomerIdentityAccountingLinks link
          ON link.SourceDatabase=i.SourceDatabase AND link.FiscalYear=i.FiscalYear
         AND link.DetailCode=i.CustomerDetailCode
        OUTER APPLY
        (
          SELECT STRING_AGG(CAST(x.ProductName AS nvarchar(max)),N'، ') ProductNames
          FROM
          (
            SELECT DISTINCT COALESCE(NULLIF(item.ProductName,N''),NULLIF(item.Description,N'')) ProductName
            FROM dbo.AccountingInvoiceItems item
            WHERE item.SourceDatabase=i.SourceDatabase AND item.FiscalYear=i.FiscalYear
              AND item.FactorCode=i.FactorCode
          ) x
          WHERE x.ProductName IS NOT NULL
        ) products
        WHERE link.IdentityId=
        (
          SELECT TOP(1) IdentityId FROM dbo.CustomerIdentityPhones
          WHERE NormalizedPhone=dbo.NormalizeIranPhone(@phone)
          ORDER BY IsVerified DESC,Priority,Id
        )
        ORDER BY i.FactorDate DESC,i.FactorCode DESC;
        """;
        await using var invoiceCommand = new SqlCommand(invoiceSql, connection) { CommandTimeout = 15 };
        invoiceCommand.Parameters.Add("@take", SqlDbType.Int).Value = Math.Clamp(take, 1, 100);
        invoiceCommand.Parameters.Add("@phone", SqlDbType.NVarChar, 32).Value = phone;
        await using var invoiceReader = await invoiceCommand.ExecuteReaderAsync(ct);
        while (await invoiceReader.ReadAsync(ct))
        {
            var factorDate = GetString(invoiceReader, 1);
            var factorNumber = invoiceReader.IsDBNull(2) ? invoiceReader.GetInt32(0).ToString(CultureInfo.InvariantCulture) :
                Convert.ToDecimal(invoiceReader[2], CultureInfo.InvariantCulture).ToString("0", CultureInfo.InvariantCulture);
            var products = GetString(invoiceReader, 8);
            var descriptionParts = new[]
            {
                products,
                GetString(invoiceReader, 4),
                invoiceReader.IsDBNull(5) ? null : $"مبلغ: {invoiceReader.GetDecimal(5):N0} ریال"
            }.Where(value => !string.IsNullOrWhiteSpace(value));
            rows.Add(new SellerTimelineRow(
                -2_000_000_000L - invoiceReader.GetInt32(0), "INVOICE",
                PersianDateToUtc(factorDate) ?? TehranClock.AsUtc(invoiceReader.GetDateTime(7)),
                GetString(invoiceReader, 6) ?? "حسابداری",
                $"فاکتور فروش {factorNumber}", string.Join(" · ", descriptionParts),
                products, null, null, null, "ORDER", null, false));
        }
        return rows.OrderByDescending(value => value.EventAtUtc).ThenByDescending(value => value.Id)
            .Take(Math.Clamp(take, 1, 100)).ToArray();
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

    public async Task<SellerInteractionEditResponse?> GetInteractionAsync(
        SellerIdentity seller, long id, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        const string sql = """
        SELECT i.Id,i.CustomerPhone,i.CallLinkedId,i.OccurredAtUtc,
          p.ProductName,p.ProductSize,p.ProductBrand,p.Quantity,p.QuantityUnit,
          actions.ActionCodes,i.Outcome,i.LossReason,i.CompetitorName,i.CompetitorPrice,
          followup.DueAtUtc,followup.Subject,i.Note
        FROM dbo.SellerInteractions i
        OUTER APPLY
        (
          SELECT TOP(1) ProductName,ProductSize,ProductBrand,Quantity,QuantityUnit
          FROM dbo.SellerInteractionProducts WHERE InteractionId=i.Id ORDER BY Id
        ) p
        OUTER APPLY
        (
          SELECT STRING_AGG(ActionCode,N'|') ActionCodes
          FROM dbo.SellerInteractionActions WHERE InteractionId=i.Id
        ) actions
        OUTER APPLY
        (
          SELECT TOP(1) DueAtUtc,Subject
          FROM dbo.SellerFollowUps WHERE InteractionId=i.Id AND Status=N'OPEN'
          ORDER BY Id DESC
        ) followup
        WHERE i.Id=@id AND i.SellerKey=@seller;
        """;
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 15 };
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        command.Parameters.Add("@seller", SqlDbType.NVarChar, 80).Value = seller.Key;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var actions = (GetString(reader, 9) ?? string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new SellerInteractionEditResponse(
            reader.GetInt64(0), reader.GetString(1), GetString(reader, 2), TehranClock.AsUtc(reader.GetDateTime(3)),
            GetString(reader, 4), GetString(reader, 5), GetString(reader, 6), GetDecimal(reader, 7), GetString(reader, 8),
            actions, reader.GetString(10), GetString(reader, 11), GetString(reader, 12), GetDecimal(reader, 13),
            reader.IsDBNull(14) ? null : TehranClock.AsUtc(reader.GetDateTime(14)), GetString(reader, 15), GetString(reader, 16));
    }

    public async Task<SellerInteractionResult?> UpdateInteractionAsync(
        SellerIdentity seller, long id, SellerInteractionRequest request, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        var normalized = Validate(request);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var identityId = await ResolveIdentityIdAsync(connection, transaction, normalized.Phone, ct);
            const string updateSql = """
            UPDATE dbo.SellerInteractions
            SET CustomerIdentityId=@identity,CustomerPhone=@phone,CallLinkedId=@linked,Outcome=@outcome,
              LossReason=@loss,CompetitorName=@competitor,CompetitorPrice=@competitorPrice,Note=@note,
              OccurredAtUtc=@occurred,UpdatedAtUtc=SYSUTCDATETIME()
            OUTPUT inserted.CreatedAtUtc
            WHERE Id=@id AND SellerKey=@seller;
            """;
            DateTime created;
            await using (var command = new SqlCommand(updateSql, connection, transaction))
            {
                Add(command, "@id", SqlDbType.BigInt, id);
                Add(command, "@seller", SqlDbType.NVarChar, seller.Key, 80);
                Add(command, "@identity", SqlDbType.BigInt, identityId);
                Add(command, "@phone", SqlDbType.NVarChar, normalized.Phone, 32);
                Add(command, "@linked", SqlDbType.NVarChar, Clean(request.CallLinkedId, 100), 100);
                Add(command, "@outcome", SqlDbType.NVarChar, normalized.Outcome, 30);
                Add(command, "@loss", SqlDbType.NVarChar, normalized.LossReason, 30);
                Add(command, "@competitor", SqlDbType.NVarChar, Clean(request.CompetitorName, 200), 200);
                AddDecimal(command, "@competitorPrice", request.CompetitorPrice);
                Add(command, "@note", SqlDbType.NVarChar, Clean(request.Note, 1000), 1000);
                Add(command, "@occurred", SqlDbType.DateTime2, request.OccurredAtUtc?.ToUniversalTime() ?? DateTime.UtcNow);
                var value = await command.ExecuteScalarAsync(ct);
                if (value is null || value is DBNull)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    return null;
                }
                created = (DateTime)value;
            }

            await using (var clear = new SqlCommand(
                "DELETE FROM dbo.SellerInteractionProducts WHERE InteractionId=@id; DELETE FROM dbo.SellerInteractionActions WHERE InteractionId=@id; UPDATE dbo.SellerFollowUps SET Status=N'CANCELLED',UpdatedAtUtc=SYSUTCDATETIME() WHERE InteractionId=@id AND Status=N'OPEN';",
                connection, transaction))
            {
                clear.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                await clear.ExecuteNonQueryAsync(ct);
            }
            if (!string.IsNullOrWhiteSpace(request.ProductName))
                await InsertProductAsync(connection, transaction, id, request, ct);
            foreach (var action in normalized.Actions)
                await InsertActionAsync(connection, transaction, id, action, ct);
            if (normalized.Outcome == "FOLLOW_UP")
                await InsertFollowUpAsync(connection, transaction, id, seller, normalized.Phone, request, ct);
            await InsertAuditAsync(connection, transaction, seller.Key, "UPDATE", "INTERACTION", id.ToString(),
                normalized.Key, new { normalized.Outcome, normalized.LossReason, normalized.Phone }, ct);
            await transaction.CommitAsync(ct);
            return new SellerInteractionResult(id, normalized.Key.ToString(), false, created);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
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
        if (!Guid.TryParse(r.IdempotencyKey, out var key)) throw new ArgumentException("شناسه یکتای ثبت تعامل معتبر نیست؛ صفحه را تازه‌سازی کنید.");
        var phone = NormalizePhone(r.CustomerPhone);
        if (phone.Length < 7 || phone.Length > 15) throw new ArgumentException("شماره مشتری معتبر نیست؛ ابتدا یک تماس یا مشتری واقعی را انتخاب کنید.");
        var outcome = (r.Outcome ?? string.Empty).Trim().ToUpperInvariant();
        if (!Outcomes.Contains(outcome)) throw new ArgumentException("نتیجه مکالمه را انتخاب کنید.");
        var loss = string.IsNullOrWhiteSpace(r.LossReason) ? null : r.LossReason.Trim().ToUpperInvariant();
        if (outcome == "LOST" && (loss is null || !LossReasons.Contains(loss))) throw new ArgumentException("دلیل انجام‌نشدن خرید را انتخاب کنید.");
        if (outcome != "LOST" && loss is not null) throw new ArgumentException("دلیل عدم خرید فقط برای نتیجه «خرید انجام نشد» قابل ثبت است.");
        if (outcome == "FOLLOW_UP" && (!r.FollowUpAtUtc.HasValue || string.IsNullOrWhiteSpace(r.FollowUpSubject)))
            throw new ArgumentException("زمان و موضوع پیگیری بعدی را وارد کنید.");
        if (r.Quantity < 0 || r.CompetitorPrice < 0) throw new ArgumentException("مقدار و قیمت نمی‌توانند منفی باشند.");
        var actions = (r.Actions ?? Array.Empty<string>()).Select(x => (x ?? string.Empty).Trim().ToUpperInvariant()).Where(ActionCodes.Contains).Distinct().ToArray();
        return (key, phone, outcome, loss, actions);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct) { var c = new SqlConnection(_connectionString); await c.OpenAsync(ct); return c; }
    private static string NormalizePhone(string? value)
    {
        var latin = new string((value ?? string.Empty).Select(ch => ch switch
        {
            >= '۰' and <= '۹' => (char)('0' + ch - '۰'),
            >= '٠' and <= '٩' => (char)('0' + ch - '٠'),
            _ => ch
        }).ToArray());
        var digits = Regex.Replace(latin, @"\D", "");
        if (digits.StartsWith("0098", StringComparison.Ordinal)) digits = "0" + digits[4..];
        else if (digits.StartsWith("98", StringComparison.Ordinal) && digits.Length >= 12) digits = "0" + digits[2..];
        else if (digits.Length == 10 && digits[0] != '0') digits = "0" + digits;
        return digits;
    }
    private static string NormalizeSearchText(string? value) => Regex.Replace((value ?? string.Empty)
        .Replace('ي', 'ی').Replace('ك', 'ک').Trim(), @"\s+", " ");
    private static DateTime? PersianDateToUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var latin = new string(value.Select(ch => ch switch
        {
            >= '۰' and <= '۹' => (char)('0' + ch - '۰'),
            >= '٠' and <= '٩' => (char)('0' + ch - '٠'),
            _ => ch
        }).ToArray());
        var parts = latin.Split(new[] { '/', '-', '.' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var year) || !int.TryParse(parts[1], out var month) || !int.TryParse(parts[2], out var day))
            return null;
        try
        {
            var calendar = new PersianCalendar();
            return TehranClock.ToUtc(calendar.ToDateTime(year, month, day, 12, 0, 0, 0));
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }
    private static string? Clean(string? value, int max) { value = value?.Trim(); return string.IsNullOrWhiteSpace(value) ? null : value[..Math.Min(value.Length, max)]; }
    private static string? GetString(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static decimal? GetDecimal(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);
    private static void Add(SqlCommand c, string n, SqlDbType t, object? v, int size = 0) { var p = size > 0 ? c.Parameters.Add(n, t, size) : c.Parameters.Add(n, t); p.Value = v ?? DBNull.Value; }
    private static void AddDecimal(SqlCommand c, string n, decimal? v, byte precision = 19, byte scale = 4) { var p = c.Parameters.Add(n, SqlDbType.Decimal); p.Precision = precision; p.Scale = scale; p.Value = v.HasValue ? v.Value : DBNull.Value; }
    private static (string Sql, string[] Values) BuildExtensions(string[] values) => (string.Join(",", values.Select((_, i) => $"@e{i}")), values);
    private static void AddExtensionParameters(SqlCommand c, string[] values) { for (var i = 0; i < values.Length; i++) c.Parameters.Add($"@e{i}", SqlDbType.NVarChar, 10).Value = values[i]; }
}
