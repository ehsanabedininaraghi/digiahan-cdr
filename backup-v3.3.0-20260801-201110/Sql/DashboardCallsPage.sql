WITH Raw AS
(
    SELECT
        r.*,
        K=COALESCE(NULLIF(r.LinkedId,N''),NULLIF(r.UniqueId,N''),CONVERT(nvarchar(30),r.RawCDRId))
    FROM dbo.RawCDR r
    WHERE r.Calldate>=@s AND r.Calldate<@e
),
GroupPhone AS
(
    SELECT
        K,
        ExternalPhone=MAX(CASE
            WHEN (NULLIF(Did,N'') IS NOT NULL OR Dcontext LIKE N'%from-trunk%')
                 AND LEN(ISNULL(Src,N''))>4 THEN Src
            WHEN (Dcontext LIKE N'%from-internal%' OR Dcontext LIKE N'%outbound%')
                 AND LEN(ISNULL(Dst,N''))>4 THEN Dst
            WHEN LEN(ISNULL(Src,N''))>4 THEN Src
            WHEN LEN(ISNULL(Dst,N''))>4 THEN Dst
        END)
    FROM Raw
    GROUP BY K
),
CallData AS
(
    SELECT
        r.RawCDRId,
        r.Calldate,
        r.Src,
        r.Dst,
        Direction=CASE
            WHEN NULLIF(r.Did,N'') IS NOT NULL OR r.Dcontext LIKE N'%from-trunk%' THEN N'inbound'
            WHEN r.Dcontext LIKE N'%from-internal%' OR r.Dcontext LIKE N'%outbound%' THEN N'outbound'
            ELSE N'unknown'
        END,
        r.Disposition,
        ISNULL(r.Duration,0) AS Duration,
        ISNULL(r.Billsec,0) AS Billsec,
        r.RecordingFile,
        r.LinkedId,
        r.UniqueId,
        r.Did,
        r.Dcontext,
        gp.ExternalPhone AS CustomerPhone
    FROM Raw r
    INNER JOIN GroupPhone gp ON gp.K=r.K
    WHERE @ext=N'all' OR r.Src=@ext OR r.Dst=@ext
),
Resolved AS
(
    SELECT
        c.*,
        d.DidarContactCode,
        d.FullName,
        d.CompanyName,
        d.OwnerName,
        CONVERT(bit,CASE
            WHEN dbo.NormalizeIranPhone(c.CustomerPhone) IS NOT NULL
                 AND d.DidarContactCode IS NULL THEN 1
            ELSE 0
        END) AS IsNewCustomer
    FROM CallData c
    OUTER APPLY
    (
        SELECT TOP(1)
            dc.DidarContactCode,
            dc.FullName,
            dc.CompanyName,
            dc.OwnerName
        FROM dbo.DidarContactPhones p
        INNER JOIN dbo.DidarContacts dc
            ON dc.DidarContactCode=p.DidarContactCode
           AND dc.IsDeleted=0
        WHERE p.NormalizedPhone=dbo.NormalizeIranPhone(c.CustomerPhone)
        ORDER BY p.IsPrimary DESC,p.Id ASC
    ) d
),
Filtered AS
(
    SELECT *
    FROM Resolved
    WHERE
        (@q=N''
         OR Src LIKE N'%'+@q+N'%'
         OR Dst LIKE N'%'+@q+N'%'
         OR Did LIKE N'%'+@q+N'%'
         OR LinkedId LIKE N'%'+@q+N'%'
         OR CustomerPhone LIKE N'%'+@q+N'%'
         OR FullName LIKE N'%'+@q+N'%'
         OR CompanyName LIKE N'%'+@q+N'%'
         OR OwnerName LIKE N'%'+@q+N'%')
        AND (@st=N'all'
         OR (@st=N'answered' AND (Disposition=N'ANSWERED' OR Billsec>0))
         OR (@st=N'missed' AND NOT(Disposition=N'ANSWERED' OR Billsec>0))
         OR (@st=N'new' AND IsNewCustomer=1)
         OR (@st=N'known' AND IsNewCustomer=0 AND DidarContactCode IS NOT NULL))
)
,
Paged AS
(
    SELECT
        RawCDRId,Calldate,Src,Dst,Direction,Disposition,Duration,Billsec,
        RecordingFile,LinkedId,UniqueId,Did,Dcontext,CustomerPhone,
        CASE
            WHEN DidarContactCode IS NOT NULL
                 AND NULLIF(LTRIM(RTRIM(FullName)),N'') IS NOT NULL THEN FullName
            WHEN DidarContactCode IS NOT NULL
                 AND NULLIF(LTRIM(RTRIM(CompanyName)),N'') IS NOT NULL THEN CompanyName
            WHEN DidarContactCode IS NOT NULL THEN N'مخاطب دیدار'
            WHEN dbo.NormalizeIranPhone(CustomerPhone) IS NOT NULL THEN N'مشتری جدید'
            ELSE NULL
        END AS CustomerName,
        CompanyName,OwnerName,DidarContactCode,IsNewCustomer,
        ROW_NUMBER() OVER(ORDER BY Calldate DESC,RawCDRId DESC) AS RowNum
    FROM Filtered
)
SELECT
    RawCDRId,Calldate,Src,Dst,Direction,Disposition,Duration,Billsec,
    RecordingFile,LinkedId,UniqueId,Did,Dcontext,CustomerPhone,
    CustomerName,CompanyName,OwnerName,DidarContactCode,IsNewCustomer
FROM Paged
WHERE RowNum BETWEEN @rowStart AND @rowEnd
ORDER BY RowNum;