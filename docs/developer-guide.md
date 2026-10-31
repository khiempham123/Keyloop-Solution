# Developer Guide

Reference for working **in** this codebase: where everything lives, what you may and may
not depend on, how a request actually flows through the code, every number the service
enforces and where, and recipes for the changes you are most likely to be asked for.

**Which document to read when**

| | Answers | Read it when |
|---|---|---|
| [`system-design.md`](system-design.md) | **Why** — architecture, trade-offs, assumptions, non-goals | Before you argue with a decision |
| [`code-guide.md`](code-guide.md) | **Orientation** — a ten-minute narrative tour | You have just cloned the repository |
| **this document** | **Reference** — files, flows, numbers, extension points | You are about to change something |
| [`../README.md`](../README.md) | Running it, the API, the AI collaboration narrative | You want it working on your machine |

---

## 1. Solution map

```
KeyloopDocumentViewer.sln
├─ src/
│  ├─ DocumentViewer.Domain/          7 files   — vocabulary. No dependencies at all.
│  ├─ DocumentViewer.Application/     9 files   — business rules. Depends on Domain only.
│  ├─ DocumentViewer.Infrastructure/ 20 files   — the outside world. Depends on Application.
│  └─ DocumentViewer.Api/             8 files   — HTTP edge. Depends on Application + Infrastructure.
├─ mocks/
│  ├─ MockSalesApi/                   stands in for the Sales system of record
│  └─ MockServiceApi/                 stands in for the Service system of record
└─ tests/
   ├─ DocumentViewer.UnitTests/       65 tests, ~5s, no Docker
   └─ DocumentViewer.IntegrationTests/ 43 tests, ~2m, needs Docker
```

### Dependency rules

```
Api ──────► Infrastructure ──┐
 │                           ├──► Application ──► Domain
 └───────────────────────────┘
```

Three rules, and the reason each one exists:

1. **`Domain` references nothing.** If a type here needs a `DbContext` or an `HttpClient`,
   it is in the wrong project. This is what keeps `Vin` and `UpstreamResult<T>` testable
   with no setup at all.
2. **`Application` must not know about HTTP, SQL, JSON or Polly.** It depends on
   `IDocumentSource`, `IDocumentCacheRepository`, `ISearchAuditRepository` and
   `IAggregationMetrics`, all of which it defines itself. This is what makes the
   partial-failure rules — the hardest part of the service — unit-testable without a
   network or a database.
3. **`Api` holds no business rules.** It parses a VIN, calls one service, and maps one
   outcome to one status code. It does not know how many upstreams exist.

If a change makes you want to break one of these, that is a signal about the change, not
about the rule.

---

## 2. Everyday commands

```bash
source ./env.sh                  # .NET 9 SDK from the external drive (author-local; see README prerequisites)

dotnet build KeyloopDocumentViewer.sln
dotnet test                      # everything; integration tests need Docker running
dotnet test tests/DocumentViewer.UnitTests                      # fast loop, ~5s
dotnet test --filter FullyQualifiedName~Resilience               # the reliability rules
dotnet test --filter FullyQualifiedName~VinTests                 # one group

docker compose up -d --build     # the two mock systems of record, on :5101 and :5102
dotnet run --project src/DocumentViewer.Api --urls http://localhost:5080
```

Migrations (needs the **direct** connection, port 5432 — not the pooler):

```bash
dotnet tool restore
export ConnectionStrings__Migrations='Host=…;Port=5432;…'
dotnet ef migrations add <Name> -p src/DocumentViewer.Infrastructure -s src/DocumentViewer.Api
dotnet ef database update       -p src/DocumentViewer.Infrastructure -s src/DocumentViewer.Api
```

`DesignTimeDbContextFactory` is what makes `dotnet ef` work without booting the host.

### Debugging a request

```bash
# follow one request end to end: the correlation id ties every line together
curl -s -H 'X-Correlation-Id: debug-1' localhost:5080/api/v1/vehicles/1HGCM82633A004352/documents | jq
# then grep the API's stdout for it
```

Set `OTEL_EXPORTER_OTLP_ENDPOINT` to send traces to a collector; with it unset, traces
and metrics go to the console, interleaved with the Serilog JSON (noted as a known rough
edge — see §12).

---

## 3. Configuration reference

Every key, its default, and what breaks if you get it wrong.

