using DigiAhan.CDR.Receiver.Models;
using Microsoft.Data.SqlClient;
using System.Data;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class AiAnalysisRepository
{
    private readonly string _connectionString;
    private readonly RecordingAssetRepository _recordings;

    public AiAnalysisRepository(
        IConfiguration configuration,
        RecordingAssetRepository recordings)
    {
        _connectionString = configuration.GetConnectionString("DigiAhanCdr")
            ?? throw new InvalidOperationException("ConnectionStrings:DigiAhanCdr is missing.");
        _recordings = recordings;
    }

    public async Task<bool> IsInstalledAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand("""
            SELECT CASE WHEN OBJECT_ID(N'dbo.AiCallAssessments',N'U') IS NOT NULL
                              AND OBJECT_ID(N'dbo.AiExtractedFacts',N'U') IS NOT NULL
                              AND OBJECT_ID(N'dbo.AiReviewItems',N'U') IS NOT NULL
                        THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END;
            """, connection);
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }

    public async Task SaveAnalysisAsync(
        long runId,
        AiAnalyzeRunRequest request,
        AiAnalysisResult analysis,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var runUpdated = await UpdateRunAndTranscriptAsync(
                connection, transaction, runId, request, ct);
            if (!runUpdated)
                throw new KeyNotFoundException($"AI pipeline run {runId} was not found.");

            var assessmentId = await UpsertAssessmentAsync(
                connection, transaction, runId, request, analysis, ct);

            await ReplaceFactsAsync(connection, transaction, assessmentId, analysis.Facts, ct);
            await ReplaceReviewsAsync(connection, transaction, assessmentId, analysis.ReviewItems, ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<AiCallListItem>> ListCallsAsync(
        string? search,
        string? audioClass,
        string? reviewStatus,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        audioClass = string.IsNullOrWhiteSpace(audioClass) ? null : audioClass.Trim().ToUpperInvariant();
        reviewStatus = string.IsNullOrWhiteSpace(reviewStatus) ? null : reviewStatus.Trim().ToUpperInvariant();

        await using var connection = await OpenAsync(ct);
        const string sql = """
            SELECT
                c.LogicalCallId,r.RunId,c.CallKey,c.StartedAt,c.LegCount,c.RecordingFile,
                r.RunStatus,a.AudioClass,a.Direction,a.InternalExtension,a.Confidence,a.Summary,
                ISNULL(f.FactCount,0) AS FactCount,ISNULL(v.OpenReviewCount,0) AS OpenReviewCount
            FROM dbo.AiLogicalCalls c
            CROSS APPLY
            (
                SELECT TOP(1) x.RunId,x.RunStatus
                FROM dbo.AiPipelineRuns x
                WHERE x.LogicalCallId=c.LogicalCallId
                ORDER BY x.RunNumber DESC
            ) r
            LEFT JOIN dbo.AiCallAssessments a ON a.RunId=r.RunId
            OUTER APPLY
            (
                SELECT COUNT(*) AS FactCount FROM dbo.AiExtractedFacts x
                WHERE x.AssessmentId=a.AssessmentId
            ) f
            OUTER APPLY
            (
                SELECT COUNT(*) AS OpenReviewCount FROM dbo.AiReviewItems x
                WHERE x.AssessmentId=a.AssessmentId AND x.ReviewStatus=N'OPEN'
            ) v
            LEFT JOIN dbo.AiTranscripts t ON t.RunId=r.RunId
            WHERE (@class IS NULL OR a.AudioClass=@class)
              AND (@review IS NULL OR EXISTS
                  (SELECT 1 FROM dbo.AiReviewItems q
                   WHERE q.AssessmentId=a.AssessmentId AND q.ReviewStatus=@review))
              AND (@search IS NULL OR c.CallKey LIKE N'%'+@search+N'%'
                   OR c.RecordingFile LIKE N'%'+@search+N'%'
                   OR a.Summary LIKE N'%'+@search+N'%'
                   OR t.TranscriptText LIKE N'%'+@search+N'%'
                   OR EXISTS (SELECT 1 FROM dbo.AiExtractedFacts s
                              WHERE s.AssessmentId=a.AssessmentId
                                AND (s.RawValue LIKE N'%'+@search+N'%'
                                  OR s.NormalizedValue LIKE N'%'+@search+N'%')))
            ORDER BY c.StartedAt DESC,c.LogicalCallId DESC
            OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;
            """;
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        AddNullable(command, "@search", SqlDbType.NVarChar, 200, search);
        AddNullable(command, "@class", SqlDbType.NVarChar, 40, audioClass);
        AddNullable(command, "@review", SqlDbType.NVarChar, 20, reviewStatus);
        command.Parameters.Add("@offset", SqlDbType.Int).Value = (page - 1) * pageSize;
        command.Parameters.Add("@take", SqlDbType.Int).Value = pageSize;

        var result = new List<AiCallListItem>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadCall(reader));
        return result;
    }

    public async Task<AiCallDetail?> GetCallAsync(long logicalCallId, CancellationToken ct)
    {
        var calls = await GetSingleCallAsync(logicalCallId, ct);
        if (calls is null) return null;

        await using var connection = await OpenAsync(ct);
        string? transcript = null;
        string? segments = null;
        long? assessmentId = null;
        await using (var command = new SqlCommand("""
            SELECT t.TranscriptText,t.SegmentsJson,a.AssessmentId
            FROM dbo.AiPipelineRuns r
            LEFT JOIN dbo.AiTranscripts t ON t.RunId=r.RunId
            LEFT JOIN dbo.AiCallAssessments a ON a.RunId=r.RunId
            WHERE r.RunId=@runId;
            """, connection))
        {
            command.Parameters.Add("@runId", SqlDbType.BigInt).Value = calls.RunId;
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                transcript = GetString(reader, 0);
                segments = GetString(reader, 1);
                assessmentId = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            }
        }

        var facts = new List<AiFactView>();
        var reviews = new List<AiReviewView>();
        if (assessmentId.HasValue)
        {
            await using (var command = new SqlCommand("""
                SELECT FactId,FactType,RawValue,NormalizedValue,Unit,StartSeconds,EndSeconds,Confidence,ReviewStatus
                FROM dbo.AiExtractedFacts WHERE AssessmentId=@id ORDER BY StartSeconds,FactId;
                """, connection))
            {
                command.Parameters.Add("@id", SqlDbType.BigInt).Value = assessmentId.Value;
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    facts.Add(new AiFactView(reader.GetInt64(0),reader.GetString(1),GetString(reader,2),
                        GetString(reader,3),GetString(reader,4),GetDecimal(reader,5),GetDecimal(reader,6),
                        reader.GetDecimal(7),reader.GetString(8)));
            }
            reviews.AddRange(await ReadReviewsAsync(connection, assessmentId.Value, logicalCallId, null, ct));
        }
        var recording = await _recordings.GetForCallAsync(logicalCallId, ct);
        return new AiCallDetail(calls, transcript, segments, recording, facts, reviews);
    }

    public async Task<IReadOnlyList<AiReviewView>> ListReviewsAsync(
        string? status, int take, CancellationToken ct)
    {
        var normalizedStatus = string.IsNullOrWhiteSpace(status)
            ? "OPEN"
            : status.Trim().ToUpperInvariant();
        await using var connection = await OpenAsync(ct);
        return await ReadReviewsAsync(connection, null, null,
            normalizedStatus == "ALL" ? null : normalizedStatus,
            ct, Math.Clamp(take, 1, 500));
    }

    public async Task<bool> ResolveReviewAsync(
        long reviewItemId, AiReviewResolutionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Status))
            throw new ArgumentException("Review status is required.");
        var status = request.Status.Trim().ToUpperInvariant();
        if (status is not ("CONFIRMED" or "CORRECTED" or "REJECTED" or "DEFERRED"))
            throw new ArgumentException("Invalid review status.");
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand("""
            UPDATE dbo.AiReviewItems
            SET ReviewStatus=@status,Resolution=@resolution,ResolvedBy=@by,
                ResolvedAtUtc=SYSUTCDATETIME(),UpdatedAtUtc=SYSUTCDATETIME()
            WHERE ReviewItemId=@id;
            """, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = reviewItemId;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 20).Value = status;
        AddNullable(command, "@resolution", SqlDbType.NVarChar, 2000, request.Resolution);
        AddNullable(command, "@by", SqlDbType.NVarChar, 100, request.ResolvedBy);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private async Task<AiCallListItem?> GetSingleCallAsync(long id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand("""
            SELECT TOP(1)
                c.LogicalCallId,r.RunId,c.CallKey,c.StartedAt,c.LegCount,c.RecordingFile,
                r.RunStatus,a.AudioClass,a.Direction,a.InternalExtension,a.Confidence,a.Summary,
                (SELECT COUNT(*) FROM dbo.AiExtractedFacts f WHERE f.AssessmentId=a.AssessmentId),
                (SELECT COUNT(*) FROM dbo.AiReviewItems v WHERE v.AssessmentId=a.AssessmentId AND v.ReviewStatus=N'OPEN')
            FROM dbo.AiLogicalCalls c
            CROSS APPLY(SELECT TOP(1) x.RunId,x.RunStatus FROM dbo.AiPipelineRuns x
                        WHERE x.LogicalCallId=c.LogicalCallId ORDER BY x.RunNumber DESC) r
            LEFT JOIN dbo.AiCallAssessments a ON a.RunId=r.RunId
            WHERE c.LogicalCallId=@id;
            """, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadCall(reader) : null;
    }

    private static AiCallListItem ReadCall(SqlDataReader r) => new(
        r.GetInt64(0),r.GetInt64(1),r.GetString(2),r.IsDBNull(3)?null:r.GetDateTime(3),
        r.GetInt32(4),GetString(r,5),r.GetString(6),GetString(r,7),GetString(r,8),GetString(r,9),
        GetDecimal(r,10),GetString(r,11),r.GetInt32(12),r.GetInt32(13));

    private static async Task<bool> UpdateRunAndTranscriptAsync(
        SqlConnection connection, SqlTransaction transaction, long runId,
        AiAnalyzeRunRequest request, CancellationToken ct)
    {
        await using var command = new SqlCommand("""
            UPDATE dbo.AiPipelineRuns SET RunStatus=N'COMPLETED',Engine=@engine,ModelName=@model,
                CompletedAtUtc=SYSUTCDATETIME(),UpdatedAtUtc=SYSUTCDATETIME(),LastError=NULL
            WHERE RunId=@runId;
            IF @@ROWCOUNT=0 BEGIN SELECT CAST(0 AS bit); RETURN; END;
            MERGE dbo.AiTranscripts AS target
            USING(SELECT @runId AS RunId) AS source ON source.RunId=target.RunId
            WHEN MATCHED THEN UPDATE SET LanguageCode=@language,TranscriptText=@text,
                SegmentsJson=@segments,AudioDurationSeconds=@duration,ProcessingSeconds=@processing
            WHEN NOT MATCHED THEN INSERT
                (RunId,LanguageCode,TranscriptText,SegmentsJson,AudioDurationSeconds,ProcessingSeconds)
                VALUES(@runId,@language,@text,@segments,@duration,@processing);
            SELECT CAST(1 AS bit);
            """, connection, transaction);
        command.Parameters.Add("@runId", SqlDbType.BigInt).Value = runId;
        AddNullable(command,"@engine",SqlDbType.NVarChar,100,request.Engine);
        AddNullable(command,"@model",SqlDbType.NVarChar,200,request.ModelName);
        AddNullable(command,"@language",SqlDbType.NVarChar,16,request.LanguageCode);
        command.Parameters.Add("@text",SqlDbType.NVarChar,-1).Value=request.TranscriptText;
        AddNullable(command,"@segments",SqlDbType.NVarChar,-1,request.SegmentsJson);
        AddNullableDecimal(command,"@duration",request.AudioDurationSeconds);
        AddNullableDecimal(command,"@processing",request.ProcessingSeconds);
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }

    private static async Task<long> UpsertAssessmentAsync(
        SqlConnection connection, SqlTransaction transaction, long runId,
        AiAnalyzeRunRequest request, AiAnalysisResult analysis, CancellationToken ct)
    {
        await using var command = new SqlCommand("""
            MERGE dbo.AiCallAssessments AS target
            USING(SELECT @runId AS RunId) AS source ON source.RunId=target.RunId
            WHEN MATCHED THEN UPDATE SET AudioClass=@class,HasHumanSpeech=@speech,
                IsBusinessRelevant=@business,Direction=@direction,InternalExtension=@extension,
                QueueName=@queue,Confidence=@confidence,SpeechSeconds=@speechSeconds,
                Summary=@summary,StructuredJson=@json,AnalyzerVersion=@version,UpdatedAtUtc=SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (RunId,AudioClass,HasHumanSpeech,IsBusinessRelevant,Direction,InternalExtension,
                 QueueName,Confidence,SpeechSeconds,Summary,StructuredJson,AnalyzerVersion)
                VALUES(@runId,@class,@speech,@business,@direction,@extension,@queue,@confidence,
                       @speechSeconds,@summary,@json,@version);
            SELECT AssessmentId FROM dbo.AiCallAssessments WHERE RunId=@runId;
            """, connection, transaction);
        command.Parameters.Add("@runId",SqlDbType.BigInt).Value=runId;
        command.Parameters.Add("@class",SqlDbType.NVarChar,40).Value=analysis.AudioClass;
        command.Parameters.Add("@speech",SqlDbType.Bit).Value=analysis.HasHumanSpeech;
        command.Parameters.Add("@business",SqlDbType.Bit).Value=analysis.IsBusinessRelevant;
        command.Parameters.Add("@direction",SqlDbType.NVarChar,20).Value=
            NormalizeDirection(request.Direction);
        AddNullable(command,"@extension",SqlDbType.NVarChar,32,request.InternalExtension);
        AddNullable(command,"@queue",SqlDbType.NVarChar,32,request.Queue);
        var confidence=command.Parameters.Add("@confidence",SqlDbType.Decimal);
        confidence.Precision=5;confidence.Scale=4;confidence.Value=analysis.Confidence;
        AddNullableDecimal(command,"@speechSeconds",request.SpeechSeconds);
        command.Parameters.Add("@summary",SqlDbType.NVarChar,2000).Value=analysis.Summary;
        command.Parameters.Add("@json",SqlDbType.NVarChar,-1).Value=analysis.StructuredJson;
        command.Parameters.Add("@version",SqlDbType.NVarChar,100).Value=AiTranscriptAnalyzer.Version;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private static async Task ReplaceFactsAsync(SqlConnection c, SqlTransaction tx, long id,
        IEnumerable<AiExtractedFact> facts, CancellationToken ct)
    {
        await ExecuteDeleteAsync(c,tx,"DELETE dbo.AiExtractedFacts WHERE AssessmentId=@id",id,ct);
        foreach(var fact in facts)
        {
            await using var command=new SqlCommand("""
                INSERT dbo.AiExtractedFacts
                    (AssessmentId,FactType,RawValue,NormalizedValue,Unit,StartSeconds,EndSeconds,
                     Confidence,ReviewStatus,ExtractionVersion)
                VALUES(@id,@type,@raw,@normalized,@unit,@start,@end,@confidence,@review,@version);
                """,c,tx);
            command.Parameters.Add("@id",SqlDbType.BigInt).Value=id;
            command.Parameters.Add("@type",SqlDbType.NVarChar,50).Value=fact.FactType;
            AddNullable(command,"@raw",SqlDbType.NVarChar,1000,fact.RawValue);
            AddNullable(command,"@normalized",SqlDbType.NVarChar,1000,fact.NormalizedValue);
            AddNullable(command,"@unit",SqlDbType.NVarChar,50,fact.Unit);
            AddNullableDecimal(command,"@start",fact.StartSeconds);AddNullableDecimal(command,"@end",fact.EndSeconds);
            var confidence=command.Parameters.Add("@confidence",SqlDbType.Decimal);
            confidence.Precision=5;confidence.Scale=4;confidence.Value=fact.Confidence;
            command.Parameters.Add("@review",SqlDbType.NVarChar,20).Value=fact.ReviewStatus;
            command.Parameters.Add("@version",SqlDbType.NVarChar,100).Value=AiTranscriptAnalyzer.Version;
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task ReplaceReviewsAsync(SqlConnection c, SqlTransaction tx, long id,
        IEnumerable<AiReviewItem> items, CancellationToken ct)
    {
        await ExecuteDeleteAsync(c,tx,"DELETE dbo.AiReviewItems WHERE AssessmentId=@id",id,ct);
        foreach(var item in items)
        {
            await using var command=new SqlCommand("""
                INSERT dbo.AiReviewItems
                    (AssessmentId,Category,Priority,ReasonCode,RawText,StartSeconds,EndSeconds,ReviewStatus)
                VALUES(@id,@category,@priority,@reason,@raw,@start,@end,@status);
                """,c,tx);
            command.Parameters.Add("@id",SqlDbType.BigInt).Value=id;
            command.Parameters.Add("@category",SqlDbType.NVarChar,50).Value=item.Category;
            command.Parameters.Add("@priority",SqlDbType.NVarChar,10).Value=item.Priority;
            command.Parameters.Add("@reason",SqlDbType.NVarChar,100).Value=item.ReasonCode;
            AddNullable(command,"@raw",SqlDbType.NVarChar,2000,item.RawText);
            AddNullableDecimal(command,"@start",item.StartSeconds);AddNullableDecimal(command,"@end",item.EndSeconds);
            command.Parameters.Add("@status",SqlDbType.NVarChar,20).Value=item.Status;
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task ExecuteDeleteAsync(SqlConnection c,SqlTransaction tx,string sql,long id,CancellationToken ct)
    { await using var command=new SqlCommand(sql,c,tx);command.Parameters.Add("@id",SqlDbType.BigInt).Value=id;await command.ExecuteNonQueryAsync(ct); }

    private static async Task<List<AiReviewView>> ReadReviewsAsync(SqlConnection c,long? assessmentId,long? logicalCallId,string? status,CancellationToken ct,int take=500)
    {
        await using var command=new SqlCommand("""
            SELECT TOP(@take) v.ReviewItemId,c.LogicalCallId,v.Category,v.Priority,v.ReasonCode,
                   v.RawText,v.StartSeconds,v.EndSeconds,v.ReviewStatus,v.Resolution,v.CreatedAtUtc
            FROM dbo.AiReviewItems v
            JOIN dbo.AiCallAssessments a ON a.AssessmentId=v.AssessmentId
            JOIN dbo.AiPipelineRuns r ON r.RunId=a.RunId
            JOIN dbo.AiLogicalCalls c ON c.LogicalCallId=r.LogicalCallId
            WHERE (@assessment IS NULL OR v.AssessmentId=@assessment)
              AND (@logicalCall IS NULL OR c.LogicalCallId=@logicalCall)
              AND (@status IS NULL OR v.ReviewStatus=@status)
            ORDER BY CASE v.Priority WHEN N'HIGH' THEN 0 WHEN N'MEDIUM' THEN 1 ELSE 2 END,
                     v.CreatedAtUtc;
            """,c);
        command.Parameters.Add("@take",SqlDbType.Int).Value=take;
        command.Parameters.Add("@assessment",SqlDbType.BigInt).Value=assessmentId.HasValue?assessmentId.Value:DBNull.Value;
        command.Parameters.Add("@logicalCall",SqlDbType.BigInt).Value=logicalCallId.HasValue?logicalCallId.Value:DBNull.Value;
        AddNullable(command,"@status",SqlDbType.NVarChar,20,status);
        var result=new List<AiReviewView>();await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct)) result.Add(new AiReviewView(reader.GetInt64(0),reader.GetInt64(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),GetString(reader,5),GetDecimal(reader,6),GetDecimal(reader,7),reader.GetString(8),GetString(reader,9),reader.GetDateTime(10)));
        return result;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct){var c=new SqlConnection(_connectionString);await c.OpenAsync(ct);return c;}
    private static string NormalizeDirection(string? value){var v=value?.Trim().ToUpperInvariant();return v is "INBOUND" or "OUTBOUND" or "INTERNAL"?v:"UNKNOWN";}
    private static string? GetString(SqlDataReader r,int i)=>r.IsDBNull(i)?null:r.GetString(i);
    private static decimal? GetDecimal(SqlDataReader r,int i)=>r.IsDBNull(i)?null:r.GetDecimal(i);
    private static void AddNullable(SqlCommand c,string n,SqlDbType t,int size,string? v){var p=c.Parameters.Add(n,t,size);p.Value=string.IsNullOrWhiteSpace(v)?DBNull.Value:v;}
    private static void AddNullableDecimal(SqlCommand c,string n,decimal? v){var p=c.Parameters.Add(n,SqlDbType.Decimal);p.Precision=12;p.Scale=3;p.Value=v.HasValue?v.Value:DBNull.Value;}
}
