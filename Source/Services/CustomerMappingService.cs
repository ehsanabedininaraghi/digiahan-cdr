using DigiAhan.CDR.Receiver.Models;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Security.Cryptography;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class CustomerMappingService
{
    private readonly string _connectionString;
    private readonly string _sourceDatabase;
    private readonly int _fiscalYear;
    private readonly SqlQueryStore _queries;
    private readonly ExcelMappingReader _reader;
    private readonly ILogger<CustomerMappingService> _logger;

    public CustomerMappingService(
        IConfiguration configuration,
        SqlQueryStore queries,
        ExcelMappingReader reader,
        ILogger<CustomerMappingService> logger)
    {
        _connectionString = configuration.GetConnectionString("DigiAhanCdr")
            ?? throw new InvalidOperationException("ConnectionStrings:DigiAhanCdr is required.");
        _sourceDatabase = configuration["Accounting:Database"]?.Trim() ?? "daftar1405";
        _fiscalYear = configuration.GetValue<int?>("Accounting:FiscalYear") ?? 1405;
        _queries = queries;
        _reader = reader;
        _logger = logger;
    }

    public async Task<CustomerMappingImportResult> ImportExcelAsync(
        Stream input,
        string fileName,
        CancellationToken ct)
    {
        await using var memory = new MemoryStream();
        await input.CopyToAsync(memory, ct);
        var bytes = memory.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        await using var connection = await OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);
        var previous = await FindImportByHashAsync(connection, hash, ct);
        if (previous is not null) return previous with { AlreadyImported = true };

        memory.Position = 0;
        var rawRows = _reader.Read(memory);
        var prepared = rawRows.Select(Prepare).ToArray();
        var duplicateCodes = prepared
            .Where(x => x.Code is not null)
            .GroupBy(x => x.Code!, StringComparer.Ordinal)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .ToHashSet(StringComparer.Ordinal);

        var importId = Guid.NewGuid();
        var linked = 0;
        var unmapped = 0;
        var conflicts = 0;
        var invalid = 0;

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            foreach (var row in prepared)
            {
                if (row.Code is null)
                {
                    invalid++;
                    continue;
                }

                if (duplicateCodes.Contains(row.Code))
                {
                    conflicts++;
                    await UpsertMappingAsync(connection, transaction, row.Code, row.Name, row.Phone,
                        null, "CONFLICT", "Accounting code is repeated in the Excel file.", fileName, row.RowNumber, ct);
                    continue;
                }

                if (row.Phone is null)
                {
                    unmapped++;
                    await UpsertMappingAsync(connection, transaction, row.Code, row.Name, null,
                        null, "UNMAPPED", "Telephone is empty or invalid.", fileName, row.RowNumber, ct);
                    continue;
                }

                var identityId = await ResolveIdentityAsync(
                    connection, transaction, row.Phone, row.Name, ct);
                await LinkAccountingAsync(
                    connection, transaction, identityId, row.Code, row.Name, row.Phone, ct);
                await UpsertMappingAsync(connection, transaction, row.Code, row.Name, row.Phone,
                    identityId, "LINKED", null, fileName, row.RowNumber, ct);
                linked++;
            }

            await InsertImportAsync(connection, transaction, importId, fileName, hash,
                rawRows.Count, linked, unmapped, conflicts, invalid, ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }

        _logger.LogInformation(
            "Mapping file imported. ImportId={ImportId} File={FileName} Total={Total} Linked={Linked} Unmapped={Unmapped} Conflicts={Conflicts} Invalid={Invalid}",
            importId, fileName, rawRows.Count, linked, unmapped, conflicts, invalid);
        return new CustomerMappingImportResult(
            importId, fileName, rawRows.Count, linked, unmapped, conflicts, invalid, false);
    }

    public async Task<CustomerMappingSummary> ReconcileAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);
        await DiscoverAccountingCodesAsync(connection, ct);

        const string select = """
            SELECT AccountingCode,CustomerName,NormalizedPhone
            FROM dbo.CustomerAccountingMappings
            WHERE NormalizedPhone IS NOT NULL AND Status IN (N'LINKED',N'UNMAPPED');
            """;
        var rows = new List<(string Code, string? Name, string Phone)>();
        await using (var command = new SqlCommand(select, connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2)));
        }

        foreach (var row in rows)
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            try
            {
                var identity = await ResolveIdentityAsync(connection, transaction, row.Phone, row.Name, ct);
                await LinkAccountingAsync(connection, transaction, identity, row.Code, row.Name, row.Phone, ct);
                await UpsertMappingAsync(connection, transaction, row.Code, row.Name, row.Phone,
                    identity, "LINKED", null, null, null, ct);
                await transaction.CommitAsync(ct);
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        }

        return await GetSummaryAsync(connection, ct);
    }

    public async Task<CustomerMappingSummary> GetSummaryAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);
        return await GetSummaryAsync(connection, ct);
    }

    public async Task<IReadOnlyList<UnmappedAccountingCode>> GetUnmappedAsync(int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 2000);
        await using var connection = await OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);
        var result = new List<UnmappedAccountingCode>();
        var sql = $"""
            SELECT TOP({take}) AccountingCode,CustomerName,Status,ErrorMessage,UpdatedAtUtc
            FROM dbo.CustomerAccountingMappings
            WHERE Status<>N'LINKED'
            ORDER BY CASE Status WHEN N'CONFLICT' THEN 0 WHEN N'UNMAPPED' THEN 1 ELSE 2 END,AccountingCode;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new UnmappedAccountingCode(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetDateTime(4)));
        return result;
    }

    public async Task<IReadOnlyList<PendingInvoiceMapping>> GetPendingInvoiceMappingsAsync(int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 500);
        await using var connection = await OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);
        var sql = $"""
            ;WITH InvoiceCodes AS
            (
                SELECT
                    AccountingCode=RIGHT(N'000000'+REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(COALESCE(NULLIF(i.CustomerDetailCode,N''),i.CustomerShortCode))),N'30/',N''),N'/30',N''),N'/',N''),6),
                    i.CustomerName,i.FactorDate,i.FactorNumber,i.Amount,i.ImportedAtUtc
                FROM dbo.AccountingInvoices i
                WHERE TRY_CONVERT(int,REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(COALESCE(NULLIF(i.CustomerDetailCode,N''),i.CustomerShortCode))),N'30/',N''),N'/30',N''),N'/',N'')) BETWEEN 1 AND 999999
            )
            SELECT TOP({take}) x.AccountingCode,MAX(COALESCE(m.CustomerName,x.CustomerName)),
                   MAX(x.FactorDate),MAX(x.FactorNumber),SUM(ISNULL(x.Amount,0)),COUNT(*),MAX(x.ImportedAtUtc)
            FROM InvoiceCodes x
            LEFT JOIN dbo.CustomerAccountingMappings m ON m.AccountingCode=x.AccountingCode
            WHERE ISNULL(m.Status,N'UNMAPPED')<>N'LINKED'
              AND TRY_CONVERT(int,x.AccountingCode) NOT BETWEEN 40000 AND 49999
              AND TRY_CONVERT(int,x.AccountingCode) NOT BETWEEN 60000 AND 69999
            GROUP BY x.AccountingCode
            ORDER BY MAX(x.ImportedAtUtc) DESC,MAX(x.FactorDate) DESC;
            """;
        var result = new List<PendingInvoiceMapping>();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new PendingInvoiceMapping(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                reader.GetDecimal(4), reader.GetInt32(5), TehranClock.AsUtc(reader.GetDateTime(6))));
        return result;
    }

    public async Task<ManualAccountingMappingResult> LinkManuallyAsync(
        string? rawCode, string? rawPhone, CancellationToken ct)
    {
        if (!MappingValueNormalizer.TryAccountingCode(rawCode, out var code, out var codeError))
            throw new ArgumentException(codeError ?? "Accounting code is invalid.");
        var phone = MappingValueNormalizer.Phone(rawPhone);
        if (phone is null) throw new ArgumentException("شماره تلفن معتبر نیست.");

        await using var connection = await OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);
        string? name;
        await using (var nameCommand = new SqlCommand(
            "SELECT TOP(1) COALESCE(m.CustomerName,a.CustomerName) FROM dbo.CustomerAccountingMappings m FULL JOIN dbo.AccountingCustomers a ON RIGHT(N'000000'+REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(COALESCE(NULLIF(a.DetailCode,N''),a.ShortCode))),N'30/',N''),N'/30',N''),N'/',N''),6)=m.AccountingCode WHERE COALESCE(m.AccountingCode,RIGHT(N'000000'+REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(COALESCE(NULLIF(a.DetailCode,N''),a.ShortCode))),N'30/',N''),N'/30',N''),N'/',N''),6))=@code;",
            connection))
        {
            nameCommand.Parameters.Add("@code", SqlDbType.Char, 6).Value = code;
            name = (string?)await nameCommand.ExecuteScalarAsync(ct);
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var identity = await ResolveIdentityAsync(connection, transaction, phone, name, ct);
            await LinkAccountingAsync(connection, transaction, identity, code, name, phone, ct);
            await UpsertMappingAsync(connection, transaction, code, name, phone, identity,
                "LINKED", null, "SELLER_201_202", null, ct);
            await transaction.CommitAsync(ct);
            return new ManualAccountingMappingResult(code, phone, identity, "LINKED");
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task StartRunAsync(Guid runId, DateTime started, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);
        const string sql = "INSERT dbo.DataGatheringRuns(RunId,StartedAtUtc,Status) VALUES(@id,@started,N'RUNNING');";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", runId);
        command.Parameters.AddWithValue("@started", started);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task FinishRunAsync(DataGatheringRunResult result, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);
        const string sql = """
            UPDATE dbo.DataGatheringRuns
            SET FinishedAtUtc=@finished,Status=@status,AccountingStatus=@accounting,
                LinkedCodes=@linked,UnmappedCodes=@unmapped,ErrorMessage=@error
            WHERE RunId=@id;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@finished", result.FinishedAtUtc);
        command.Parameters.AddWithValue("@status", result.Status);
        command.Parameters.AddWithValue("@accounting", result.AccountingStatus);
        command.Parameters.AddWithValue("@linked", result.LinkedCodes);
        command.Parameters.AddWithValue("@unmapped", result.UnmappedCodes);
        command.Parameters.AddWithValue("@error", (object?)result.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("@id", result.RunId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<DataGatheringStatus> GetGatheringStatusAsync(bool enabled, int interval, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);
        const string sql = """
            SELECT TOP(1) StartedAtUtc,FinishedAtUtc,Status,AccountingStatus,LinkedCodes,UnmappedCodes,ErrorMessage
            FROM dbo.DataGatheringRuns ORDER BY StartedAtUtc DESC;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new DataGatheringStatus(enabled, interval, null, null, null, null, 0, 0, null);
        return new DataGatheringStatus(enabled, interval, reader.GetDateTime(0),
            reader.IsDBNull(1) ? null : reader.GetDateTime(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    private PreparedRow Prepare(CustomerMappingInputRow row)
    {
        var valid = MappingValueNormalizer.TryAccountingCode(row.RawAccountingCode, out var code, out var error);
        return new PreparedRow(row.RowNumber, valid ? code : null, row.CustomerName?.Trim(),
            MappingValueNormalizer.Phone(row.RawPhone), error);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private async Task EnsureSchemaAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = new SqlCommand(_queries.Get("CustomerMappingV41Schema.sql"), connection)
        {
            CommandTimeout = 120
        };
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> ResolveIdentityAsync(
        SqlConnection connection, SqlTransaction transaction, string phone, string? name, CancellationToken ct)
    {
        const string find = """
            SELECT TOP(1) IdentityId FROM dbo.CustomerIdentityPhones
            WHERE NormalizedPhone=@phone ORDER BY IsVerified DESC,Priority,Id;
            """;
        await using (var command = new SqlCommand(find, connection, transaction))
        {
            command.Parameters.Add("@phone", SqlDbType.NVarChar, 32).Value = phone;
            var existing = await command.ExecuteScalarAsync(ct);
            if (existing is not null && existing is not DBNull) return Convert.ToInt64(existing);
        }

        long identityId;
        const string insertIdentity = """
            INSERT dbo.CustomerIdentities(DisplayName) VALUES(@name);
            SELECT CAST(SCOPE_IDENTITY() AS bigint);
            """;
        await using (var command = new SqlCommand(insertIdentity, connection, transaction))
        {
            command.Parameters.Add("@name", SqlDbType.NVarChar, 300).Value = (object?)name ?? DBNull.Value;
            identityId = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        }

        const string insertPhone = """
            INSERT dbo.CustomerIdentityPhones
                (IdentityId,NormalizedPhone,RawPhone,PhoneType,SourceSystem,IsPrimary,IsVerified,Priority)
            VALUES(@identity,@phone,@phone,N'MANUAL',N'EXCEL_V41',1,1,0);
            """;
        await using (var command = new SqlCommand(insertPhone, connection, transaction))
        {
            command.Parameters.AddWithValue("@identity", identityId);
            command.Parameters.Add("@phone", SqlDbType.NVarChar, 32).Value = phone;
            await command.ExecuteNonQueryAsync(ct);
        }
        return identityId;
    }

    private async Task LinkAccountingAsync(SqlConnection connection, SqlTransaction transaction,
        long identityId, string code, string? name, string phone, CancellationToken ct)
    {
        const string updateIdentity = """
            UPDATE dbo.CustomerIdentities SET DisplayName=COALESCE(NULLIF(@name,N''),DisplayName),UpdatedAtUtc=SYSUTCDATETIME()
            WHERE IdentityId=@identity;
            IF NOT EXISTS(SELECT 1 FROM dbo.CustomerIdentityPhones WHERE IdentityId=@identity AND NormalizedPhone=@phone)
                INSERT dbo.CustomerIdentityPhones(IdentityId,NormalizedPhone,RawPhone,PhoneType,SourceSystem,IsPrimary,IsVerified,Priority)
                VALUES(@identity,@phone,@phone,N'MANUAL',N'EXCEL_V41',0,1,0);
            """;
        await using (var command = new SqlCommand(updateIdentity, connection, transaction))
        {
            command.Parameters.AddWithValue("@identity", identityId);
            command.Parameters.Add("@name", SqlDbType.NVarChar, 300).Value = (object?)name ?? DBNull.Value;
            command.Parameters.Add("@phone", SqlDbType.NVarChar, 32).Value = phone;
            await command.ExecuteNonQueryAsync(ct);
        }

        const string link = """
            DECLARE @actualDetail nvarchar(30)=@code;
            DECLARE @actualShort nvarchar(30)=@code;
            DECLARE @actualName nvarchar(300)=@name;
            SELECT TOP(1)
                @actualDetail=DetailCode,
                @actualShort=ShortCode,
                @actualName=COALESCE(NULLIF(@name,N''),CustomerName)
            FROM dbo.AccountingCustomers
            WHERE SourceDatabase=@db AND FiscalYear=@year
              AND
              (
                  RIGHT(N'000000'+REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(DetailCode)),N'30/',N''),N'/30',N''),N'/',N''),6)=@code
                  OR RIGHT(N'000000'+REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(ShortCode)),N'30/',N''),N'/30',N''),N'/',N''),6)=@code
              )
            ORDER BY ImportedAtUtc DESC;

            DECLARE @existing bigint=(
                SELECT TOP(1) Id FROM dbo.CustomerIdentityAccountingLinks
                WHERE SourceDatabase=@db AND FiscalYear=@year
                  AND
                  (
                      DetailCode=@actualDetail
                      OR RIGHT(N'000000'+REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(DetailCode)),N'30/',N''),N'/30',N''),N'/',N''),6)=@code
                  )
                ORDER BY IsVerified DESC,Id);
            IF @existing IS NOT NULL
                UPDATE dbo.CustomerIdentityAccountingLinks
                SET IdentityId=@identity,ShortCode=@actualShort,CustomerName=@actualName,IsVerified=1
                WHERE Id=@existing;
            ELSE
                INSERT dbo.CustomerIdentityAccountingLinks
                    (IdentityId,SourceDatabase,FiscalYear,DetailCode,ShortCode,CustomerName,IsVerified)
                VALUES(@identity,@db,@year,@actualDetail,@actualShort,@actualName,1);

            IF NOT EXISTS(SELECT 1 FROM dbo.CustomerIdentityManualMappings WHERE Phone=@phone AND AccountingCode=@code)
                INSERT dbo.CustomerIdentityManualMappings(DisplayName,Phone,AccountingCode,IsVerified,IsActive)
                VALUES(@name,@phone,@code,1,1);

            INSERT dbo.CustomerIdentityDidarLinks(IdentityId,DidarContactCode,IsVerified)
            SELECT TOP(1) @identity,p.DidarContactCode,1
            FROM dbo.DidarContactPhones p
            WHERE p.NormalizedPhone=dbo.NormalizeIranPhone(@phone)
              AND NOT EXISTS(SELECT 1 FROM dbo.CustomerIdentityDidarLinks d WHERE d.DidarContactCode=p.DidarContactCode);
            """;
        await using var linkCommand = new SqlCommand(link, connection, transaction);
        linkCommand.Parameters.AddWithValue("@identity", identityId);
        linkCommand.Parameters.Add("@code", SqlDbType.Char, 6).Value = code;
        linkCommand.Parameters.Add("@db", SqlDbType.NVarChar, 128).Value = _sourceDatabase;
        linkCommand.Parameters.AddWithValue("@year", _fiscalYear);
        linkCommand.Parameters.Add("@name", SqlDbType.NVarChar, 300).Value = (object?)name ?? DBNull.Value;
        linkCommand.Parameters.Add("@phone", SqlDbType.NVarChar, 32).Value = phone;
        await linkCommand.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpsertMappingAsync(SqlConnection connection, SqlTransaction transaction,
        string code, string? name, string? phone, long? identity, string status, string? error,
        string? sourceFile, int? sourceRow, CancellationToken ct)
    {
        const string sql = """
            UPDATE dbo.CustomerAccountingMappings
            SET CustomerName=COALESCE(@name,CustomerName),NormalizedPhone=COALESCE(@phone,NormalizedPhone),
                IdentityId=COALESCE(@identity,IdentityId),Status=@status,ErrorMessage=@error,
                SourceFile=COALESCE(@file,SourceFile),SourceRow=COALESCE(@row,SourceRow),UpdatedAtUtc=SYSUTCDATETIME()
            WHERE AccountingCode=@code;
            IF @@ROWCOUNT=0
                INSERT dbo.CustomerAccountingMappings
                    (AccountingCode,CustomerName,NormalizedPhone,IdentityId,Status,ErrorMessage,SourceFile,SourceRow)
                VALUES(@code,@name,@phone,@identity,@status,@error,@file,@row);
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@code", SqlDbType.Char, 6).Value = code;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 300).Value = (object?)name ?? DBNull.Value;
        command.Parameters.Add("@phone", SqlDbType.NVarChar, 32).Value = (object?)phone ?? DBNull.Value;
        command.Parameters.Add("@identity", SqlDbType.BigInt).Value = identity.HasValue ? identity.Value : DBNull.Value;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 20).Value = status;
        command.Parameters.Add("@error", SqlDbType.NVarChar, 1000).Value = (object?)error ?? DBNull.Value;
        command.Parameters.Add("@file", SqlDbType.NVarChar, 260).Value = (object?)sourceFile ?? DBNull.Value;
        command.Parameters.Add("@row", SqlDbType.Int).Value = sourceRow.HasValue ? sourceRow.Value : DBNull.Value;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertImportAsync(SqlConnection connection, SqlTransaction transaction,
        Guid id, string name, string hash, int total, int linked, int unmapped, int conflicts, int invalid, CancellationToken ct)
    {
        const string sql = """
            INSERT dbo.CustomerMappingImports
                (ImportId,FileName,FileHash,ImportedAtUtc,TotalRows,LinkedRows,UnmappedRows,ConflictRows,InvalidRows)
            VALUES(@id,@name,@hash,SYSUTCDATETIME(),@total,@linked,@unmapped,@conflicts,@invalid);
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@hash", hash);
        command.Parameters.AddWithValue("@total", total);
        command.Parameters.AddWithValue("@linked", linked);
        command.Parameters.AddWithValue("@unmapped", unmapped);
        command.Parameters.AddWithValue("@conflicts", conflicts);
        command.Parameters.AddWithValue("@invalid", invalid);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<CustomerMappingImportResult?> FindImportByHashAsync(
        SqlConnection connection, string hash, CancellationToken ct)
    {
        const string sql = """
            SELECT ImportId,FileName,TotalRows,LinkedRows,UnmappedRows,ConflictRows,InvalidRows
            FROM dbo.CustomerMappingImports WHERE FileHash=@hash;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@hash", hash);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new CustomerMappingImportResult(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2),
            reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), true);
    }

    private static async Task DiscoverAccountingCodesAsync(SqlConnection connection, CancellationToken ct)
    {
        const string sql = """
            IF OBJECT_ID(N'dbo.AccountingCustomers',N'U') IS NOT NULL
            BEGIN
                ;WITH Codes AS
                (
                    SELECT CustomerName,
                           CleanCode=REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(COALESCE(NULLIF(DetailCode,N''),ShortCode))),N'30/',N''),N'/30',N''),N'/',N'')
                    FROM dbo.AccountingCustomers
                ), Valid AS
                (
                    SELECT CustomerName,AccountingCode=RIGHT(N'000000'+CleanCode,6)
                    FROM Codes
                    WHERE TRY_CONVERT(int,CleanCode) BETWEEN 1 AND 999999
                      AND TRY_CONVERT(int,CleanCode) NOT BETWEEN 40000 AND 49999
                      AND TRY_CONVERT(int,CleanCode) NOT BETWEEN 60000 AND 69999
                )
                INSERT dbo.CustomerAccountingMappings(AccountingCode,CustomerName,Status,ErrorMessage)
                SELECT v.AccountingCode,MAX(v.CustomerName),N'UNMAPPED',N'Accounting code has no verified telephone mapping.'
                FROM Valid v
                WHERE NOT EXISTS(SELECT 1 FROM dbo.CustomerAccountingMappings m WHERE m.AccountingCode=v.AccountingCode)
                GROUP BY v.AccountingCode;
            END;
            """;
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<CustomerMappingSummary> GetSummaryAsync(SqlConnection connection, CancellationToken ct)
    {
        const string sql = """
            SELECT COUNT(*),
                   SUM(CASE WHEN Status=N'LINKED' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN Status=N'UNMAPPED' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN Status=N'CONFLICT' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN Status=N'INVALID' THEN 1 ELSE 0 END)
            FROM dbo.CustomerAccountingMappings;
            SELECT TOP(1) ImportedAtUtc,FileName FROM dbo.CustomerMappingImports ORDER BY ImportedAtUtc DESC;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var total = 0; var linked = 0; var unmapped = 0; var conflicts = 0; var invalid = 0;
        if (await reader.ReadAsync(ct))
        {
            total = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            linked = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            unmapped = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            conflicts = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
            invalid = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
        }
        DateTime? imported = null; string? file = null;
        await reader.NextResultAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            imported = reader.GetDateTime(0);
            file = reader.GetString(1);
        }
        return new CustomerMappingSummary(total, linked, unmapped, conflicts, invalid, imported, file);
    }

    private sealed record PreparedRow(int RowNumber, string? Code, string? Name, string? Phone, string? Error);
}
