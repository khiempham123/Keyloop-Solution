# Unified Document Viewer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A .NET backend that, given a VIN, fans out in parallel to two mocked systems of record and returns one consolidated, source-attributed document list that stays useful when either upstream is down.

**Architecture:** Four-project layered solution (`Api` / `Application` / `Infrastructure` / `Domain`) plus two standalone mock upstream services. Failure is modelled as a return value (`UpstreamResult<T>`), never an exception, so `Task.WhenAll` can never discard a sibling source's completed work. A per-`(vin, source)` Postgres cache lets a healthy source be served live while a sick one is served stale in the same response.

**Tech Stack:** .NET 9 (`net9.0`, SDK 9.0.320), ASP.NET Core Minimal APIs, `Microsoft.Extensions.Http.Resilience` (Polly v8), EF Core + Npgsql against Supabase PostgreSQL, Serilog, OpenTelemetry, xUnit + Shouldly + NSubstitute, WireMock.Net, Testcontainers for PostgreSQL.

**Spec:** [`docs/system-design.md`](../../system-design.md)

**Deadline:** Wednesday 2026-11-04. Tasks 1–5 are the minimum submittable product; Tasks 6–9 are each independently valuable. If time runs short, cut from the bottom of §Non-Goals in the spec, never from Tasks 1–5, 8 or 9.

## Global Constraints

- Target framework `net9.0` (SDK 9.0.320, installed at `/Volumes/240GB/dotnet`; run `source ./env.sh` first in every shell). Nullable reference types and `TreatWarningsAsErrors` enabled in `Directory.Build.props`.
- `TargetFramework` is set **once** in `Directory.Build.props` and inherited by every project — never repeated in a `.csproj`. This is what makes the documented .NET 10 LTS migration a one-line change.
- **No FluentAssertions** (v8+ licence is not free for commercial use). Use Shouldly.
- **No raw VIN in any log message or metric tag.** Log `VinHash` (first 12 hex of SHA-256) and `VinMasked` (last 6 chars) only. Full VIN appears only in the `search_audit` table.
- Every `async` method takes a `CancellationToken` and passes it down; no `.Result`, no `.Wait()`, no `async void`.
- All times are `DateTimeOffset` in UTC. Inject `TimeProvider`; never call `DateTime.Now` or `DateTimeOffset.UtcNow` directly in testable code.
- Timeout budget, exact values: per attempt per upstream **2 s**; total per upstream including retries **5 s**; whole request **6 s**.
- Retry: **2** retries, exponential backoff with jitter, transient only (`5xx`, `408`, `429`, timeout, socket). Never retry other `4xx`.
- Circuit breaker: per upstream, failure ratio **0.5**, sampling window **30 s**, break duration **15 s**.
- Cache TTL **15 minutes**. Failures are never cached.
- Document `id` is namespaced: `"sales:{externalId}"` / `"service:{externalId}"`.
- Supabase: runtime connection uses the transaction-mode pooler (port **6543**); EF Core migrations use the direct connection (port **5432**). Connection strings come from configuration/env vars and are never committed.
- Conventional Commits for every commit message.

## Review Focus

Five input classes the spec implies but no task's happy path exercises. Each has its test assigned to the task that owns the code.

1. **VIN pasted with surrounding whitespace or in lowercase** — users copy from emails and spreadsheets. `" 1hgcm82633a004352 "` must be trimmed, upper-cased and accepted, not rejected as a 400. *(Task 1)*
2. **Postgres unreachable** — the cache and audit must never be on the critical path for the main read. With the database down, a request where both upstreams are healthy must still return `200` with full documents. *(Task 6)*
3. **Upstream returns `200` with a malformed or incomplete payload** — a missing title, a null date, a non-numeric size. The source must be reported `failed` with reason `InvalidPayload`, or the bad document skipped, but never a `500` from this service. *(Task 3)*
4. **Two concurrent requests for the same VIN** — both miss the cache and both try to upsert the same `(vin, source)` primary key. The second write must not surface as a `500`. *(Task 6)*
5. **Client disconnects mid-request** — cancellation must not be logged at `Error`, must not fire an audit write, and must not be counted as an upstream failure in the metrics. *(Task 7)*

---

### Task 1: Solution skeleton and domain primitives

