WITH Raw AS
(
    SELECT r.*, CallKey=COALESCE(NULLIF(r.LinkedId,N''),NULLIF(r.UniqueId,N''),CONVERT(nvarchar(30),r.RawCDRId))
    FROM dbo.RawCDR r
    WHERE r.Calldate>=@s AND r.Calldate<@e
),
Eligible AS
(
    SELECT * FROM Raw WHERE @ext=N'all' OR Src=@ext OR Dst=@ext
),
Grouped AS
(
    SELECT
        CallKey,
        FirstId=MIN(RawCDRId),
        StartedAt=MIN(Calldate),
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
    SELECT g.*,d.DidarContactCode,d.FullName,d.CompanyName,d.OwnerName,
           CONVERT(bit,CASE WHEN dbo.NormalizeIranPhone(g.CustomerPhone) IS NOT NULL AND d.DidarContactCode IS NULL THEN 1 ELSE 0 END) AS IsNewCustomer
    FROM Grouped g
    OUTER APPLY
    (
        SELECT TOP(1) dc.DidarContactCode,dc.FullName,dc.CompanyName,dc.OwnerName
        FROM dbo.DidarContactPhones p
        INNER JOIN dbo.DidarContacts dc ON dc.DidarContactCode=p.DidarContactCode AND dc.IsDeleted=0
        WHERE p.NormalizedPhone=dbo.NormalizeIranPhone(g.CustomerPhone)
        ORDER BY p.IsPrimary DESC,p.Id ASC
    ) d
),
Filtered AS
(
    SELECT * FROM Resolved
    WHERE (@q=N'' OR CustomerPhone LIKE N'%'+@q+N'%' OR FullName LIKE N'%'+@q+N'%' OR CompanyName LIKE N'%'+@q+N'%' OR OwnerName LIKE N'%'+@q+N'%' OR LinkedId LIKE N'%'+@q+N'%' OR UniqueId LIKE N'%'+@q+N'%' OR AnsweredExtension LIKE N'%'+@q+N'%')
      AND (@st=N'all' OR (@st=N'answered' AND Answered=1) OR (@st=N'missed' AND Answered=0) OR (@st=N'new' AND IsNewCustomer=1) OR (@st=N'known' AND IsNewCustomer=0 AND DidarContactCode IS NOT NULL))
)
,
Paged AS
(
    SELECT
        FirstId AS RawCDRId,StartedAt AS Calldate,
        CASE WHEN Direction=N'outbound' THEN AnsweredExtension ELSE CustomerPhone END AS Src,
        CASE WHEN Direction=N'inbound' THEN AnsweredExtension ELSE CustomerPhone END AS Dst,
        Direction,
        CASE WHEN Answered=1 THEN N'ANSWERED' ELSE N'NO ANSWER' END AS Disposition,
        Duration,Billsec,RecordingFile,LinkedId,UniqueId,Did,Dcontext,CustomerPhone,
        CASE
            WHEN DidarContactCode IS NOT NULL AND NULLIF(LTRIM(RTRIM(FullName)),N'') IS NOT NULL THEN FullName
            WHEN DidarContactCode IS NOT NULL AND NULLIF(LTRIM(RTRIM(CompanyName)),N'') IS NOT NULL THEN CompanyName
            WHEN DidarContactCode IS NOT NULL THEN N'مخاطب دیدار'
            WHEN dbo.NormalizeIranPhone(CustomerPhone) IS NOT NULL THEN N'مشتری جدید'
            ELSE NULL END AS CustomerName,
        CompanyName,OwnerName,DidarContactCode,IsNewCustomer,
        ROW_NUMBER() OVER(ORDER BY StartedAt DESC,FirstId DESC) AS RowNum
    FROM Filtered
)
SELECT RawCDRId,Calldate,Src,Dst,Direction,Disposition,Duration,Billsec,RecordingFile,LinkedId,UniqueId,Did,Dcontext,CustomerPhone,CustomerName,CompanyName,OwnerName,DidarContactCode,IsNewCustomer
FROM Paged
WHERE RowNum BETWEEN @rowStart AND @rowEnd
ORDER BY RowNum;
