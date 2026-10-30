using DocumentViewer.Application.Observability;
using DocumentViewer.Application.Persistence;
using DocumentViewer.Application.Sources;
using DocumentViewer.Infrastructure.Sources.Sales;
using DocumentViewer.Infrastructure.Sources.Service;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace DocumentViewer.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddDocumentSources(this IServiceCollection services, IConfiguration config)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDocumentCacheRepository, NullDocumentCacheRepository>();
        services.TryAddSingleton<IAggregationMetrics, NullAggregationMetrics>();

        services.AddSingleton<IDocumentNormalizer<SalesDocumentsResponse>, SalesDocumentNormalizer>();
        services.AddSingleton<IDocumentNormalizer<ServiceDocumentsResponse>, ServiceDocumentNormalizer>();

        var salesBaseAddress = config["Upstreams:SalesSystem:BaseAddress"] ?? "http://localhost:5101";
        var serviceBaseAddress = config["Upstreams:ServiceSystem:BaseAddress"] ?? "http://localhost:5102";
        var minimumThroughput = int.TryParse(config["Upstreams:CircuitBreakerMinimumThroughput"], out var parsedThroughput) && parsedThroughput >= 2
            ? parsedThroughput
            : 5;

        services
            .AddHttpClient<IDocumentSource, SalesSystemClient>("SalesSystem", client =>
            {
                client.BaseAddress = new Uri(salesBaseAddress);
            })
            .AddStandardResilienceHandler(options => ConfigureResilience(options, minimumThroughput));

        services
            .AddHttpClient<IDocumentSource, ServiceSystemClient>("ServiceSystem", client =>
            {
                client.BaseAddress = new Uri(serviceBaseAddress);
            })
            .AddStandardResilienceHandler(options => ConfigureResilience(options, minimumThroughput));

        return services;
    }

    private static void ConfigureResilience(HttpStandardResilienceOptions options, int minimumThroughput)
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
    }
}
