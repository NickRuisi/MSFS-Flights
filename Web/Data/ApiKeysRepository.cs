using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace MsfsTelemetry.Web.Data;

/// <summary>A user's API key as shown on the profile page.</summary>
/// <param name="Key">The full key, or null if it can no longer be decrypted (see <see cref="ApiKeysRepository"/>).</param>
/// <param name="Prefix">The first characters of the key. Always available.</param>
public sealed record ApiKey(string? Key, string Prefix, DateTimeOffset CreatedUtc, DateTimeOffset? LastUsedUtc);

/// <summary>Data access for the <c>api_keys</c> table: one key per user.</summary>
/// <remarks>
/// Keys are 256 random bits, so a plain SHA-256 hash is enough to look them up safely (no salt or
/// slow hash needed: there's nothing to brute-force). The key is also kept encrypted with Data
/// Protection so the user can see and copy it again. If the Data Protection keys are lost (for
/// example the app pool's profile was reset), the stored key can't be decrypted any more. It still
/// works, because checks use the hash, but it can't be shown; the user replaces it to get a new one.
/// </remarks>
public sealed class ApiKeysRepository(AirportsDb db, IDataProtectionProvider dataProtection)
{
    /// <summary>Every key starts with this, so it's recognisable in config files and secret scanners.</summary>
    public const string KeyPrefix = "fsf_";

    private const int KeyBytes = 32;
    private const int DisplayPrefixLength = 12;   // "fsf_" + 8 characters

    /// <summary>How often Last_Used_UTC is rewritten for a busy key. Saves a write on every request.</summary>
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    private readonly IDataProtector _protector = dataProtection.CreateProtector("MsfsTelemetry.Web.ApiKeys.v1");

    /// <summary>The user's key, created on first request.</summary>
    public async Task<ApiKey> GetOrCreateAsync(int userId, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        if (await ReadAsync(connection, userId, cancellationToken) is { } existing)
        {
            return existing;
        }

        // Two first requests at once: ON CONFLICT keeps whichever INSERT came first, and both read it back.
        var key = NewKey();
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO api_keys (User_ID, Key_Hash, Key_Protected, Key_Prefix)
                VALUES ($userId, $hash, $protected, $prefix)
                ON CONFLICT (User_ID) DO NOTHING;
                """;
            AddKeyParameters(insert, userId, key);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        return await ReadAsync(connection, userId, cancellationToken)
            ?? throw new InvalidOperationException($"API key for user {userId} was not saved.");
    }

    /// <summary>Replaces the user's key with a new one. The old key stops working immediately.</summary>
    public async Task<ApiKey> ReplaceAsync(int userId, CancellationToken cancellationToken)
    {
        var key = NewKey();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO api_keys (User_ID, Key_Hash, Key_Protected, Key_Prefix)
            VALUES ($userId, $hash, $protected, $prefix)
            ON CONFLICT (User_ID) DO UPDATE SET
                Key_Hash      = excluded.Key_Hash,
                Key_Protected = excluded.Key_Protected,
                Key_Prefix    = excluded.Key_Prefix,
                Created_UTC   = {Schema.UtcNowSql},
                Last_Used_UTC = NULL
            RETURNING Key_Prefix, Created_UTC;
            """;
        AddKeyParameters(command, userId, key);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new ApiKey(key, reader.GetString(0), ParseUtc(reader.GetString(1)), null);
    }

    /// <summary>
    /// The active user a key belongs to, or null if the key is unknown, replaced, or its user is
    /// deactivated. Records when the key was last used. For the companion app's upload endpoints.
    /// </summary>
    public async Task<int?> FindUserIdAsync(string? key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT k.User_ID, k.Last_Used_UTC
            FROM api_keys k JOIN users u ON u.User_ID = k.User_ID
            WHERE k.Key_Hash = $hash AND u.Is_Active = 1;
            """;
        command.Parameters.AddWithValue("$hash", Hash(key.Trim()));

        int userId;
        DateTimeOffset? lastUsed;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
            userId = reader.GetInt32(0);
            lastUsed = reader.IsDBNull(1) ? null : ParseUtc(reader.GetString(1));
        }

        if (lastUsed is null || DateTimeOffset.UtcNow - lastUsed > LastUsedResolution)
        {
            await using var touch = connection.CreateCommand();
            touch.CommandText = $"UPDATE api_keys SET Last_Used_UTC = {Schema.UtcNowSql} WHERE User_ID = $userId;";
            touch.Parameters.AddWithValue("$userId", userId);
            await touch.ExecuteNonQueryAsync(cancellationToken);
        }

        return userId;
    }

    private async Task<ApiKey?> ReadAsync(SqliteConnection connection, int userId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key_Protected, Key_Prefix, Created_UTC, Last_Used_UTC FROM api_keys WHERE User_ID = $userId;";
        command.Parameters.AddWithValue("$userId", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ApiKey(
            Key: TryUnprotect(reader.GetString(0)),
            Prefix: reader.GetString(1),
            CreatedUtc: ParseUtc(reader.GetString(2)),
            LastUsedUtc: reader.IsDBNull(3) ? null : ParseUtc(reader.GetString(3)));
    }

    private void AddKeyParameters(SqliteCommand command, int userId, string key)
    {
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$hash", Hash(key));
        command.Parameters.AddWithValue("$protected", _protector.Protect(key));
        command.Parameters.AddWithValue("$prefix", key[..DisplayPrefixLength]);
    }

    private string? TryUnprotect(string protectedKey)
    {
        try
        {
            return _protector.Unprotect(protectedKey);
        }
        catch (CryptographicException)
        {
            return null;   // Data Protection keys changed; the key still works but can't be shown
        }
    }

    private static string NewKey() => KeyPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(KeyBytes));

    private static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
