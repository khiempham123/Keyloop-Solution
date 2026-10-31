# AI Collaboration — Review Log

A running record of what GenAI proposed, what was done with it, and why. Entries are
added **while** the work happens, not reconstructed afterwards — the point of the log is
to be evidence, and evidence written from memory at the end is not evidence.

Each entry answers three questions: what did the model produce, what did I do with it,
and what would have gone wrong if I had just accepted it.

---

## Design phase — 2026-10-29

### D-1 · Scenario selection scored against an explicit rubric

**Proposed.** Given the brief, the model scored all four scenarios on implementation
complexity, risk of scope overrun, and how much substance each left for the design
document. It ranked Scenario D first on signal per unit of effort and advised against
Scenario A.

**Decision.** Accepted, with the reasoning checked rather than the ranking. Scenario A
scores highest on technical depth — resource-constrained booking is a genuine
concurrency problem — but it is also where a subtly wrong solution is easy to ship and
certain to be probed. Scenario D's reliability concerns are intrinsic to the problem
rather than bolted on, which is what the brief's "scalability, performance, reliability,
maintainability, observability" language is asking for.

**If accepted blindly.** Little harm here — but the rubric itself was the valuable
output, not the answer. Being able to say *why* D over A is what the first evaluation
dimension is asking for.

---

### D-2 · A mandatory requirement that Scenario D does not naturally satisfy

**Proposed.** The model was asked to re-read Part 2 of the brief adversarially and
report any requirement Scenario D fails to satisfy by construction. It found that
*"If you choose Backend: Expose a RESTful API and **use a persistent database**"* is
mandatory, while Scenario D is pure read-only aggregation that generates no data of its
own.

**Decision.** Accepted the finding; made the design decision myself. Rather than add a
table to satisfy a checkbox, two tables were chosen that each answer a real operational
question: `search_audit` (who accessed which vehicle's documents, and did they see a
complete answer — auditable access is a real expectation in automotive retail) and
`document_metadata_cache` (which is what makes the stale-fallback behaviour in §4.3
possible at all). Recorded as assumption #1 in the design document.

**If accepted blindly — or missed.** Submitting a backend with no database would fail an
explicit requirement of the brief. This is the single highest-value thing the AI
contributed in the design phase.

---

### D-3 · `Task.WhenAll` silently destroys partial results

**Proposed.** The draft fan-out design was handed back to the model with one
instruction: find the failure mode. It identified that `await Task.WhenAll(sales,
service)` rethrows the first faulted task's exception and discards the sibling task's
already-completed result.

**Decision.** Accepted and designed around. Every source call is wrapped so that failure
is a **return value**, not an exception (`UpstreamResult<T>` with `Ok` / `Failed`
cases); `IDocumentSource.FetchAsync` catches at its own boundary. With that in place
`Task.WhenAll` cannot throw and the aggregator always receives exactly one outcome per
source. Two tests pin it: one where a source returns `Failed`, and one where a source
*throws* despite its contract.

**If accepted blindly.** The bug only appears when an upstream is down — that is, never
in a demo and always in production. It would have broken acceptance criterion #3
(consolidated list) and made the headline `response.partial` metric unmeasurable.

---

### D-4 · Dependency with a licence that cannot ship

**Proposed.** The model's first test-stack suggestion included FluentAssertions.

**Decision.** Rejected and replaced with Shouldly. FluentAssertions v8 moved to a
commercial licence; a test dependency that a company cannot legally use in a commercial
product is not a usable dependency, however familiar it is.

**If accepted blindly.** A licence problem in a submission for a commercial employer —
a bad first impression that has nothing to do with engineering skill.

---

### D-5 · Status code for a partial result

**Proposed.** The model initially reached for a `5xx`/`207`-flavoured response when one
upstream fails.

**Decision.** Rejected. The endpoint returns `200` with `partial: true` and a per-source
`sources[]` report; only the all-sources-failed case returns `503`. Rationale: the
user's goal is to see the documents that *do* exist, and many HTTP clients discard the
response body on a `5xx` — a status code choice would have thrown away data the service
successfully retrieved. `207 Multi-Status` is a WebDAV semantic about per-resource
outcomes and does not describe this; the `sources[]` block carries that information
without abusing a status code. Recorded as assumption #2.

