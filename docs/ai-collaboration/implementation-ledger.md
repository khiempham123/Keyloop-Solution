# Implementation Ledger

The task-by-task record kept while the plan was executed: the pre-flight scan for
conflicts between tasks, every ruling made where the plan and the spec disagreed — each
with its reasoning and what it costs if wrong — and the test command and result that
closed each task.

It is included because a reviewer should be able to see the decisions taken on their
behalf, not just the code that came out. The plan it executes is in
[`../superpowers/plans/`](../superpowers/plans/); the defects found along the way are in
[`review-log.md`](review-log.md).

---

# SDD ledger — plan: docs/superpowers/plans/2026-10-29-scenario-d-document-viewer.md

## Pre-flight Interface Scan

| Producer → Consumer | Interface / Contract | Finding & Ruling |
|---|---|---|
| Task 1 → Task 3, 4, 5, 6, 7 | `Vin`, `VinError`, `SourceSystem`, `DocumentType`, `VehicleDocument`, `UpstreamResult<T>`, `UpstreamFailure`, `UpstreamFailureReason` | Clean match across all tasks and spec §4.2, §5.2. |
| Task 2 → Task 3, 5 | `MockSalesApi` (`:5101`) & `MockServiceApi` (`:5102`) schemas + `/admin/chaos` | Clean match with spec §5.3 table. |
| Task 4 ↔ Task 5, 6, 7 | `IDocumentCacheRepository` (Task 6) and `IAggregationMetrics` (Task 7) consumed by `DocumentAggregationService` (Task 4) and DI host (Task 5) | Ruling: Define `IDocumentCacheRepository` (`CachedSourceDocuments`) and `IAggregationMetrics` in `DocumentViewer.Application` during Task 4 using exact Task 6/7 signatures, and register no-op defaults in Task 5 (`TryAddSingleton`) until Tasks 6 & 7 replace them — why: `Application` must compile in Task 4 and `Api` must run end-to-end in Task 5 — what it costs if wrong: zero, signatures are identical to Tasks 6 & 7. |
| Task 4 → Task 5 | `SourceStatus` JSON representation in `DocumentsResponse` | Ruling: Annotate `SourceStatus` enum members with `[JsonStringEnumMemberName("ok" / "stale" / "failed")]` while keeping `JsonStringEnumConverter` PascalCase for `SourceSystem`, `DocumentType`, and `UpstreamFailureReason` — why: matches spec §4.3 & §5.2 JSON example verbatim — what it costs if wrong: trivial attribute adjustment. |
| Spec §8.4 ↔ Task 6 & 7 | `search_audit.correlation_id` column type (`uuid` in §8.4 diagram vs `string CorrelationId` in Task 6 and arbitrary `X-Correlation-Id` / 32-hex W3C `TraceId` in §7.1 & Task 7) | Ruling: Use `varchar(128)` for `search_audit.correlation_id` instead of Postgres `uuid` — why: inbound `X-Correlation-Id` headers and W3C 32-char `TraceId` values are not RFC 4122 UUIDs and would fail insert on `uuid` — what it costs if wrong: ~100 bytes per audit row vs broken audit writes on custom correlation IDs. |

