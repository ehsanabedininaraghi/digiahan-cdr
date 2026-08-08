using DigiAhan.CDR.Receiver.Models;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class InvoiceNotificationRepository
{
    private readonly string _connectionString;
    private readonly SqlQueryStore _queries;
    private readonly IConfiguration _configuration;
    private readonly ILogger<InvoiceNotificationRepository> _logger;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private volatile bool _schemaReady;

    public InvoiceNotificationRepository(
        IConfiguration configuration,
        SqlQueryStore queries,
        ILogger<InvoiceNotificationRepository> logger)
    {
        _connectionString = configuration.GetConnectionString("DigiAhanCdr")
            ?? throw new InvalidOperationException("ConnectionStrings:DigiAhanCdr is missing.");
        _queries = queries;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_schemaReady) return;
        await _schemaGate.WaitAsync(ct);
        try
        {
            if (_schemaReady) return;
            await using var connection = await OpenAsync(ct);
            await using var command = new SqlCommand(_queries.Get("InvoiceNotificationsV43.sql"), connection)
            {
                CommandTimeout = 180
            };
            await command.ExecuteNonQueryAsync(ct);
            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    public async Task<InvoiceNotificationDiscoveryResult> DiscoverAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        const string sql = """
            SELECT
                i.SourceDatabase,i.FiscalYear,i.FactorCode,i.FactorNumber,i.FactorDate,
                i.CustomerDetailCode,i.CustomerName,i.FactorDescription,
                link.IdentityId,phone.NormalizedPhone,
                itemData.ItemCount,itemData.FirstProduct
            FROM dbo.AccountingInvoices i
            OUTER APPLY
            (
                SELECT TOP(1) l.IdentityId
                FROM dbo.CustomerIdentityAccountingLinks l
                WHERE l.SourceDatabase=i.SourceDatabase
                  AND l.FiscalYear=i.FiscalYear
                  AND l.DetailCode=i.CustomerDetailCode
                ORDER BY l.IsVerified DESC,l.Id
            ) link
            OUTER APPLY
            (
                SELECT TOP(1) p.NormalizedPhone
                FROM dbo.CustomerIdentityPhones p
                LEFT JOIN dbo.CustomerIdentities ci ON ci.IdentityId=p.IdentityId
                WHERE p.IdentityId=link.IdentityId
                  AND p.IsVerified=1
                  AND LEN(p.NormalizedPhone)=11
                  AND p.NormalizedPhone LIKE N'09%'
                  AND NOT EXISTS
                  (
                      SELECT 1 FROM dbo.CustomerIdentityPhones conflict
                      WHERE conflict.NormalizedPhone=p.NormalizedPhone
                        AND conflict.IdentityId<>p.IdentityId
                  )
                ORDER BY CASE WHEN ci.PrimaryMobilePhoneId=p.Id THEN 0 ELSE 1 END,
                         p.IsPrimary DESC,p.Priority,p.Id
            ) phone
            OUTER APPLY
            (
                SELECT COUNT(*) AS ItemCount,MIN(NULLIF(LTRIM(RTRIM(ii.ProductName)),N'')) AS FirstProduct
                FROM dbo.AccountingInvoiceItems ii
                WHERE ii.SourceDatabase=i.SourceDatabase
                  AND ii.FiscalYear=i.FiscalYear
                  AND ii.FactorCode=i.FactorCode
            ) itemData
            WHERE NULLIF(LTRIM(RTRIM(i.FactorDescription)),N'') IS NOT NULL
              AND i.FactorDescription LIKE N'%حواله%';
            """;

        var candidates = new List<DiscoveryCandidate>();
        await using (var command = new SqlCommand(sql, connection) { CommandTimeout = 120 })
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var voucher = DeliveryVoucherParser.Parse(GetString(reader, "FactorDescription"));
                if (voucher is null) continue;
                var itemCount = GetInt(reader, "ItemCount");
                var firstProduct = GetString(reader, "FirstProduct");
                var productSummary = string.IsNullOrWhiteSpace(firstProduct)
                    ? null
                    : itemCount > 1 ? $"{firstProduct} + {itemCount - 1} قلم" : firstProduct;
                var factorNumber = GetDecimal(reader, "FactorNumber");
                candidates.Add(new DiscoveryCandidate(
                    GetString(reader, "SourceDatabase")!,
                    GetInt(reader, "FiscalYear"),
                    GetInt(reader, "FactorCode"),
                    factorNumber?.ToString("0", CultureInfo.InvariantCulture),
                    GetString(reader, "FactorDate"),
                    GetString(reader, "CustomerDetailCode"),
                    GetString(reader, "CustomerName"),
                    voucher,
                    GetLongNullable(reader, "IdentityId"),
                    GetString(reader, "NormalizedPhone"),
                    productSummary));
            }
        }

        var ready = 0;
        var needsIdentity = 0;
        var needsPhone = 0;
        foreach (var candidate in candidates)
        {
            var status = candidate.IdentityId is null
                ? "NEEDS_IDENTITY"
                : string.IsNullOrWhiteSpace(candidate.PrimaryPhone) ? "NEEDS_PHONE" : "READY";
            switch (status)
            {
                case "READY": ready++; break;
                case "NEEDS_IDENTITY": needsIdentity++; break;
                default: needsPhone++; break;
            }
            await UpsertCandidateAsync(connection, candidate, status, ct);
        }

        var result = new InvoiceNotificationDiscoveryResult(
            candidates.Count, ready, needsIdentity, needsPhone, DateTime.UtcNow);
        _logger.LogInformation(
            "Invoice notification discovery completed. Scanned={Scanned} Ready={Ready} NeedsIdentity={NeedsIdentity} NeedsPhone={NeedsPhone}",
            result.Scanned, result.Ready, result.NeedsIdentity, result.NeedsPhone);
        return result;
    }

    public async Task<IReadOnlyList<InvoiceNotificationListItem>> ListAsync(
        string? status, int take, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        take = Math.Clamp(take, 1, 500);
        status = string.IsNullOrWhiteSpace(status) ? "all" : status.Trim().ToUpperInvariant();
        var rows = new List<NotificationRow>();
        await using var connection = await OpenAsync(ct);
        const string sql = """
            SELECT TOP(@take)
                Id,IdentityId,InvoiceNumber,FactorDate,CustomerNameSnapshot,
                ProductSummarySnapshot,DeliveryVoucherNumber,PrimaryPhoneSnapshot,
                SmsStatus,CreatedAtUtc,PreparedAtUtc,SmsSentAt
            FROM dbo.InvoiceNotifications
            WHERE @status=N'ALL' OR SmsStatus=@status
            ORDER BY CASE SmsStatus
                        WHEN N'READY' THEN 0 WHEN N'NEEDS_PHONE' THEN 1
                        WHEN N'NEEDS_IDENTITY' THEN 2 WHEN N'PREPARED' THEN 3 ELSE 4 END,
                     CreatedAtUtc DESC;
            """;
        await using (var command = new SqlCommand(sql, connection))
        {
            command.Parameters.Add("@take", SqlDbType.Int).Value = take;
            command.Parameters.Add("@status", SqlDbType.NVarChar, 30).Value = status;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new NotificationRow(
                    GetLong(reader, "Id"), GetLongNullable(reader, "IdentityId"),
                    GetString(reader, "InvoiceNumber"), GetString(reader, "FactorDate"),
                    GetString(reader, "CustomerNameSnapshot"), GetString(reader, "ProductSummarySnapshot"),
                    GetString(reader, "DeliveryVoucherNumber")!, GetString(reader, "PrimaryPhoneSnapshot"),
                    GetString(reader, "SmsStatus")!, reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")),
                    GetDateTime(reader, "PreparedAtUtc"), GetDateTime(reader, "SmsSentAt")));
            }
        }

        var phones = await LoadPhonesAsync(connection, rows, ct);
        return rows.Select(row => new InvoiceNotificationListItem(
            row.Id, row.InvoiceNumber, row.FactorDate, row.CustomerName, row.ProductSummary,
            row.DeliveryVoucherNumber, row.PrimaryPhone,
            phones.TryGetValue(row.Id, out var values) ? values : Array.Empty<string>(),
            row.Status, row.CreatedAtUtc, row.PreparedAtUtc, row.SmsSentAt)).ToArray();
    }

    public async Task<string> SetPrimaryMobileAsync(long notificationId, string? rawPhone, string? actor, CancellationToken ct)
    {
        var phone = MappingValueNormalizer.Phone(rawPhone);
        if (phone is null || phone.Length != 11 || !phone.StartsWith("09", StringComparison.Ordinal))
            throw new ArgumentException("شماره موبایل باید به شکل 09xxxxxxxxx باشد.", nameof(rawPhone));

        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            long identityId;
            const string identitySql = "SELECT IdentityId FROM dbo.InvoiceNotifications WITH (UPDLOCK,ROWLOCK) WHERE Id=@id;";
            await using (var identityCommand = new SqlCommand(identitySql, connection, transaction))
            {
                identityCommand.Parameters.Add("@id", SqlDbType.BigInt).Value = notificationId;
                var value = await identityCommand.ExecuteScalarAsync(ct);
                if (value is null or DBNull)
                    throw new InvalidOperationException("این فاکتور هنوز به IdentityId متصل نشده است.");
                identityId = Convert.ToInt64(value);
            }

            const string conflictSql = """
                SELECT COUNT(DISTINCT IdentityId)
                FROM dbo.CustomerIdentityPhones
                WHERE NormalizedPhone=@phone AND IdentityId<>@identity;
                """;
            await using (var conflictCommand = new SqlCommand(conflictSql, connection, transaction))
            {
                conflictCommand.Parameters.Add("@phone", SqlDbType.NVarChar, 32).Value = phone;
                conflictCommand.Parameters.Add("@identity", SqlDbType.BigInt).Value = identityId;
                if (Convert.ToInt32(await conflictCommand.ExecuteScalarAsync(ct)) > 0)
                    throw new InvalidOperationException("این شماره به هویت دیگری متصل است و نمی‌تواند Primary شود.");
            }

            const string updateSql = """
                DECLARE @phoneId bigint=(
                    SELECT TOP(1) Id FROM dbo.CustomerIdentityPhones
                    WHERE IdentityId=@identity AND NormalizedPhone=@phone
                    ORDER BY IsVerified DESC,Priority,Id);

                IF @phoneId IS NULL
                BEGIN
                    INSERT dbo.CustomerIdentityPhones
                        (IdentityId,NormalizedPhone,RawPhone,PhoneType,SourceSystem,IsPrimary,IsVerified,Priority)
                    VALUES(@identity,@phone,@raw,N'Mobile',N'MANUAL_V43',1,1,0);
                    SET @phoneId=SCOPE_IDENTITY();
                END;

                UPDATE dbo.CustomerIdentityPhones SET IsPrimary=0 WHERE IdentityId=@identity;
                UPDATE dbo.CustomerIdentityPhones
                SET IsPrimary=1,IsVerified=1,Priority=0,PhoneType=N'Mobile'
                WHERE Id=@phoneId;
                UPDATE dbo.CustomerIdentities
                SET PrimaryMobilePhoneId=@phoneId,UpdatedAtUtc=SYSUTCDATETIME()
                WHERE IdentityId=@identity;

                UPDATE dbo.InvoiceNotifications
                SET PrimaryPhoneSnapshot=@phone,
                    SmsStatus=CASE WHEN SmsStatus=N'MANUALLY_SENT' THEN SmsStatus ELSE N'READY' END,
                    PublicTokenHash=CASE WHEN SmsStatus=N'MANUALLY_SENT' THEN PublicTokenHash ELSE NULL END,
                    TokenExpiresAtUtc=CASE WHEN SmsStatus=N'MANUALLY_SENT' THEN TokenExpiresAtUtc ELSE NULL END,
                    PreparedAtUtc=CASE WHEN SmsStatus=N'MANUALLY_SENT' THEN PreparedAtUtc ELSE NULL END,
                    UpdatedAtUtc=SYSUTCDATETIME()
                WHERE IdentityId=@identity;

                INSERT dbo.InvoiceNotificationAttempts(NotificationId,Action,Status,PhoneSnapshot,Actor,Detail)
                VALUES(@notification,N'PRIMARY_PHONE_CHANGED',N'SUCCESS',@phone,@actor,N'Primary mobile phone updated.');
                """;
            await using (var updateCommand = new SqlCommand(updateSql, connection, transaction))
            {
                updateCommand.Parameters.Add("@identity", SqlDbType.BigInt).Value = identityId;
                updateCommand.Parameters.Add("@notification", SqlDbType.BigInt).Value = notificationId;
                updateCommand.Parameters.Add("@phone", SqlDbType.NVarChar, 32).Value = phone;
                updateCommand.Parameters.Add("@raw", SqlDbType.NVarChar, 200).Value = rawPhone!.Trim();
                updateCommand.Parameters.Add("@actor", SqlDbType.NVarChar, 100).Value = NormalizeActor(actor);
                await updateCommand.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return phone;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<PreparedInvoiceNotification>> PrepareAsync(
        IReadOnlyList<long>? notificationIds, string? actor, CancellationToken ct)
    {
        var ids = (notificationIds ?? Array.Empty<long>()).Where(x => x > 0).Distinct().Take(100).ToArray();
        if (ids.Length == 0) throw new ArgumentException("حداقل یک فاکتور باید انتخاب شود.");

        await EnsureSchemaAsync(ct);
        var expiresDays = Math.Clamp(_configuration.GetValue("InvoiceNotifications:TokenExpiryDays", 7), 1, 30);
        var baseUrl = (_configuration["InvoiceNotifications:PublicOrderBaseUrl"]
                       ?? "https://www.digiahan.com/order").Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBase)
            || parsedBase.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("InvoiceNotifications:PublicOrderBaseUrl معتبر نیست.");

        var prepared = new List<PreparedInvoiceNotification>();
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            foreach (var id in ids)
            {
                var row = await LoadForPrepareAsync(connection, transaction, id, ct)
                          ?? throw new KeyNotFoundException($"اعلان {id} پیدا نشد.");
                if (row.IdentityId is null) throw new InvalidOperationException($"اعلان {id} IdentityId ندارد.");
                if (string.IsNullOrWhiteSpace(row.PrimaryPhone))
                    throw new InvalidOperationException($"برای اعلان {id} شماره موبایل معتبر و بدون تعارض وجود ندارد.");

                var token = PublicTokenService.Create();
                var tokenHash = PublicTokenService.Hash(token);
                var expiresAt = DateTime.UtcNow.AddDays(expiresDays);
                var publicUrl = $"{baseUrl}/{token}";
                var template = BuildMessageTemplate(row.ProductSummary, row.DeliveryVoucherNumber);
                var message = template.Replace("{LINK}", publicUrl, StringComparison.Ordinal);

                const string updateSql = """
                    UPDATE dbo.InvoiceNotifications
                    SET PrimaryPhoneSnapshot=@phone,SmsStatus=N'PREPARED',PublicTokenHash=@hash,
                        TokenExpiresAtUtc=@expires,MessageBodySnapshot=@message,
                        PreparedAtUtc=SYSUTCDATETIME(),PreparedBy=@actor,LastError=NULL,
                        UpdatedAtUtc=SYSUTCDATETIME()
                    WHERE Id=@id;
                    INSERT dbo.InvoiceNotificationAttempts
                        (NotificationId,Action,Status,PhoneSnapshot,Actor,Detail)
                    VALUES(@id,N'PREPARE',N'SUCCESS',@phone,@actor,N'Secure public link generated.');
                    """;
                await using var update = new SqlCommand(updateSql, connection, transaction);
                update.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                update.Parameters.Add("@phone", SqlDbType.NVarChar, 32).Value = row.PrimaryPhone;
                update.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = tokenHash;
                update.Parameters.Add("@expires", SqlDbType.DateTime2).Value = expiresAt;
                update.Parameters.Add("@message", SqlDbType.NVarChar, 2000).Value = template;
                update.Parameters.Add("@actor", SqlDbType.NVarChar, 100).Value = NormalizeActor(actor);
                await update.ExecuteNonQueryAsync(ct);
                prepared.Add(new PreparedInvoiceNotification(id, row.PrimaryPhone, publicUrl, message, expiresAt));
            }
            await transaction.CommitAsync(ct);
            return prepared;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task MarkManualSentAsync(long notificationId, string? actor, string? note, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        const string sql = """
            UPDATE dbo.InvoiceNotifications
            SET SmsStatus=N'MANUALLY_SENT',SmsSentAt=SYSUTCDATETIME(),SmsProviderId=N'MANUAL',
                UpdatedAtUtc=SYSUTCDATETIME()
            WHERE Id=@id AND SmsStatus=N'PREPARED';
            IF @@ROWCOUNT=0 THROW 51000,N'Notification must be PREPARED before marking it sent.',1;
            INSERT dbo.InvoiceNotificationAttempts
                (NotificationId,Action,Status,PhoneSnapshot,Actor,ProviderId,Detail)
            SELECT Id,N'MANUAL_SEND',N'SUCCESS',PrimaryPhoneSnapshot,@actor,N'MANUAL',@note
            FROM dbo.InvoiceNotifications WHERE Id=@id;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = notificationId;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 100).Value = NormalizeActor(actor);
        command.Parameters.Add("@note", SqlDbType.NVarChar, 1000).Value =
            string.IsNullOrWhiteSpace(note) ? DBNull.Value : note.Trim();
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<PublicOrderView?> FindPublicOrderAsync(string token, CancellationToken ct)
    {
        if (!PublicTokenService.IsWellFormed(token)) return null;
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        const string sql = """
            SELECT TOP(1)
                Id,SourceDatabase,FiscalYear,FactorCode,DeliveryVoucherNumber,
                FactorDate,ProductSummarySnapshot
            FROM dbo.InvoiceNotifications
            WHERE PublicTokenHash=@hash
              AND TokenExpiresAtUtc>=SYSUTCDATETIME()
              AND SmsStatus IN (N'PREPARED',N'MANUALLY_SENT');
            """;
        long id;
        string source;
        int fiscalYear;
        int factorCode;
        string voucher;
        string? factorDate;
        string? productSummary;
        await using (var command = new SqlCommand(sql, connection))
        {
            command.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = PublicTokenService.Hash(token);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            id = GetLong(reader, "Id");
            source = GetString(reader, "SourceDatabase")!;
            fiscalYear = GetInt(reader, "FiscalYear");
            factorCode = GetInt(reader, "FactorCode");
            voucher = GetString(reader, "DeliveryVoucherNumber")!;
            factorDate = GetString(reader, "FactorDate");
            productSummary = GetString(reader, "ProductSummarySnapshot");
        }

        const string itemsSql = """
            SELECT ProductName,Quantity
            FROM dbo.AccountingInvoiceItems
            WHERE SourceDatabase=@source AND FiscalYear=@year AND FactorCode=@factor
              AND NULLIF(LTRIM(RTRIM(ProductName)),N'') IS NOT NULL
            ORDER BY ISNULL(ItemRow,2147483647),ItemCode;
            """;
        var products = new List<PublicOrderProduct>();
        await using (var command = new SqlCommand(itemsSql, connection))
        {
            command.Parameters.Add("@source", SqlDbType.NVarChar, 128).Value = source;
            command.Parameters.Add("@year", SqlDbType.Int).Value = fiscalYear;
            command.Parameters.Add("@factor", SqlDbType.Int).Value = factorCode;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                products.Add(new PublicOrderProduct(reader.GetString(0),
                    reader.IsDBNull(1) ? null : Convert.ToDouble(reader[1])));
        }

        _logger.LogInformation("Public order viewed. NotificationId={NotificationId}", id);
        return new PublicOrderView(voucher, factorDate, productSummary, products);
    }

    private static async Task UpsertCandidateAsync(
        SqlConnection connection, DiscoveryCandidate row, string status, CancellationToken ct)
    {
        const string sql = """
            UPDATE dbo.InvoiceNotifications
            SET AccountingCustomerCode=@customerCode,IdentityId=@identity,InvoiceNumber=@invoice,
                FactorDate=@date,DeliveryVoucherNumber=@voucher,CustomerNameSnapshot=@customer,
                ProductSummarySnapshot=@product,
                PrimaryPhoneSnapshot=CASE WHEN SmsStatus IN (N'PREPARED',N'MANUALLY_SENT')
                                          THEN PrimaryPhoneSnapshot ELSE @phone END,
                SmsStatus=CASE WHEN SmsStatus IN (N'PREPARED',N'MANUALLY_SENT',N'CANCELLED')
                               THEN SmsStatus ELSE @status END,
                UpdatedAtUtc=SYSUTCDATETIME()
            WHERE SourceDatabase=@source AND FiscalYear=@year AND FactorCode=@factor;

            IF @@ROWCOUNT=0
                INSERT dbo.InvoiceNotifications
                    (SourceDatabase,FiscalYear,FactorCode,AccountingCustomerCode,IdentityId,
                     InvoiceNumber,FactorDate,DeliveryVoucherNumber,CustomerNameSnapshot,
                     ProductSummarySnapshot,PrimaryPhoneSnapshot,SmsStatus)
                VALUES(@source,@year,@factor,@customerCode,@identity,@invoice,@date,@voucher,
                       @customer,@product,@phone,@status);
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@source", SqlDbType.NVarChar, 128).Value = row.SourceDatabase;
        command.Parameters.Add("@year", SqlDbType.Int).Value = row.FiscalYear;
        command.Parameters.Add("@factor", SqlDbType.Int).Value = row.FactorCode;
        command.Parameters.Add("@customerCode", SqlDbType.NVarChar, 30).Value = Db(row.AccountingCustomerCode);
        command.Parameters.Add("@identity", SqlDbType.BigInt).Value = Db(row.IdentityId);
        command.Parameters.Add("@invoice", SqlDbType.NVarChar, 50).Value = Db(row.InvoiceNumber);
        command.Parameters.Add("@date", SqlDbType.NVarChar, 10).Value = Db(row.FactorDate);
        command.Parameters.Add("@voucher", SqlDbType.NVarChar, 100).Value = row.DeliveryVoucherNumber;
        command.Parameters.Add("@customer", SqlDbType.NVarChar, 400).Value = Db(row.CustomerName);
        command.Parameters.Add("@product", SqlDbType.NVarChar, 800).Value = Db(row.ProductSummary);
        command.Parameters.Add("@phone", SqlDbType.NVarChar, 32).Value = Db(row.PrimaryPhone);
        command.Parameters.Add("@status", SqlDbType.NVarChar, 30).Value = status;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Dictionary<long, IReadOnlyList<string>>> LoadPhonesAsync(
        SqlConnection connection, IReadOnlyList<NotificationRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return new Dictionary<long, IReadOnlyList<string>>();
        var names = rows.Select((_, index) => $"@id{index}").ToArray();
        var sql = $"""
            SELECT n.Id,p.NormalizedPhone,
                   CASE WHEN ci.PrimaryMobilePhoneId=p.Id THEN 0 ELSE 1 END AS PrimaryRank,
                   p.IsPrimary,p.Priority,p.Id AS PhoneId
            FROM dbo.InvoiceNotifications n
            INNER JOIN dbo.CustomerIdentities ci ON ci.IdentityId=n.IdentityId
            INNER JOIN dbo.CustomerIdentityPhones p ON p.IdentityId=n.IdentityId
            WHERE n.Id IN ({string.Join(',', names)})
              AND LEN(p.NormalizedPhone)=11 AND p.NormalizedPhone LIKE N'09%'
              AND NOT EXISTS
              (
                  SELECT 1 FROM dbo.CustomerIdentityPhones conflict
                  WHERE conflict.NormalizedPhone=p.NormalizedPhone
                    AND conflict.IdentityId<>p.IdentityId
              )
            ORDER BY n.Id,PrimaryRank,p.IsPrimary DESC,p.Priority,p.Id;
            """;
        var result = new Dictionary<long, List<string>>();
        await using var command = new SqlCommand(sql, connection);
        for (var i = 0; i < rows.Count; i++)
            command.Parameters.Add(names[i], SqlDbType.BigInt).Value = rows[i].Id;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            var phone = reader.GetString(1);
            if (!result.TryGetValue(id, out var list)) result[id] = list = new List<string>();
            if (!list.Contains(phone, StringComparer.Ordinal)) list.Add(phone);
        }
        return result.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value);
    }

    private static async Task<PrepareRow?> LoadForPrepareAsync(
        SqlConnection connection, SqlTransaction transaction, long id, CancellationToken ct)
    {
        const string sql = """
            SELECT n.IdentityId,n.DeliveryVoucherNumber,n.ProductSummarySnapshot,
                   phone.NormalizedPhone
            FROM dbo.InvoiceNotifications n WITH (UPDLOCK,ROWLOCK)
            OUTER APPLY
            (
                SELECT TOP(1) p.NormalizedPhone
                FROM dbo.CustomerIdentityPhones p
                LEFT JOIN dbo.CustomerIdentities ci ON ci.IdentityId=p.IdentityId
                WHERE p.IdentityId=n.IdentityId
                  AND p.IsVerified=1
                  AND LEN(p.NormalizedPhone)=11 AND p.NormalizedPhone LIKE N'09%'
                  AND NOT EXISTS
                  (
                      SELECT 1 FROM dbo.CustomerIdentityPhones conflict
                      WHERE conflict.NormalizedPhone=p.NormalizedPhone
                        AND conflict.IdentityId<>p.IdentityId
                  )
                ORDER BY CASE WHEN ci.PrimaryMobilePhoneId=p.Id THEN 0 ELSE 1 END,
                         p.IsPrimary DESC,p.Priority,p.Id
            ) phone
            WHERE n.Id=@id AND n.SmsStatus<>N'CANCELLED';
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new PrepareRow(
            GetLongNullable(reader, "IdentityId"), GetString(reader, "DeliveryVoucherNumber")!,
            GetString(reader, "ProductSummarySnapshot"), GetString(reader, "NormalizedPhone"));
    }

    private static string BuildMessageTemplate(string? product, string voucher)
    {
        var productLine = string.IsNullOrWhiteSpace(product) ? string.Empty : $"محصول: {product.Trim()}\n";
        return $"مشتری گرامی،\nاطلاعات خرید شما آماده است.\n{productLine}شماره حواله: {voucher}\nمشاهده اطلاعات خرید: {{LINK}}\nدیجی‌آهن";
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static object Db(object? value) => value ?? DBNull.Value;
    private static string NormalizeActor(string? actor)
        => string.IsNullOrWhiteSpace(actor) ? "MANAGER" : actor.Trim()[..Math.Min(actor.Trim().Length, 100)];
    private static int GetInt(SqlDataReader reader, string name)
        => reader.IsDBNull(reader.GetOrdinal(name)) ? 0 : Convert.ToInt32(reader[name]);
    private static long GetLong(SqlDataReader reader, string name)
        => Convert.ToInt64(reader[name]);
    private static long? GetLongNullable(SqlDataReader reader, string name)
        => reader.IsDBNull(reader.GetOrdinal(name)) ? null : Convert.ToInt64(reader[name]);
    private static decimal? GetDecimal(SqlDataReader reader, string name)
        => reader.IsDBNull(reader.GetOrdinal(name)) ? null : Convert.ToDecimal(reader[name]);
    private static DateTime? GetDateTime(SqlDataReader reader, string name)
        => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetDateTime(reader.GetOrdinal(name));
    private static string? GetString(SqlDataReader reader, string name)
        => reader.IsDBNull(reader.GetOrdinal(name)) ? null : Convert.ToString(reader[name]);

    private sealed record DiscoveryCandidate(
        string SourceDatabase, int FiscalYear, int FactorCode, string? InvoiceNumber,
        string? FactorDate, string? AccountingCustomerCode, string? CustomerName,
        string DeliveryVoucherNumber, long? IdentityId, string? PrimaryPhone, string? ProductSummary);

    private sealed record NotificationRow(
        long Id, long? IdentityId, string? InvoiceNumber, string? FactorDate,
        string? CustomerName, string? ProductSummary, string DeliveryVoucherNumber,
        string? PrimaryPhone, string Status, DateTime CreatedAtUtc,
        DateTime? PreparedAtUtc, DateTime? SmsSentAt);

    private sealed record PrepareRow(
        long? IdentityId, string DeliveryVoucherNumber, string? ProductSummary, string? PrimaryPhone);
}
