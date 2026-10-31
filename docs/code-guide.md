# Code Guide

A tour of the source for someone who has just cloned this repository. It explains **why
each piece exists and what would break without it** — the design reasoning is in
[`system-design.md`](system-design.md), and the record of what the AI got wrong is in
[`ai-collaboration/review-log.md`](ai-collaboration/review-log.md).

Read in this order. Each section says where to look and what to notice.

---

## 0. The one idea that explains the rest

Everything in this codebase follows from a single decision: **an upstream failure is a
return value, not an exception.**

`src/DocumentViewer.Domain/UpstreamResult.cs`

```csharp
public abstract record UpstreamResult<T>
{
    public sealed record Ok(T Value, TimeSpan Duration) : UpstreamResult<T>;
    public sealed record Failed(UpstreamFailure Failure, TimeSpan Duration) : UpstreamResult<T>;
}
```

The obvious implementation of "call two systems in parallel" is
`await Task.WhenAll(sales, service)`. That rethrows the first task's exception and
**throws away the other task's completed result**. Since the entire scenario is about
still being useful when one system is down, that one line would have destroyed the
feature — and only when an upstream was actually down, which is to say never in a demo.

Because every source returns an `UpstreamResult` instead of throwing, `Task.WhenAll` can
never throw, and the aggregator always receives exactly one outcome per source. Two
tests hold this: one where a source returns `Failed`, and one where a source *throws*
despite its contract.

---

## 1. Domain — `src/DocumentViewer.Domain/`

No dependencies on anything. If a type here needed a database or an `HttpClient`, it
would be in the wrong project.

| File | What to notice |
|---|---|
| `Vin.cs` | A value object, not a `string`. `TryParse` trims and upper-cases before validating, enforces 17 characters, rejects `I`/`O`/`Q`, and checks the ISO 3779 check digit at position 9 — including the remainder-10 `X` branch. **`ToString()` returns the masked form**, so an accidental `$"…{vin}…"` in a log message cannot leak personal data. `Hash12` and `Masked` are what logs are allowed to carry. |
| `VehicleDocument.cs` | The unified model both systems normalise into. `IssuedAt` and `SizeBytes` are nullable because one upstream legitimately omits them, and dropping a document for a missing date would be worse than showing it without one. |
| `SourceSystem.cs`, `DocumentType.cs` | `DocumentType.Unknown = 0` is deliberate: an unrecognised type from an upstream maps to `Unknown` and the document is still returned. Hiding a document because its category is new is a worse failure than showing it with an unknown type. |
| `UpstreamResult.cs`, `UpstreamFailure.cs` | Section 0. `UpstreamFailureReason` is what makes the `sources[].error.reason` field in the response meaningful. |

**Start here:** `Vin.cs` and its 13 tests are the clearest small example of how this
codebase is tested.

---

## 2. Application — `src/DocumentViewer.Application/`

The business rules, with no knowledge of HTTP, SQL, or retries.

### `Sources/IDocumentSource.cs`

```csharp
public interface IDocumentSource
{
    SourceSystem System { get; }
    Task<UpstreamResult<IReadOnlyList<VehicleDocument>>> FetchAsync(Vin vin, CancellationToken ct);
}
```

The seam that makes "add a third system of record" a matter of registering one more
client, and makes the partial-failure rules unit-testable without a network. Note the
return type: an implementation that throws is violating its contract.

### `Aggregation/DocumentAggregationService.cs`

The heart of the service. Read it in four passes:

1. **The fan-out.** Each registered source is projected to a task that already contains
   its own `try`/`catch`, then `Task.WhenAll`. A source that throws despite its contract
   is caught here and converted to `Failed(Unknown)` — belt and braces for the one
   decision the whole design rests on.

2. **The failure branch.** For every source that failed, the cache is consulted. A hit
   becomes `Stale` with the cached documents; a miss stays `Failed`. This is what makes
   a sick system of record degrade gracefully rather than disappear.

3. **The budgets.** Two numbers worth understanding:
   - `CacheOperationBudget` (1 s) bounds how long the **response path** will wait for the
     cache. Beyond it the read completes and the cache work finishes on its own. A
     database that accepted the TCP connection and then never answered used to hold the
     entire document read hostage.
   - `TryCacheUpsertAsync` passes **`CancellationToken.None`**, not the request's token.
     This looks wrong and is the single most important line in the file: the request's
     token is cancelled the instant the response is sent, and a cross-region write takes
     longer than the request's remaining lifetime. Passing `ct` meant *nothing was ever
     cached*, so the stale fallback above could never trigger — while every test passed.
     The repository applies its own write budget instead.

4. **Ordering and the partial flag.** Documents are ordered by `IssuedAt` descending with
   nulls last, tie-broken by `Id` for stability. `Partial` is true when **any** source
   report is not `Ok` — including `Stale`, because stale data is something the client
   deserves to know about.

### `Aggregation/AggregationResult.cs`, `SourceReport.cs`

`AllSourcesFailed` is what the API layer maps to `503`. `SourceReport` is why the client
never has to guess: one entry per source, with its status, count, duration, and error.

### `Persistence/`, `Observability/`

