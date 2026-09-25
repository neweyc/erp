using Npgsql;

namespace AppPlatform.Auth;

/// <summary>
/// The only code that calls <c>identity_v1.session_context</c> and
/// <c>identity_v1.touch_session</c>.
///
/// Both are SECURITY DEFINER functions rather than a view, because a grant on a view is a
/// grant to read ALL of it and a session id is credential-equivalent: enumeration must not
/// be possible even for a service that legitimately resolves sessions.
/// </summary>
public sealed class NpgsqlSessionStore(NpgsqlDataSource dataSource) : ISessionStore
{
    public async Task<SessionContext?> FindAsync(
        Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT * FROM identity_v1.session_context($1)");
        command.Parameters.AddWithValue(sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // No row is the ordinary case for an expired or forged cookie. Returning null
        // rather than throwing keeps that a 401 instead of a 500.
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new SessionContext
        {
            SessionId = reader.GetGuid(reader.GetOrdinal("session_id")),
            UserId = reader.GetGuid(reader.GetOrdinal("user_id")),
            TenantId = reader.GetInt32(reader.GetOrdinal("tenant_id")),
            CompanyId = reader.GetInt32(reader.GetOrdinal("company_id")),
            TenantStatus = ParseStatus(reader.GetString(reader.GetOrdinal("tenant_status"))),
            Role = reader.GetString(reader.GetOrdinal("role")),
            UserActive = reader.GetBoolean(reader.GetOrdinal("user_active")),
            MfaSatisfied = reader.GetBoolean(reader.GetOrdinal("mfa_satisfied")),
            LastSeenAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_seen_at")),
            AbsoluteExpiry = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("absolute_expiry")),
            RevokedAt = NullableValue<DateTimeOffset>(reader, "revoked_at"),
            EmployeeId = NullableValue<Guid>(reader, "employee_id"),
            LicensedApps = reader.GetFieldValue<string[]>(reader.GetOrdinal("licensed_apps")),
            IdleTimeoutMinutes = NullableValue<int>(reader, "idle_timeout_minutes"),
        };
    }

    public async Task TouchAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("SELECT identity_v1.touch_session($1)");
        command.Parameters.AddWithValue(sessionId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Columns are read BY NAME, never by position. The function returns a TABLE, which is
    /// positional, so a migration that reorders two same-typed columns would silently
    /// transpose their values here — in the code that decides who gets in.
    /// </summary>
    private static T? NullableValue<T>(NpgsqlDataReader reader, string column) where T : struct
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);
    }

    /// <summary>
    /// Parsed CASE-INSENSITIVELY, because EF's enum-to-string conversion writes the enum's own
    /// name — "Active", not "active".
    ///
    /// The first version of this matched lowercase only, so every valid session fell through to
    /// Retired and was rejected. It passed its tests because the integration fixture seeded
    /// lowercase by hand: the fixture accommodated the bug instead of mirroring what the real
    /// migrations write. Hence the session-resolution test that runs against the generated
    /// scripts rather than a hand-written schema.
    ///
    /// An unrecognised status is still treated as RETIRED. If the platform gains a lifecycle
    /// state this build has never heard of, refusing access is the recoverable failure;
    /// granting it is not.
    /// </summary>
    private static TenantStatus ParseStatus(string value)
        => Enum.TryParse<TenantStatus>(value, ignoreCase: true, out var status)
           && Enum.IsDefined(status)
            ? status
            : TenantStatus.Retired;
}
