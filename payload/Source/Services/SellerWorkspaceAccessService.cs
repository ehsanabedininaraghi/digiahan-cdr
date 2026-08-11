using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DigiAhan.CDR.Receiver.Models;
using Microsoft.Data.SqlClient;

namespace DigiAhan.CDR.Receiver.Services;

public sealed record SellerLoginResult(string AccessToken, SellerIdentity Seller, DateTime ExpiresAtUtc);

public sealed class SellerWorkspaceAccessService
{
    private const int PasswordIterations = 180_000;
    private static readonly SemaphoreSlim BootstrapGate = new(1, 1);
    private static bool _bootstrapped;
    private readonly IConfiguration _configuration;
    private readonly SellerWorkspaceRepository _workspace;
    private readonly string _connectionString;

    public SellerWorkspaceAccessService(IConfiguration configuration, SellerWorkspaceRepository workspace)
    {
        _configuration = configuration;
        _workspace = workspace;
        _connectionString = configuration.GetConnectionString("DigiAhanCdr")
            ?? throw new InvalidOperationException("ConnectionStrings:DigiAhanCdr is missing.");
    }

    public async Task<SellerLoginResult?> LoginAsync(string? username, string? password, CancellationToken ct)
    {
        await EnsureBootstrapAsync(ct);
        var normalized = NormalizeUsername(username);
        if (normalized.Length == 0 || string.IsNullOrEmpty(password)) return null;

        await using var connection = await OpenAsync(ct);
        const string sql = """
            SELECT TOP(1) Id,Username,PasswordHash,PasswordSalt,PasswordIterations,
                          SellerKey,DisplayName
            FROM dbo.SellerUsers
            WHERE NormalizedUsername=@username AND IsActive=1;
            """;
        long userId;
        string sellerKey;
        string displayName;
        byte[] expected;
        byte[] salt;
        int iterations;
        await using (var command = new SqlCommand(sql, connection))
        {
            command.Parameters.Add("@username", SqlDbType.NVarChar, 80).Value = normalized;
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            userId = reader.GetInt64(0);
            expected = (byte[])reader[2];
            salt = (byte[])reader[3];
            iterations = reader.GetInt32(4);
            sellerKey = reader.GetString(5);
            displayName = reader.GetString(6);
        }

        var actual = HashPassword(password, salt, iterations);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return null;

        var identity = await LoadIdentityAsync(connection, userId, sellerKey, displayName, ct);
        if (identity is null) return null;

        var rawToken = RandomNumberGenerator.GetBytes(32);
        var token = Base64Url(rawToken);
        var tokenHash = SHA256.HashData(rawToken);
        var expires = DateTime.UtcNow.AddHours(12);
        const string insert = """
            INSERT dbo.SellerSessions(Id,SellerUserId,TokenHash,ExpiresAtUtc)
            VALUES(@id,@user,@hash,@expires);
            """;
        await using (var command = new SqlCommand(insert, connection))
        {
            command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            command.Parameters.Add("@user", SqlDbType.BigInt).Value = userId;
            command.Parameters.Add("@hash", SqlDbType.VarBinary, 32).Value = tokenHash;
            command.Parameters.Add("@expires", SqlDbType.DateTime2).Value = expires;
            await command.ExecuteNonQueryAsync(ct);
        }

        return new SellerLoginResult(token, identity, expires);
    }