| Key | Default | Notes |
|---|---|---|
| `Upstreams:SalesSystem:BaseAddress` | `http://localhost:5101` | Resolved **per client creation**, not at startup — see §11 |
| `Upstreams:ServiceSystem:BaseAddress` | `http://localhost:5102` | ditto |
| `Upstreams:CircuitBreakerMinimumThroughput` | `5` | Calls needed in the 30s window before a breaker may open. Values below 2 are ignored |
| `ConnectionStrings:Default` | *(none)* | Runtime. Supabase: **transaction pooler, port 6543**. Absent is a supported configuration |
| `ConnectionStrings:Migrations` | *(none)* | `dotnet ef` only. Supabase: **direct/session, port 5432** |
| `HealthChecks:DatabaseTimeoutMs` | `5000` | Readiness budget. Values ≤ 0 are ignored |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | *(none)* | Unset → console exporter |

**Why two connection strings.** Supabase's transaction-mode pooler multiplexes
connections and does not support the session-level features EF Core migrations need.
Using one string for both means either migrations fail or runtime holds a session
connection per request.

**Connection-string hardening.** Whatever you supply is passed through
`NpgsqlConnectionHardening`, which fills in `Timeout=15`, `Minimum Pool Size=1` and
`Connection Idle Lifetime=300` **only where you have not set them yourself**. Of the
three, `Minimum Pool Size` is the one that changes Npgsql's behaviour (its default is 0);
the other two pin Npgsql's current defaults so a future change cannot silently widen the
handshake budget this service depends on. One caveat: an unrecognised keyword anywhere in
the string makes `NpgsqlConnectionStringBuilder` throw, and the string is then passed
through untouched — including without the warm connection.

---

## 4. How a request actually flows

### 4.1 The happy path

```
CorrelationIdMiddleware          traceparent → X-Correlation-Id → generate; sets CorrelationContext
  └─ VehicleDocumentsEndpoint.HandleAsync
       ├─ Vin.TryParse(vin)                                  400 + ProblemDetails on failure
       ├─ budgetCts = linked(RequestAborted) + CancelAfter(6s)
       ├─ DocumentAggregationService.GetDocumentsAsync
       │    ├─ for each IDocumentSource  ──┐  Task.WhenAll over wrapped tasks
       │    │    └─ SalesSystemClient / ServiceSystemClient
       │    │         └─ UpstreamClientExecutor.ExecuteAsync
       │    │              ├─ httpClient.GetAsync(…, ResponseHeadersRead, ct)   ← Polly ends here
       │    │              ├─ Content-Length > 4 MiB?            → InvalidPayload
       │    │              ├─ bodyCts = linked(ct) + CancelAfter(2s)
       │    │              ├─ BoundedStream(4 MiB) + DeserializeAsync
       │    │              └─ normalizer.Normalize(payload)      → UpstreamResult.Ok | .Failed
       │    ├─ for each Failed source: TryCacheGetWithinBudgetAsync (1s)   → Stale | Failed
       │    ├─ cache upserts, fired with CancellationToken.None, raced against 1s
       │    ├─ order by IssuedAt desc (nulls last), then Id
       │    └─ metrics.RecordAggregation / RecordPartial
       ├─ FireAndForgetAudit(…, CancellationToken.None)
       └─ 200 | 503
```

Two places in that chain are counter-intuitive and deliberate:

- **Polly's budget ends at the response headers.** `HttpCompletionOption.ResponseHeadersRead`
  means the pipeline returns before the body arrives, so the body gets its own size limit
  and its own 2s budget. Without them, a trickled body is bounded only by the whole-request
  timeout — and it was once bounded by nothing at all, hanging indefinitely.
- **Cache writes and the audit write use `CancellationToken.None`.** The request token is
  cancelled the moment the response is sent. Passing it meant a cross-region write was
  always cancelled mid-handshake and nothing was ever cached.

### 4.2 One source down

Identical until the fan-out returns. The failed source's `UpstreamResult.Failed` sends the
aggregator to the cache: a hit becomes `SourceStatus.Stale` and its documents are merged
in; a miss stays `Failed`. `partial` is true for either. The response is still `200` — see
§5.1 of the design document for why that choice, and §9 below for what the client sees.

### 4.3 Both sources down

Every report is `Failed`, so `AggregationResult.AllSourcesFailed` is true and the endpoint
returns `503` with `Retry-After: 30`. This path **is** counted and audited. So is the
whole-request timeout path, which returns the same status with a different
`ProblemDetails.type`.

---

## 5. File reference

### `DocumentViewer.Domain` — vocabulary

