# TODO — Keyloop Scenario D (Unified Document Viewer)

**Deadline:** thứ 4, **2026-11-04**. Hôm nay 29/10 → còn 6 ngày.
**Trạng thái:** design + plan + AI log đã xong. **Chưa có dòng code nào.** Chưa `git init`.

Chi tiết từng task (signature chính xác, tên test, assertion) nằm ở
[docs/superpowers/plans/2026-10-29-scenario-d-document-viewer.md](docs/superpowers/plans/2026-10-29-scenario-d-document-viewer.md).
File này là bản checklist để tick; cần biết *viết gì* thì mở plan ra đọc task tương ứng.
Thiết kế và lý do nằm ở [docs/system-design.md](docs/system-design.md).

> File này + `docs/superpowers/` là ghi chú nội bộ. Trước khi nộp: `.gitignore` chúng, hoặc
> giữ lại `docs/superpowers/plans/` làm bằng chứng cho AI Collaboration Narrative (khuyến nghị
> giữ) nhưng xoá `TODO.md` và `docs/superpowers/RESUME.md`.

---

## 0. Chặn trước — phải xong mới code được

- [x] **Tạo Supabase project**, lấy **cả hai** connection string:
  - direct, port `5432` → dùng cho `dotnet ef database update`
  - pooler (transaction mode), port `6543` → dùng cho runtime
  - Lý do phải tách: pooler không hỗ trợ session-level features mà migration cần.
  - Để trong env var `ConnectionStrings__Default` / `ConnectionStrings__Migrations`. **Không commit.**
