using Microsoft.Data.SqlClient;
using System.Data;
using System.Text;
using System.Text.Json;

namespace DigiAhan.CDR.Receiver.Services;

public sealed record DidarApiSyncResult(int Received, int Inserted, int Updated);

public sealed class DidarApiSyncService
{
    private const int PageSize = 300;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DidarApiSyncService> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public DidarApiSyncService(IConfiguration configuration, ILogger<DidarApiSyncService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<DidarApiSyncResult> SyncAsync(CancellationToken ct)
    {
        if (!_configuration.GetValue("Didar:Enabled", false))
            throw new InvalidOperationException("Didar API sync is disabled.");

        var apiKey = _configuration["Didar:ApiKey"]?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("PASTE_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Didar:ApiKey is not configured.");

        var baseUrl = (_configuration["Didar:BaseUrl"] ?? "https://app.didar.me").TrimEnd('/');
        var contacts = new List<DidarContact>();
        var offset = 0;
        var total = int.MaxValue;

        while (offset < total)
        {
            var payload = JsonSerializer.Serialize(new
            {
                Criteria = new
                {
                    SearchFromTime = "1000-01-01T00:00:00.000Z",
                    SearchToTime = "9999-12-01T00:00:00.000Z",
                    SortOrder = 0,
                    MobilePhone = "",
                    WorkPhone = "",
                    Email = "",
                    NationalCode = "",
                    ZipCode = "",
                    CustomerCode = ""
                },
                From = offset,
                Limit = PageSize
            });

            HttpResponseMessage? response = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"{baseUrl}/api/contact/PersonSearch?apikey={Uri.EscapeDataString(apiKey)}")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                request.Headers.UserAgent.ParseAdd("DigiAhan-DidarSync/4.4.0");
                response = await _http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode || attempt == 3) break;
                response.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }

            using var finalResponse = response!;
            if (!finalResponse.IsSuccessStatusCode)
                throw new InvalidOperationException($"Didar API returned HTTP {(int)finalResponse.StatusCode}.");