Task 1: Ruling: Plan's VinTests snippet contained 12 cases while claiming 13 and omitted the ISO 3779 remainder-10 ('X') branch; added 1M8GDM9AXKP042788 as the 13th test case — why: exercises mod 11 == 10 branch and matches 13-test expectation — what it costs if wrong: nothing, valid ISO 3779 VIN.
Task 1: complete (commits 1fed2a3..403f43d, tests: dotnet test tests/DocumentViewer.UnitTests → Passed!  - Failed:     0, Passed:    13, Skipped:     0, Total:    13, Duration: 31 ms - DocumentViewer.UnitTests.dll (net9.0))
Task 2: complete (commits 403f43d..3c99371, tests: dotnet test KeyloopDocumentViewer.sln → No test is available in /Volumes/240GB/Keyloop/tests/DocumentViewer.IntegrationTests/bin/Debug/net9.0/DocumentViewer.IntegrationTests.dll. Make sure that test discoverer & executors are registered and platform & framework version settings are appropriate and try again.)
Task 3: complete (commits 3c99371..a3b0c7f, tests: dotnet test tests/DocumentViewer.UnitTests → Passed!  - Failed:     0, Passed:    36, Skipped:     0, Total:    36, Duration: 168 ms - DocumentViewer.UnitTests.dll (net9.0))
Task 4: complete (commits a3b0c7f..84406b1, tests: dotnet test tests/DocumentViewer.UnitTests → Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47, Duration: 501 ms - DocumentViewer.UnitTests.dll (net9.0))
Task 5: complete (commits 84406b1..717e717, tests: dotnet test KeyloopDocumentViewer.sln → Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 1 s - DocumentViewer.IntegrationTests.dll (net9.0))
Task 6: complete (commits 717e717..f9086e0, tests: dotnet test tests/DocumentViewer.IntegrationTests --filter FullyQualifiedName~Persistence --no-build → Passed!  - Failed:     0, Passed:     8, Skipped:     0, Total:     8, Duration: 3 s - DocumentViewer.IntegrationTests.dll (net9.0))
Ruling: Continue committing on `main` rather than cutting a branch — why: all 8 prior commits of this plan are already on `main`, this is a single-author submission repo, and Task 9 verifies from a clean clone of the default branch — what it costs if wrong: a rebase onto a branch, no code change.
Task 7: complete (commits f9086e0..358567b, tests: dotnet test tests/DocumentViewer.IntegrationTests --filter FullyQualifiedName~ObservabilityTests → Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9)
Defect fix: Ruling: Readiness budget enforced by racing the probe instead of cancelling it, and made configurable (default 5s) — why: Npgsql does not reliably observe a CancellationToken while opening a physical connection, so a 200ms budget was ignored and readiness ran the full 30s connect timeout against live Supabase — what it costs if wrong: an abandoned probe task per timed-out readiness poll, which is observed and discarded.
Defect fix: Ruling: Cache writes use CancellationToken.None rather than the request token, with an 8s repository write budget and a 1s response-path budget — why: the request token is cancelled when the response is sent, so cross-region writes were always cancelled mid-handshake and the cache stayed permanently empty, making the spec §4.3 stale fallback dead code — what it costs if wrong: a cache write can outlive its request by up to 8s and is not cancellable by the client.
Defect fix: complete (commits 358567b..a4985d7, tests: dotnet test KeyloopDocumentViewer.sln → Passed! 56 unit + 29 integration = 85, Failed: 0)
Task 8: Ruling: Fixed AddDocumentSources to resolve upstream configuration lazily from IServiceProvider rather than capturing IConfiguration at registration time, and dropped its IConfiguration parameter — why: the eager read ignored every configuration source added after registration, so the tests called the real mock containers and any deployment setting an upstream address via environment variable would have been silently ignored — what it costs if wrong: one extra IConfiguration lookup per HttpClient creation.
Task 8: Ruling: Disabled parallel test collections for the integration test project — why: four tests assert on elapsed time and CPU contention from sibling classes pushed a healthy upstream past its 2s per-attempt timeout, producing a correct 503 that the test could not parse — what it costs if wrong: integration suite runs ~2m11s instead of ~1m44s.
Task 8: complete (commits a4985d7..9cdb6a3, tests: dotnet test KeyloopDocumentViewer.sln → Passed! 56 unit + 39 integration = 95, Failed: 0)
Task 9: Ruling: Readiness reports Healthy when no database is configured at all, Unhealthy only when one is configured and unreachable — why: running without cache and audit is a supported configuration and the service still answers document searches, so spec §7.4's "I can do my job" definition applies; the clean-clone experience otherwise opens on a 503 — what it costs if wrong: an operator who forgot to configure a database sees a healthy service with persistence silently disabled, which the health response body states explicitly.
Task 9: Ruling: Video deliverable deferred at the user's explicit request — why: the user asked to complete today's work excluding the video — what it costs if wrong: one of three required submission artefacts is outstanding; it is the only remaining item.
Task 9: complete (commits 9cdb6a3..bea60ed, tests: dotnet test from a clean clone with no database and no docker compose → Passed! 56 unit + 40 integration = 96, Failed: 0)
Final review: fresh reviewer (Opus, high effort) on review-f9086e0..bea60ed.diff — 1 Critical, 7 Important, 13 Minor.
Final: Ruling: Re-graded minor #8 (submission hygiene) up to Important — why: the README linked the git-ignored .superpowers/sdd/ workspace, which is a dead link in any clone, and TODO.md/env.sh were committed internal scaffolding — what it costs if wrong: nothing; the ledger is published as docs/ai-collaboration/implementation-ledger.md and the two files stay on disk, untracked.
Final: Ruling: Re-graded minor #4 (observability stubs using field names the normalisers do not read) up to Important — why: all nine tests in that class were running against zero-document responses and passing, so the group was not pinning what it claimed to — what it costs if wrong: one extra guard test.
Final: fixed C1 (a hung database discarded a healthy source's documents, turning 200-partial into 503-empty) — A_hung_database_cannot_discard_a_healthy_source_when_the_request_budget_expires RED→GREEN, suite 101/101
Final: fixed I1 (Parallel_fan_out failed 3/3 run alone; --filter Resilience flaky 1/4) — warm-up request before the clock + breaker loops fail loudly; verified 3/3 and 4/4 green, suite 101/101
Final: fixed I2 (committed openapi.json marked omitted-when-null members required, so a generated client rejects the happy path) — OpenApiContractTests RED→GREEN, suite 101/101
Final: fixed I3 (design doc §8.4 still drew correlation_id as uuid against varchar(128) in code) — doc corrected
Final: fixed I4 (requests.http pointed at port 5000 while the README runs 5080) — requests.http and launchSettings aligned to 5080
Final: fixed I5 (cache.lookup recorded a hit from an abandoned lookup after the response went out without it) — A_cache_lookup_that_exceeds_its_budget_is_recorded_as_a_timeout_not_a_hit RED→GREEN, suite 101/101
Final: fixed I6 (whole-request timeout recorded no metric and no audit row, blinding the §7.5 availability SLO) — TimeoutAccountingTests RED→GREEN, suite 101/101
Final: fixed I7 (two of three hardening settings matched Npgsql defaults, so the test passed even if Harden returned its input) — Pins_every_setting_it_depends_on_instead_of_inheriting_an_Npgsql_default now asserts emission; verified by mutation: returning the input unchanged fails 2 tests, suite 101/101
Final: minor (deferred): health-check Task.Delay(budget, cancellationToken) reports "did not answer within 5000ms" when the caller cancels, which is a false message
Final: minor (deferred): cache reads for failed sources are awaited sequentially, so two failed sources can cost 2s on the response path
Final: minor (deferred): Task.Delay with no token leaves a 1s timer per cache operation even when the operation wins the race
Final: minor (deferred): NullAggregationMetrics is dead code since AddDocumentSources always replaces IAggregationMetrics
Final: minor (deferred): with no OTLP endpoint the console exporter interleaves traces and metrics with Serilog JSON on the same stdout
Final: minor (deferred): SalesDocumentNormalizer's fileSizeKb * 1024 is unchecked, so an absurd upstream value yields a negative sizeBytes
Final: minor (deferred): the upstream response body is read with no size cap and outside the Polly budget (HttpCompletionOption.ResponseHeadersRead)
Post-review: Ruling: Acted on deferred minors #6 and #7 at the user's explicit request, each red-to-green — why: the user asked for them specifically, and writing the test for #7 showed the finding was understated (an unbounded body read did not merely grow memory, it never returned; the test process had to be killed after 70s) — what it costs if wrong: an upstream body above 4 MiB or slower than 2s is reported as a source failure rather than read.
Post-review: fixed minor #6 (unchecked kilobytes * 1024 yielded a negative sizeBytes) — Reports_a_size_it_cannot_represent_as_unknown_rather_than_a_negative_number RED→GREEN, suite 108/108
Post-review: fixed minor #7 (upstream body read with no size cap and outside the Polly budget) — Reports_a_body_that_never_stops_arriving / Reports_a_body_that_trickles_past_its_budget RED (hung, killed at 70s) →GREEN, suite 108/108