**If accepted blindly.** Clients would have had no safe way to render a degraded answer,
which is the one behaviour this scenario exists to demonstrate.

---

### D-6 · Database choice, and where tests point

**Proposed.** The model offered a Postgres-versus-SQLite trade-off: SQLite runs anywhere
with no Docker, Postgres keeps the index and concurrency story credible.

**Decision.** Supabase PostgreSQL for the running application — managed, real Postgres,
nothing to install locally. But integration tests point at a **local** Postgres via
Testcontainers, not at Supabase: tests that depend on a network and on shared remote
state are flaky, slow, and capable of corrupting the data being used for the demo video.
Same Npgsql provider in both places, so no behavioural divergence. Two practical
consequences recorded in the plan: migrations must use Supabase's direct connection
(5432) because the transaction-mode pooler (6543) does not support session-level
features, and no connection string is committed.

**If accepted blindly.** Pointing the test suite at Supabase would have produced an
intermittently red build — the worst possible thing to hand a reviewer.

---

### D-7 · Where the AI was given no authority

Deliberately **not** delegated, and recorded with reasoning in the design document so a
reviewer can disagree with the argument rather than the output: the API contract and its
status-code semantics (§5.1), the timeout budget (§8.1), treating a VIN as personal data
and hashing it in logs (§7.1), excluding upstreams from `/health/ready` (§7.4), and
every entry in the assumptions table (§10).

---

## Implementation phase

Filled in as Tasks 1–8 are executed. Format: what the model generated, what was changed,
why it mattered. One row per real defect — an empty section is better than a padded one.