| File | Public surface | Notice |
|---|---|---|
| `Vin.cs` | `Vin.TryParse(string?, out Vin, out VinError)`, `.Value`, `.Hash12`, `.Masked` | Trims and upper-cases before validating. **`ToString()` returns `Masked`** so an accidental interpolation cannot leak PII |
| `VinError.cs` | `None, Empty, InvalidLength, IllegalCharacter, InvalidCheckDigit` | Maps to the `400` detail text |
| `VehicleDocument.cs` | `record(Id, SourceSystem, DocumentType, Title, IssuedAt?, SizeBytes?, DownloadUrl?)` | Three nullable members; they are omitted from the JSON, which is why the OpenAPI contract must not mark them required |
| `SourceSystem.cs` | `SalesSystem = 1, ServiceSystem = 2` | Explicit values: they are persisted as `smallint` |
| `DocumentType.cs` | `Unknown = 0, PurchaseOrder, FinanceAgreement, RegistrationDocument, JobCard, InspectionReport, WarrantyClaim` | `Unknown = 0` on purpose — an unrecognised upstream type keeps the document |
| `UpstreamResult.cs` | `UpstreamResult<T>.Ok(Value, Duration)` / `.Failed(Failure, Duration)` | The one idea the codebase is built on |
| `UpstreamFailure.cs` | `UpstreamFailure(Reason, Message, StatusCode?)`, `UpstreamFailureReason { Timeout, CircuitOpen, HttpError, InvalidPayload, Unknown }` | Surfaces to the client as `sources[].error.reason` |

### `DocumentViewer.Application` — business rules

| File | Public surface | Notice |
|---|---|---|
| `Sources/IDocumentSource.cs` | `SourceSystem System`, `FetchAsync(Vin, CancellationToken)` | The return type is the contract: an implementation that throws is breaking it |
| `Aggregation/DocumentAggregationService.cs` | `GetDocumentsAsync(Vin, CancellationToken)` | 252 lines, the largest file in the solution and the one to read first |
| `Aggregation/AggregationResult.cs` | `record(Vin, RetrievedAt, Partial, Documents, Sources)`, `.AllSourcesFailed` | `AllSourcesFailed` is the only thing the API layer branches on |
| `Aggregation/SourceReport.cs` | `SourceStatus { Ok, Stale, Failed }`, `SourceReport(System, Status, DocumentCount, DurationMs, Error?)` | `SourceStatus` serialises lower-case via `JsonStringEnumMemberName` |
| `Persistence/IDocumentCacheRepository.cs` | `GetAsync`, `UpsertAsync(…, TimeSpan ttl, …)`, `CachedSourceDocuments` | Keyed per `(vin, source)`, never per request |
| `Persistence/ISearchAuditRepository.cs` | `AppendAsync(SearchAuditEntry, …)` | Implementations must swallow their own failures |
| `Observability/IAggregationMetrics.cs` | `RecordAggregation`, `RecordUpstream`, `RecordPartial`, `RecordCacheLookup` | Defined here so `Application` can measure without referencing OpenTelemetry |
| `Observability/DocumentViewerTelemetry.cs` | `ActivitySource`, `CorrelationContext` | `CorrelationContext.Current` is how the log enricher finds the id |

### `DocumentViewer.Infrastructure` — the outside world

| File | Notice |
|---|---|
| `DependencyInjection.cs` | `AddDocumentSources()`, `AddPersistence()`. Both clients are **named**; configuration is resolved from the `IServiceProvider` inside the delegates |
| `Sources/UpstreamClientExecutor.cs` | Where "never throw" is enforced, and where the body limits live. Every exception type maps to a `UpstreamFailureReason`; caller cancellation is **rethrown** |
| `Sources/BoundedStream.cs` | Read-only wrapper that throws `UpstreamBodyTooLargeException` past 4 MiB. Enforced while reading, because a chunked response declares no length |
| `Sources/Sales/*`, `Sources/Service/*` | Contracts, normaliser and typed client per upstream. The two schemas disagree on purpose — see §6 |
| `Persistence/DocumentViewerDbContext.cs` | snake_case mapping, `jsonb` payloads, the two indexes |
| `Persistence/DocumentCacheRepository.cs` | `ON CONFLICT … DO UPDATE` raw SQL. **Two budgets:** 1500 ms for reads (response path), 8 s for writes (after it, must survive a cold connection) |
| `Persistence/SearchAuditRepository.cs` | 8 s budget, swallows everything |
| `Persistence/NpgsqlConnectionHardening.cs` | §3. Reads *supplied* keys from a plain `DbConnectionStringBuilder`, because `NpgsqlConnectionStringBuilder.ContainsKey` answers a different question |
| `Persistence/DatabaseWarmupService.cs` | A `BackgroundService`, so an unreachable database cannot stop the service booting |
| `Observability/AggregationMetrics.cs` | The six instruments in §7 |
| `Observability/CorrelationPropagationHandler.cs` | `DelegatingHandler` on both clients |
| `DefaultNoOpServices.cs` | No-op cache/audit used when no connection string is configured |

