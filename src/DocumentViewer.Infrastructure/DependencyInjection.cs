using DocumentViewer.Application.Observability;
using DocumentViewer.Application.Persistence;
using DocumentViewer.Application.Sources;
using DocumentViewer.Domain;
using DocumentViewer.Infrastructure.Observability;
using DocumentViewer.Infrastructure.Persistence;
using DocumentViewer.Infrastructure.Sources.Sales;
using DocumentViewer.Infrastructure.Sources.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace DocumentViewer.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddDocumentSources(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDocumentCacheRepository, NullDocumentCacheRepository>();
        services.TryAddSingleton<ISearchAuditRepository, NullSearchAuditRepository>();
        services.AddSingleton<AggregationMetrics>();
        services.RemoveAll<IAggregationMetrics>();
        services.AddSingleton<IAggregationMetrics>(static sp => sp.GetRequiredService<AggregationMetrics>());
        services.AddTransient<CorrelationPropagationHandler>();

        services.AddSingleton<IDocumentNormalizer<SalesDocumentsResponse>, SalesDocumentNormalizer>();
        services.AddSingleton<IDocumentNormalizer<ServiceDocumentsResponse>, ServiceDocumentNormalizer>();

        // Configuration is read from the service provider, not captured here. Reading it
        // at registration time bakes in whatever appsettings.json said and silently
        // ignores every later source — which is how an integration test that points the
        // upstreams at a stub ends up calling the real ones instead.
        services
            .AddHttpClient<IDocumentSource, SalesSystemClient>("SalesSystem", (sp, client) =>
            {
                client.BaseAddress = new Uri(BaseAddress(sp, "SalesSystem", "http://localhost:5101"));
            })
            .AddHttpMessageHandler<CorrelationPropagationHandler>()
            .AddStandardResilienceHandler()
            .Configure((options, sp) => ConfigureResilience(
                options,
                MinimumThroughput(sp),
                SourceSystem.SalesSystem,
                sp.GetRequiredService<AggregationMetrics>()));

        services
            .AddHttpClient<IDocumentSource, ServiceSystemClient>("ServiceSystem", (sp, client) =>
            {
                client.BaseAddress = new Uri(BaseAddress(sp, "ServiceSystem", "http://localhost:5102"));
            })
            .AddHttpMessageHandler<CorrelationPropagationHandler>()
            .AddStandardResilienceHandler()
            .Configure((options, sp) => ConfigureResilience(
                options,
                MinimumThroughput(sp),
                SourceSystem.ServiceSystem,
                sp.GetRequiredService<AggregationMetrics>()));

        return services;
    }

    private static string BaseAddress(IServiceProvider sp, string system, string fallback)
    {
        var configured = sp.GetRequiredService<IConfiguration>()[$"Upstreams:{system}:BaseAddress"];
        return string.IsNullOrWhiteSpace(configured) ? fallback : configured;
    }

    private static int MinimumThroughput(IServiceProvider sp)
    {
        const int defaultMinimumThroughput = 5;
        var configured = sp.GetRequiredService<IConfiguration>()["Upstreams:CircuitBreakerMinimumThroughput"];
        return int.TryParse(configured, out var parsed) && parsed >= 2
            ? parsed
            : defaultMinimumThroughput;
    }

    public static IServiceCollection AddPersistence(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContextFactory<DocumentViewerDbContext>((sp, options) =>
        {
            var cs = sp.GetRequiredService<IConfiguration>().GetConnectionString("Default");
            options.UseNpgsql(string.IsNullOrWhiteSpace(cs)
                ? "Host=127.0.0.1;Port=5432;Database=unconfigured"
                // Keeps one connection warm so no request pays the cold cross-region
                // handshake. See NpgsqlConnectionHardening.
                : NpgsqlConnectionHardening.Harden(cs));
        });

        // Warms the pool after startup; never blocks boot, never throws.
        services.AddHostedService<DatabaseWarmupService>();

        services.RemoveAll<IDocumentCacheRepository>();
        services.AddSingleton<IDocumentCacheRepository>(sp =>
        {
            var cs = sp.GetRequiredService<IConfiguration>().GetConnectionString("Default");
            return string.IsNullOrWhiteSpace(cs)
                ? new NullDocumentCacheRepository()
                : ActivatorUtilities.CreateInstance<DocumentCacheRepository>(sp);
        });

        services.RemoveAll<ISearchAuditRepository>();
        services.AddSingleton<ISearchAuditRepository>(sp =>
        {
            var cs = sp.GetRequiredService<IConfiguration>().GetConnectionString("Default");
            return string.IsNullOrWhiteSpace(cs)
                ? new NullSearchAuditRepository()
                : ActivatorUtilities.CreateInstance<SearchAuditRepository>(sp);
        });

        return services;
    }

    private static void ConfigureResilience(
        HttpStandardResilienceOptions options,
        int minimumThroughput,
        SourceSystem system,
        AggregationMetrics metrics)
    {
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(5);

        options.Retry.MaxRetryAttempts = 2;
        options.Retry.BackoffType = DelayBackoffType.Exponential;
        options.Retry.UseJitter = true;
        options.Retry.Delay = TimeSpan.FromMilliseconds(200);
        options.Retry.ShouldHandle = static args =>
            ValueTask.FromResult(HttpClientResiliencePredicates.IsTransient(args.Outcome));

        options.CircuitBreaker.FailureRatio = 0.5;
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);
        options.CircuitBreaker.MinimumThroughput = minimumThroughput;
        options.CircuitBreaker.ShouldHandle = static args =>
            ValueTask.FromResult(HttpClientResiliencePredicates.IsTransient(args.Outcome));
        options.CircuitBreaker.OnOpened = _ =>
        {
            metrics.SetCircuitState(system, 1);
            return ValueTask.CompletedTask;
        };
        options.CircuitBreaker.OnClosed = _ =>
        {
            metrics.SetCircuitState(system, 0);
            return ValueTask.CompletedTask;
        };
        options.CircuitBreaker.OnHalfOpened = _ =>
        {
            metrics.SetCircuitState(system, 2);
            return ValueTask.CompletedTask;
        };
    }
}
