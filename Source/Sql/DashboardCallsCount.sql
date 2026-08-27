WITH Raw AS
(
    SELECT r.RawCDRId,r.LinkedId,r.UniqueId,r.ReceivedAtUtc,r.Src,r.Dst,r.Did,r.Dcontext,
           r.Disposition,r.Billsec,r.Duration,r.RecordingFile,
           CallKey=COALESCE(NULLIF(r.LinkedId,N''),NULLIF(r.UniqueId,N''),CONVERT(nvarchar(30),r.RawCDRId))
    FROM dbo.RawCDR r
    WHERE r.ReceivedAtUtc>=@s AND r.ReceivedAtUtc<@e
),
Eligible AS
(
    SELECT * FROM Raw
    WHERE @ext=N'all' OR EXISTS
    (
        SELECT 1 FROM STRING_SPLIT(@ext,N',') x
        WHERE LTRIM(RTRIM(x.value))=Src OR LTRIM(RTRIM(x.value))=Dst
    )
),
Grouped AS
(
    SELECT
        CallKey,
        FirstId=MIN(RawCDRId),
        StartedAt=MIN(ReceivedAtUtc),
        CustomerPhone=MAX(CASE
            WHEN (NULLIF(Did,N'') IS NOT NULL OR Dcontext LIKE N'%from-trunk%') AND LEN(ISNULL(Src,N''))>4 THEN Src
            WHEN (Dcontext LIKE N'%from-internal%' OR Dcontext LIKE N'%outbound%') AND LEN(ISNULL(Dst,N''))>4 THEN Dst
            WHEN LEN(ISNULL(Src,N''))>4 THEN Src
            WHEN LEN(ISNULL(Dst,N''))>4 THEN Dst END),
        Direction=CASE
            WHEN MAX(CASE WHEN NULLIF(Did,N'') IS NOT NULL OR Dcontext LIKE N'%from-trunk%' THEN 1 ELSE 0 END)=1 THEN N'inbound'
            WHEN MAX(CASE WHEN Dcontext LIKE N'%from-internal%' OR Dcontext LIKE N'%outbound%' THEN 1 ELSE 0 END)=1 THEN N'outbound'
            ELSE N'unknown' END,
        Answered=MAX(CASE WHEN Disposition=N'ANSWERED' OR ISNULL(Billsec,0)>0 THEN 1 ELSE 0 END),
        Duration=MAX(ISNULL(Duration,0)),
        Billsec=MAX(ISNULL(Billsec,0)),
        RecordingFile=MAX(NULLIF(RecordingFile,N'')),
        LinkedId=MAX(NULLIF(LinkedId,N'')),
        UniqueId=MAX(NULLIF(UniqueId,N'')),
        Did=MAX(NULLIF(Did,N'')),
        Dcontext=MAX(NULLIF(Dcontext,N'')),
        AnsweredExtension=MAX(CASE
            WHEN (Disposition=N'ANSWERED' OR ISNULL(Billsec,0)>0) AND LEN(ISNULL(Dst,N'')) BETWEEN 3 AND 4 THEN Dst
            WHEN (Disposition=N'ANSWERED' OR ISNULL(Billsec,0)>0) AND LEN(ISNULL(Src,N'')) BETWEEN 3 AND 4 THEN Src END)
    FROM Eligible
    GROUP BY CallKey
),
Resolved AS
(
    SELECT
        g.*,
        d.IdentityId,
        d.DidarContactCode,
        d.DisplayName AS FullName,
        d.CompanyName,
        d.OwnerName,
        d.AccountingDetailCode,
        CONVERT(bit,CASE
            WHEN dbo.NormalizeIranPhone(g.CustomerPhone) IS NOT NULL AND d.IdentityId IS NULL THEN 1
            ELSE 0
        END) AS IsNewCustomer
    FROM Grouped g
    OUTER APPLY
    (
        SELECT TOP(1)
            IdentityId,DidarContactCode,DisplayName,CompanyName,OwnerName,
            AccountingDetailCode,IsVerified
        FROM dbo.CustomerPhoneDirectory
        WHERE NormalizedPhone=dbo.NormalizeIranPhone(g.CustomerPhone)
        ORDER BY IsVerified DESC,IdentityId
    ) d
),
Filtered AS
(
    SELECT * FROM Resolved
    WHERE
        (
            @q=N''
            OR CustomerPhone LIKE N'%'+@q+N'%'
            OR FullName LIKE N'%'+@q+N'%'
            OR CompanyName LIKE N'%'+@q+N'%'
            OR OwnerName LIKE N'%'+@q+N'%'
            OR AccountingDetailCode LIKE N'%'+@q+N'%'
            OR LinkedId LIKE N'%'+@q+N'%'
            OR UniqueId LIKE N'%'+@q+N'%'
            OR AnsweredExtension LIKE N'%'+@q+N'%'
        )
      AND
        (
            @st=N'all'
            OR (@st=N'answered' AND Answered=1)
            OR (@st=N'missed' AND Answered=0)
            OR (@st=N'new' AND IsNewCustomer=1)
            OR (@st=N'known' AND IsNewCustomer=0 AND IdentityId IS NOT NULL)
        )
)
SELECT COUNT(*) AS Total FROM Filtered
OPTION(RECOMPILE);