### `DocumentViewer.Api` — HTTP edge

| File | Notice |
|---|---|
| `Program.cs` | 30 lines. If it outgrows one screen, something has leaked into it |
| `Endpoints/VehicleDocumentsEndpoint.cs` | VIN parse, 6 s budget, outcome → status, audit |
| `Contracts/DocumentsResponse.cs` | The wire shape. Nullable members are omitted, not nulled |
| `Contracts/OptionalPropertySchemaTransformer.cs` | Keeps `openapi.json` honest by deriving optionality from the records. A **schema** transformer, because `document.Components.Schemas` is still null when document transformers run |
| `Middleware/CorrelationIdMiddleware.cs` | `traceparent` → `X-Correlation-Id` → generate; echoed on the response |
| `Observability/LoggingSetup.cs` | 278 lines, mostly `VinRedactingLogEnricher`, which recurses through `ScalarValue` / `StructureValue` / `SequenceValue` / `DictionaryValue` — a third-party retry logger stored the URL as a `Uri` object and leaked a VIN |
| `Observability/TelemetrySetup.cs` | Tracing + metrics, OTLP or console |
| `Observability/HealthChecks.cs` | Three endpoints, three deliberate distinctions — see §8 |

---

## 6. The two upstream schemas

Normalisation only means something if there is something to normalise:

| Unified | Sales | Service |
|---|---|---|
| `Id` | `documentId` — string | `id` — integer |
| `DocumentType` | `docType` `"PURCHASE_ORDER"` | `category` `"job-card"` |
| `Title` | `description` | `name` |
| `IssuedAt` | `createdDate` `"2026-03-04"` | `timestamp` epoch **milliseconds** |
| `SizeBytes` | `fileSizeKb` — **kilobytes** | `bytes` |
| `DownloadUrl` | `url` — flat | `links.self` — nested |
| envelope | `{ documents: [...] }` | `{ data: { items: [...] }, meta: {...} }` |

Every row has a test. Two are easy to get wrong in ways that reach the client:

- **Kilobytes.** `fileSizeKb * 1024` is unchecked arithmetic over untrusted input. A value
  above `long.MaxValue / 1024` reports an **unknown** size rather than a negative one, and
  the document is still returned.
- **Missing title.** The record is skipped and the source stays `ok`. One unusable record
  is not a reason to fail a whole system of record.

---

## 7. Every number in one place

| What | Value | Enforced in | Test |
|---|---|---|---|
| Per-attempt upstream timeout | 2 s | `DependencyInjection` (Polly) | `One_upstream_timing_out…` |
| Total per upstream | 5 s | `DependencyInjection` (Polly) | same |
| Whole request | 6 s | `VehicleDocumentsEndpoint` | same |
| Upstream retries | 2, exponential + jitter, transient only | `DependencyInjection` | `Transient_500_is_retried…` / `Client_error_responses_are_never_retried` |
| Retry delay | 200 ms | `DependencyInjection` | — (2 s default cannot fit two retries in 5 s) |
| Circuit breaker | 0.5 ratio / 30 s window / 15 s break, **per upstream** | `DependencyInjection` | `Repeated_failures_open_the_circuit…` / `Open_circuit_on_one_source…` |
| Breaker minimum throughput | 5 (configurable) | `DependencyInjection` | — |
| Upstream body size | 4 MiB | `UpstreamClientExecutor` + `BoundedStream` | `Reports_a_body_that_never_stops_arriving…` |
| Upstream body read | 2 s | `UpstreamClientExecutor` | `Reports_a_body_that_trickles_past_its_budget…` |
| Cache read, response path | 1500 ms | `DocumentCacheRepository` | — |
| Cache work, response path | 1 s | `DocumentAggregationService` | `A_database_that_accepts_but_never_answers…` |
| Cache write | 8 s | `DocumentCacheRepository` | `Cache_writes_outlive_the_request…` |
| Audit write | 8 s | `SearchAuditRepository` | — |
| Cache TTL | 15 min | `DocumentAggregationService` | `Get_returns_null_for_expired_entry` |
| Readiness budget | 5 s (configurable) | `HealthChecks` | `Readiness_budget_comes_from_configuration…` |
| Database warm-up | 20 s | `DatabaseWarmupService` | — |

