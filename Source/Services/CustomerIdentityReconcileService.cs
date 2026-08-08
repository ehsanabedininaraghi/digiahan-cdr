using Microsoft.Data.SqlClient;

namespace DigiAhan.CDR.Receiver.Services;

public sealed record CustomerIdentityStatus(long TotalActiveDidar, long LinkedDidar,
    long TotalIdentities, long DidarPhones, long CreatedIdentities);

public sealed class CustomerIdentityReconcileService
{
    private readonly IConfiguration _configuration;
    private readonly SqlQueryStore _queries;
    private readonly DidarPhoneRebuildService _phones;
    private readonly ILogger<CustomerIdentityReconcileService> _logger;

    public CustomerIdentityReconcileService(IConfiguration configuration, SqlQueryStore queries, DidarPhoneRebuildService phones,
        ILogger<CustomerIdentityReconcileService> logger)
    {
        _configuration = configuration;
        _queries = queries;
        _phones = phones;
        _logger = logger;
    }

    private string ConnectionString => _configuration.GetConnectionString("DigiAhanCdr")
        ?? throw new InvalidOperationException("ConnectionStrings:DigiAhanCdr is missing.");

    public async Task<CustomerIdentityStatus> ReconcileAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await _phones.RebuildAsync(ct);

        await using var command = new SqlCommand(_queries.Get("CustomerIdentityDidarReconcileV412.sql"), connection)
        { CommandTimeout = 600 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("Identity reconciliation returned no status row.");
        var result = Read(reader);
        if (result.LinkedDidar != result.TotalActiveDidar)
            throw new InvalidOperationException($"Not every active Didar contact has an identity. Active={result.TotalActiveDidar}, Linked={result.LinkedDidar}.");
        _logger.LogInformation("Didar-first identity reconciliation completed. Active={Active} Linked={Linked} Identities={Identities} Phones={Phones} Created={Created}",
            result.TotalActiveDidar,result.LinkedDidar,result.TotalIdentities,result.DidarPhones,result.CreatedIdentities);
        return result;
    }

    public async Task<CustomerIdentityStatus> GetStatusAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        const string sql = """
            SELECT
              (SELECT COUNT_BIG(*) FROM dbo.DidarContacts WHERE ISNULL(IsDeleted,0)=0),
              (SELECT COUNT_BIG(*) FROM dbo.CustomerIdentityDidarLinks l INNER JOIN dbo.DidarContacts d ON d.DidarContactCode=l.DidarContactCode WHERE ISNULL(d.IsDeleted,0)=0),
              (SELECT COUNT_BIG(*) FROM dbo.CustomerIdentities),
              (SELECT COUNT_BIG(*) FROM dbo.CustomerIdentityPhones WHERE SourceSystem=N'DIDAR'),
              CAST(0 AS bigint);
            """;
        await using var command = new SqlCommand(sql,connection) { CommandTimeout=60 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return Read(reader);
    }

    private static CustomerIdentityStatus Read(SqlDataReader reader) => new(
        reader.GetInt64(0),reader.GetInt64(1),reader.GetInt64(2),reader.GetInt64(3),reader.GetInt64(4));
}