| # | Task | What the AI produced | What I changed | Why it mattered |
|---|---|---|---|---|
| I-1 | Task 1 | Scaffolding script ran `dotnet add package Microsoft.AspNetCore.Mvc.Testing --no-restore`, writing floating `Version="*"` into `.csproj` and resolving `10.0.14` (`net10.0`-only), failing restore with `NU1202` and `NU1604` under `TreatWarningsAsErrors`. | Removed `--no-restore` / wildcard versions and pinned deterministic `.NET 9`-compatible package versions (`9.0.*` for `Microsoft.AspNetCore.*` / `EF Core`, explicit versions for `Shouldly` and `NSubstitute`). Also noted for §6 of the design doc that moving to `.NET 10 LTS` changes `TargetFramework` once in `Directory.Build.props` plus framework-aligned `9.x` package pins. | Floating `*` versions break reproducible builds (`TreatWarningsAsErrors` rejects `NU1604`) and pull incompatible major-version packages the moment a newer .NET major ships on NuGet. |
| I-2 | Task 1 & 2 | The AI-generated plan specified ISO 3779 check-digit validation in `Vin.TryParse`, miscounted 12 unit tests as 13 while omitting the `mod 11 == 10 → 'X'` check-digit branch, and chose European Audi VIN `WAUZZZ8V7JA003421` in Task 2 for the empty-documents `200 OK` test case. | Added `1M8GDM9AXKP042788` (check digit `X`, remainder 10) as the 13th `VinTests` case, and computed the ISO 3779 weighted sum for `WAUZZZ8V7JA003421` (`309 ≡ 1 mod 11`, not `7`), replacing the empty-seed VIN with valid `WAUZZZ8V1JA003421` (and keeping `WAUZZZ8V7JA003421` only as a cautionary note). | European `WAUZZZ` filler VINs do not enforce North American check digits; using `WAUZZZ8V7JA003421` for the empty-result scenario would have failed edge validation with `400 InvalidCheckDigit` instead of testing `200` + `documents: []`. |
| I-3 | Task 3 | Standard `AddHttpClient<IDocumentSource, TClient>()` + `AddStandardResilienceHandler()` defaults in .NET / Polly v8 carry three silent traps: (1) omitting an explicit client `name` keys both registrations under `"IDocumentSource"`, sharing one `BaseAddress` and one circuit breaker; (2) default `Retry.Delay = 2s` plus `AttemptTimeout = 2s` exceeds `TotalRequestTimeout = 5s` before a second retry can run; (3) default `CircuitBreaker.MinimumThroughput = 100` never trips under moderate burst traffic or integration tests. | Named the two `IHttpClientFactory` pipelines `"SalesSystem"` and `"ServiceSystem"`, set `Retry.Delay = 200ms` (exponential + jitter), and made `CircuitBreaker.MinimumThroughput` configurable (default `5`). | Preserved true per-upstream fault isolation (§2 Principle 3) and made retry + breaker budgets mathematically consistent with the 2s / 5s / 6s timeout hierarchy. |
| I-4 | Task 5 | In `Response_body_never_contains_a_lowercase_or_untrimmed_vin`, generated test code called `raw.ShouldNotContain("1hgcm82633a004352")` assuming `string.Contains` / FluentAssertions case-sensitive semantics. Shouldly's `ShouldContain` / `ShouldNotContain` defaults to `Case.Insensitive`, so it matched the properly normalized `"1HGCM82633A004352"` and failed. | Passed explicit `Case.Sensitive` (`ShouldNotContain(..., Case.Sensitive)`) and checked untrimmed whitespace as well. | Direct consequence of switching from FluentAssertions to Shouldly (D-4); also prevents false-green case-sensitivity assertions (`ShouldContain` matching wrong-cased output). |
| I-5 | Task 6 | Three persistence subtleties surfaced against the draft plan/spec: (1) §8.4 drew `search_audit.correlation_id` as `uuid`, whereas §7.1 accepts arbitrary `X-Correlation-Id` strings and 32-hex W3C `TraceId`s; (2) standard request-scoped `AddDbContext` fails on concurrent `UpsertAsync` calls (`"A second operation was started on this context instance"`) and throws `ObjectDisposedException` inside fire-and-forget `Task.Run` audit writes after the HTTP response completes (silently dropping every audit row behind `try/catch`); (3) branching on `builder.Configuration.GetConnectionString("Default")` before `builder.Build()` ignores `WebApplicationFactory.ConfigureAppConfiguration` overrides, causing `DatabaseOutageTests` to hit a no-op repository instead of a broken DB. | Mapped `correlation_id` as `varchar(128)`, switched to `AddDbContextFactory<DocumentViewerDbContext>` with per-operation `DbContext` creation, resolved `IConfiguration` lazily inside DI factory delegates, and asserted `ShouldBeOfType<DocumentCacheRepository>()` in `DatabaseOutageTests`. | Kept non-UUID correlation IDs valid, made concurrent upserts and fire-and-forget audit writes safe in production, and prevented a false-green database-outage integration test. |
| I-6 | Task 7 | Even after application loggers logged only `VinHash` / `VinMasked` and a Serilog enricher scrubbed string properties (fixing `Microsoft.AspNetCore.Hosting`'s ambient `RequestPath` scope), `No_log_line_contains_the_raw_vin` failed on a degraded `503` upstream call because `Microsoft.Extensions.Http.Resilience` (`Polly` execution-attempt logger) stores the outbound URL as a `ScalarValue` wrapping a `System.Uri` object rather than `string`, leaking the raw VIN into `@m`. Separately, model-suggested `OpenTelemetry.* 1.11.2` failed restore under `TreatWarningsAsErrors` with `NU1902` security advisories. | Extended `VinRedactingLogEnricher` to recursively scrub `ScalarValue` (`string` and `Uri`), `StructureValue`, `SequenceValue`, and `DictionaryValue` (plus a matching `BaseProcessor<Activity>` for OpenTelemetry span URL tags), passed `preserveStaticLogger: true` to `UseSerilog` for parallel test isolation, and upgraded `OpenTelemetry.*` to patched `1.20.1`. | Prevented a silent PII leak into production JSON logs during upstream retries — the exact degraded-mode path when Polly warning logs fire — and kept the dependency tree free of known CVEs. |
| I-7 | Task 7 | Inspecting the first real Jaeger waterfall screenshot against live Supabase (`eu-west-1`) showed that while the two upstream HTTP calls overlapped (`354 ms` and `384 ms`), `DocumentAggregationService` awaited `TryCacheUpsertAsync` sequentially inside the `foreach` loop over `outcomes`, serialising two cross-region Postgres `ON CONFLICT DO UPDATE` round-trips after fan-out completed. | Collected `TryCacheUpsertAsync` tasks into `cacheUpsertTasks` and awaited them concurrently via `Task.WhenAll(cacheUpsertTasks)` (preserving deterministic read-after-write cache semantics while overlapping both DB writes). | Cut steady-state cache-write latency in half on multi-source hits — directly discovered by inspecting our own OpenTelemetry waterfall rather than synthetic mocks. |