Interfaces only (`IDocumentCacheRepository`, `ISearchAuditRepository`,
`IAggregationMetrics`). They live here, not in Infrastructure, because `Application`
defines what it needs and Infrastructure supplies it.

---

## 3. Infrastructure — `src/DocumentViewer.Infrastructure/`

### `Sources/Sales/`, `Sources/Service/`

Each upstream gets a contract (`*Contracts.cs`), a normaliser, and a typed client.

The two schemas disagree on purpose, because normalisation is only meaningful if there is
something to normalise:

| Unified field | Sales | Service |
|---|---|---|
| identifier | `documentId` — string `"PO-88213"` | `id` — integer `88213` |
| type | `docType` `"PURCHASE_ORDER"` | `category` `"job-card"` |
| issued | `createdDate` `"2026-03-04"` (ISO date) | `timestamp` `1772582400000` (epoch **ms**) |
| size | `fileSizeKb` — **kilobytes** | `bytes` |
| link | `url` (flat) | `links.self` (nested) |
| envelope | `{ documents: [...] }` | `{ data: { items: [...] }, meta: {...} }` |

Every row is a bug waiting to happen, and every row has a test. `id` is namespaced on the
way out (`sales:` / `service:`) because the two systems assign identifiers independently
and their key spaces can collide.

### `Sources/UpstreamClientExecutor.cs`

Where "never throw" is enforced. Each exception type maps to a reason:
`TimeoutRejectedException` and `TaskCanceledException` → `Timeout`;
`BrokenCircuitException` → `CircuitOpen`; a non-success status → `HttpError` with the
code; `JsonException` → `InvalidPayload`. **Caller cancellation is rethrown**, so a client
disconnect is distinguishable from an upstream failure — it is not an upstream's fault
and must not be counted as one.

### `DependencyInjection.cs`

Two things to notice:

- **Each client is named** (`"SalesSystem"`, `"ServiceSystem"`). An unnamed
  `AddHttpClient<IDocumentSource, T>()` keys both registrations under `IDocumentSource`,
  so they share one base address and **one circuit breaker** — which silently destroys
  the per-upstream isolation the design is built on.
- **Configuration is read from the `IServiceProvider`, inside the delegates** — never
  captured at registration time. An eager read captures whatever `appsettings.json` said
  and ignores every source added afterwards, so a deployment setting an upstream address
  by environment variable would be silently ignored.

Resilience per client: 2 s per attempt, 5 s total, 2 retries with exponential backoff and
jitter on transient conditions only, and a circuit breaker at a 0.5 failure ratio over a
30 s window with a 15 s break. The retry delay is set to 200 ms explicitly: the default
2 s plus a 2 s attempt timeout cannot fit a second retry inside a 5 s budget.

### `Persistence/`

| File | Why it exists |
|---|---|
| `DocumentViewerDbContext.cs`, `Entities/` | Two tables. `search_audit` records who looked at which vehicle's documents and whether they saw a complete answer. `document_metadata_cache` is keyed per **`(vin, source_system)`** — not per request — which is what lets a healthy source be served live while a sick one is served from cache in the same response. |
| `DocumentCacheRepository.cs` | Upsert uses `ON CONFLICT (vin, source_system) DO UPDATE` in raw SQL rather than EF's change tracker, so concurrent writers converge instead of colliding. **Two budgets, not one:** a 1500 ms *read* budget because reads sit on the response path, and an 8 s *write* budget because writes happen after it and must survive a cold cross-region connection. Both operations swallow their own failures and log at `Warning` — a cache is an optimisation, never a reason a read fails. |
| `SearchAuditRepository.cs` | Fire-and-forget from the endpoint, inside its own `try`/`catch`. Observability must never be why a user's request fails. |
| `NpgsqlConnectionHardening.cs` | Applies `Minimum Pool Size=1`, `Connection Idle Lifetime=300`, `Timeout=15` to the operator's connection string — filling gaps only, never overriding a value they set. One warm connection means no user request pays the TLS + pooler-tenant-lookup + SASL handshake. Note the subtlety it works around: `NpgsqlConnectionStringBuilder.ContainsKey` answers *"is this a keyword I recognise"*, not *"did the operator set it"*, so the supplied keys are read from a plain `DbConnectionStringBuilder`. |
| `DatabaseWarmupService.cs` | A `BackgroundService`, deliberately — a warm-up that blocked startup would turn an unreachable database into a service that will not boot, and this service is required to keep serving cached documents when the database is down. Failure is logged and nothing more. |

### `Observability/`

`AggregationMetrics` owns the six instruments. `CorrelationPropagationHandler` is a
`DelegatingHandler` on both typed clients, so one correlation ID spans the API log line,
both upstream calls, and the audit row.

---

## 4. API — `src/DocumentViewer.Api/`

### `Endpoints/VehicleDocumentsEndpoint.cs`

Thin on purpose. It parses the VIN, calls the aggregation service, and maps one outcome
to one status code:

| Outcome | Status |
|---|---|
| `Vin.TryParse` fails | `400` + `ProblemDetails`, `type: …/invalid-vin` |
| `AllSourcesFailed` | `503` + `Retry-After: 30` |
| anything else | `200`, with `partial` set from the reports |

