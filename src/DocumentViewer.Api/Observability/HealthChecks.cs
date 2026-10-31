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

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(2));

                await using var db = await dbFactory.CreateDbContextAsync(cts.Token);
                var canConnect = await db.Database.CanConnectAsync(cts.Token);
                return canConnect
                    ? HealthCheckResult.Healthy("PostgreSQL is reachable.")
                    : HealthCheckResult.Unhealthy("PostgreSQL failed readiness check.");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("PostgreSQL is unreachable.", ex);
            }
        }
    }
}
