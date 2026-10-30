using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using DocumentViewer.Application.Sources;
using DocumentViewer.Domain;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace DocumentViewer.Infrastructure.Sources;

internal static class UpstreamClientExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<UpstreamResult<IReadOnlyList<VehicleDocument>>> ExecuteAsync<TPayload>(
        HttpClient httpClient,
        string relativePath,
        SourceSystem system,
        Vin vin,
        IDocumentNormalizer<TPayload> normalizer,
        ILogger logger,
        CancellationToken ct)
    {
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            using var response = await httpClient.GetAsync(relativePath, HttpCompletionOption.ResponseHeadersRead, ct);
            var elapsed = Stopwatch.GetElapsedTime(startedAt);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                logger.LogWarning(
                    "Upstream {SourceSystem} returned HTTP {StatusCode} for VIN {VinHash} ({VinMasked}) in {DurationMs}ms",
                    system,
                    statusCode,
                    vin.Hash12,
                    vin.Masked,
                    (long)elapsed.TotalMilliseconds);

                return new UpstreamResult<IReadOnlyList<VehicleDocument>>.Failed(
                    new UpstreamFailure(
                        UpstreamFailureReason.HttpError,
                        $"Upstream {system} responded with HTTP {statusCode}.",
                        statusCode),
                    elapsed);
            }

            var payload = await response.Content.ReadFromJsonAsync<TPayload>(JsonOptions, ct);
            elapsed = Stopwatch.GetElapsedTime(startedAt);

            if (payload is null)
            {
                logger.LogWarning(
                    "Upstream {SourceSystem} returned null JSON payload for VIN {VinHash} ({VinMasked})",
                    system,
                    vin.Hash12,
                    vin.Masked);

                return new UpstreamResult<IReadOnlyList<VehicleDocument>>.Failed(
                    new UpstreamFailure(
                        UpstreamFailureReason.InvalidPayload,
                        $"Upstream {system} returned an empty or null JSON body.",
                        (int)response.StatusCode),
                    elapsed);
            }

            var documents = normalizer.Normalize(payload);
            return new UpstreamResult<IReadOnlyList<VehicleDocument>>.Ok(documents, elapsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutRejectedException ex)
        {
            return Fail(system, vin, startedAt, UpstreamFailureReason.Timeout, ex.Message, null, logger);
        }
        catch (TaskCanceledException ex)
        {
            return Fail(system, vin, startedAt, UpstreamFailureReason.Timeout, ex.Message, null, logger);
        }
        catch (BrokenCircuitException ex)
        {
            return Fail(system, vin, startedAt, UpstreamFailureReason.CircuitOpen, ex.Message, null, logger);
        }
        catch (JsonException ex)
        {
            return Fail(system, vin, startedAt, UpstreamFailureReason.InvalidPayload, ex.Message, null, logger);
        }
        catch (HttpRequestException ex)
        {
            return Fail(
                system,
                vin,
                startedAt,
                UpstreamFailureReason.HttpError,
                ex.Message,
                ex.StatusCode is { } code ? (int)code : null,
                logger);
        }
        catch (Exception ex)
        {
            return Fail(system, vin, startedAt, UpstreamFailureReason.Unknown, ex.Message, null, logger);
        }
    }

    private static UpstreamResult<IReadOnlyList<VehicleDocument>>.Failed Fail(
        SourceSystem system,
        Vin vin,
        long startedAt,
        UpstreamFailureReason reason,
        string message,
        int? statusCode,
        ILogger logger)
    {
        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        logger.LogWarning(
            "Upstream {SourceSystem} degraded ({Reason}) for VIN {VinHash} ({VinMasked}) after {DurationMs}ms: {Message}",
            system,
            reason,
            vin.Hash12,
            vin.Masked,
            (long)elapsed.TotalMilliseconds,
            message);

        return new UpstreamResult<IReadOnlyList<VehicleDocument>>.Failed(
            new UpstreamFailure(reason, message, statusCode),
            elapsed);
    }
}