**Files:**
- Create: `KeyloopDocumentViewer.sln`, `Directory.Build.props`, `.gitignore`, `.editorconfig`
- Create: `src/DocumentViewer.Domain/Vin.cs`, `VinError.cs`, `SourceSystem.cs`, `DocumentType.cs`, `VehicleDocument.cs`, `UpstreamResult.cs`, `UpstreamFailure.cs`
- Test: `tests/DocumentViewer.UnitTests/Domain/VinTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  ```csharp
  public enum VinError { None, Empty, InvalidLength, IllegalCharacter, InvalidCheckDigit }
  public enum SourceSystem { SalesSystem = 1, ServiceSystem = 2 }
  public enum DocumentType { Unknown = 0, PurchaseOrder, FinanceAgreement, RegistrationDocument, JobCard, InspectionReport, WarrantyClaim }
  public enum UpstreamFailureReason { Timeout, CircuitOpen, HttpError, InvalidPayload, Unknown }

  public readonly record struct Vin
  {
      public string Value { get; }
      public string Hash12 { get; }   // first 12 lowercase hex chars of SHA-256(Value)
      public string Masked { get; }   // "***" + last 6 chars of Value
      public static bool TryParse(string? input, out Vin vin, out VinError error);
      public override string ToString();   // returns Masked — guards against accidental logging
  }

  public sealed record UpstreamFailure(UpstreamFailureReason Reason, string Message, int? StatusCode);

  public sealed record VehicleDocument(
      string Id, SourceSystem SourceSystem, DocumentType DocumentType,
      string Title, DateTimeOffset? IssuedAt, long? SizeBytes, string? DownloadUrl);

  public abstract record UpstreamResult<T>
  {
      public sealed record Ok(T Value, TimeSpan Duration) : UpstreamResult<T>;
      public sealed record Failed(UpstreamFailure Failure, TimeSpan Duration) : UpstreamResult<T>;
  }
  ```

- [ ] **Step 1: Create the solution, the four `src` projects, the two test projects, `Directory.Build.props` (net9.0, nullable, warnings-as-errors), `.gitignore` (dotnet template) and `.editorconfig`; wire project references `Api → Application → Domain`, `Infrastructure → Application`, `Api → Infrastructure`.**

Verify with `dotnet build` before writing any test.

- [ ] **Step 2: Write the failing VIN tests**

```csharp
[Theory]
[InlineData("1HGCM82633A004352")]           // valid, check digit 3
[InlineData(" 1hgcm82633a004352 ")]         // Review Focus #1: whitespace + lowercase
public void TryParse_accepts_valid_vin(string input)
{
    Vin.TryParse(input, out var vin, out var error).ShouldBeTrue();
    error.ShouldBe(VinError.None);
    vin.Value.ShouldBe("1HGCM82633A004352");
}

[Theory]
[InlineData(null, VinError.Empty)]
[InlineData("", VinError.Empty)]
[InlineData("   ", VinError.Empty)]
[InlineData("1HGCM82633A00435", VinError.InvalidLength)]      // 16
[InlineData("1HGCM82633A0043521", VinError.InvalidLength)]     // 18
[InlineData("1HGCM82633A0O4352", VinError.IllegalCharacter)]   // contains O
[InlineData("1HGCM82633A0I4352", VinError.IllegalCharacter)]   // contains I
[InlineData("1HGCM82633A0Q4352", VinError.IllegalCharacter)]   // contains Q
[InlineData("1HGCM82633A004353", VinError.InvalidCheckDigit)]  // last digit altered
public void TryParse_rejects_invalid_vin(string? input, VinError expected)
{
    Vin.TryParse(input, out _, out var error).ShouldBeFalse();
    error.ShouldBe(expected);
}