- [x] **Tạo GitHub repo** → `git init`, commit 5 file hiện có, push.
- [x] *(tuỳ chọn)* Paste 4 dòng `export` từ [env.sh](env.sh) vào `~/.zshrc` để khỏi `source ./env.sh` mỗi lần.
- [x] **Đọc lại và tự xác nhận** 3 quyết định xoay cả thiết kế, trước khi code theo chúng:
  - `search_audit` + `document_metadata_cache` (assumption #1 — đề **bắt buộc** có DB, mà Scenario D không sinh ra dữ liệu nào)
  - partial result trả `200` + `partial: true`, **không** `5xx` (assumption #2)
  - **không** dedupe chéo nguồn (assumption #6)
- [x] **Tự verify §11 design doc** (GenAI in design phase) — người chấm sẽ hỏi lại trong video. Đừng trình bày thứ chưa tự kiểm.

**Mỗi shell:** `source ./env.sh` → dotnet 9.0.320 từ `/Volumes/240GB/dotnet`.

---

## 1. Task 1 — Solution skeleton + domain primitives

Plan §Task 1. **DoD:** `dotnet test` xanh, 13 test VIN.

- [x] `dotnet new sln`, 4 project `src/`, 2 project `tests/`, wire project reference
- [x] `Directory.Build.props`: `net9.0`, nullable, `TreatWarningsAsErrors` — **khai báo `TargetFramework` đúng một lần ở đây**, không lặp trong `.csproj` nào (đây là thứ làm việc chuyển lên .NET 10 LTS chỉ còn sửa một dòng)
- [x] `.gitignore`, `.editorconfig`
- [x] Viết 13 test `VinTests` **trước** → chạy cho FAIL
- [x] `Vin.TryParse` (trim + upper trước khi validate), `VinError`, `SourceSystem`, `DocumentType`, `VehicleDocument`, `UpstreamResult<T>`, `UpstreamFailure`
- [x] Check-digit ISO 3779 (thuật toán đầy đủ trong plan Step 4)
- [x] `Vin.ToString()` trả về `Masked`, **không** trả raw VIN — chặn lỡ tay log PII
- [x] Test xanh → commit `feat(domain): add Vin value object, document model and UpstreamResult`

## 2. Task 2 — Hai mock upstream với schema khác nhau

Plan §Task 2. **DoD:** `docker compose up` + 2 lệnh curl trả 2 payload **khác shape**; chaos `fail` trả 503.

- [x] `mocks/MockSalesApi` — `GET /sales/vehicles/{vin}/documents`, port `5101`
- [x] `mocks/MockServiceApi` — `GET /service/v2/vehicles/{vin}/docs`, port `5102`
- [x] **Schema phải lệch thật** (bảng đối chiếu ở design doc §5.3): `documentId` string vs `id` int; `createdDate` ISO vs `timestamp` epoch-millis; `fileSizeKb` KB vs `bytes`; `url` phẳng vs `links.self` lồng; envelope `{documents}` vs `{data:{items},meta}`
- [x] Seed ≥4 doc cho `1HGCM82633A004352`, rỗng cho `WAUZZZ8V7JA003421`, và **một `category` lạ** để test mapping → `Unknown`
- [x] `POST /admin/chaos` với `mode: normal|slow|fail|garbage` — `garbage` trả 200 kèm payload thiếu field
- [x] `docker-compose.yml` (chỉ 2 mock; DB là Supabase)
- [x] Commit `feat(mocks): add Sales and Service mock APIs with divergent schemas and chaos injection`

> Chaos endpoint là 30 giây ấn tượng nhất của video: bật `fail` ngay trên camera rồi show response `partial: true`.

## 3. Task 3 — Normalizer + typed HTTP client có resilience

Plan §Task 3. **DoD:** 15 test normalizer xanh.

- [x] 15 test normalizer trước → FAIL
- [x] `SalesDocumentNormalizer`: KB→bytes (`*1024`), ISO date → UTC midnight, id → `sales:{id}`
- [x] `ServiceDocumentNormalizer`: epoch-millis → UTC, đọc `links.self` lồng, id → `service:{id}`
- [x] Enum lạ → `DocumentType.Unknown` nhưng **vẫn trả document** (assumption #7); thiếu title → skip document, **không throw**
- [x] `SalesSystemClient` / `ServiceSystemClient` implement `IDocumentSource`, bọc `try/catch` **toàn bộ** → trả `UpstreamResult.Failed`, không bao giờ throw
- [x] Map lỗi → reason: timeout→`Timeout`, `BrokenCircuitException`→`CircuitOpen`, non-2xx→`HttpError`, `JsonException`→`InvalidPayload`; caller cancel thì **rethrow** `OperationCanceledException`
- [x] `AddStandardResilienceHandler`: per-attempt **2s**, total **5s**, retry **2** lần có jitter, breaker ratio **0.5** / window **30s** / break **15s** — **mỗi upstream một pipeline riêng**
- [x] Retry **chỉ** transient (`5xx`, `408`, `429`, timeout, socket). **Không** retry `4xx` khác
- [x] Commit `feat(infrastructure): add resilient typed clients and per-source normalizers`

## 4. Task 4 — Aggregation song song  ← trái tim của đề bài

Plan §Task 4. **DoD:** 11 test xanh, trong đó test đo thời gian chứng minh gọi song song.

- [x] 11 test trước → FAIL
- [x] `DocumentAggregationService.GetDocumentsAsync`
- [x] **Không** `await Task.WhenAll(sales, service)` thẳng — mỗi source đã tự `try/catch` trả `UpstreamResult`, nên `WhenAll` không bao giờ throw. Source nào throw bất chấp contract thì bắt ở đây → `Failed(Unknown)`
- [x] Source `Failed` → tra cache: hit → `Stale` + dùng doc cache; miss → `Failed`
- [x] `Partial = true` khi có bất kỳ report nào không `Ok`
- [x] Sort `IssuedAt` desc, **null xuống cuối**, tie-break bằng `Id`
- [x] **Không bao giờ** cache kết quả fail
- [x] Test `Calls_sources_in_parallel_not_sequentially` — 2 source delay 400ms, tổng phải < 700ms
- [x] Test `A_throwing_source_does_not_lose_the_sibling_source_result`
- [x] Commit `feat(application): add parallel document aggregation with partial-result semantics`

## 5. Task 5 — Endpoint + status mapping + OpenAPI  ← mốc nộp được tối thiểu

Plan §Task 5. **DoD:** `curl` thật qua 2 mock trả doc từ **cả hai** nguồn, `partial: false`.

- [x] 9 test endpoint trước (`WebApplicationFactory`) → FAIL
- [x] `GET /api/v1/vehicles/{vin}/documents`, response đúng shape design doc §5.2
- [x] Status mapping: `200` (có data, live hoặc cache) · `200` + `documents: []` (VIN hợp lệ, không có doc — **không** 404) · `400` ProblemDetails (VIN sai) · `503` + `Retry-After: 30` (cả hai nguồn fail)
- [x] JSON camelCase, enum serialize ra string
- [x] Budget toàn request **6s** bằng linked `CancellationTokenSource`
- [x] Sinh + **commit `openapi.json`** (đây là client stub mà đề yêu cầu)
- [x] `requests.http` phủ: happy · partial · both-down · VIN sai · rỗng
- [x] Commit `feat(api): expose unified vehicle documents endpoint with partial-result status mapping`

> **🚩 Mốc cứng: hết Chủ nhật 01/11 phải tới được đây.** Chưa tới thì cắt scope ngay theo §9 bên dưới.

## 6. Task 6 — Persistence trên Supabase

Plan §Task 6. **DoD:** 8 test xanh, gồm test DB sập mà request vẫn `200` đủ document.

- [x] 8 test persistence trước (Testcontainers Postgres **local**, không trỏ Supabase) → FAIL
- [x] `DocumentViewerDbContext`, 2 entity, mapping snake_case, schema đúng design doc §8.4
- [x] Index `search_audit(vin, searched_at desc)` và `document_metadata_cache(expires_at)`
- [x] `UpsertAsync` dùng `ON CONFLICT (vin, source_system) DO UPDATE` bằng raw SQL, **không** qua change tracker → ghi đồng thời không vỡ PK
- [x] Documents lưu `jsonb`, TTL **15 phút**
- [x] **Cả hai repository tự swallow lỗi**, log `Warning`, trả `null` / im lặng — **không** để exception lọt vào đường đọc
- [x] Audit ghi fire-and-forget trong `try/catch` riêng
- [x] `dotnet ef migrations add InitialCreate` → `dotnet ef database update` qua **port 5432**; runtime chuyển sang **6543**
- [x] Commit `feat(persistence): add search audit and per-source document cache on Supabase`

## 7. Task 7 — Observability

Plan §Task 7. **DoD:** 9 test xanh + có screenshot trace 2 span song song.

- [ ] 9 test trước → FAIL
- [ ] Serilog JSON ra stdout, enrich `CorrelationId` / `TraceId` / `VinHash`
- [ ] `CorrelationIdMiddleware`: ưu tiên `traceparent` → `X-Correlation-Id` → tự sinh; echo lại trên response
- [ ] `CorrelationPropagationHandler` gắn vào **cả hai** typed client
- [ ] **Không log line nào chứa VIN thô** — chỉ `VinHash` (12 hex đầu SHA-256) + 6 ký tự cuối. VIN đầy đủ chỉ nằm trong bảng `search_audit`
- [ ] OpenTelemetry: tracing (ASP.NET Core + HttpClient + Npgsql), 1 span con/upstream; metrics đúng 6 instrument ở design doc §7.2; OTLP khi có `OTEL_EXPORTER_OTLP_ENDPOINT`, không thì console
- [ ] Health: `/health/live` (self) · `/health/ready` (**chỉ DB**) · `/health/upstream` (informational, luôn 200)
- [ ] Level: upstream timeout là `Warning`, **không** `Error` — page on-call vì upstream chậm là cách huấn luyện người ta bỏ qua alert
- [ ] Client disconnect: không log `Error`, không ghi audit, không tính là upstream failure
- [ ] **Screenshot trace waterfall** 2 span song song → `docs/images/trace-parallel-fanout.png`, link vào design doc §7.3
- [ ] Commit `feat(observability): add correlation ids, structured logging, OpenTelemetry and health checks`

## 8. Task 8 — Test resilience với upstream dàn dựng

Plan §Task 8. **DoD:** `dotnet test` xanh toàn bộ, 10 test resilience.

- [ ] `WireMockUpstreamFixture` (2 server, script được delay/status/payload, đếm số request nhận được)
- [ ] Test song song: 2 upstream delay 1s → tổng < 1.6s (tuần tự sẽ > 2s)
- [ ] 1 timeout → `200` partial trong budget · cả hai timeout → `503`
- [ ] `500` transient được retry và thành công lần 2
- [ ] `404` và `400` **không** retry (đếm đúng 1 request)
- [ ] Lỗi liên tiếp → breaker mở → fail fast; **breaker nguồn này không ảnh hưởng nguồn kia**
- [ ] Payload rác → `InvalidPayload`, **không** `500`
- [ ] Cache stale được dùng khi upstream fail sau một lần fetch thành công
- [ ] Commit `test(resilience): prove parallel fan-out, timeout budget, retry policy and breaker isolation`

> Test ở đây fail thì phần lớn là **bug thật** ở Task 3–6, không phải bug test. Đừng sửa test cho xanh.

## 9. Task 9 — Ba deliverable nộp bài

Plan §Task 9.

- [ ] **`README.md`**: prerequisite · `docker compose up` + `dotnet run` · cấu hình connection string Supabase qua env var · `dotnet test` · cURL examples · giải thích response shape · link design doc
- [ ] **AI Collaboration Narrative trong README**: strategy → quy trình verify → **bảng defect**. Seed 5 dòng đã có thật ở [docs/ai-collaboration/review-log.md](docs/ai-collaboration/review-log.md): `Task.WhenAll` mất partial result · retry `4xx` · cache lỗi · `DateTime.Now` thay vì `TimeProvider` inject · FluentAssertions licence
- [ ] **Ghi thêm defect vào `review-log.md` NGAY LÚC gặp** trong Task 1–8, không viết lại từ trí nhớ cuối tuần
- [ ] Copy prompt thật đã dùng vào `docs/ai-collaboration/prompts.md` (Phase 8)
- [ ] **`docs/system-design.md` → v2**: đồng bộ diagram/interface với code thật, thêm screenshot trace, chuyển mọi thứ đã cắt vào §9 Non-Goals **kèm lý do**
- [ ] **Verify README từ clone sạch**: `git clone <repo> /tmp/keyloop-verify && cd /tmp/keyloop-verify && docker compose up -d && dotnet test` — phải xanh khi chỉ làm theo README. Sai thì **sửa README**, đừng sửa shell history
- [ ] **Quay video 5–10 phút**: giới thiệu + lý do chọn scenario · walkthrough design · AI story (1–2 phút) · demo có bật chaos cho 1 upstream chết để show partial response · learnings & challenges
- [ ] Commit `docs: add README with AI collaboration narrative and finalise system design v2`

---

## Lịch còn lại

| Ngày | Task |
|---|---|
| T6 30/10 | Task 1 + Task 2 |
| T7 31/10 | Task 3 + Task 4 |
| **CN 01/11** | **Task 5 — hết ngày phải chạy end-to-end** |
| T2 02/11 | Task 6 + Task 7 |
| T3 03/11 | Task 8 + Task 9 (README, AI narrative, design doc v2) + **quay video** |
| T4 04/11 sáng | Verify từ clone sạch, quay lại video nếu cần, **nộp** |

Trừ ra **nửa ngày** cho video. Nó là deliverable bắt buộc và quay lại nhiều lần rất tốn thời gian.

## Thứ tự cắt khi trễ

Cắt từ trên xuống, và **ghi vào §9 Non-Goals kèm lý do** — chủ động loại trừ đọc ra như sự trưởng thành, im lặng bỏ qua đọc ra như sót việc:

auth/authz → pagination → dedup phức tạp → Redis (dùng `IMemoryCache`) → Supabase (dùng SQLite) → Testcontainers (dùng in-memory provider) → OTLP exporter (console là đủ)

**Tuyệt đối không cắt:** gọi song song · partial failure · test suite · DB · design doc · AI narrative · video. Mỗi thứ map 1-1 vào một dòng trong Evaluation Framework của đề.

## Bốn trục người chấm dùng

| Trục | Nằm ở đâu trong việc trên |
|---|---|
| Problem Solving & System Design | `docs/system-design.md` — nhất là §10 Assumptions và §9 Non-Goals |
| Technical Execution | Task 1–8, đặc biệt test đo song song (Task 4, 8) và partial failure |
| **AI Engineering & Verification** | Bảng defect trong README + `docs/ai-collaboration/` — **ghi trong lúc làm** |
| Communication & Presentation | README chạy được từ clone sạch + video 5–10 phút |
