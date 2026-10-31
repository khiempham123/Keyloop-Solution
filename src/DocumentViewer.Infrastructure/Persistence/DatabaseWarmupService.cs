using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocumentViewer.Infrastructure.Persistence;

/// <summary>
/// Opens one database connection shortly after startup so that no user request is the
/// one that pays the cold cross-region handshake.
///
/// It is a <see cref="BackgroundService"/> on purpose: a warm-up that blocked startup
/// would turn an unreachable database into a service that will not boot, and spec §7.4
/// is explicit that this service must keep serving cached documents when the database
/// is down. Failure here is logged and nothing more.
/// </summary>
public sealed class DatabaseWarmupService(
    IDbContextFactory<DocumentViewerDbContext> dbFactory,
    ILogger<DatabaseWarmupService> logger) : BackgroundService
{
    private static readonly TimeSpan WarmupBudget = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(WarmupBudget);

            await using var db = await dbFactory.CreateDbContextAsync(cts.Token);
            var reachable = await db.Database.CanConnectAsync(cts.Token);

            if (reachable)
            {
                logger.LogInformation("Database connection pool warmed; no request will pay the handshake.");
            }
            else
            {
                logger.LogWarning(
                    "Database warm-up could not reach PostgreSQL; the service continues without cache and audit.");
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Database warm-up did not finish within {Budget}; the service continues without cache and audit.",
                WarmupBudget);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Database warm-up failed; the service continues without cache and audit.");
        }
    }
}