The six metrics, under the `DocumentViewer` meter:

```
documentviewer.aggregation.duration   histogram  {outcome}
documentviewer.upstream.duration      histogram  {system}
documentviewer.upstream.result        counter    {system, status}
documentviewer.response.partial       counter
documentviewer.cache.lookup           counter    {result: hit|miss|error|timeout}
documentviewer.circuit.state          gauge      {system}
```

---

## 8. Health checks

| Endpoint | Checks | Fails when |
|---|---|---|
| `/health/live` | the process | never, while it is running |
| `/health/ready` | the database **only** | a database is configured and cannot be reached |
| `/health/upstream` | both upstreams, informational | never — always `200` with a per-source body |

Three distinctions worth reading the code for:

1. **Readiness excludes the upstreams.** If it did not, both systems of record going down
   would pull every instance out of rotation — and the service would stop serving the
   cached documents it is still perfectly able to serve.
2. **An absent database is `Healthy`.** Running without cache and audit is a supported
   configuration. A *configured but unreachable* database is not.
3. **The budget is enforced by racing the probe, not by cancelling it.** Npgsql does not
   reliably observe a cancellation token while it is still opening a physical connection,
   so a cancel-based budget was silently ignored. Note the ordering in the code:
   `cts.Cancel()` happens **before** the `using` disposes it, so a late `Register` by
   Npgsql runs inline instead of throwing. That ordering is load-bearing.

---

## 9. What the client sees, for every failure mode

| Condition | Status | `partial` | `sources[].status` | `error.reason` |
|---|---|---|---|---|
| Both sources answer | `200` | `false` | `ok`, `ok` | — |
| Valid VIN, no documents anywhere | `200` | `false` | `ok`, `ok` | — |
| One source `5xx` | `200` | `true` | `ok`, `failed` | `HttpError` |
| One source `4xx` | `200` | `true` | `ok`, `failed` | `HttpError` |
| One source times out | `200` | `true` | `ok`, `failed` | `Timeout` |
| One source's breaker open | `200` | `true` | `ok`, `failed` | `CircuitOpen` |
| One source, unparseable body | `200` | `true` | `ok`, `failed` | `InvalidPayload` |
| One source, body > 4 MiB | `200` | `true` | `ok`, `failed` | `InvalidPayload` |
| One source fails, cache has it | `200` | `true` | `ok`, `stale` | the live failure |
| One source's record missing a title | `200` | `false` | `ok`, `ok` | — (record skipped) |
| Both sources fail, nothing cached | `503` | — | `ProblemDetails`, `Retry-After: 30` | — |
| Whole-request budget elapses | `503` | — | `ProblemDetails` (`…/request-timeout`) | — |
| Invalid VIN | `400` | — | `ProblemDetails` (`…/invalid-vin`) | — |
| Client disconnects | *(no response)* | — | nothing logged at `Error`, no audit row | — |
| Database unreachable | as above, unaffected | — | `stale` becomes impossible | — |

---

## 10. Extension recipes

### Add a third system of record

1. `Sources/Warranty/WarrantyContracts.cs` — records matching its payload.
2. `WarrantyDocumentNormalizer : IDocumentNormalizer<WarrantyDocumentsResponse>` — map to
   `VehicleDocument`, namespace the id (`warranty:{id}`), unknown types → `Unknown`,
   records missing a title → skipped.
3. `WarrantySystemClient : IDocumentSource` — delegate to `UpstreamClientExecutor`.
4. Add `SourceSystem.WarrantySystem = 3` (an explicit value: it is persisted).
5. Register it in `AddDocumentSources` with its own named client and its own resilience
   pipeline. **Give it a distinct name** or it will share a base address and a circuit
   breaker with another client.
6. Add `Upstreams:WarrantySystem:BaseAddress` to `appsettings.json`.

Nothing in `Application` or `Api` changes: the aggregator iterates
`IEnumerable<IDocumentSource>`, and `sources[]` grows by one entry.

### Add a document type