            await using var stream = await finalResponse.Content.ReadAsStreamAsync(ct);
            var envelope = await JsonSerializer.DeserializeAsync<DidarSearchEnvelope>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct)
                ?? throw new InvalidOperationException("Didar API returned an empty response.");

            var page = envelope.Response?.List ?? [];
            total = envelope.Response?.TotalCount ?? page.Count;
            contacts.AddRange(page);
            if (page.Count == 0) break;
            offset += page.Count;
        }

        var result = await SaveAsync(contacts, ct);
        _logger.LogInformation("Didar API snapshot synchronized. Received={Received} Inserted={Inserted} Updated={Updated}",
            result.Received, result.Inserted, result.Updated);
        return result;
    }

    private async Task<DidarApiSyncResult> SaveAsync(List<DidarContact> contacts, CancellationToken ct)
    {
        var connectionString = _configuration.GetConnectionString("DigiAhanCdr")
            ?? throw new InvalidOperationException("ConnectionStrings:DigiAhanCdr is not configured.");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var inserted = 0;
        var updated = 0;

        const string sql = """
MERGE dbo.DidarContacts AS T
USING (SELECT @DidarContactCode AS DidarContactCode) AS S
ON T.DidarContactCode = S.DidarContactCode
WHEN MATCHED THEN UPDATE SET
    RecordType=@RecordType, CustomerCode=@CustomerCode, CustomerTitle=@CustomerTitle,
    FirstName=@FirstName, LastName=@LastName, Email=@Email, JobTitle=@JobTitle,
    PostalCode=@PostalCode, PersonDescription=@PersonDescription, MobilePhone=@MobilePhone,
    LandlinePhone=@LandlinePhone, CompanyName=@CompanyName, DidarCompanyCode=@DidarCompanyCode,
    NationalCode=@NationalCode, CreatedDateText=@CreatedDateText, IsDeleted=@IsDeleted,
    SourceHash=HASHBYTES('SHA2_256', CONCAT_WS(N'|',@CustomerTitle,@FirstName,@LastName,@Email,@MobilePhone,@LandlinePhone,@CompanyName,@NationalCode)),
    LastSyncedAt=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
    (RecordType,DidarContactCode,CustomerCode,CustomerTitle,FirstName,LastName,Email,JobTitle,PostalCode,
     PersonDescription,MobilePhone,LandlinePhone,CompanyName,DidarCompanyCode,NationalCode,CreatedDateText,
     SourceHash,IsDeleted,FirstImportedAt,LastSyncedAt)
VALUES
    (@RecordType,@DidarContactCode,@CustomerCode,@CustomerTitle,@FirstName,@LastName,@Email,@JobTitle,@PostalCode,
     @PersonDescription,@MobilePhone,@LandlinePhone,@CompanyName,@DidarCompanyCode,@NationalCode,@CreatedDateText,
     HASHBYTES('SHA2_256', CONCAT_WS(N'|',@CustomerTitle,@FirstName,@LastName,@Email,@MobilePhone,@LandlinePhone,@CompanyName,@NationalCode)),
     @IsDeleted,SYSUTCDATETIME(),SYSUTCDATETIME())
OUTPUT $action;
""";

        foreach (var item in contacts)
        {
            var code = item.Code?.ToString() ?? item.Id;
            if (string.IsNullOrWhiteSpace(code)) continue;
            await using var command = new SqlCommand(sql, connection, transaction);
            Add(command, "@DidarContactCode", code);
            Add(command, "@RecordType", item.ContactType ?? item.Type ?? "Person");
            Add(command, "@CustomerCode", item.CustomerCode);
            Add(command, "@CustomerTitle", item.DisplayName);
            Add(command, "@FirstName", item.FirstName);
            Add(command, "@LastName", item.LastName);
            Add(command, "@Email", item.Email);
            Add(command, "@JobTitle", item.Position);
            Add(command, "@PostalCode", item.ZipCode);
            Add(command, "@PersonDescription", item.BackgroundInfo);
            Add(command, "@MobilePhone", item.MobilePhone);
            Add(command, "@LandlinePhone", item.WorkPhone);
            Add(command, "@CompanyName", item.CompanyName);
            Add(command, "@DidarCompanyCode", item.CompanyId);
            Add(command, "@NationalCode", item.NationalCode);
            Add(command, "@CreatedDateText", item.RegisterTime);
            command.Parameters.Add("@IsDeleted", SqlDbType.Bit).Value = item.IsDeleted;
            var action = (string?)await command.ExecuteScalarAsync(ct);
            if (string.Equals(action, "INSERT", StringComparison.OrdinalIgnoreCase)) inserted++;
            else if (string.Equals(action, "UPDATE", StringComparison.OrdinalIgnoreCase)) updated++;
        }

        await transaction.CommitAsync(ct);
        return new DidarApiSyncResult(contacts.Count, inserted, updated);
    }

    private static void Add(SqlCommand command, string name, string? value) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, -1).Value = (object?)value ?? DBNull.Value;

    private sealed class DidarSearchEnvelope { public DidarSearchResponse? Response { get; set; } }
    private sealed class DidarSearchResponse { public List<DidarContact> List { get; set; } = []; public int TotalCount { get; set; } }
    private sealed class DidarContact
    {
        public string? Id { get; set; }
        public int? Code { get; set; }
        public string? ContactType { get; set; }
        public string? Type { get; set; }
        public string? CustomerCode { get; set; }
        public string? DisplayName { get; set; }
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? Email { get; set; }
        public string? Position { get; set; }
        public string? ZipCode { get; set; }
        public string? BackgroundInfo { get; set; }
        public string? MobilePhone { get; set; }
        public string? WorkPhone { get; set; }
        public string? CompanyName { get; set; }
        public string? CompanyId { get; set; }
        public string? NationalCode { get; set; }
        public string? RegisterTime { get; set; }
        public bool IsDeleted { get; set; }
    }
}