It also enforces the 6 s whole-request budget with a linked `CancellationTokenSource`,
and fires the audit write with `CancellationToken.None` so it is not killed by the
response completing.

Notice what is **not** here: no knowledge of how many upstreams exist, or how they fail.

### `Observability/`

| File | What to notice |
|---|---|
| `LoggingSetup.cs` | Serilog to stdout as JSON. `VinRedactingLogEnricher` recurses through `ScalarValue`, `StructureValue`, `SequenceValue` and `DictionaryValue` — because a third-party retry logger stored the outbound URL as a `Uri` **object**, not a string, and leaked a raw VIN into the logs on the degraded path. A test asserts no log line contains a raw VIN. |
| `TelemetrySetup.cs` | Tracing and metrics. The parent span plus two **sibling** upstream spans is the clearest proof the fan-out is real. |
| `HealthChecks.cs` | Three distinctions worth reading carefully. `/health/ready` checks the **database only** — if it checked the upstreams, both systems of record going down would pull every instance out of rotation, and the service would stop serving the cached documents it is still perfectly able to serve. A database that is **absent** by configuration is `Healthy` (running without persistence is supported); one that is **configured and unreachable** is not. And the readiness budget is enforced by **racing** the probe rather than cancelling it, because Npgsql does not reliably observe a cancellation token while it is still opening a physical connection. |
| `CorrelationIdMiddleware.cs` | Honours W3C `traceparent`, then `X-Correlation-Id`, then generates one; echoes it on the response. |

### `Program.cs`

Thirty lines. If the composition root grows past one screen, something that belongs in a
module has leaked into it.

---

## 5. Mocks — `mocks/`

Two standalone minimal APIs, run by `docker compose`, not in-process fakes. That is a
deliberate cost: it is the only way the parallel fan-out, the timeouts and the circuit
breakers are exercised over a real socket rather than against a stub that cannot be slow.

Each exposes `POST /admin/chaos` with `mode`: `normal`, `slow` (plus `delayMs`), `fail`
(`503`), or `garbage` (`200` with an unreadable payload). This is what makes degradation
demonstrable live rather than described in prose — see the README's
*Seeing it degrade*.

---

## 6. Tests — `tests/`

`DocumentViewer.UnitTests` is fast and pure: VIN, normalisers, client error mapping,
aggregation rules, connection-string hardening. `DocumentViewer.IntegrationTests` runs the
real application.

Three fixtures are worth knowing:

| Fixture | What it buys |
|---|---|
| `Fixtures/WireMockUpstreamFixture.cs` | Upstreams scriptable per test — slow, broken, or answering with nonsense — **and they count requests**, which is the only way to prove a `404` was not retried. |
| `Persistence/PostgresContainerFixture.cs` | A real PostgreSQL per run via Testcontainers. No network dependency on Supabase, no shared state, and no risk of a failing test corrupting demo data. |
| The black-hole listener in `DatabaseWarmupTests` | A `TcpListener` that accepts the connection and then never answers. A closed port is refused instantly and proves nothing about timeouts; this is what a cold managed pooler looks like from the client's side. |

And one configuration decision: `xunit.runner.json` sets
`parallelizeTestCollections: false`. Five tests assert on elapsed time, and CPU contention
from sibling test classes pushed a deliberately *healthy* upstream past its 2 s
per-attempt timeout — the product was right and the harness was wrong. Latency assertions
and parallel execution do not mix.

---

## 7. If you change something, these are the tests that will tell you

| Change | Test that should fail |
|---|---|
| Make the fan-out sequential | `Parallel_fan_out_takes_roughly_one_upstream_latency_not_two` |
| Use a bare `Task.WhenAll` over the sources | `A_throwing_source_does_not_lose_the_sibling_source_result` |
| Retry a `4xx` | `Client_error_responses_are_never_retried` |
| Share one resilience pipeline between the clients | `Open_circuit_on_one_source_does_not_affect_the_other_source` |
| Pass the request token to a cache write | `Cache_writes_outlive_the_request_and_never_hold_the_response` |
| Log a raw VIN anywhere | `No_log_line_contains_the_raw_vin` |
| Add the upstreams to `/health/ready` | `Health_ready_returns_200_when_both_upstreams_are_down` |
| Await the cache on the response path | `A_database_that_accepts_but_never_answers_does_not_hold_up_a_document_read` |
| Hardcode the readiness budget | `Readiness_budget_comes_from_configuration_not_a_hardcoded_two_seconds` |
| Read an upstream body with no size limit | `Reports_a_body_that_never_stops_arriving_as_an_invalid_payload` |
| Read an upstream body with no time limit | `Reports_a_body_that_trickles_past_its_budget_as_a_timeout` |
| Multiply kilobytes by 1024 unchecked | `Reports_a_size_it_cannot_represent_as_unknown_rather_than_a_negative_number` |
| Cache a failed fetch | `Failed_source_results_are_never_written_to_cache` |

If you can make a change that none of these notices, that is a gap in the suite worth
reporting.
