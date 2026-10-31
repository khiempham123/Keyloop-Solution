using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using DocumentViewer.Application.Observability;
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

    /// <summary>
    /// The largest upstream body this service will read. A document list for one vehicle
    /// is kilobytes; anything approaching this is a misbehaving or hostile upstream.
    /// </summary>
    private const long MaxBodyBytes = 4L * 1024 * 1024;

    /// <summary>
    /// How long the body may take to arrive. The Polly pipeline has already returned by
    /// the time the body is read, so without this the read is bounded only by the
    /// whole-request timeout — and running that long is the condition under which a
    /// healthy source's documents are at risk.
    /// </summary>
    private static readonly TimeSpan BodyReadBudget = TimeSpan.FromSeconds(2);

    public static async Task<UpstreamResult<IReadOnlyList<VehicleDocument>>> ExecuteAsync<TPayload>(
        HttpClient httpClient,
        string relativePath,
        SourceSystem system,
        Vin vin,
        IDocumentNormalizer<TPayload> normalizer,
        ILogger logger,
        CancellationToken ct)
    {
        using var activity = DocumentViewerTelemetry.ActivitySource.StartActivity(
            $"upstream.fetch {system}",
            ActivityKind.Client);
        activity?.SetTag("upstream.system", system.ToString());
        activity?.SetTag("vehicle.vin_hash", vin.Hash12);

        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            using var response = await httpClient.GetAsync(relativePath, HttpCompletionOption.ResponseHeadersRead, ct);
            var elapsed = Stopwatch.GetElapsedTime(startedAt);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                activity?.SetTag("http.response.status_code", statusCode);
                activity?.SetTag("upstream.status", "failed");
                activity?.SetTag("upstream.failure_reason", UpstreamFailureReason.HttpError.ToString());

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

            // The resilience pipeline returns at the response headers
            // (HttpCompletionOption.ResponseHeadersRead above), so everything from here
            // on is outside the per-upstream budget and needs limits of its own.
            if (response.Content.Headers.ContentLength is { } declared && declared > MaxBodyBytes)
            {
                elapsed = Stopwatch.GetElapsedTime(startedAt);
                return OversizedBody(activity, system, vin, elapsed, declared, logger);
            }

            using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bodyCts.CancelAfter(BodyReadBudget);

            await using var bodyStream = await response.Content.ReadAsStreamAsync(bodyCts.Token);
            await using var boundedBody = new BoundedStream(bodyStream, MaxBodyBytes);

            var payload = await JsonSerializer.DeserializeAsync<TPayload>(
                boundedBody,
                JsonOptions,
                bodyCts.Token);

            elapsed = Stopwatch.GetElapsedTime(startedAt);

            if (payload is null)
            {
                activity?.SetTag("upstream.status", "failed");
                activity?.SetTag("upstream.failure_reason", UpstreamFailureReason.InvalidPayload.ToString());

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
            activity?.SetTag("http.response.status_code", (int)response.StatusCode);
            activity?.SetTag("upstream.status", "ok");
            activity?.SetTag("upstream.document_count", documents.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);

            return new UpstreamResult<IReadOnlyList<VehicleDocument>>.Ok(documents, elapsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            activity?.SetTag("upstream.status", "cancelled");
            throw;
        }
        catch (UpstreamBodyTooLargeException ex)
        {
            return Fail(
                activity,
                system,
                vin,
                startedAt,
                UpstreamFailureReason.InvalidPayload,
                ex.Message,
                null,
                logger);
        }
        catch (TimeoutRejectedException ex)
        {
            return Fail(activity, system, vin, startedAt, UpstreamFailureReason.Timeout, ex.Message, null, logger);
        }
        catch (TaskCanceledException ex)
        {
            return Fail(activity, system, vin, startedAt, UpstreamFailureReason.Timeout, ex.Message, null, logger);
        }
        catch (OperationCanceledException ex)
        {
            // Not the caller's cancellation (that is filtered above), so this is the
            // body-read budget expiring.
            return Fail(activity, system, vin, startedAt, UpstreamFailureReason.Timeout, ex.Message, null, logger);
        }
        catch (BrokenCircuitException ex)
        {
            return Fail(activity, system, vin, startedAt, UpstreamFailureReason.CircuitOpen, ex.Message, null, logger);
        }
        catch (JsonException ex)
        {
            return Fail(activity, system, vin, startedAt, UpstreamFailureReason.InvalidPayload, ex.Message, null, logger);
        }
        catch (HttpRequestException ex)
        {
            return Fail(
                activity,
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
            return Fail(activity, system, vin, startedAt, UpstreamFailureReason.Unknown, ex.Message, null, logger);
        }
    }

    private static UpstreamResult<IReadOnlyList<VehicleDocument>>.Failed Fail(
        Activity? activity,
        SourceSystem system,
        Vin vin,
        long startedAt,
        UpstreamFailureReason reason,
        string message,
        int? statusCode,
        ILogger logger)
    {
        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        activity?.SetTag("upstream.status", "failed");
        activity?.SetTag("upstream.failure_reason", reason.ToString());
        if (statusCode.HasValue)
        {
            activity?.SetTag("http.response.status_code", statusCode.Value);
        }

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
    private static UpstreamResult<IReadOnlyList<VehicleDocument>> OversizedBody(
        Activity? activity,
        SourceSystem system,
        Vin vin,
        TimeSpan elapsed,
        long declaredBytes,
        ILogger logger)
    {
        activity?.SetTag("upstream.status", "failed");
        activity?.SetTag("upstream.failure_reason", UpstreamFailureReason.InvalidPayload.ToString());

        logger.LogWarning(
            "Upstream {SourceSystem} declared a {DeclaredBytes}-byte body for VIN {VinHash} "
            + "({VinMasked}), above the {MaxBodyBytes}-byte limit; refusing to read it",
            system,
            declaredBytes,
            vin.Hash12,
            vin.Masked,
            MaxBodyBytes);

        return new UpstreamResult<IReadOnlyList<VehicleDocument>>.Failed(
            new UpstreamFailure(
                UpstreamFailureReason.InvalidPayload,
                $"Upstream {system} declared a body of {declaredBytes:N0} bytes, above the "
                + $"{MaxBodyBytes:N0}-byte limit.",
                null),
            elapsed);
    }

}