Add the member to `DocumentType` (never renumber existing ones — they are persisted in
the cache payload), then map the upstream's string in the relevant normaliser. Unmapped
strings already fall through to `Unknown`, so forgetting step two loses the type, not the
document.

### Change a budget

Every value is in §7 with the file that owns it. Change it there, then run the test named
beside it — several assert on elapsed time and will tell you if the budgets have become
mutually inconsistent (for example, a retry delay that cannot fit inside the total).

### Add a metric

Add the method to `IAggregationMetrics`, implement it in `AggregationMetrics`, call it
from `Application`. The no-op in `DefaultNoOpServices` exists so a partially configured
host still runs. Keep the instrument name under the `documentviewer.` prefix.

---

## 11. Invariants you must not break

Each of these has been broken at least once in this repository's history, and each has a
test guarding it now. [`code-guide.md`](code-guide.md) §7 lists which test catches which.

1. **No `IDocumentSource` may throw.** Failure is an `UpstreamResult.Failed`. A bare
   `Task.WhenAll` over throwing sources discards the siblings' completed work.
2. **The cache may never throw into the aggregator** — cancellation included. It is the
   same failure as #1 arriving by a different route: an exception escaping the cache
   read discarded a healthy source's documents and turned a `200` into an empty `503`.
3. **Cache and audit writes must not use the request's cancellation token.** It is
   cancelled when the response is sent.
4. **Never log a raw VIN.** Use `Vin.Hash12` and `Vin.Masked`. `ToString()` is already the
   masked form; the risk is third-party loggers, which is why the enricher recurses.
5. **Resolve configuration from the `IServiceProvider`, not at registration time.** An
   eager read captures `appsettings.json` and ignores environment variables.
6. **Each upstream gets its own named client and its own resilience pipeline.** Shared
   state between breakers lets one failing system degrade the other.
7. **Never retry a non-transient `4xx`.** The upstream is answering correctly.
8. **Never cache a failure.** It converts a one-second blip into a TTL-long outage.
9. **`TargetFramework` is declared once**, in `Directory.Build.props`. That is what makes
   the move to .NET 10 LTS a one-line change.
10. **Latency assertions and parallel test execution do not mix.** Integration test
    collections run sequentially (`xunit.runner.json`); five tests measure elapsed time.

---

## 12. Troubleshooting

| Symptom | Likely cause | Where to look |
|---|---|---|
| `/health/ready` is `503` | A connection string is configured but the database is unreachable, or the handshake is slower than `HealthChecks:DatabaseTimeoutMs` | the `postgres` health check's log line; try the direct connection with `dotnet ef migrations list` |
| Everything works, but no source is ever `stale` | No database configured, or cache writes are failing | grep the log for `Cache upsert failed` / `Cache writes … exceeded` |
| Documents come back empty from a source that returns `200` | The payload's field names do not match the contract, so every record was skipped | the normaliser's tests; this exact mistake once made nine tests pass against empty responses |
| An integration test fails only when run with others | A latency assertion meeting CPU contention | `xunit.runner.json`; add a warm-up request before the clock |
| A test passes whether or not the fix is present | The assertion matches a library default, or the failure mode is refused instantly instead of hanging | ask "what production change would make this fail?" — if there is no answer, rewrite it |
| Upstream calls go somewhere unexpected | Configuration read at registration time instead of per client | `DependencyInjection.BaseAddress(sp, …)` |
| Console full of interleaved trace output | No `OTEL_EXPORTER_OTLP_ENDPOINT`, so the console exporter shares stdout with Serilog | `TelemetrySetup`; known rough edge, listed as a deferred minor |

---

## 13. Where decisions are recorded

| Document | Contents |
|---|---|
| [`system-design.md`](system-design.md) §10 | 11 assumptions — every ambiguity in the brief, and the call made |
| [`system-design.md`](system-design.md) §9 | 6 non-goals — what was deliberately not built, and why |
| [`ai-collaboration/review-log.md`](ai-collaboration/review-log.md) | 22 entries: 7 design-phase, 15 implementation — every defect found, what changed, why it mattered |
| [`ai-collaboration/implementation-ledger.md`](ai-collaboration/implementation-ledger.md) | Every ruling made while executing the plan, with what it costs if wrong |
| [`superpowers/plans/`](superpowers/plans/) | The task-by-task plan the implementation followed |

If you are about to change something and cannot find the reasoning, it is worth assuming
there was some and looking in the review log before overruling it.
