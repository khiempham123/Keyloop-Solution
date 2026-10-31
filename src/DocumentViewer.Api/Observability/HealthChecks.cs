using DocumentViewer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DocumentViewer.Api.Observability;

public static class HealthChecks
{
    private const string LiveTag = "live";
    private const string ReadyTag = "ready";

    public static IServiceCollection AddDocumentViewerHealthChecks(this IServiceCollection services)
    {
        services.AddHttpClient("UpstreamHealthProbe", static client =>
        {
            client.Timeout = TimeSpan.FromMilliseconds(1500);
        });

        services
            .AddHealthChecks()
            .AddCheck("self", static () => HealthCheckResult.Healthy("Process is live."), tags: [LiveTag])
            .AddCheck<PostgresReadinessHealthCheck>("postgres", tags: [ReadyTag]);

        return services;
    }

    public static IEndpointRouteBuilder MapDocumentViewerHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = static check => check.Tags.Contains(LiveTag)
        });

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = static check => check.Tags.Contains(ReadyTag)
        });

        endpoints.MapGet("/health/upstream", HandleUpstreamProbeAsync)
            .WithName("GetUpstreamHealth")
            .WithSummary("Informational probe of upstream systems of record; always returns 200 OK.");

        return endpoints;
    }

    private static async Task<IResult> HandleUpstreamProbeAsync(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var salesBase = configuration["Upstreams:SalesSystem:BaseAddress"] ?? "http://localhost:5101";
        var serviceBase = configuration["Upstreams:ServiceSystem:BaseAddress"] ?? "http://localhost:5102";

        var salesTask = ProbeAsync(httpClientFactory, salesBase, ct);
        var serviceTask = ProbeAsync(httpClientFactory, serviceBase, ct);
        await Task.WhenAll(salesTask, serviceTask);

        var salesStatus = await salesTask;
        var serviceStatus = await serviceTask;
        var overall = salesStatus == "reachable" && serviceStatus == "reachable"
            ? "ok"
            : "degraded";

        return TypedResults.Ok(new UpstreamHealthResponse(
            Status: overall,
            Sources: new Dictionary<string, string>
            {
                ["SalesSystem"] = salesStatus,
                ["ServiceSystem"] = serviceStatus
            }));
    }

    private static async Task<string> ProbeAsync(
        IHttpClientFactory httpClientFactory,
        string baseAddress,
        CancellationToken ct)
    {
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromMilliseconds(1500));

            var client = httpClientFactory.CreateClient("UpstreamHealthProbe");
            var healthUri = new Uri(new Uri(baseAddress.TrimEnd('/') + "/"), "health");
            using var response = await client.GetAsync(healthUri, probeCts.Token);
            return response.IsSuccessStatusCode ? "reachable" : $"http_{(int)response.StatusCode}";
        }
        catch
        {
            return "unreachable";
        }
    }

    private sealed record UpstreamHealthResponse(
        string Status,
        IReadOnlyDictionary<string, string> Sources);

    /// <summary>
    /// How long readiness waits for PostgreSQL. Configurable because the right value
    /// depends on where the database is: a local instance answers in under a
    /// millisecond, while a cold cross-region managed pooler needs seconds for its
    /// TLS + tenant-lookup + SASL handshake. A hardcoded 2s silently failed readiness
    /// against live Supabase.
    /// </summary>
    private const int DefaultReadinessBudgetMs = 5_000;

    private static TimeSpan ReadinessBudget(IConfiguration configuration)
    {
        var configured = configuration.GetValue<int?>("HealthChecks:DatabaseTimeoutMs");
        return TimeSpan.FromMilliseconds(
            configured is > 0 ? configured.Value : DefaultReadinessBudgetMs);
    }

    private sealed class PostgresReadinessHealthCheck(
        IDbContextFactory<DocumentViewerDbContext> dbFactory,
        IConfiguration configuration) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            var connectionString = configuration.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return HealthCheckResult.Unhealthy("Database connection string is not configured.");
            }

            var budget = ReadinessBudget(configuration);

            // Npgsql does not reliably observe a cancellation token while it is still
            // opening a physical connection, so the budget is enforced by racing the
            // probe rather than by cancelling it. Found against live Supabase: a short
            // budget was ignored and readiness ran for the full connect timeout.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var probe = ProbeAsync(cts.Token);
            var finished = await Task.WhenAny(probe, Task.Delay(budget, cancellationToken));

            if (finished != probe)
            {
                cts.Cancel();
                Forget(probe);
                return HealthCheckResult.Unhealthy(
                    $"PostgreSQL did not answer within {budget.TotalMilliseconds:F0}ms.");
            }

            return await probe;
        }

        private async Task<HealthCheckResult> ProbeAsync(CancellationToken ct)
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var canConnect = await db.Database.CanConnectAsync(ct);
                return canConnect
                    ? HealthCheckResult.Healthy("PostgreSQL is reachable.")
                    : HealthCheckResult.Unhealthy("PostgreSQL failed readiness check.");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("PostgreSQL is unreachable.", ex);
            }
        }

        private static void Forget(Task task) =>
            task.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
    }
}
