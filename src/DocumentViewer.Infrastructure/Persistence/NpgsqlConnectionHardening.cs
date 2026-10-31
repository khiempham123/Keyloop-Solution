using System.Data.Common;
using Npgsql;

namespace DocumentViewer.Infrastructure.Persistence;

/// <summary>
/// Applies pool-warmth and connect-timeout defaults to an operator-supplied connection
/// string.
///
/// Found against live Supabase: a cold pool makes the first database operation pay the
/// full TLS + Supavisor tenant lookup + SASL handshake. Cross-region that is seconds,
/// not milliseconds, and it blew both the readiness budget and the stale-cache fallback.
/// Keeping one connection warm moves that cost off the request path entirely.
///
/// Anything the operator set explicitly is left alone — this fills gaps, it does not
/// override decisions.
/// </summary>
public static class NpgsqlConnectionHardening
{
    private const int ConnectTimeoutSeconds = 15;
    private const int MinimumPoolSize = 1;
    private const int ConnectionIdleLifetimeSeconds = 300;

    public static string? Harden(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception)
        {
            // Not parseable as a connection string: hand it back untouched and let the
            // database provider produce its own, clearer error.
            return connectionString;
        }

        if (string.IsNullOrWhiteSpace(builder.Host))
        {
            return connectionString;
        }

        // NpgsqlConnectionStringBuilder.ContainsKey answers "is this a keyword I know",
        // not "did the operator set it", so it is true for every Npgsql option. The
        // base builder holds only the keys actually present in the supplied string.
        var supplied = new DbConnectionStringBuilder { ConnectionString = connectionString };

        if (!Supplied(supplied, "Timeout"))
        {
            builder.Timeout = ConnectTimeoutSeconds;
        }

        if (!Supplied(supplied, "Connection Idle Lifetime", "ConnectionIdleLifetime"))
        {
            builder.ConnectionIdleLifetime = ConnectionIdleLifetimeSeconds;
        }

        // Npgsql rejects a minimum pool size when pooling is off, and a non-pooled
        // connection has no pool to keep warm in the first place.
        if (builder.Pooling && !Supplied(supplied, "Minimum Pool Size", "MinPoolSize", "MinimumPoolSize"))
        {
            builder.MinPoolSize = MinimumPoolSize;
        }

        return builder.ConnectionString;
    }

    private static bool Supplied(DbConnectionStringBuilder supplied, params string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (supplied.ContainsKey(keyword))
            {
                return true;
            }
        }

        return false;
    }
}