[Fact]
public void Vin_does_not_expose_raw_value_when_formatted()
{
    Vin.TryParse("1HGCM82633A004352", out var vin, out _);
    vin.ToString().ShouldNotContain("1HGCM82633A004352");
    vin.ToString().ShouldEndWith("004352");
    vin.Hash12.Length.ShouldBe(12);
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/DocumentViewer.UnitTests --filter FullyQualifiedName~VinTests`
Expected: FAIL — `Vin` does not exist.

- [ ] **Step 4: Implement `Vin.TryParse` and the remaining domain types**

`TryParse` trims and upper-cases before validating. Check-digit algorithm (ISO 3779), since the tests do not determine it: transliterate each character (`0-9` → face value; `A..Z` → `1,2,3,4,5,6,7,8,_,1,2,3,4,5,_,7,_,9,2,3,4,5,6,7,8,9` with `I`,`O`,`Q` invalid), multiply by positional weights `8,7,6,5,4,3,2,10,0,9,8,7,6,5,4,3,2`, sum, take `mod 11`; the expected digit is that remainder, or `X` when it is 10. Compare against position 9.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/DocumentViewer.UnitTests --filter FullyQualifiedName~VinTests`
Expected: PASS, 13 tests.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(domain): add Vin value object, document model and UpstreamResult"
```

---

### Task 2: Mock upstream services with divergent schemas

**Files:**
- Create: `mocks/MockSalesApi/Program.cs`, `mocks/MockSalesApi/SeedData.cs`
- Create: `mocks/MockServiceApi/Program.cs`, `mocks/MockServiceApi/SeedData.cs`
- Create: `docker-compose.yml` (both mocks only — the database is Supabase)

**Interfaces:**
- Consumes: nothing.
- Produces two HTTP contracts, consumed by Task 3. The payload shapes **must** differ exactly as the spec's §5.3 table specifies.

  `GET /sales/vehicles/{vin}/documents` →
  ```json
  { "documents": [ { "documentId": "PO-88213", "docType": "PURCHASE_ORDER",
                     "description": "Purchase order PO-88213",
                     "createdDate": "2026-03-04", "fileSizeKb": 145,
                     "url": "https://sales.mock/documents/PO-88213" } ] }
  ```

  `GET /service/v2/vehicles/{vin}/docs` →
  ```json
  { "data": { "items": [ { "id": 88213, "category": "job-card", "name": "Job card #88213",
                           "timestamp": 1772582400000, "bytes": 148213,
                           "links": { "self": "https://service.mock/docs/88213" } } ] },
    "meta": { "count": 1 } }
  ```

  Chaos control, on both: `POST /admin/chaos` with `{ "mode": "normal" | "slow" | "fail" | "garbage", "delayMs": 8000 }`, and `GET /admin/chaos` to read current state. `slow` delays every response; `fail` returns `503`; `garbage` returns `200` with a payload missing required fields (this is what Review Focus #3 is tested against).

- [ ] **Step 1: Implement `MockSalesApi` as a minimal API: the documents endpoint reading from an in-memory seed keyed by VIN, the two chaos endpoints, and a `/health` endpoint. Seed at least 4 documents for VIN `1HGCM82633A004352` and an empty result for `WAUZZZ8V7JA003421`.**

- [ ] **Step 2: Implement `MockServiceApi` the same way, with the nested envelope, integer IDs, epoch-millisecond timestamps and `bytes`. Seed 3 documents for the same VIN, including one whose `category` is a value Task 3's mapping will not recognise.**

- [ ] **Step 3: Add `docker-compose.yml` exposing MockSalesApi on `:5101` and MockServiceApi on `:5102`.**

- [ ] **Step 4: Verify both mocks by hand**

Run: `docker compose up -d && curl -s localhost:5101/sales/vehicles/1HGCM82633A004352/documents && curl -s localhost:5102/service/v2/vehicles/1HGCM82633A004352/docs`
Expected: two payloads with the shapes above; `jq` confirms the field names differ.

- [ ] **Step 5: Verify chaos works**

Run: `curl -s -XPOST localhost:5101/admin/chaos -H 'content-type: application/json' -d '{"mode":"fail"}' && curl -s -o /dev/null -w '%{http_code}\n' localhost:5101/sales/vehicles/1HGCM82633A004352/documents`
Expected: `503`. Reset to `normal` afterwards.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(mocks): add Sales and Service mock APIs with divergent schemas and chaos injection"
```

---

### Task 3: Normalizers and resilient typed HTTP clients

**Files:**
- Create: `src/DocumentViewer.Application/Sources/IDocumentSource.cs`
- Create: `src/DocumentViewer.Infrastructure/Sources/Sales/SalesContracts.cs`, `SalesDocumentNormalizer.cs`, `SalesSystemClient.cs`
- Create: `src/DocumentViewer.Infrastructure/Sources/Service/ServiceContracts.cs`, `ServiceDocumentNormalizer.cs`, `ServiceSystemClient.cs`
- Create: `src/DocumentViewer.Infrastructure/DependencyInjection.cs`
- Test: `tests/DocumentViewer.UnitTests/Sources/SalesDocumentNormalizerTests.cs`, `ServiceDocumentNormalizerTests.cs`

**Interfaces:**
- Consumes: `Vin`, `VehicleDocument`, `DocumentType`, `SourceSystem`, `UpstreamResult<T>`, `UpstreamFailure` (Task 1).
- Produces:
  ```csharp
  public interface IDocumentSource
  {
      SourceSystem System { get; }
      Task<UpstreamResult<IReadOnlyList<VehicleDocument>>> FetchAsync(Vin vin, CancellationToken ct);
  }

  public interface IDocumentNormalizer<in TPayload>
  {
      IReadOnlyList<VehicleDocument> Normalize(TPayload payload);
  }

  // Infrastructure.DependencyInjection
  public static IServiceCollection AddDocumentSources(this IServiceCollection services, IConfiguration config);
  ```

- [ ] **Step 1: Write the failing normalizer tests**

```csharp
// SalesDocumentNormalizerTests
[Fact] public void Maps_kilobytes_to_bytes()               // fileSizeKb 145 -> SizeBytes 148480
[Fact] public void Maps_iso_date_to_utc_midnight()          // "2026-03-04" -> 2026-03-04T00:00:00Z
[Fact] public void Namespaces_id_with_source_prefix()       // "PO-88213" -> "sales:PO-88213"
[Fact] public void Maps_known_doctype()                     // "PURCHASE_ORDER" -> PurchaseOrder
[Fact] public void Maps_unknown_doctype_to_Unknown_and_keeps_document()
[Fact] public void Deduplicates_repeated_documentId_within_source()
[Fact] public void Skips_document_with_missing_description_rather_than_throwing()   // Review Focus #3
[Fact] public void Returns_empty_list_for_empty_payload()

// ServiceDocumentNormalizerTests
[Fact] public void Maps_epoch_millis_to_utc()               // 1772582400000 -> 2026-03-04T00:00:00Z
[Fact] public void Reads_url_from_nested_links_self()
[Fact] public void Maps_bytes_directly()                    // bytes 148213 -> SizeBytes 148213
[Fact] public void Namespaces_integer_id()                  // 88213 -> "service:88213"
[Fact] public void Maps_unknown_category_to_Unknown_and_keeps_document()
[Fact] public void Tolerates_null_timestamp_as_null_IssuedAt()
[Fact] public void Returns_empty_list_when_items_missing()
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/DocumentViewer.UnitTests --filter FullyQualifiedName~Normalizer`
Expected: FAIL — normalizers do not exist.

- [ ] **Step 3: Implement the request/response contract records and both normalizers.**

Kilobytes → bytes uses `kb * 1024`. Unrecognised type/category strings map to `DocumentType.Unknown` and the document is still returned; a document missing a title is skipped. Deserialisation uses `System.Text.Json` with `PropertyNameCaseInsensitive = true` and no exceptions escaping `Normalize`.

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet test tests/DocumentViewer.UnitTests --filter FullyQualifiedName~Normalizer`
Expected: PASS, 15 tests.

- [ ] **Step 5: Implement `SalesSystemClient` and `ServiceSystemClient` as typed clients implementing `IDocumentSource`.**

Each wraps its whole operation in `try/catch` and returns `UpstreamResult<T>.Failed` — never throws. Map `TimeoutRejectedException`/`TaskCanceledException` (when `ct` is not cancelled) → `Timeout`; `BrokenCircuitException` → `CircuitOpen`; non-success status → `HttpError` with the code; `JsonException` → `InvalidPayload`; caller cancellation → rethrow `OperationCanceledException` so the endpoint can distinguish a client disconnect. Record elapsed time with `Stopwatch.GetTimestamp`.

- [ ] **Step 6: Register both clients in `AddDocumentSources` with `AddStandardResilienceHandler`, using the exact budget values from Global Constraints and base addresses from configuration keys `Upstreams:SalesSystem:BaseAddress` and `Upstreams:ServiceSystem:BaseAddress`.**

- [ ] **Step 7: Run the whole unit suite and commit**

```bash
dotnet test tests/DocumentViewer.UnitTests
git add -A
git commit -m "feat(infrastructure): add resilient typed clients and per-source normalizers"
```

---

### Task 4: Parallel aggregation service

**Files:**
- Create: `src/DocumentViewer.Application/Aggregation/IDocumentAggregationService.cs`, `AggregationResult.cs`, `SourceReport.cs`, `DocumentAggregationService.cs`
- Test: `tests/DocumentViewer.UnitTests/Aggregation/DocumentAggregationServiceTests.cs`

**Interfaces:**
- Consumes: `IDocumentSource` (Task 3), domain types (Task 1).
- Produces:
  ```csharp
  public enum SourceStatus { Ok, Stale, Failed }

  public sealed record SourceReport(
      SourceSystem System, SourceStatus Status, int DocumentCount,
      long DurationMs, UpstreamFailure? Error);

  public sealed record AggregationResult(
      Vin Vin, DateTimeOffset RetrievedAt, bool Partial,
      IReadOnlyList<VehicleDocument> Documents, IReadOnlyList<SourceReport> Sources)
  {
      public bool AllSourcesFailed { get; }   // true when every report is Failed
  }

  public interface IDocumentAggregationService
  {
      Task<AggregationResult> GetDocumentsAsync(Vin vin, CancellationToken ct);
  }
  ```
  Constructor: `DocumentAggregationService(IEnumerable<IDocumentSource> sources, IDocumentCacheRepository cache, IAggregationMetrics metrics, TimeProvider clock, ILogger<DocumentAggregationService> logger)`. `IDocumentCacheRepository` and `IAggregationMetrics` are defined in Tasks 6 and 7; until then, stub them with no-op implementations in the test project so this task stays independently testable.

- [ ] **Step 1: Write the failing aggregation tests**

```csharp
[Fact] public async Task Merges_documents_from_all_sources()
[Fact] public async Task Orders_documents_by_issuedAt_descending_with_nulls_last()
[Fact] public async Task Reports_ok_for_each_successful_source_and_partial_is_false()

[Fact]
public async Task One_source_failing_still_returns_the_other_sources_documents()
{
    var ok     = Source(SourceSystem.SalesSystem, Docs(3));
    var broken = FailingSource(SourceSystem.ServiceSystem, UpstreamFailureReason.HttpError, 503);
    var result = await Service(ok, broken).GetDocumentsAsync(TestVin, default);

    result.Documents.Count.ShouldBe(3);
    result.Partial.ShouldBeTrue();
    result.AllSourcesFailed.ShouldBeFalse();
    result.Sources.Single(s => s.System == SourceSystem.ServiceSystem).Status.ShouldBe(SourceStatus.Failed);
}

[Fact]
public async Task A_throwing_source_does_not_lose_the_sibling_source_result()
{
    // guards the Task.WhenAll trap in spec §4.2
    var ok       = Source(SourceSystem.SalesSystem, Docs(2));
    var throwing = ThrowingSource(SourceSystem.ServiceSystem, new InvalidOperationException("boom"));
    var result   = await Service(ok, throwing).GetDocumentsAsync(TestVin, default);

    result.Documents.Count.ShouldBe(2);
    result.Partial.ShouldBeTrue();
}

[Fact]
public async Task Calls_sources_in_parallel_not_sequentially()
{
    var a = DelayedSource(SourceSystem.SalesSystem,   TimeSpan.FromMilliseconds(400), Docs(1));
    var b = DelayedSource(SourceSystem.ServiceSystem, TimeSpan.FromMilliseconds(400), Docs(1));

    var sw = Stopwatch.StartNew();
    await Service(a, b).GetDocumentsAsync(TestVin, default);
    sw.Stop();

    sw.ElapsedMilliseconds.ShouldBeLessThan(700);   // sequential would be >= 800
}

[Fact] public async Task All_sources_failing_sets_AllSourcesFailed()
[Fact] public async Task Failed_source_with_cached_entry_is_reported_Stale_and_its_documents_included()
[Fact] public async Task Successful_source_results_are_written_to_cache()
[Fact] public async Task Failed_source_results_are_never_written_to_cache()
[Fact] public async Task Empty_results_from_all_sources_yield_empty_list_and_partial_false()
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/DocumentViewer.UnitTests --filter FullyQualifiedName~DocumentAggregationServiceTests`
Expected: FAIL — `DocumentAggregationService` does not exist.

- [ ] **Step 3: Implement `DocumentAggregationService.GetDocumentsAsync`.**

Project each source to a task that already contains its own `try/catch` so `Task.WhenAll` cannot throw — a source that throws despite its contract is caught here and converted to `Failed(Unknown)`. For each `Failed` source, look up the cache: a hit becomes `Stale` with its cached documents, a miss stays `Failed`. `Partial` is true when any report is not `Ok`. Order by `IssuedAt` descending with nulls last, then by `Id` for stable ordering.

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet test tests/DocumentViewer.UnitTests --filter FullyQualifiedName~DocumentAggregationServiceTests`
Expected: PASS, 11 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(application): add parallel document aggregation with partial-result semantics"
```

---

### Task 5: HTTP endpoint, status mapping and OpenAPI

**Files:**
- Create: `src/DocumentViewer.Api/Program.cs`, `Endpoints/VehicleDocumentsEndpoint.cs`, `Contracts/DocumentsResponse.cs`, `appsettings.json`, `appsettings.Development.json`
- Create: `requests.http`
- Test: `tests/DocumentViewer.IntegrationTests/VehicleDocumentsEndpointTests.cs`

**Interfaces:**
- Consumes: `IDocumentAggregationService`, `AggregationResult` (Task 4), `Vin.TryParse` (Task 1).
- Produces: `GET /api/v1/vehicles/{vin}/documents`, and the response DTOs matching spec §5.2 verbatim (`vin`, `retrievedAt`, `partial`, `documents[]`, `sources[]`). JSON uses camelCase; enums serialise as strings.

- [ ] **Step 1: Write the failing endpoint tests using `WebApplicationFactory` with `IDocumentAggregationService` substituted**

```csharp
[Fact] public async Task Returns_200_and_documents_when_all_sources_ok()
[Fact] public async Task Returns_200_with_partial_true_when_one_source_failed()
[Fact] public async Task Returns_503_with_Retry_After_when_all_sources_failed()
[Fact] public async Task Returns_200_with_empty_list_when_no_documents_exist()

[Theory]
[InlineData("TOO-SHORT")]
[InlineData("1HGCM82633A0O4352")]
[InlineData("1HGCM82633A004353")]
public async Task Returns_400_problem_details_for_invalid_vin(string vin)
{
    var response = await Client.GetAsync($"/api/v1/vehicles/{vin}/documents");
    response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
    problem!.Type.ShouldEndWith("invalid-vin");
}

[Fact] public async Task Response_body_never_contains_a_lowercase_or_untrimmed_vin()
[Fact] public async Task Source_system_is_present_on_every_document()
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/DocumentViewer.IntegrationTests --filter FullyQualifiedName~VehicleDocumentsEndpointTests`
Expected: FAIL — the API does not respond.

- [ ] **Step 3: Implement `Program.cs` and the endpoint.**

Compose: `AddProblemDetails`, `AddDocumentSources`, OpenAPI generation, JSON options. The endpoint parses the VIN via `Vin.TryParse` and returns `TypedResults.Problem` with `type` `https://keyloop.example/problems/invalid-vin` on failure; otherwise calls the aggregation service and maps `AllSourcesFailed` → `503` with `Retry-After: 30`, anything else → `200`. Enforce the 6-second whole-request budget with a linked `CancellationTokenSource`.

- [ ] **Step 4: Run them to verify they pass, then confirm end to end against the live mocks**

Run: `dotnet test tests/DocumentViewer.IntegrationTests --filter FullyQualifiedName~VehicleDocumentsEndpointTests`
Expected: PASS, 9 tests.

Run: `docker compose up -d && dotnet run --project src/DocumentViewer.Api` then `curl -s localhost:5000/api/v1/vehicles/1HGCM82633A004352/documents | jq`
Expected: documents from **both** sources, each tagged with its `sourceSystem`, `partial: false`.

- [ ] **Step 5: Generate and commit `openapi.json`, and write `requests.http` covering the happy path, partial failure, both-down, invalid VIN and empty result.**

- [ ] **Step 6: Commit — this is the first submittable milestone**

```bash
git add -A
git commit -m "feat(api): expose unified vehicle documents endpoint with partial-result status mapping"
```

---

### Task 6: Persistence — audit and cache on Supabase

**Files:**
- Create: `src/DocumentViewer.Infrastructure/Persistence/DocumentViewerDbContext.cs`, `Entities/SearchAuditEntity.cs`, `Entities/DocumentCacheEntity.cs`, `DocumentCacheRepository.cs`, `SearchAuditRepository.cs`
- Create: `src/DocumentViewer.Infrastructure/Persistence/Migrations/` (generated)
- Modify: `src/DocumentViewer.Infrastructure/DependencyInjection.cs`, `src/DocumentViewer.Application/Aggregation/DocumentAggregationService.cs`
- Test: `tests/DocumentViewer.IntegrationTests/Persistence/DocumentCacheRepositoryTests.cs`, `SearchAuditRepositoryTests.cs`, `DatabaseOutageTests.cs`

**Interfaces:**
- Consumes: `AggregationResult`, `SourceReport` (Task 4).
- Produces:
  ```csharp
  public sealed record CachedSourceDocuments(IReadOnlyList<VehicleDocument> Documents, DateTimeOffset FetchedAt);

  public interface IDocumentCacheRepository
  {
      Task<CachedSourceDocuments?> GetAsync(Vin vin, SourceSystem system, CancellationToken ct);
      Task UpsertAsync(Vin vin, SourceSystem system, IReadOnlyList<VehicleDocument> documents,
                       TimeSpan ttl, CancellationToken ct);
  }

  public sealed record SearchAuditEntry(
      string Vin, string CorrelationId, DateTimeOffset SearchedAt,
      int DurationMs, bool Partial, IReadOnlyList<SourceReport> Sources);

  public interface ISearchAuditRepository
  {
      Task AppendAsync(SearchAuditEntry entry, CancellationToken ct);
  }
  ```
  Schema exactly as spec §8.4, including index `(vin, searched_at desc)` on `search_audit` and `(expires_at)` on `document_metadata_cache`.

- [ ] **Step 1: Write the failing persistence tests against a Testcontainers PostgreSQL instance**

```csharp
[Fact] public async Task Upsert_then_get_round_trips_documents()
[Fact] public async Task Get_returns_null_for_expired_entry()
[Fact] public async Task Get_returns_null_for_unknown_vin()
[Fact] public async Task Upsert_overwrites_existing_entry_for_same_vin_and_source()

[Fact]
public async Task Concurrent_upserts_for_same_key_do_not_throw()   // Review Focus #4
{
    var tasks = Enumerable.Range(0, 8).Select(_ =>
        Repository.UpsertAsync(TestVin, SourceSystem.SalesSystem, Docs(2), TimeSpan.FromMinutes(15), default));
    await Should.NotThrowAsync(() => Task.WhenAll(tasks));
}

[Fact] public async Task Audit_append_persists_full_vin_and_source_report()
[Fact] public async Task Audit_rows_are_queryable_by_vin_most_recent_first()

[Fact]
public async Task Request_succeeds_with_full_documents_when_database_is_unreachable()   // Review Focus #2
{
    // point the connection string at a dead port; both upstreams healthy
    var response = await ClientWithBrokenDatabase.GetAsync($"/api/v1/vehicles/{TestVin}/documents");
    response.StatusCode.ShouldBe(HttpStatusCode.OK);
    var body = await response.Content.ReadFromJsonAsync<DocumentsResponse>();
    body!.Documents.ShouldNotBeEmpty();
    body.Partial.ShouldBeFalse();
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/DocumentViewer.IntegrationTests --filter FullyQualifiedName~Persistence`
Expected: FAIL — `DocumentViewerDbContext` does not exist.

- [ ] **Step 3: Implement the `DbContext`, both entities with snake_case table/column mapping, and both repositories.**

`UpsertAsync` uses `ON CONFLICT (vin, source_system) DO UPDATE` via raw SQL rather than EF's change tracker, so concurrent writers converge instead of colliding. Documents are stored as `jsonb`. Both repositories swallow and log their own failures at `Warning` and return `null` / complete silently — neither may propagate an exception into the read path.

- [ ] **Step 4: Generate the initial migration and apply it to Supabase using the direct connection**

Run: `dotnet ef migrations add InitialCreate -p src/DocumentViewer.Infrastructure -s src/DocumentViewer.Api` then `dotnet ef database update` with `ConnectionStrings__Default` pointed at port 5432.
Expected: both tables and both indexes present in Supabase.

- [ ] **Step 5: Wire the audit write into the endpoint as fire-and-forget behind `try/catch`, and switch the runtime connection string to the pooler (6543).**

- [ ] **Step 6: Run them to verify they pass**

Run: `dotnet test tests/DocumentViewer.IntegrationTests --filter FullyQualifiedName~Persistence`
Expected: PASS, 8 tests.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(persistence): add search audit and per-source document cache on Supabase"
```

---

### Task 7: Observability

**Files:**
- Create: `src/DocumentViewer.Api/Middleware/CorrelationIdMiddleware.cs`, `Observability/LoggingSetup.cs`, `Observability/TelemetrySetup.cs`, `Observability/HealthChecks.cs`
- Create: `src/DocumentViewer.Infrastructure/Observability/AggregationMetrics.cs`, `CorrelationPropagationHandler.cs`
- Create: `src/DocumentViewer.Application/Observability/IAggregationMetrics.cs`
- Modify: `src/DocumentViewer.Api/Program.cs`
- Test: `tests/DocumentViewer.IntegrationTests/ObservabilityTests.cs`

**Interfaces:**
- Consumes: `SourceSystem`, `SourceStatus`, `Vin.Hash12` / `Vin.Masked`.
- Produces:
  ```csharp
  public interface IAggregationMetrics
  {
      void RecordAggregation(string outcome, TimeSpan duration);
      void RecordUpstream(SourceSystem system, SourceStatus status, TimeSpan duration);
      void RecordPartial();
      void RecordCacheLookup(string result);   // "hit" | "miss" | "error"
  }
  ```
  Meter name `DocumentViewer`; instrument names exactly as spec §7.2. `ActivitySource` name `DocumentViewer`.

- [ ] **Step 1: Write the failing observability tests**

```csharp
[Fact] public async Task Echoes_inbound_X_Correlation_Id_on_the_response()
[Fact] public async Task Generates_a_correlation_id_when_none_supplied()
[Fact] public async Task Propagates_correlation_id_to_both_upstream_requests()
[Fact] public async Task Emits_two_overlapping_upstream_spans_for_one_request()
[Fact] public async Task No_log_line_contains_the_raw_vin()
[Fact] public async Task Health_live_returns_200_when_both_upstreams_are_down()
[Fact] public async Task Health_ready_returns_200_when_both_upstreams_are_down()
[Fact] public async Task Health_ready_returns_503_when_the_database_is_unreachable()

[Fact]
public async Task Client_cancellation_is_not_logged_as_error_and_writes_no_audit_row()   // Review Focus #5
{
    using var cts = new CancellationTokenSource();
    var call = Client.GetAsync($"/api/v1/vehicles/{TestVin}/documents", cts.Token);
    cts.CancelAfter(50);
    await Should.ThrowAsync<TaskCanceledException>(() => call);

    LogSink.Entries.ShouldNotContain(e => e.Level >= LogLevel.Error);
    (await AuditCount(TestVin)).ShouldBe(0);
}
```

The overlapping-spans test asserts that the two upstream activities share the request's parent span and that `salesSpan.StartTime < serviceSpan.StartTime + serviceSpan.Duration && serviceSpan.StartTime < salesSpan.StartTime + salesSpan.Duration` — i.e. their intervals intersect.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/DocumentViewer.IntegrationTests --filter FullyQualifiedName~ObservabilityTests`
Expected: FAIL.

- [ ] **Step 3: Implement Serilog JSON-to-stdout with enrichers for `CorrelationId`, `TraceId`, `VinHash`; `CorrelationIdMiddleware` honouring `traceparent` then `X-Correlation-Id` then generating one; and `CorrelationPropagationHandler` added to both typed clients.**

- [ ] **Step 4: Implement `TelemetrySetup`: OpenTelemetry tracing with ASP.NET Core, `HttpClient` and Npgsql instrumentation, a child span per upstream call, and metrics for every instrument in spec §7.2. OTLP exporter when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, console exporter otherwise.**

- [ ] **Step 5: Implement `HealthChecks`: `/health/live` self only; `/health/ready` database only; `/health/upstream` informational, always `200` with a per-source body.**

- [ ] **Step 6: Run them to verify they pass**

Run: `dotnet test tests/DocumentViewer.IntegrationTests --filter FullyQualifiedName~ObservabilityTests`
Expected: PASS, 9 tests.

- [ ] **Step 7: Capture a trace waterfall screenshot showing the two parallel upstream spans into `docs/images/trace-parallel-fanout.png` and reference it from spec §7.3.**

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(observability): add correlation ids, structured logging, OpenTelemetry and health checks"
```

---

### Task 8: Resilience integration tests against scripted upstreams

**Files:**
- Create: `tests/DocumentViewer.IntegrationTests/Resilience/UpstreamResilienceTests.cs`, `Fixtures/WireMockUpstreamFixture.cs`
- Test: as above

**Interfaces:**
- Consumes: the full composed application (Tasks 5–7) with both upstream base addresses pointed at WireMock.Net servers.
- Produces: nothing consumed downstream.

- [ ] **Step 1: Write the failing resilience tests**

```csharp
[Fact]
public async Task Parallel_fan_out_takes_roughly_one_upstream_latency_not_two()
{
    SalesStub.WithFixedDelay(TimeSpan.FromSeconds(1));
    ServiceStub.WithFixedDelay(TimeSpan.FromSeconds(1));

    var sw = Stopwatch.StartNew();
    var response = await Client.GetAsync($"/api/v1/vehicles/{TestVin}/documents");
    sw.Stop();

    response.StatusCode.ShouldBe(HttpStatusCode.OK);
    sw.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(1600));   // sequential would exceed 2000
}

