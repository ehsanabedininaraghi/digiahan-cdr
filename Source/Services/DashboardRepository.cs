using DigiAhan.CDR.Receiver.Models;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Diagnostics;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class DashboardRepository
{
    private readonly string _cs;
    private readonly SqlQueryStore _queries;
    private readonly ILogger<DashboardRepository> _logger;

    public DashboardRepository(
        IConfiguration configuration,
        SqlQueryStore queries,
        ILogger<DashboardRepository> logger)
    {
        _cs = configuration.GetConnectionString("DigiAhanCdr")
              ?? throw new InvalidOperationException("Missing connection string.");
        _queries = queries;
        _logger = logger;
    }

    public async Task<DashboardSummary> Summary(
        DateTime startDate, DateTime endDate, string? extension, CancellationToken ct)
    {
        await using var connection = await OpenConnection(ct);
        await using var command = CreateCommand(_queries.Get("DashboardSummary.sql"), connection);
        AddRangeParameters(command, startDate, endDate, extension);
        await using var reader = await ExecuteReader(command, "DashboardSummary", ct);
        await reader.ReadAsync(ct);

        return new DashboardSummary(
            startDate.Date,
            GetInt(reader, "T"), GetInt(reader, "A"), GetInt(reader, "M"),
            GetInt(reader, "I"), GetInt(reader, "O"), GetInt(reader, "B"),
            GetInt(reader, "Av"), GetInt(reader, "KnownCustomers"),
            GetInt(reader, "NewCustomers"), GetUtcDateTime(reader, "L"), GetUtcDateTime(reader, "R"));
    }

    public async Task<IReadOnlyList<HourlyPoint>> Hourly(
        DateTime startDate, DateTime endDate, string? extension, CancellationToken ct)
    {
        var items = new List<HourlyPoint>();
        await using var connection = await OpenConnection(ct);
        await using var command = CreateCommand(_queries.Get("DashboardHourly.sql"), connection);
        AddRangeParameters(command, startDate, endDate, extension);
        await using var reader = await ExecuteReader(command, "DashboardHourly", ct);

        while (await reader.ReadAsync(ct))
            items.Add(new HourlyPoint(GetInt(reader, "H"), GetInt(reader, "T"), GetInt(reader, "A"), GetInt(reader, "M")));

        return items;
    }

    public async Task<IReadOnlyList<DailyPoint>> Daily(
        DateTime startDate, DateTime endDate, string? extension, CancellationToken ct)
    {
        var items = new List<DailyPoint>();
        await using var connection = await OpenConnection(ct);
        await using var command = CreateCommand(_queries.Get("DashboardDaily.sql"), connection);
        AddRangeParameters(command, startDate, endDate, extension);
        await using var reader = await ExecuteReader(command, "DashboardDaily", ct);

        while (await reader.ReadAsync(ct))
        {
            items.Add(new DailyPoint(
                GetDateTime(reader, "D") ?? startDate.Date,
                GetInt(reader, "T"),
                GetInt(reader, "A"),
                GetInt(reader, "M"),
                GetInt(reader, "I"),
                GetInt(reader, "O"),
                GetInt(reader, "NewCustomers"),
                GetInt(reader, "KnownCustomers"),
                GetInt(reader, "B")));
        }

        return items;
    }

    public async Task<IReadOnlyList<ExtensionStat>> Extensions(
        DateTime startDate, DateTime endDate, CancellationToken ct)
    {
        var items = new List<ExtensionStat>();
        await using var connection = await OpenConnection(ct);
        await using var command = CreateCommand(_queries.Get("DashboardExtensions.sql"), connection);
        AddRangeParameters(command, startDate, endDate, null);
        await using var reader = await ExecuteReader(command, "DashboardExtensions", ct);

        while (await reader.ReadAsync(ct))
            items.Add(new ExtensionStat(
                GetString(reader, "E") ?? string.Empty,
                GetInt(reader, "T"), GetInt(reader, "I"), GetInt(reader, "O"),
                GetInt(reader, "A"), GetInt(reader, "M"),
                GetInt(reader, "B"), GetInt(reader, "Av")));

        return items;
    }

    public async Task<CallsPage> Calls(
        DateTime startDate, DateTime endDate, string? extension,
        string? search, string? status,
        int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 200);

        await using var connection = await OpenConnection(ct);

        int total;
        await using (var countCommand = CreateCommand(_queries.Get("DashboardCallsCount.sql"), connection))
        {
            AddCallsParameters(countCommand, startDate, endDate, extension, search, status);
            total = Convert.ToInt32(await ExecuteScalar(countCommand, "DashboardCallsCount", ct) ?? 0);
        }

        var rowStart = ((page - 1) * pageSize) + 1;
        var rowEnd = page * pageSize;
        var items = new List<CallRow>();

        await using (var pageCommand = CreateCommand(_queries.Get("DashboardCallsPage.sql"), connection))
        {
            AddCallsParameters(pageCommand, startDate, endDate, extension, search, status);
            pageCommand.Parameters.Add("@rowStart", SqlDbType.Int).Value = rowStart;
            pageCommand.Parameters.Add("@rowEnd", SqlDbType.Int).Value = rowEnd;

            await using var reader = await ExecuteReader(pageCommand, "DashboardCallsPage", ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(new CallRow(
                    GetLong(reader, "RawCDRId"),
                    GetUtcDateTime(reader, "Calldate"),
                    GetString(reader, "Src"),
                    GetString(reader, "Dst"),
                    GetString(reader, "Direction") ?? "unknown",
                    GetString(reader, "Disposition"),
                    GetInt(reader, "Duration"),
                    GetInt(reader, "Billsec"),
                    GetString(reader, "RecordingFile"),
                    GetString(reader, "LinkedId"),
                    GetString(reader, "UniqueId"),
                    GetString(reader, "Did"),
                    GetString(reader, "Dcontext"),
                    GetString(reader, "CustomerPhone"),
                    GetString(reader, "CustomerName"),
                    GetString(reader, "CompanyName"),
                    GetString(reader, "OwnerName"),
                    GetString(reader, "DidarContactCode"),
                    GetBool(reader, "IsNewCustomer")));
            }
        }

        return new CallsPage(total, page, pageSize, items);
    }

    public async Task<SyncStatus> Sync(CancellationToken ct)
    {
        await using var connection = await OpenConnection(ct);
        await using var command = CreateCommand(_queries.Get("DashboardSync.sql"), connection);
        await using var reader = await ExecuteReader(command, "DashboardSync", ct);

        DateTime? started = null, finished = null, lastReceived = null, lastCdr = null;
        string? batchStatus = null;
        int inserted = 0, duplicates = 0, errors = 0, rowsLastHour = 0;

        if (await reader.ReadAsync(ct))
        {
            started = GetDateTime(reader, "StartedAtUtc");
            finished = GetDateTime(reader, "FinishedAtUtc");
            batchStatus = GetString(reader, "Status");
            inserted = GetInt(reader, "InsertedCount");
            duplicates = GetInt(reader, "DuplicateCount");
            errors = GetInt(reader, "ErrorCount");
        }

        await reader.NextResultAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            lastReceived = reader.IsDBNull(0) ? null : TehranClock.AsUtc(reader.GetDateTime(0));
            lastCdr = reader.IsDBNull(1) ? null : TehranClock.AsUtc(reader.GetDateTime(1));
            rowsLastHour = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader[2]);
        }

        return new SyncStatus(started, finished, batchStatus, inserted, duplicates, errors, lastReceived, lastCdr, rowsLastHour);
    }

    public async Task<IReadOnlyList<SellerPerformanceRow>> SellerPerformance(
        DateTime startDate, DateTime endDate, string? extension, CancellationToken ct)
    {
        const string sql = """
            ;WITH SellerDirectory AS
            (
              SELECT u.SellerKey,u.DisplayName,
                Extensions=STRING_AGG(CONVERT(nvarchar(max),ue.Extension),N'، ')
              FROM dbo.SellerUsers u
              JOIN dbo.SellerUserExtensions ue ON ue.SellerUserId=u.Id
              WHERE u.IsActive=1 AND (@ext=N'all' OR ue.Extension=@ext)
              GROUP BY u.SellerKey,u.DisplayName
            ),
            RawLegs AS
            (
              SELECT
                CallKey=COALESCE(NULLIF(LinkedId,N''),NULLIF(UniqueId,N''),CONVERT(nvarchar(30),RawCDRId)),
                IsInbound=CASE WHEN NULLIF(Did,N'') IS NOT NULL OR Dcontext LIKE N'%from-trunk%' THEN 1 ELSE 0 END,
                IsOutbound=CASE WHEN Dcontext LIKE N'%from-internal%' OR Dcontext LIKE N'%outbound%' THEN 1 ELSE 0 END,
                IsAnswered=CASE WHEN Disposition=N'ANSWERED' OR ISNULL(Billsec,0)>0 THEN 1 ELSE 0 END,
                AnsweredExtension=CASE
                  WHEN (Disposition=N'ANSWERED' OR ISNULL(Billsec,0)>0)
                   AND Dst LIKE N'[0-9][0-9][0-9]' THEN Dst
                  WHEN (Disposition=N'ANSWERED' OR ISNULL(Billsec,0)>0)
                   AND Src LIKE N'[0-9][0-9][0-9]' THEN Src END,
                OutboundExtension=CASE
                  WHEN (Dcontext LIKE N'%from-internal%' OR Dcontext LIKE N'%outbound%')
                   AND Src LIKE N'[0-9][0-9][0-9]' THEN Src END,
                CustomerPhone=CASE WHEN LEN(Src)>4 THEN Src WHEN LEN(Dst)>4 THEN Dst END,
                EventAtUtc=Calldate,
                TalkSeconds=ISNULL(Billsec,0)
              FROM dbo.RawCDR
              WHERE ReceivedAtUtc>=@s AND ReceivedAtUtc<@e
            ),
            Calls AS
            (
              SELECT CallKey,IsInbound=MAX(IsInbound),IsOutbound=MAX(IsOutbound),
                IsAnswered=MAX(IsAnswered),AnsweredExtension=MAX(AnsweredExtension),
                OutboundExtension=MAX(OutboundExtension),CustomerPhone=MAX(CustomerPhone),
                EventAtUtc=MIN(EventAtUtc),TalkSeconds=MAX(TalkSeconds)
              FROM RawLegs GROUP BY CallKey
            ),
            AttributedCalls AS
            (
              SELECT *,AttributedExtension=CASE
                WHEN IsInbound=1 THEN AnsweredExtension
                WHEN IsOutbound=1 THEN OutboundExtension
                ELSE COALESCE(AnsweredExtension,OutboundExtension) END
              FROM Calls
            ),
            CallOwnership AS
            (
              SELECT u.SellerKey,c.*,
                HasInteraction=CASE WHEN EXISTS
                (
                  SELECT 1 FROM dbo.SellerInteractions i
                  WHERE i.SellerKey=u.SellerKey
                    AND ((NULLIF(c.CallKey,N'') IS NOT NULL AND i.CallLinkedId=c.CallKey)
                      OR (dbo.NormalizeIranPhone(i.CustomerPhone)=dbo.NormalizeIranPhone(c.CustomerPhone)
                        AND i.OccurredAtUtc BETWEEN DATEADD(minute,-15,c.EventAtUtc) AND DATEADD(hour,4,c.EventAtUtc)))
                ) THEN 1 ELSE 0 END
              FROM AttributedCalls c
              JOIN dbo.SellerUserExtensions ue ON ue.Extension=c.AttributedExtension
              JOIN dbo.SellerUsers u ON u.Id=ue.SellerUserId AND u.IsActive=1
              WHERE c.AttributedExtension IS NOT NULL
                AND (@ext=N'all' OR c.AttributedExtension=@ext)
            ),
            CallPerformance AS
            (
              SELECT SellerKey,
                HandledCalls=COUNT(*),
                InboundAnswered=SUM(CASE WHEN IsInbound=1 AND IsAnswered=1 THEN 1 ELSE 0 END),
                OutboundCalls=SUM(CASE WHEN IsOutbound=1 THEN 1 ELSE 0 END),
                AnsweredCalls=SUM(IsAnswered),
                MissingInteractions=SUM(CASE WHEN HasInteraction=0 THEN 1 ELSE 0 END),
                TalkSeconds=SUM(TalkSeconds),
                AverageTalkSeconds=CASE WHEN SUM(IsAnswered)>0 THEN SUM(TalkSeconds)/SUM(IsAnswered) ELSE 0 END
              FROM CallOwnership GROUP BY SellerKey
            ),
            InteractionPerformance AS
            (
              SELECT i.SellerKey,Interactions=COUNT(*),
                FollowUps=SUM(CASE WHEN i.Outcome=N'FOLLOW_UP' THEN 1 ELSE 0 END),
                Quotes=SUM(CASE WHEN quoteAction.InteractionId IS NOT NULL THEN 1 ELSE 0 END),
                Orders=SUM(CASE WHEN i.Outcome=N'ORDER' THEN 1 ELSE 0 END),
                Lost=SUM(CASE WHEN i.Outcome=N'LOST' THEN 1 ELSE 0 END)
              FROM dbo.SellerInteractions i
              LEFT JOIN dbo.SellerInteractionActions quoteAction
                ON quoteAction.InteractionId=i.Id AND quoteAction.ActionCode=N'PRICE_QUOTED'
              WHERE i.OccurredAtUtc>=@s AND i.OccurredAtUtc<@e
              GROUP BY i.SellerKey
            )
            SELECT d.SellerKey,d.DisplayName,d.Extensions,
              HandledCalls=ISNULL(c.HandledCalls,0),
              InboundAnswered=ISNULL(c.InboundAnswered,0),
              OutboundCalls=ISNULL(c.OutboundCalls,0),
              AnsweredCalls=ISNULL(c.AnsweredCalls,0),
              Interactions=ISNULL(i.Interactions,0),
              MissingInteractions=ISNULL(c.MissingInteractions,0),
              QualityPercent=CASE WHEN ISNULL(c.HandledCalls,0)=0 THEN 100
                ELSE CONVERT(int,ROUND((c.HandledCalls-c.MissingInteractions)*100.0/c.HandledCalls,0)) END,
              TalkSeconds=ISNULL(c.TalkSeconds,0),
              AverageTalkSeconds=ISNULL(c.AverageTalkSeconds,0),
              FollowUps=ISNULL(i.FollowUps,0),Quotes=ISNULL(i.Quotes,0),
              Orders=ISNULL(i.Orders,0),Lost=ISNULL(i.Lost,0)
            FROM SellerDirectory d
            LEFT JOIN CallPerformance c ON c.SellerKey=d.SellerKey
            LEFT JOIN InteractionPerformance i ON i.SellerKey=d.SellerKey
            ORDER BY QualityPercent DESC,AnsweredCalls DESC,Orders DESC,d.DisplayName;
            """;
        var rows = new List<SellerPerformanceRow>();
        await using var connection = await OpenConnection(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        command.Parameters.Add("@s", SqlDbType.DateTime2).Value = TehranClock.ToUtc(startDate.Date);
        command.Parameters.Add("@e", SqlDbType.DateTime2).Value = TehranClock.ToUtc(endDate.Date.AddDays(1));
        command.Parameters.Add("@ext", SqlDbType.NVarChar, 20).Value =
            string.IsNullOrWhiteSpace(extension) ? "all" : extension.Trim();
        await using var reader = await ExecuteReader(command, "SellerPerformance", ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new SellerPerformanceRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5),
                reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8),
                reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11),
                reader.GetInt32(12), reader.GetInt32(13), reader.GetInt32(14),
                reader.GetInt32(15)));
        return rows;
    }

    public async Task<IReadOnlyList<SellerDailyActivityRow>> SellerDailyActivity(
        DateTime startDate, DateTime endDate, CancellationToken ct)
    {
        const string sql = """
            ;WITH Directory AS
            (
              SELECT u.SellerKey,u.DisplayName,Extensions=STRING_AGG(CONVERT(nvarchar(max),ue.Extension),N'، ')
              FROM dbo.SellerUsers u JOIN dbo.SellerUserExtensions ue ON ue.SellerUserId=u.Id
              WHERE u.IsActive=1 GROUP BY u.SellerKey,u.DisplayName
            ),
            Raw AS
            (
              SELECT CallKey=COALESCE(NULLIF(LinkedId,N''),NULLIF(UniqueId,N''),CONVERT(nvarchar(30),RawCDRId)),
                EventAtUtc=Calldate,CustomerPhone=CASE WHEN LEN(Src)>4 THEN Src WHEN LEN(Dst)>4 THEN Dst END,
                Answered=CASE WHEN Disposition=N'ANSWERED' OR ISNULL(Billsec,0)>0 THEN 1 ELSE 0 END,
                AnsweredExtension=CASE WHEN (Disposition=N'ANSWERED' OR ISNULL(Billsec,0)>0) AND Dst LIKE N'[0-9][0-9][0-9]' THEN Dst
                  WHEN (Disposition=N'ANSWERED' OR ISNULL(Billsec,0)>0) AND Src LIKE N'[0-9][0-9][0-9]' THEN Src END,
                OutboundExtension=CASE WHEN (Dcontext LIKE N'%from-internal%' OR Dcontext LIKE N'%outbound%') AND Src LIKE N'[0-9][0-9][0-9]' THEN Src END,
                IsInbound=CASE WHEN NULLIF(Did,N'') IS NOT NULL OR Dcontext LIKE N'%from-trunk%' THEN 1 ELSE 0 END,
                IsOutbound=CASE WHEN Dcontext LIKE N'%from-internal%' OR Dcontext LIKE N'%outbound%' THEN 1 ELSE 0 END
              FROM dbo.RawCDR WHERE ReceivedAtUtc>=@s AND ReceivedAtUtc<@e
            ),
            Calls AS
            (
              SELECT CallKey,EventAtUtc=MIN(EventAtUtc),CustomerPhone=MAX(CustomerPhone),Answered=MAX(Answered),
                IsInbound=MAX(IsInbound),IsOutbound=MAX(IsOutbound),AnsweredExtension=MAX(AnsweredExtension),OutboundExtension=MAX(OutboundExtension)
              FROM Raw GROUP BY CallKey
            ),
            CallOwnership AS
            (
              SELECT c.*,u.SellerKey,
                HasInteraction=CASE WHEN EXISTS
                  (SELECT 1 FROM dbo.SellerInteractions i WHERE i.SellerKey=u.SellerKey AND
                    ((i.CallLinkedId=c.CallKey) OR (dbo.NormalizeIranPhone(i.CustomerPhone)=dbo.NormalizeIranPhone(c.CustomerPhone)
                      AND i.OccurredAtUtc BETWEEN DATEADD(minute,-15,c.EventAtUtc) AND DATEADD(hour,4,c.EventAtUtc)))) THEN 1 ELSE 0 END
              FROM Calls c JOIN dbo.SellerUserExtensions ue ON ue.Extension=CASE WHEN c.IsInbound=1 THEN c.AnsweredExtension ELSE c.OutboundExtension END
                JOIN dbo.SellerUsers u ON u.Id=ue.SellerUserId AND u.IsActive=1
            ),
            CallDaily AS
            (
              SELECT DayUtc=CONVERT(date,DATEADD(MINUTE,210,c.EventAtUtc)),c.SellerKey,
                UniqueCalls=COUNT(*),AnsweredCalls=SUM(c.Answered),MissedCalls=SUM(CASE WHEN c.Answered=0 THEN 1 ELSE 0 END),
                UnregisteredResults=SUM(CASE WHEN c.Answered=1 AND c.HasInteraction=0 THEN 1 ELSE 0 END)
              FROM CallOwnership c
              GROUP BY CONVERT(date,DATEADD(MINUTE,210,c.EventAtUtc)),c.SellerKey
            ),
            ActionLinks AS (SELECT DISTINCT InteractionId FROM dbo.SellerInteractionActions WHERE ActionCode=N'PRICE_QUOTED'),
            InteractionDaily AS
            (
              SELECT DayUtc=CONVERT(date,DATEADD(MINUTE,210,i.OccurredAtUtc)),i.SellerKey,Interactions=COUNT(*),
                Quotes=COUNT(a.InteractionId),FollowUps=SUM(CASE WHEN i.Outcome=N'FOLLOW_UP' THEN 1 ELSE 0 END),
                Orders=SUM(CASE WHEN i.Outcome=N'ORDER' THEN 1 ELSE 0 END),Lost=SUM(CASE WHEN i.Outcome=N'LOST' THEN 1 ELSE 0 END)
              FROM dbo.SellerInteractions i LEFT JOIN ActionLinks a ON a.InteractionId=i.Id
              WHERE i.OccurredAtUtc>=@s AND i.OccurredAtUtc<@e
              GROUP BY CONVERT(date,DATEADD(MINUTE,210,i.OccurredAtUtc)),i.SellerKey
            ),
            Keys AS (SELECT DayUtc,SellerKey FROM CallDaily UNION SELECT DayUtc,SellerKey FROM InteractionDaily)
            SELECT k.DayUtc,k.SellerKey,d.DisplayName,d.Extensions,
              ISNULL(c.UniqueCalls,0),ISNULL(c.AnsweredCalls,0),ISNULL(c.MissedCalls,0),ISNULL(c.UnregisteredResults,0),
              ISNULL(i.Interactions,0),ISNULL(i.Quotes,0),ISNULL(i.FollowUps,0),ISNULL(i.Orders,0),ISNULL(i.Lost,0)
            FROM Keys k JOIN Directory d ON d.SellerKey=k.SellerKey
              LEFT JOIN CallDaily c ON c.DayUtc=k.DayUtc AND c.SellerKey=k.SellerKey
              LEFT JOIN InteractionDaily i ON i.DayUtc=k.DayUtc AND i.SellerKey=k.SellerKey
            ORDER BY k.DayUtc DESC,d.DisplayName;
            """;
        var rows = new List<SellerDailyActivityRow>();
        await using var connection = await OpenConnection(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 45 };
        AddRangeParameters(command, startDate, endDate, null);
        await using var reader = await ExecuteReader(command, "SellerDailyActivity", ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new SellerDailyActivityRow(reader.GetDateTime(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8),
                reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11), reader.GetInt32(12)));
        return rows;
    }

    public async Task<SellerActivityPage> SellerActivities(
        DateTime startDate, DateTime endDate, string? sellerKey, int page, int pageSize, CancellationToken ct)
    {
        const string sql = """
            ;WITH ActionLinks AS (SELECT DISTINCT InteractionId FROM dbo.SellerInteractionActions WHERE ActionCode=N'PRICE_QUOTED'),
            Activities AS
            (
              SELECT EventAtUtc=i.OccurredAtUtc,N'INTERACTION' EventType,i.SellerKey,i.SellerDisplayName,i.CustomerPhone,
                CustomerName=COALESCE(NULLIF(d.DisplayName,N''),NULLIF(d.CompanyName,N''),N'ثبت‌نشده'),
                Status=N'تعامل ثبت‌شده',i.Outcome,
                Details=CONCAT(CASE WHEN a.InteractionId IS NOT NULL THEN N'قیمت اعلام شد · ' ELSE N'' END,COALESCE(NULLIF(i.Note,N''),N'بدون یادداشت')),
                i.CallLinkedId,RequiresFollowUp=CONVERT(bit,CASE WHEN i.Outcome=N'FOLLOW_UP' THEN 1 ELSE 0 END)
              FROM dbo.SellerInteractions i
              LEFT JOIN ActionLinks a ON a.InteractionId=i.Id
              OUTER APPLY (SELECT TOP(1) DisplayName,CompanyName FROM dbo.CustomerPhoneDirectory WHERE NormalizedPhone=dbo.NormalizeIranPhone(i.CustomerPhone) ORDER BY IsVerified DESC,IdentityId) d
              WHERE i.OccurredAtUtc>=@s AND i.OccurredAtUtc<@e AND (@seller=N'all' OR i.SellerKey=@seller)
            )
            SELECT Total=COUNT(*) OVER(),EventAtUtc,EventType,SellerKey,SellerDisplayName,CustomerPhone,CustomerName,Status,Outcome,Details,CallLinkedId,RequiresFollowUp
            FROM Activities ORDER BY EventAtUtc DESC
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
            """;
        await using var connection = await OpenConnection(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        AddRangeParameters(command, startDate, endDate, null);
        command.Parameters.Add("@seller", SqlDbType.NVarChar, 80).Value = string.IsNullOrWhiteSpace(sellerKey) ? "all" : sellerKey.Trim();
        command.Parameters.Add("@skip", SqlDbType.Int).Value = (Math.Max(1, page) - 1) * Math.Clamp(pageSize, 1, 200);
        command.Parameters.Add("@take", SqlDbType.Int).Value = Math.Clamp(pageSize, 1, 200);
        await using var reader = await ExecuteReader(command, "SellerActivities", ct);
        var total = 0; var rows = new List<SellerActivityRow>();
        while (await reader.ReadAsync(ct))
        {
            total = reader.GetInt32(0);
            rows.Add(new SellerActivityRow(reader.GetDateTime(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),
                GetString(reader,"CustomerPhone"),GetString(reader,"CustomerName"),reader.GetString(7),GetString(reader,"Outcome"),
                GetString(reader,"Details"),GetString(reader,"CallLinkedId"),GetBool(reader,"RequiresFollowUp")));
        }
        return new SellerActivityPage(total, rows);
    }

    private async Task<SqlConnection> OpenConnection(CancellationToken ct)
    {
        var connection = new SqlConnection(_cs);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static SqlCommand CreateCommand(string sql, SqlConnection connection)
        => new(sql, connection) { CommandTimeout = 90 };

    private async Task<SqlDataReader> ExecuteReader(SqlCommand command, string operation, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var reader = await command.ExecuteReaderAsync(ct);
            _logger.LogInformation("SQL success. Operation={Operation} DurationMs={DurationMs}", operation, sw.ElapsedMilliseconds);
            return reader;
        }
        catch (Exception ex)
        {
            LogSqlFailure(ex, operation, command, sw.ElapsedMilliseconds);
            throw;
        }
    }

    private async Task<object?> ExecuteScalar(SqlCommand command, string operation, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await command.ExecuteScalarAsync(ct);
            _logger.LogInformation("SQL success. Operation={Operation} DurationMs={DurationMs}", operation, sw.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            LogSqlFailure(ex, operation, command, sw.ElapsedMilliseconds);
            throw;
        }
    }

    private void LogSqlFailure(Exception ex, string operation, SqlCommand command, long durationMs)
    {
        var parameters = string.Join(", ", command.Parameters.Cast<SqlParameter>()
            .Select(p => $"{p.ParameterName}={FormatParameter(p.Value)}"));

        _logger.LogError(ex,
            "SQL failed. Operation={Operation} DurationMs={DurationMs} Parameters=[{Parameters}] SQL={Sql}",
            operation, durationMs, parameters, command.CommandText);
    }

    private static string FormatParameter(object? value)
        => value is null or DBNull ? "NULL" : value is DateTime dt ? dt.ToString("O") : value.ToString() ?? string.Empty;

    private static void AddRangeParameters(
        SqlCommand command, DateTime startDate, DateTime endDate, string? extension)
    {
        var start = TehranClock.ToUtc(startDate.Date);
        var endExclusive = TehranClock.ToUtc(endDate.Date.AddDays(1));

        command.Parameters.Add("@s", SqlDbType.DateTime2).Value = start;
        command.Parameters.Add("@e", SqlDbType.DateTime2).Value = endExclusive;
        command.Parameters.Add("@ext", SqlDbType.NVarChar, 20).Value =
            string.IsNullOrWhiteSpace(extension) ? "all" : extension.Trim();
    }

    private static void AddCallsParameters(
        SqlCommand command, DateTime startDate, DateTime endDate, string? extension,
        string? search, string? status)
    {
        AddRangeParameters(command, startDate, endDate, extension);
        command.Parameters.Add("@q", SqlDbType.NVarChar, 200).Value = (search ?? string.Empty).Trim();
        command.Parameters.Add("@st", SqlDbType.NVarChar, 20).Value =
            string.IsNullOrWhiteSpace(status) ? "all" : status.Trim().ToLowerInvariant();
    }

    private static int GetInt(SqlDataReader reader, string name)
        => reader.IsDBNull(reader.GetOrdinal(name)) ? 0 : Convert.ToInt32(reader[name]);

    private static long GetLong(SqlDataReader reader, string name)
        => reader.IsDBNull(reader.GetOrdinal(name)) ? 0L : Convert.ToInt64(reader[name]);

    private static bool GetBool(SqlDataReader reader, string name)
        => !reader.IsDBNull(reader.GetOrdinal(name)) && Convert.ToBoolean(reader[name]);

    private static DateTime? GetDateTime(SqlDataReader reader, string name)
        => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetDateTime(reader.GetOrdinal(name));

    private static DateTime? GetUtcDateTime(SqlDataReader reader, string name)
        => reader.IsDBNull(reader.GetOrdinal(name)) ? null : TehranClock.AsUtc(reader.GetDateTime(reader.GetOrdinal(name)));

    private static string? GetString(SqlDataReader reader, string name)
        => reader.IsDBNull(reader.GetOrdinal(name)) ? null : Convert.ToString(reader[name]);
}