    public async Task<SellerIdentity?> AuthenticateAsync(HttpContext context, CancellationToken ct)
    {
        await EnsureBootstrapAsync(ct);
        var supplied = context.Request.Cookies["digiahan_seller_session"]
            ?? context.Request.Headers["X-Seller-Token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(supplied)) return null;

        byte[] raw;
        try { raw = FromBase64Url(supplied); }
        catch (FormatException) { return AuthenticateLegacy(supplied); }

        var hash = SHA256.HashData(raw);
        await using var connection = await OpenAsync(ct);
        const string sql = """
            SELECT TOP(1) u.Id,u.SellerKey,u.DisplayName
            FROM dbo.SellerSessions s
            INNER JOIN dbo.SellerUsers u ON u.Id=s.SellerUserId
            WHERE s.TokenHash=@hash AND s.RevokedAtUtc IS NULL
              AND s.ExpiresAtUtc>SYSUTCDATETIME() AND u.IsActive=1;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@hash", SqlDbType.VarBinary, 32).Value = hash;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return AuthenticateLegacy(supplied);
        var userId = reader.GetInt64(0);
        var key = reader.GetString(1);
        var name = reader.GetString(2);
        await reader.CloseAsync();
        return await LoadIdentityAsync(connection, userId, key, name, ct);
    }

    public async Task LogoutAsync(HttpContext context, CancellationToken ct)
    {
        var supplied = context.Request.Cookies["digiahan_seller_session"]
            ?? context.Request.Headers["X-Seller-Token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(supplied)) return;
        try
        {
            var hash = SHA256.HashData(FromBase64Url(supplied));
            await using var connection = await OpenAsync(ct);
            await using var command = new SqlCommand(
                "UPDATE dbo.SellerSessions SET RevokedAtUtc=SYSUTCDATETIME() WHERE TokenHash=@hash;", connection);
            command.Parameters.Add("@hash", SqlDbType.VarBinary, 32).Value = hash;
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (FormatException) { }
    }

    private async Task EnsureBootstrapAsync(CancellationToken ct)
    {
        if (_bootstrapped) return;
        await BootstrapGate.WaitAsync(ct);
        try
        {
            if (_bootstrapped) return;
            await _workspace.EnsureSchemaAsync(ct);
            var options = _configuration.GetSection("SellerWorkspace").Get<SellerWorkspaceOptions>()
                ?? new SellerWorkspaceOptions();
            if (!options.Enabled) return;

            await using var connection = await OpenAsync(ct);
            foreach (var agent in options.Agents)
                await UpsertConfiguredAgentAsync(connection, agent, ct);
            _bootstrapped = true;
        }
        finally { BootstrapGate.Release(); }
    }

    private static async Task UpsertConfiguredAgentAsync(
        SqlConnection connection, SellerWorkspaceAgentOptions agent, CancellationToken ct)
    {
        var key = agent.Key?.Trim() ?? string.Empty;
        var username = string.IsNullOrWhiteSpace(agent.Username) ? key : agent.Username.Trim();
        var normalized = NormalizeUsername(username);
        var initialPassword = string.IsNullOrWhiteSpace(agent.InitialPassword)
            ? agent.AccessToken
            : agent.InitialPassword;
        var extensions = agent.Extensions.Where(x => Regex.IsMatch(x ?? string.Empty, @"^\d{3}$"))
            .Distinct(StringComparer.Ordinal).Take(20).ToArray();
        if (key.Length == 0 || normalized.Length == 0 || extensions.Length == 0) return;

        const string find = "SELECT Id FROM dbo.SellerUsers WHERE NormalizedUsername=@username;";
        long? userId;
        await using (var command = new SqlCommand(find, connection))
        {
            command.Parameters.Add("@username", SqlDbType.NVarChar, 80).Value = normalized;
            var value = await command.ExecuteScalarAsync(ct);
            userId = value is null or DBNull ? null : Convert.ToInt64(value);
        }

        if (userId is null)
        {
            if (string.IsNullOrWhiteSpace(initialPassword)) return;
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = HashPassword(initialPassword, salt, PasswordIterations);
            const string insert = """
                INSERT dbo.SellerUsers
                  (Username,NormalizedUsername,PasswordHash,PasswordSalt,PasswordIterations,SellerKey,DisplayName)
                OUTPUT inserted.Id
                VALUES(@username,@normalized,@hash,@salt,@iterations,@key,@name);
                """;
            await using var command = new SqlCommand(insert, connection);
            command.Parameters.Add("@username", SqlDbType.NVarChar, 80).Value = username;
            command.Parameters.Add("@normalized", SqlDbType.NVarChar, 80).Value = normalized;
            command.Parameters.Add("@hash", SqlDbType.VarBinary, 32).Value = hash;
            command.Parameters.Add("@salt", SqlDbType.VarBinary, 16).Value = salt;
            command.Parameters.Add("@iterations", SqlDbType.Int).Value = PasswordIterations;
            command.Parameters.Add("@key", SqlDbType.NVarChar, 80).Value = key;
            command.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value =
                string.IsNullOrWhiteSpace(agent.DisplayName) ? key : agent.DisplayName.Trim();
            userId = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        }
        else
        {
            const string update = """
                UPDATE dbo.SellerUsers SET SellerKey=@key,DisplayName=@name,IsActive=1,UpdatedAtUtc=SYSUTCDATETIME()
                WHERE Id=@id;
                """;
            await using var command = new SqlCommand(update, connection);
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = userId.Value;
            command.Parameters.Add("@key", SqlDbType.NVarChar, 80).Value = key;
            command.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value =
                string.IsNullOrWhiteSpace(agent.DisplayName) ? key : agent.DisplayName.Trim();
            await command.ExecuteNonQueryAsync(ct);
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            await using (var clear = new SqlCommand(
                "DELETE dbo.SellerUserExtensions WHERE SellerUserId=@id; DELETE dbo.SellerUserProductGroups WHERE SellerUserId=@id;",
                connection, transaction))
            {
                clear.Parameters.Add("@id", SqlDbType.BigInt).Value = userId.Value;
                await clear.ExecuteNonQueryAsync(ct);
            }
            foreach (var extension in extensions)
            {
                await using var insert = new SqlCommand(
                    "INSERT dbo.SellerUserExtensions(SellerUserId,Extension) VALUES(@id,@value);", connection, transaction);
                insert.Parameters.Add("@id", SqlDbType.BigInt).Value = userId.Value;
                insert.Parameters.Add("@value", SqlDbType.NVarChar, 10).Value = extension;
                await insert.ExecuteNonQueryAsync(ct);
            }
            foreach (var group in agent.ProductGroups.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(20))
            {
                await using var insert = new SqlCommand(
                    "INSERT dbo.SellerUserProductGroups(SellerUserId,ProductGroup) VALUES(@id,@value);", connection, transaction);
                insert.Parameters.Add("@id", SqlDbType.BigInt).Value = userId.Value;
                insert.Parameters.Add("@value", SqlDbType.NVarChar, 120).Value = group.Trim();
                await insert.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<SellerIdentity?> LoadIdentityAsync(
        SqlConnection connection, long userId, string key, string displayName, CancellationToken ct)
    {
        var extensions = new List<string>();
        var products = new List<string>();
        const string sql = """
            SELECT Extension FROM dbo.SellerUserExtensions WHERE SellerUserId=@id ORDER BY Extension;
            SELECT ProductGroup FROM dbo.SellerUserProductGroups WHERE SellerUserId=@id ORDER BY ProductGroup;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = userId;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) extensions.Add(reader.GetString(0));
        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct)) products.Add(reader.GetString(0));
        return extensions.Count == 0 ? null : new SellerIdentity(key, displayName, extensions.ToArray(), products.ToArray());
    }

    private SellerIdentity? AuthenticateLegacy(string supplied)
    {
        var options = _configuration.GetSection("SellerWorkspace").Get<SellerWorkspaceOptions>()
            ?? new SellerWorkspaceOptions();
        if (!options.Enabled) return null;
        foreach (var agent in options.Agents)
        {
            if (string.IsNullOrWhiteSpace(agent.AccessToken) || !FixedTimeEquals(supplied, agent.AccessToken)) continue;
            var extensions = agent.Extensions.Where(x => Regex.IsMatch(x ?? string.Empty, @"^\d{3}$"))
                .Distinct(StringComparer.Ordinal).Take(20).ToArray();
            if (extensions.Length == 0 || string.IsNullOrWhiteSpace(agent.Key)) return null;
            return new SellerIdentity(agent.Key.Trim(),
                string.IsNullOrWhiteSpace(agent.DisplayName) ? agent.Key.Trim() : agent.DisplayName.Trim(),
                extensions, agent.ProductGroups.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray());
        }
        return null;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static byte[] HashPassword(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static string NormalizeUsername(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        return Convert.FromBase64String(padded);
    }
}