[Fact] public async Task One_upstream_timing_out_returns_200_partial_within_the_total_budget()
[Fact] public async Task Both_upstreams_timing_out_returns_503()
[Fact] public async Task Transient_500_is_retried_and_succeeds_on_the_second_attempt()
[Fact] public async Task Upstream_404_is_not_retried()                      // exactly one request recorded
[Fact] public async Task Upstream_400_is_not_retried()
[Fact] public async Task Repeated_failures_open_the_circuit_and_subsequent_calls_fail_fast()
[Fact] public async Task Open_circuit_on_one_source_does_not_affect_the_other_source()
[Fact] public async Task Malformed_upstream_payload_reports_InvalidPayload_not_500()   // Review Focus #3
[Fact] public async Task Stale_cache_is_served_when_upstream_fails_after_a_successful_fetch()
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/DocumentViewer.IntegrationTests --filter FullyQualifiedName~Resilience`
Expected: FAIL.

- [ ] **Step 3: Implement `WireMockUpstreamFixture` (two servers, helpers to script delay, status, payload, and to count received requests) and fix whatever the tests expose.**

Failures here are expected to be real bugs in Tasks 3–6, not test bugs — treat each as such.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test`
Expected: PASS, all projects.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "test(resilience): prove parallel fan-out, timeout budget, retry policy and breaker isolation"
```

---

### Task 9: Submission artefacts

**Files:**
- Create: `README.md`, `docs/ai-collaboration/prompts.md`, `docs/ai-collaboration/review-log.md`
- Modify: `docs/system-design.md` (v2 — reconcile with what was actually built)

**Interfaces:**
- Consumes: the finished implementation.
- Produces: the three artefacts the brief requires.

- [ ] **Step 1: Write `README.md`: prerequisites, `docker compose up` + `dotnet run` quickstart, Supabase connection-string configuration via environment variables, `dotnet test`, the cURL examples from `requests.http`, a response-shape walkthrough, and an architecture summary linking to the design document.**

- [ ] **Step 2: Write the README's AI Collaboration Narrative section.**

Structure it as strategy, then verification process, then a defect table with one row per AI-generated problem actually caught, each naming the symptom and why it mattered. Seed rows, all of which are real: `Task.WhenAll` discarding a sibling result; retrying `4xx`; caching failure responses; `DateTime.Now` instead of an injected `TimeProvider`; FluentAssertions proposed despite its licence. Add the rest as they occur during Tasks 1–8 — **record them while implementing, not from memory at the end.**

- [ ] **Step 3: Copy the actual prompts used into `docs/ai-collaboration/prompts.md` and the review notes into `review-log.md`.**

- [ ] **Step 4: Update `docs/system-design.md` to v2** — reconcile every diagram and interface with the code as built, add the trace screenshot from Task 7, and move anything that was cut into §9 Non-Goals with its reason.

- [ ] **Step 5: Verify the README from a clean clone**

Run: `git clone <repo> /tmp/keyloop-verify && cd /tmp/keyloop-verify && docker compose up -d && dotnet test`
Expected: tests pass following only the README, with no step the README does not mention. Fix the README, not the shell history.

- [ ] **Step 6: Record the video (5–10 minutes): introduction and scenario choice; design walkthrough; the AI collaboration story (1–2 min); live demo including a chaos-injected upstream failure showing the partial response; learnings and challenges.**

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "docs: add README with AI collaboration narrative and finalise system design v2"
```
