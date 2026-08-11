using System.Data;
using System.Security.Cryptography;
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
        catch (FormatException) { return null; }

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
        if (!await reader.ReadAsync(ct)) return null;
        var userId = reader.GetInt64(0);
        var key = reader.GetString(1);
        var name = reader.GetString(2);
        await reader.CloseAsync();
        await using (var touch = new SqlCommand(
            "UPDATE dbo.SellerSessions SET LastSeenAtUtc=SYSUTCDATETIME() WHERE TokenHash=@hash AND LastSeenAtUtc<DATEADD(minute,-1,SYSUTCDATETIME());",
            connection))
        {
            touch.Parameters.Add("@hash", SqlDbType.VarBinary, 32).Value = hash;
            await touch.ExecuteNonQueryAsync(ct);
        }
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

    public async Task<IReadOnlyList<SellerAdminUserRow>> ListUsersAsync(CancellationToken ct)
    {
        await EnsureBootstrapAsync(ct);
        await using var connection = await OpenAsync(ct);
        const string sql = """
            SELECT u.Id,u.Username,u.SellerKey,u.DisplayName,u.IsActive,
                   ISNULL(ext.Extensions,N''),ISNULL(groups.ProductGroups,N''),
                   u.CreatedAtUtc,u.UpdatedAtUtc,
                   (SELECT MAX(s.LastSeenAtUtc) FROM dbo.SellerSessions s WHERE s.SellerUserId=u.Id),
                   (SELECT COUNT(*) FROM dbo.SellerSessions s
                    WHERE s.SellerUserId=u.Id AND s.RevokedAtUtc IS NULL AND s.ExpiresAtUtc>SYSUTCDATETIME())
            FROM dbo.SellerUsers u
            OUTER APPLY
            (
                SELECT STRING_AGG(e.Extension,N',') AS Extensions
                FROM dbo.SellerUserExtensions e WHERE e.SellerUserId=u.Id
            ) ext
            OUTER APPLY
            (
                SELECT STRING_AGG(g.ProductGroup,N',') AS ProductGroups
                FROM dbo.SellerUserProductGroups g WHERE g.SellerUserId=u.Id
            ) groups
            ORDER BY u.IsActive DESC,u.DisplayName,u.Id;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<SellerAdminUserRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new SellerAdminUserRow(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4),
                SplitValues(reader.GetString(5)), SplitValues(reader.GetString(6)),
                TehranClock.AsUtc(reader.GetDateTime(7)), TehranClock.AsUtc(reader.GetDateTime(8)),
                reader.IsDBNull(9) ? null : TehranClock.AsUtc(reader.GetDateTime(9)), reader.GetInt32(10)));
        }
        return rows;
    }

    public async Task<SellerAdminUserRow> CreateUserAsync(SellerAdminUserSaveRequest request, CancellationToken ct)
    {
        await EnsureBootstrapAsync(ct);
        var value = ValidateUser(request, requirePassword: true);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            await EnsureUniqueAsync(connection, transaction, null, value.NormalizedUsername, value.SellerKey, ct);
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = HashPassword(value.Password!, salt, PasswordIterations);
            const string insert = """
                INSERT dbo.SellerUsers
                  (Username,NormalizedUsername,PasswordHash,PasswordSalt,PasswordIterations,SellerKey,DisplayName,IsActive)
                OUTPUT inserted.Id
                VALUES(@username,@normalized,@hash,@salt,@iterations,@key,@name,@active);
                """;
            long id;
            await using (var command = new SqlCommand(insert, connection, transaction))
            {
                AddUserParameters(command, value, hash, salt);
                id = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
            }
            await ReplaceAssignmentsAsync(connection, transaction, id, value.Extensions, value.ProductGroups, ct);
            await transaction.CommitAsync(ct);
            return (await GetUserAsync(connection, id, ct))!;
        }
        catch
        {
            if (transaction.Connection is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<SellerAdminUserRow?> UpdateUserAsync(
        long id, SellerAdminUserSaveRequest request, CancellationToken ct)
    {
        await EnsureBootstrapAsync(ct);
        var value = ValidateUser(request, requirePassword: false);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            await using (var exists = new SqlCommand("SELECT COUNT(*) FROM dbo.SellerUsers WHERE Id=@id;", connection, transaction))
            {
                exists.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                if (Convert.ToInt32(await exists.ExecuteScalarAsync(ct)) == 0)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    return null;
                }
            }

            await EnsureUniqueAsync(connection, transaction, id, value.NormalizedUsername, value.SellerKey, ct);
            if (value.Password is null)
            {
                const string update = """
                    UPDATE dbo.SellerUsers
                    SET Username=@username,NormalizedUsername=@normalized,SellerKey=@key,
                        DisplayName=@name,IsActive=@active,UpdatedAtUtc=SYSUTCDATETIME()
                    WHERE Id=@id;
                    """;
                await using var command = new SqlCommand(update, connection, transaction);
                AddUserParameters(command, value);
                command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                await command.ExecuteNonQueryAsync(ct);
            }
            else
            {
                var salt = RandomNumberGenerator.GetBytes(16);
                var hash = HashPassword(value.Password, salt, PasswordIterations);
                const string update = """
                    UPDATE dbo.SellerUsers
                    SET Username=@username,NormalizedUsername=@normalized,SellerKey=@key,
                        DisplayName=@name,IsActive=@active,PasswordHash=@hash,PasswordSalt=@salt,
                        PasswordIterations=@iterations,UpdatedAtUtc=SYSUTCDATETIME()
                    WHERE Id=@id;
                    """;
                await using var command = new SqlCommand(update, connection, transaction);
                AddUserParameters(command, value, hash, salt);
                command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                await command.ExecuteNonQueryAsync(ct);
            }

            await ReplaceAssignmentsAsync(connection, transaction, id, value.Extensions, value.ProductGroups, ct);
            if (!value.IsActive || value.Password is not null)
            {
                await using var revoke = new SqlCommand(
                    "UPDATE dbo.SellerSessions SET RevokedAtUtc=SYSUTCDATETIME() WHERE SellerUserId=@id AND RevokedAtUtc IS NULL;",
                    connection, transaction);
                revoke.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                await revoke.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return await GetUserAsync(connection, id, ct);
        }
        catch
        {
            if (transaction.Connection is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<bool> ResetPasswordAsync(long id, string? newPassword, CancellationToken ct)
    {
        await EnsureBootstrapAsync(ct);
        ValidatePassword(newPassword, required: true);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = HashPassword(newPassword!, salt, PasswordIterations);
        await using var connection = await OpenAsync(ct);
        const string sql = """
            UPDATE dbo.SellerUsers
            SET PasswordHash=@hash,PasswordSalt=@salt,PasswordIterations=@iterations,
                UpdatedAtUtc=SYSUTCDATETIME()
            WHERE Id=@id;
            IF @@ROWCOUNT>0
            BEGIN
                UPDATE dbo.SellerSessions SET RevokedAtUtc=SYSUTCDATETIME()
                WHERE SellerUserId=@id AND RevokedAtUtc IS NULL;
                SELECT CAST(1 AS bit);
            END
            ELSE SELECT CAST(0 AS bit);
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        command.Parameters.Add("@hash", SqlDbType.VarBinary, 32).Value = hash;
        command.Parameters.Add("@salt", SqlDbType.VarBinary, 16).Value = salt;
        command.Parameters.Add("@iterations", SqlDbType.Int).Value = PasswordIterations;
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct));
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
            if (!options.Enabled)
            {
                _bootstrapped = true;
                return;
            }

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
        else return; // After the first seed, SQL/admin UI is authoritative.

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

    private static async Task<SellerAdminUserRow?> GetUserAsync(SqlConnection connection, long id, CancellationToken ct)
    {
        const string sql = """
            SELECT u.Id,u.Username,u.SellerKey,u.DisplayName,u.IsActive,u.CreatedAtUtc,u.UpdatedAtUtc,
                   (SELECT MAX(s.LastSeenAtUtc) FROM dbo.SellerSessions s WHERE s.SellerUserId=u.Id),
                   (SELECT COUNT(*) FROM dbo.SellerSessions s
                    WHERE s.SellerUserId=u.Id AND s.RevokedAtUtc IS NULL AND s.ExpiresAtUtc>SYSUTCDATETIME())
            FROM dbo.SellerUsers u WHERE u.Id=@id;
            SELECT Extension FROM dbo.SellerUserExtensions WHERE SellerUserId=@id ORDER BY Extension;
            SELECT ProductGroup FROM dbo.SellerUserProductGroups WHERE SellerUserId=@id ORDER BY ProductGroup;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var username = reader.GetString(1);
        var key = reader.GetString(2);
        var name = reader.GetString(3);
        var active = reader.GetBoolean(4);
        var created = TehranClock.AsUtc(reader.GetDateTime(5));
        var updated = TehranClock.AsUtc(reader.GetDateTime(6));
        var lastLogin = reader.IsDBNull(7) ? (DateTime?)null : TehranClock.AsUtc(reader.GetDateTime(7));
        var activeSessions = reader.GetInt32(8);
        var extensions = new List<string>();
        var products = new List<string>();
        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct)) extensions.Add(reader.GetString(0));
        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct)) products.Add(reader.GetString(0));
        return new SellerAdminUserRow(id, username, key, name, active, extensions, products,
            created, updated, lastLogin, activeSessions);
    }

    private static async Task EnsureUniqueAsync(
        SqlConnection connection, SqlTransaction transaction, long? id,
        string normalizedUsername, string sellerKey, CancellationToken ct)
    {
        const string sql = """
            SELECT TOP(1)
                CASE WHEN NormalizedUsername=@username THEN N'USERNAME_EXISTS' ELSE N'SELLER_KEY_EXISTS' END
            FROM dbo.SellerUsers
            WHERE (@id IS NULL OR Id<>@id) AND (NormalizedUsername=@username OR SellerKey=@key);
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = id is null ? DBNull.Value : id.Value;
        command.Parameters.Add("@username", SqlDbType.NVarChar, 80).Value = normalizedUsername;
        command.Parameters.Add("@key", SqlDbType.NVarChar, 80).Value = sellerKey;
        var duplicate = await command.ExecuteScalarAsync(ct) as string;
        if (!string.IsNullOrWhiteSpace(duplicate)) throw new InvalidOperationException(duplicate);
    }

    private static async Task ReplaceAssignmentsAsync(
        SqlConnection connection, SqlTransaction transaction, long id,
        IReadOnlyList<string> extensions, IReadOnlyList<string> productGroups, CancellationToken ct)
    {
        await using (var clear = new SqlCommand(
            "DELETE dbo.SellerUserExtensions WHERE SellerUserId=@id; DELETE dbo.SellerUserProductGroups WHERE SellerUserId=@id;",
            connection, transaction))
        {
            clear.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            await clear.ExecuteNonQueryAsync(ct);
        }
        foreach (var extension in extensions)
        {
            await using var command = new SqlCommand(
                "INSERT dbo.SellerUserExtensions(SellerUserId,Extension) VALUES(@id,@value);", connection, transaction);
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            command.Parameters.Add("@value", SqlDbType.NVarChar, 10).Value = extension;
            await command.ExecuteNonQueryAsync(ct);
        }
        foreach (var group in productGroups)
        {
            await using var command = new SqlCommand(
                "INSERT dbo.SellerUserProductGroups(SellerUserId,ProductGroup) VALUES(@id,@value);", connection, transaction);
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            command.Parameters.Add("@value", SqlDbType.NVarChar, 120).Value = group;
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static ValidatedSellerUser ValidateUser(SellerAdminUserSaveRequest request, bool requirePassword)
    {
        var username = (request.Username ?? string.Empty).Trim();
        var normalized = NormalizeUsername(username);
        var key = (request.SellerKey ?? string.Empty).Trim();
        var name = (request.DisplayName ?? string.Empty).Trim();
        if (username.Length is < 3 or > 80 || !Regex.IsMatch(username, @"^[\p{L}\p{N}._-]+$"))
            throw new ArgumentException("USERNAME_INVALID");
        if (key.Length is < 2 or > 80 || !Regex.IsMatch(key, @"^[A-Za-z0-9._-]+$"))
            throw new ArgumentException("SELLER_KEY_INVALID");
        if (name.Length is < 2 or > 200) throw new ArgumentException("DISPLAY_NAME_INVALID");
        ValidatePassword(request.Password, requirePassword);
        var extensions = (request.Extensions ?? Array.Empty<string>())
            .Select(value => (value ?? string.Empty).Trim())
            .Where(value => Regex.IsMatch(value, @"^\d{2,6}$"))
            .Distinct(StringComparer.Ordinal).Take(20).ToArray();
        if (extensions.Length == 0) throw new ArgumentException("EXTENSION_REQUIRED");
        var groups = (request.ProductGroups ?? Array.Empty<string>())
            .Select(value => (value ?? string.Empty).Trim())
            .Where(value => value.Length > 0)
            .Select(value => value.Length > 120 ? value[..120] : value)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
        return new ValidatedSellerUser(username, normalized, key, name,
            string.IsNullOrWhiteSpace(request.Password) ? null : request.Password, request.IsActive, extensions, groups);
    }

    private static void ValidatePassword(string? password, bool required)
    {
        if (string.IsNullOrEmpty(password))
        {
            if (required) throw new ArgumentException("PASSWORD_REQUIRED");
            return;
        }
        if (password.Length is < 8 or > 200) throw new ArgumentException("PASSWORD_INVALID");
    }

    private static void AddUserParameters(
        SqlCommand command, ValidatedSellerUser value, byte[]? hash = null, byte[]? salt = null)
    {
        command.Parameters.Add("@username", SqlDbType.NVarChar, 80).Value = value.Username;
        command.Parameters.Add("@normalized", SqlDbType.NVarChar, 80).Value = value.NormalizedUsername;
        command.Parameters.Add("@key", SqlDbType.NVarChar, 80).Value = value.SellerKey;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = value.DisplayName;
        command.Parameters.Add("@active", SqlDbType.Bit).Value = value.IsActive;
        if (hash is not null)
        {
            command.Parameters.Add("@hash", SqlDbType.VarBinary, 32).Value = hash;
            command.Parameters.Add("@salt", SqlDbType.VarBinary, 16).Value = salt!;
            command.Parameters.Add("@iterations", SqlDbType.Int).Value = PasswordIterations;
        }
    }

    private static string[] SplitValues(string value)
        => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static byte[] HashPassword(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    private static string NormalizeUsername(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        return Convert.FromBase64String(padded);
    }

    private sealed record ValidatedSellerUser(
        string Username,
        string NormalizedUsername,
        string SellerKey,
        string DisplayName,
        string? Password,
        bool IsActive,
        string[] Extensions,
        string[] ProductGroups);
}
