using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MsfsTelemetry.Web.Data;

/// <summary>A row of the <c>users</c> table.</summary>
public sealed record User(
    int UserId,
    string GoogleId,
    string Email,
    bool EmailVerified,
    string DisplayName,
    string? GivenName,
    string? FamilyName,
    string? AvatarUrl,
    string? Bio,
    string? HomeAirportIcao,
    string? TimeZone,
    bool IsActive,
    int LoginCount,
    DateTimeOffset? LastLoginUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>What Google tells us about a person, taken from a validated ID token.</summary>
public sealed record GoogleIdentity(
    string GoogleId,
    string Email,
    bool EmailVerified,
    string? Name,
    string? GivenName,
    string? FamilyName,
    string? AvatarUrl);

/// <summary>The fields a user can change on their own profile.</summary>
public sealed record ProfileUpdate(string DisplayName, string? Bio, string? HomeAirportIcao, string? TimeZone);

/// <summary>Data access for the <c>users</c> table.</summary>
public sealed class UsersRepository(AirportsDb db)
{
    private const string SelectColumns = """
        User_ID, Google_ID, Email, Email_Verified, Display_Name, Given_Name, Family_Name, Avatar_URL,
        Bio, Home_Airport_ICAO, Time_Zone, Is_Active, Login_Count, Last_Login_UTC, Created_UTC, Updated_UTC
        """;

    /// <summary>
    /// Records a Google sign-in: creates the user on first sign-in, otherwise refreshes the
    /// Google-sourced fields and the login stats. Returns the user and whether it was just created.
    /// </summary>
    /// <remarks>
    /// Returning users take a plain UPDATE. Only when no row exists does it run
    /// INSERT … ON CONFLICT, which is atomic, so two simultaneous first sign-ins still end up with
    /// one row. (UPDATE comes first because, with AUTOINCREMENT, SQLite uses up a User_ID on
    /// every INSERT attempt, even one that turns into an update. Upserting on every sign-in would
    /// leave gaps in the IDs.)
    /// Display_Name is only seeded on insert; after that it belongs to the user.
    /// The login is counted even for a deactivated user; the caller decides whether to let them in.
    /// </remarks>
    public async Task<(User User, bool Created)> UpsertFromGoogleAsync(GoogleIdentity identity, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);

        await using (var update = connection.CreateCommand())
        {
            update.CommandText = $"""
                UPDATE users SET
                    Email          = $email,
                    Email_Verified = $emailVerified,
                    Given_Name     = $givenName,
                    Family_Name    = $familyName,
                    Avatar_URL     = $avatarUrl,
                    Login_Count    = Login_Count + 1,
                    Last_Login_UTC = {Schema.UtcNowSql},
                    Updated_UTC    = {Schema.UtcNowSql}
                WHERE Google_ID = $googleId
                RETURNING {SelectColumns};
                """;
            AddGoogleParameters(update, identity);

            await using var reader = await update.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return (Read(reader), false);
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO users (Google_ID, Email, Email_Verified, Display_Name, Given_Name, Family_Name, Avatar_URL,
                               Login_Count, Last_Login_UTC)
            VALUES ($googleId, $email, $emailVerified, $displayName, $givenName, $familyName, $avatarUrl,
                    1, {Schema.UtcNowSql})
            ON CONFLICT (Google_ID) DO UPDATE SET
                Email          = excluded.Email,
                Email_Verified = excluded.Email_Verified,
                Given_Name     = excluded.Given_Name,
                Family_Name    = excluded.Family_Name,
                Avatar_URL     = excluded.Avatar_URL,
                Login_Count    = users.Login_Count + 1,
                Last_Login_UTC = excluded.Last_Login_UTC,
                Updated_UTC    = {Schema.UtcNowSql}
            RETURNING {SelectColumns};
            """;

        AddGoogleParameters(command, identity);
        command.Parameters.AddWithValue("$displayName", SeedDisplayName(identity));

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            var user = Read(reader);

            // A new row starts at Login_Count 1. If another request inserted it first, the
            // conflict branch incremented an existing count, so it's above 1.
            return (user, user.LoginCount == 1);
        }
    }

    private static void AddGoogleParameters(SqliteCommand command, GoogleIdentity identity)
    {
        command.Parameters.AddWithValue("$googleId", identity.GoogleId);
        command.Parameters.AddWithValue("$email", identity.Email);
        command.Parameters.AddWithValue("$emailVerified", identity.EmailVerified);
        command.Parameters.AddWithValue("$givenName", (object?)identity.GivenName ?? DBNull.Value);
        command.Parameters.AddWithValue("$familyName", (object?)identity.FamilyName ?? DBNull.Value);
        command.Parameters.AddWithValue("$avatarUrl", (object?)identity.AvatarUrl ?? DBNull.Value);
    }

    public async Task<User?> GetByIdAsync(int userId, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM users WHERE User_ID = $userId;";
        command.Parameters.AddWithValue("$userId", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    /// <summary>Cheap check used on every authenticated request.</summary>
    public async Task<bool> IsActiveAsync(int userId, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Is_Active FROM users WHERE User_ID = $userId;";
        command.Parameters.AddWithValue("$userId", userId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0L) == 1;
    }

    /// <summary>Updates the user-editable fields. Returns null if the user doesn't exist or is inactive.</summary>
    public async Task<User?> UpdateProfileAsync(int userId, ProfileUpdate update, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE users SET
                Display_Name      = $displayName,
                Bio               = $bio,
                Home_Airport_ICAO = $homeAirport,
                Time_Zone         = $timeZone,
                Updated_UTC       = {Schema.UtcNowSql}
            WHERE User_ID = $userId AND Is_Active = 1
            RETURNING {SelectColumns};
            """;

        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$displayName", update.DisplayName);
        command.Parameters.AddWithValue("$bio", (object?)update.Bio ?? DBNull.Value);
        command.Parameters.AddWithValue("$homeAirport", (object?)update.HomeAirportIcao ?? DBNull.Value);
        command.Parameters.AddWithValue("$timeZone", (object?)update.TimeZone ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static string SeedDisplayName(GoogleIdentity identity)
    {
        var name = !string.IsNullOrWhiteSpace(identity.Name)
            ? identity.Name
            : identity.Email.Split('@')[0];
        name = name.Trim();
        return name.Length <= ProfileRules.DisplayNameMaxLength ? name : name[..ProfileRules.DisplayNameMaxLength];
    }

    private static User Read(SqliteDataReader r) => new(
        UserId: r.GetInt32(0),
        GoogleId: r.GetString(1),
        Email: r.GetString(2),
        EmailVerified: r.GetBoolean(3),
        DisplayName: r.GetString(4),
        GivenName: r.IsDBNull(5) ? null : r.GetString(5),
        FamilyName: r.IsDBNull(6) ? null : r.GetString(6),
        AvatarUrl: r.IsDBNull(7) ? null : r.GetString(7),
        Bio: r.IsDBNull(8) ? null : r.GetString(8),
        HomeAirportIcao: r.IsDBNull(9) ? null : r.GetString(9),
        TimeZone: r.IsDBNull(10) ? null : r.GetString(10),
        IsActive: r.GetBoolean(11),
        LoginCount: r.GetInt32(12),
        LastLoginUtc: r.IsDBNull(13) ? null : ParseUtc(r.GetString(13)),
        CreatedUtc: ParseUtc(r.GetString(14)),
        UpdatedUtc: ParseUtc(r.GetString(15)));

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}

/// <summary>Limits on user-editable profile fields, shared by validation and the repository.</summary>
public static class ProfileRules
{
    public const int DisplayNameMaxLength = 50;
    public const int BioMaxLength = 500;
    public const int TimeZoneMaxLength = 64;
}
