# Sổ tay tra cứu cho lập trình viên

**Bản tiếng Việt** · Keyloop Technical Assessment — Scenario D · Tác giả: Pham Gia Khiem

| | |
|---|---|
| **Bản gốc tiếng Anh** | [`developer-guide.md`](../../docs/developer-guide.md) — **đây là bản thẩm quyền** |
| **Mục lục bộ tiếng Việt** | [`tai-lieu-tieng-viet.md`](../tai-lieu-tieng-viet.md) |
| **Nội dung** | Tra cứu — file, luồng, mọi con số, cách mở rộng |

> Nếu bản tiếng Việt và bản tiếng Anh khác nhau, **bản tiếng Anh là bản đúng**. Tên lớp,
> tên file, tên test, khóa cấu hình và khối code giữ nguyên tiếng Anh vì chúng là định
> danh trong mã nguồn.
>
> Các tham chiếu dạng **§I.x**, **§III.x**… trỏ sang phần tương ứng trong bộ tài liệu này:
> I = thiết kế, II = tour mã nguồn, III = tra cứu, IV = README, V = nhật ký review,
> VI = prompt, VII = phán quyết, VIII = plan. Mục lục ở link trên.

---

Tài liệu tra cứu để làm việc **bên trong** codebase này: mọi thứ nằm ở đâu, được và
không được phụ thuộc vào cái gì, một request **thực sự** chảy qua code như thế nào, mọi
con số service này thực thi và thực thi ở đâu, cùng các công thức cho những thay đổi anh
có khả năng cao sẽ được yêu cầu làm.

---

## III.1. Bản đồ solution

```
KeyloopDocumentViewer.sln
├─ src/
│  ├─ DocumentViewer.Domain/          7 file    — từ vựng. Không phụ thuộc gì cả.
│  ├─ DocumentViewer.Application/     9 file    — nghiệp vụ. Chỉ phụ thuộc Domain.
│  ├─ DocumentViewer.Infrastructure/ 20 file    — thế giới bên ngoài. Phụ thuộc Application.
│  └─ DocumentViewer.Api/             8 file    — biên HTTP. Phụ thuộc Application + Infrastructure.
├─ mocks/
│  ├─ MockSalesApi/                   đóng thế hệ thống nguồn Sales
│  └─ MockServiceApi/                 đóng thế hệ thống nguồn Service
└─ tests/
   ├─ DocumentViewer.UnitTests/       65 test, ~5 giây, không cần Docker
   └─ DocumentViewer.IntegrationTests/ 43 test, ~2 phút, cần Docker
```

### Luật phụ thuộc

```
Api ──────► Infrastructure ──┐
 │                           ├──► Application ──► Domain
 └───────────────────────────┘
```

Ba luật, và lý do từng luật tồn tại:

1. **`Domain` không tham chiếu gì cả.** Nếu một type ở đây cần `DbContext` hay
   `HttpClient`, nó đang nằm sai project. Đây chính là thứ giữ cho `Vin` và
   `UpstreamResult<T>` **test được mà không cần setup gì cả**.
2. **`Application` không được biết về HTTP, SQL, JSON hay Polly.** Nó phụ thuộc vào
   `IDocumentSource`, `IDocumentCacheRepository`, `ISearchAuditRepository` và
   `IAggregationMetrics` — tất cả đều **do chính nó định nghĩa**. Đây chính là thứ làm
   cho các luật xử lý lỗi một phần — phần khó nhất của service — **unit-test được mà
   không cần mạng hay database**.
3. **`Api` không chứa nghiệp vụ.** Nó parse một VIN, gọi một service, map một kết cục
   sang một status code. Nó **không biết có bao nhiêu upstream**.

Nếu một thay đổi khiến anh muốn phá một trong ba luật này, đó là **tín hiệu về thay đổi
đó**, không phải tín hiệu về cái luật.

---

## III.2. Các lệnh dùng hằng ngày

```bash
source ./env.sh                  # .NET 9 SDK từ ổ đĩa ngoài (riêng của tác giả; xem phần prerequisites trong README)

dotnet build KeyloopDocumentViewer.sln
dotnet test                      # tất cả; integration test cần Docker đang chạy
dotnet test tests/DocumentViewer.UnitTests                      # vòng lặp nhanh, ~5 giây
dotnet test --filter FullyQualifiedName~Resilience               # nhóm luật độ tin cậy
dotnet test --filter FullyQualifiedName~VinTests                 # một nhóm

docker compose up -d --build     # hai mock hệ thống nguồn, ở :5101 và :5102
dotnet run --project src/DocumentViewer.Api --urls http://localhost:5080
```

Migration (cần kết nối **trực tiếp**, cổng 5432 — **không phải** pooler):

```bash
dotnet tool restore
export ConnectionStrings__Migrations='Host=…;Port=5432;…'
dotnet ef migrations add <Name> -p src/DocumentViewer.Infrastructure -s src/DocumentViewer.Api
dotnet ef database update       -p src/DocumentViewer.Infrastructure -s src/DocumentViewer.Api
```

`DesignTimeDbContextFactory` là thứ làm cho `dotnet ef` chạy được **không cần boot host**.

### Debug một request

```bash
# theo dõi một request từ đầu tới cuối: correlation id buộc mọi dòng log lại với nhau
curl -s -H 'X-Correlation-Id: debug-1' localhost:5080/api/v1/vehicles/1HGCM82633A004352/documents | jq
# rồi grep stdout của API theo id đó
```

Set `OTEL_EXPORTER_OTLP_ENDPOINT` để gửi trace tới một collector; nếu không set, trace và
metric ra console, **xen lẫn** với JSON của Serilog (đã ghi nhận là một điểm thô — xem
§III.12).

---

## III.3. Tham chiếu cấu hình

Mọi khóa, giá trị mặc định, và điều gì vỡ nếu set sai.

| Khóa | Mặc định | Ghi chú |
|---|---|---|
| `Upstreams:SalesSystem:BaseAddress` | `http://localhost:5101` | Được phân giải **mỗi lần tạo client**, không phải lúc khởi động — xem §III.11 |
| `Upstreams:ServiceSystem:BaseAddress` | `http://localhost:5102` | như trên |
| `Upstreams:CircuitBreakerMinimumThroughput` | `5` | Số lời gọi cần có trong cửa sổ 30 giây trước khi một breaker **được phép** mở. Giá trị nhỏ hơn 2 bị bỏ qua |
| `ConnectionStrings:Default` | *(không có)* | Dùng lúc runtime. Supabase: **transaction pooler, cổng 6543**. **Không có cũng là một cấu hình được hỗ trợ** |
| `ConnectionStrings:Migrations` | *(không có)* | Chỉ cho `dotnet ef`. Supabase: **direct/session, cổng 5432** |
| `HealthChecks:DatabaseTimeoutMs` | `5000` | Ngân sách readiness. Giá trị ≤ 0 bị bỏ qua |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | *(không có)* | Không set → dùng exporter ra console |

**Vì sao cần hai connection string.** Pooler chế độ transaction của Supabase ghép kênh
(multiplex) các kết nối và **không hỗ trợ** các tính năng mức session mà migration của EF
Core cần. Dùng một string cho cả hai nghĩa là: hoặc migration fail, hoặc runtime giữ một
session connection **cho mỗi request**.

**Hardening connection string.** Bất cứ gì anh cấp đều đi qua
`NpgsqlConnectionHardening`, nó điền `Timeout=15`, `Minimum Pool Size=1` và
`Connection Idle Lifetime=300` **chỉ ở những chỗ anh chưa tự set**. Trong ba cái đó,
`Minimum Pool Size` là cái **thực sự đổi hành vi** của Npgsql (mặc định của nó là 0); hai
cái còn lại **ghim** đúng giá trị mặc định hiện tại của Npgsql, để một thay đổi trong
tương lai không thể âm thầm nới rộng ngân sách bắt tay mà service này đang phụ thuộc vào.
Một lưu ý: **một keyword lạ ở bất cứ đâu trong string sẽ làm
`NpgsqlConnectionStringBuilder` throw**, và khi đó string được truyền qua **y nguyên** —
bao gồm cả việc **không có kết nối nóng**.

---

## III.4. Một request thực sự chảy như thế nào

### III.4.1 Đường đi thuận lợi

```
CorrelationIdMiddleware          traceparent → X-Correlation-Id → tự sinh; set CorrelationContext
  └─ VehicleDocumentsEndpoint.HandleAsync
       ├─ Vin.TryParse(vin)                                  400 + ProblemDetails nếu thất bại
       ├─ budgetCts = linked(RequestAborted) + CancelAfter(6s)
       ├─ DocumentAggregationService.GetDocumentsAsync
       │    ├─ với mỗi IDocumentSource  ──┐  Task.WhenAll trên các task đã bọc
       │    │    └─ SalesSystemClient / ServiceSystemClient
       │    │         └─ UpstreamClientExecutor.ExecuteAsync
       │    │              ├─ httpClient.GetAsync(…, ResponseHeadersRead, ct)   ← Polly KẾT THÚC Ở ĐÂY
       │    │              ├─ Content-Length > 4 MiB?            → InvalidPayload
       │    │              ├─ bodyCts = linked(ct) + CancelAfter(2s)
       │    │              ├─ BoundedStream(4 MiB) + DeserializeAsync
       │    │              └─ normalizer.Normalize(payload)      → UpstreamResult.Ok | .Failed
       │    ├─ với mỗi source Failed: TryCacheGetWithinBudgetAsync (1s)   → Stale | Failed
       │    ├─ các lệnh ghi cache, phóng với CancellationToken.None, đua với 1s
       │    ├─ sắp theo IssuedAt giảm dần (null xuống cuối), rồi tới Id
       │    └─ metrics.RecordAggregation / RecordPartial
       ├─ FireAndForgetAudit(…, CancellationToken.None)
       └─ 200 | 503
```

Hai chỗ trong chuỗi đó **phản trực giác và là có chủ ý**:

- **Ngân sách của Polly kết thúc ở response header.**
  `HttpCompletionOption.ResponseHeadersRead` nghĩa là pipeline **trả về trước khi body
  tới**, nên body phải có **giới hạn kích thước riêng** và **ngân sách 2 giây riêng**.
  Không có chúng, một body được nhỏ giọt chỉ bị giới hạn bởi timeout toàn request — và
  từng có lúc nó **không bị giới hạn bởi bất cứ gì**, treo vô hạn.
- **Lệnh ghi cache và ghi audit dùng `CancellationToken.None`.** Token của request bị
  hủy **ngay khoảnh khắc response được gửi**. Truyền nó vào nghĩa là một lệnh ghi liên
  vùng **luôn bị hủy giữa lúc bắt tay** và **không có gì từng được cache**.

### III.4.2 Khi một nguồn chết

Giống hệt cho tới khi fan-out trả về. `UpstreamResult.Failed` của nguồn lỗi đưa bộ tổng
hợp sang tra cache: có dữ liệu → thành `SourceStatus.Stale` và tài liệu của nó được merge
vào; không có → vẫn `Failed`. `partial` là true trong **cả hai** trường hợp. Response
**vẫn là `200`** — xem §I.5.1 để biết vì sao chọn vậy, và §III.9 để biết client nhìn thấy
gì.

### III.4.3 Khi cả hai nguồn chết

Mọi báo cáo đều là `Failed`, nên `AggregationResult.AllSourcesFailed` là true và endpoint
trả `503` kèm `Retry-After: 30`. Đường đi này **có** được đếm metric và **có** được ghi
audit. Đường đi timeout toàn request cũng vậy, nó trả cùng status nhưng với
`ProblemDetails.type` khác.

---

## III.5. Tham chiếu theo file

### `DocumentViewer.Domain` — từ vựng

| File | Bề mặt công khai | Cần để ý |
|---|---|---|
| `Vin.cs` | `Vin.TryParse(string?, out Vin, out VinError)`, `.Value`, `.Hash12`, `.Masked` | Trim và chuyển chữ hoa **trước khi** validate. **`ToString()` trả về `Masked`** nên một lần lỡ nội suy chuỗi không thể làm lộ PII |
| `VinError.cs` | `None, Empty, InvalidLength, IllegalCharacter, InvalidCheckDigit` | Map sang nội dung chi tiết của `400` |
| `VehicleDocument.cs` | `record(Id, SourceSystem, DocumentType, Title, IssuedAt?, SizeBytes?, DownloadUrl?)` | Ba thành viên nullable; chúng **bị bỏ khỏi JSON**, đó là lý do hợp đồng OpenAPI **không được** đánh dấu chúng là required |
| `SourceSystem.cs` | `SalesSystem = 1, ServiceSystem = 2` | Giá trị tường minh: chúng **được lưu xuống DB** dưới dạng `smallint` |
| `DocumentType.cs` | `Unknown = 0, PurchaseOrder, FinanceAgreement, RegistrationDocument, JobCard, InspectionReport, WarrantyClaim` | `Unknown = 0` là **có chủ ý** — một loại lạ từ upstream **vẫn giữ được tài liệu** |
| `UpstreamResult.cs` | `UpstreamResult<T>.Ok(Value, Duration)` / `.Failed(Failure, Duration)` | Ý tưởng duy nhất mà cả codebase được xây trên đó |
| `UpstreamFailure.cs` | `UpstreamFailure(Reason, Message, StatusCode?)`, `UpstreamFailureReason { Timeout, CircuitOpen, HttpError, InvalidPayload, Unknown }` | Lộ ra tới client qua `sources[].error.reason` |

### `DocumentViewer.Application` — nghiệp vụ

| File | Bề mặt công khai | Cần để ý |
|---|---|---|
| `Sources/IDocumentSource.cs` | `SourceSystem System`, `FetchAsync(Vin, CancellationToken)` | **Kiểu trả về chính là hợp đồng**: một hiện thực mà throw là đang phá nó |
| `Aggregation/DocumentAggregationService.cs` | `GetDocumentsAsync(Vin, CancellationToken)` | 252 dòng, **file lớn nhất solution** và là file nên đọc đầu tiên |
| `Aggregation/AggregationResult.cs` | `record(Vin, RetrievedAt, Partial, Documents, Sources)`, `.AllSourcesFailed` | `AllSourcesFailed` là **thứ duy nhất** tầng API rẽ nhánh theo |
| `Aggregation/SourceReport.cs` | `SourceStatus { Ok, Stale, Failed }`, `SourceReport(System, Status, DocumentCount, DurationMs, Error?)` | `SourceStatus` serialise ra chữ thường qua `JsonStringEnumMemberName` |
| `Persistence/IDocumentCacheRepository.cs` | `GetAsync`, `UpsertAsync(…, TimeSpan ttl, …)`, `CachedSourceDocuments` | Khóa theo `(vin, source)`, **không bao giờ** theo request |
| `Persistence/ISearchAuditRepository.cs` | `AppendAsync(SearchAuditEntry, …)` | Các hiện thực **phải tự nuốt lỗi của mình** |
| `Observability/IAggregationMetrics.cs` | `RecordAggregation`, `RecordUpstream`, `RecordPartial`, `RecordCacheLookup` | Định nghĩa ở đây để `Application` đo được **mà không tham chiếu OpenTelemetry** |
| `Observability/DocumentViewerTelemetry.cs` | `ActivitySource`, `CorrelationContext` | `CorrelationContext.Current` là cách log enricher tìm ra id |

### `DocumentViewer.Infrastructure` — thế giới bên ngoài

| File | Cần để ý |
|---|---|
| `DependencyInjection.cs` | `AddDocumentSources()`, `AddPersistence()`. Cả hai client đều **có tên**; cấu hình được phân giải từ `IServiceProvider` **bên trong** các delegate |
| `Sources/UpstreamClientExecutor.cs` | Nơi luật "không bao giờ throw" được thực thi, và nơi các giới hạn body nằm. Mọi loại exception map sang một `UpstreamFailureReason`; việc người gọi hủy thì được **ném lại** |
| `Sources/BoundedStream.cs` | Wrapper chỉ-đọc, throw `UpstreamBodyTooLargeException` khi vượt 4 MiB. Thực thi **trong lúc đọc**, vì một response chunked **không khai báo độ dài** |
| `Sources/Sales/*`, `Sources/Service/*` | Contract, normaliser và typed client cho mỗi upstream. Hai schema bất đồng có chủ ý — xem §III.6 |
| `Persistence/DocumentViewerDbContext.cs` | Map sang snake_case, payload `jsonb`, hai index |
| `Persistence/DocumentCacheRepository.cs` | SQL thô `ON CONFLICT … DO UPDATE`. **Hai ngân sách:** 1500 ms cho đọc (trên đường response), 8 giây cho ghi (sau response, phải sống nổi qua kết nối lạnh) |
| `Persistence/SearchAuditRepository.cs` | Ngân sách 8 giây, nuốt mọi lỗi |
| `Persistence/NpgsqlConnectionHardening.cs` | §III.3. Đọc các khóa **đã được cấp** từ một `DbConnectionStringBuilder` thuần, vì `NpgsqlConnectionStringBuilder.ContainsKey` trả lời một câu hỏi khác |
| `Persistence/DatabaseWarmupService.cs` | Là `BackgroundService`, nên một database không với tới được **không thể làm service không boot nổi** |
| `Observability/AggregationMetrics.cs` | Sáu instrument ở §I.7 |
| `Observability/CorrelationPropagationHandler.cs` | `DelegatingHandler` trên cả hai client |
| `DefaultNoOpServices.cs` | Cache/audit rỗng (no-op) dùng khi không cấu hình connection string |

### `DocumentViewer.Api` — biên HTTP

| File | Cần để ý |
|---|---|
| `Program.cs` | 30 dòng. Nếu nó phình quá một màn hình, có thứ gì đã rò rỉ vào đó |
| `Endpoints/VehicleDocumentsEndpoint.cs` | Parse VIN, ngân sách 6 giây, kết cục → status, ghi audit |
| `Contracts/DocumentsResponse.cs` | Hình dạng trên dây. Thành viên nullable **bị bỏ đi**, không phải để null |
| `Contracts/OptionalPropertySchemaTransformer.cs` | Giữ `openapi.json` trung thực bằng cách suy ra tính optional **từ chính các record**. Phải là **schema** transformer, vì `document.Components.Schemas` **vẫn là null** khi các document transformer chạy |
| `Middleware/CorrelationIdMiddleware.cs` | `traceparent` → `X-Correlation-Id` → tự sinh; dội lại trên response |
| `Observability/LoggingSetup.cs` | 278 dòng, phần lớn là `VinRedactingLogEnricher`, nó đệ quy qua `ScalarValue` / `StructureValue` / `SequenceValue` / `DictionaryValue` — một logger retry của thư viện thứ ba đã lưu URL dưới dạng object `Uri` và làm lộ một VIN |
| `Observability/TelemetrySetup.cs` | Trace + metric, OTLP hoặc console |
| `Observability/HealthChecks.cs` | Ba endpoint, ba phân biệt có chủ ý — xem §III.8 |

---

## III.6. Hai schema upstream

Chuẩn hóa chỉ có nghĩa nếu có gì đó để chuẩn hóa:

| Hợp nhất | Sales | Service |
|---|---|---|
| `Id` | `documentId` — string | `id` — số nguyên |
| `DocumentType` | `docType` `"PURCHASE_ORDER"` | `category` `"job-card"` |
| `Title` | `description` | `name` |
| `IssuedAt` | `createdDate` `"2026-03-04"` | `timestamp` epoch **milli giây** |
| `SizeBytes` | `fileSizeKb` — **kilobyte** | `bytes` |
| `DownloadUrl` | `url` — phẳng | `links.self` — lồng |
| phong bì | `{ documents: [...] }` | `{ data: { items: [...] }, meta: {...} }` |

Mỗi dòng đều có test. **Hai dòng dễ làm sai theo kiểu chạm tới cả client:**

- **Kilobyte.** `fileSizeKb * 1024` là phép toán **không kiểm tra tràn trên dữ liệu không
  đáng tin**. Một giá trị lớn hơn `long.MaxValue / 1024` sẽ báo kích thước là **không
  biết** thay vì một số **âm**, và tài liệu **vẫn được trả về**.
- **Thiếu tiêu đề.** Bản ghi đó bị **bỏ qua** và nguồn **vẫn là `ok`**. Một bản ghi không
  dùng được **không phải lý do** để làm thất bại cả một hệ thống nguồn.

---

## III.7. Mọi con số gom về một chỗ

| Cái gì | Giá trị | Thực thi ở | Test |
|---|---|---|---|
| Timeout mỗi lần thử, mỗi upstream | 2 s | `DependencyInjection` (Polly) | `One_upstream_timing_out…` |
| Tổng mỗi upstream | 5 s | `DependencyInjection` (Polly) | như trên |
| Toàn request | 6 s | `VehicleDocumentsEndpoint` | như trên |
| Số lần retry upstream | 2, lũy thừa + jitter, chỉ lỗi tạm thời | `DependencyInjection` | `Transient_500_is_retried…` / `Client_error_responses_are_never_retried` |
| Độ trễ retry | 200 ms | `DependencyInjection` | — (mặc định 2 s **không thể** nhồi hai lần retry vào 5 s) |
| Circuit breaker | tỉ lệ 0,5 / cửa sổ 30 s / mở 15 s, **mỗi upstream một cái** | `DependencyInjection` | `Repeated_failures_open_the_circuit…` / `Open_circuit_on_one_source…` |
| Throughput tối thiểu của breaker | 5 (cấu hình được) | `DependencyInjection` | — |
| Kích thước body upstream | 4 MiB | `UpstreamClientExecutor` + `BoundedStream` | `Reports_a_body_that_never_stops_arriving…` |
| Thời gian đọc body upstream | 2 s | `UpstreamClientExecutor` | `Reports_a_body_that_trickles_past_its_budget…` |
| Đọc cache, trên đường response | 1500 ms | `DocumentCacheRepository` | — |
| Xử lý cache, trên đường response | 1 s | `DocumentAggregationService` | `A_database_that_accepts_but_never_answers…` |
| Ghi cache | 8 s | `DocumentCacheRepository` | `Cache_writes_outlive_the_request…` |
| Ghi audit | 8 s | `SearchAuditRepository` | — |
| TTL của cache | 15 phút | `DocumentAggregationService` | `Get_returns_null_for_expired_entry` |
| Ngân sách readiness | 5 s (cấu hình được) | `HealthChecks` | `Readiness_budget_comes_from_configuration…` |
| Warm-up database | 20 s | `DatabaseWarmupService` | — |

Sáu metric, dưới meter `DocumentViewer`:

```
documentviewer.aggregation.duration   histogram  {outcome}
documentviewer.upstream.duration      histogram  {system}
documentviewer.upstream.result        counter    {system, status}
documentviewer.response.partial       counter
documentviewer.cache.lookup           counter    {result: hit|miss|error|timeout}
documentviewer.circuit.state          gauge      {system}
```

---

## III.8. Health check

| Endpoint | Kiểm tra | Fail khi nào |
|---|---|---|
| `/health/live` | tiến trình | không bao giờ, miễn là nó còn chạy |
| `/health/ready` | **chỉ** database | một database đã được cấu hình mà không với tới được |
| `/health/upstream` | cả hai upstream, chỉ tham khảo | không bao giờ — luôn `200` kèm body theo từng nguồn |

Ba phân biệt đáng đọc code để hiểu:

1. **Readiness loại trừ các upstream.** Nếu không, việc cả hai hệ thống nguồn chết sẽ rút
   mọi instance khỏi vòng phục vụ — và service sẽ ngừng phục vụ chính những tài liệu
   trong cache mà nó vẫn hoàn toàn đủ sức phục vụ.
2. **Một database vắng mặt là `Healthy`.** Chạy không cache và audit là một cấu hình được
   hỗ trợ. Một database **đã cấu hình mà không với tới được** thì không.
3. **Ngân sách được thực thi bằng cách ĐUA probe, không phải HỦY nó.** Npgsql không quan
   sát cancellation token một cách đáng tin trong lúc đang mở kết nối vật lý, nên một
   ngân sách dựa trên cancel đã bị âm thầm phớt lờ. Để ý **thứ tự trong code**:
   `cts.Cancel()` xảy ra **trước** khi `using` dispose nó, nhờ đó một lệnh `Register` tới
   muộn từ Npgsql sẽ chạy **inline** thay vì throw. **Thứ tự đó là chịu lực.**

---

## III.9. Client nhìn thấy gì, với mọi chế độ lỗi

| Điều kiện | Status | `partial` | `sources[].status` | `error.reason` |
|---|---|---|---|---|
| Cả hai nguồn trả lời | `200` | `false` | `ok`, `ok` | — |
| VIN hợp lệ, không có tài liệu ở đâu cả | `200` | `false` | `ok`, `ok` | — |
| Một nguồn trả `5xx` | `200` | `true` | `ok`, `failed` | `HttpError` |
| Một nguồn trả `4xx` | `200` | `true` | `ok`, `failed` | `HttpError` |
| Một nguồn timeout | `200` | `true` | `ok`, `failed` | `Timeout` |
| Breaker của một nguồn đang mở | `200` | `true` | `ok`, `failed` | `CircuitOpen` |
| Một nguồn trả body không parse được | `200` | `true` | `ok`, `failed` | `InvalidPayload` |
| Một nguồn trả body > 4 MiB | `200` | `true` | `ok`, `failed` | `InvalidPayload` |
| Một nguồn lỗi, cache có dữ liệu | `200` | `true` | `ok`, `stale` | lỗi của lần gọi sống |
| Một bản ghi của một nguồn thiếu tiêu đề | `200` | `false` | `ok`, `ok` | — (bản ghi bị bỏ qua) |
| Cả hai nguồn lỗi, không có gì trong cache | `503` | — | `ProblemDetails`, `Retry-After: 30` | — |
| Ngân sách toàn request hết | `503` | — | `ProblemDetails` (`…/request-timeout`) | — |
| VIN không hợp lệ | `400` | — | `ProblemDetails` (`…/invalid-vin`) | — |
| Client tự ngắt kết nối | *(không có response)* | — | không log mức `Error`, không ghi audit | — |
| Database không với tới được | như trên, không bị ảnh hưởng | — | `stale` trở thành **bất khả** | — |

---

## III.10. Công thức mở rộng

### Thêm hệ thống nguồn thứ ba

1. `Sources/Warranty/WarrantyContracts.cs` — các record khớp payload của nó.
2. `WarrantyDocumentNormalizer : IDocumentNormalizer<WarrantyDocumentsResponse>` — map
   sang `VehicleDocument`, đặt tiền tố cho id (`warranty:{id}`), loại lạ → `Unknown`,
   bản ghi thiếu tiêu đề → bỏ qua.
3. `WarrantySystemClient : IDocumentSource` — ủy thác cho `UpstreamClientExecutor`.
4. Thêm `SourceSystem.WarrantySystem = 3` (giá trị tường minh: nó **được lưu xuống DB**).
5. Đăng ký trong `AddDocumentSources` với **named client riêng** và **resilience pipeline
   riêng**. **PHẢI đặt cho nó một cái tên khác** nếu không nó sẽ dùng chung base address
   và chung circuit breaker với một client khác.
6. Thêm `Upstreams:WarrantySystem:BaseAddress` vào `appsettings.json`.

**Không có gì trong `Application` hay `Api` phải đổi:** bộ tổng hợp lặp trên
`IEnumerable<IDocumentSource>`, và `sources[]` chỉ nhiều thêm một phần tử.

### Thêm một loại tài liệu

Thêm thành viên vào `DocumentType` (**tuyệt đối không đánh số lại các giá trị cũ** —
chúng được lưu trong payload của cache), rồi map chuỗi của upstream trong normaliser
tương ứng. Những chuỗi chưa map **đã** rơi về `Unknown`, nên quên bước hai sẽ làm **mất
loại, không mất tài liệu**.

### Đổi một ngân sách

Mọi giá trị đều ở §III.7 kèm file sở hữu nó. Đổi ở đó, rồi chạy test ghi bên cạnh — có
vài test khẳng định về thời gian trôi qua và chúng sẽ cho anh biết nếu các ngân sách đã
trở nên **mâu thuẫn nhau** (ví dụ: một độ trễ retry không thể nhồi nổi vào trong tổng).

### Thêm một metric

Thêm method vào `IAggregationMetrics`, hiện thực trong `AggregationMetrics`, gọi từ
`Application`. Bản no-op trong `DefaultNoOpServices` tồn tại để một host **cấu hình chưa
đủ** vẫn chạy được. Giữ tên instrument dưới tiền tố `documentviewer.`.

---

## III.11. Các bất biến KHÔNG được phá

Mỗi điều dưới đây **đã từng bị phá ít nhất một lần** trong lịch sử repo này, và mỗi điều
giờ đều có một test canh giữ. §II.7 liệt kê test nào bắt điều nào.

1. **Không `IDocumentSource` nào được phép throw.** Thất bại là một
   `UpstreamResult.Failed`. Một `Task.WhenAll` trần trên các source có throw sẽ **ném đi
   công việc đã hoàn thành của các source còn lại**.
2. **Cache không bao giờ được throw vào bộ tổng hợp** — **kể cả khi bị cancel**. Đây
   chính là thất bại #1 đến bằng một con đường khác: một exception lọt ra từ lệnh đọc
   cache đã **ném đi tài liệu của một nguồn khỏe mạnh** và biến một `200` thành một `503`
   rỗng.
3. **Lệnh ghi cache và audit KHÔNG được dùng cancellation token của request.** Nó bị hủy
   khi response được gửi.
4. **Tuyệt đối không log VIN thô.** Dùng `Vin.Hash12` và `Vin.Masked`. `ToString()` **đã**
   là dạng che; rủi ro nằm ở các logger của thư viện thứ ba, đó là lý do enricher phải
   **đệ quy**.
5. **Phân giải cấu hình từ `IServiceProvider`, không phải lúc đăng ký.** Một lần đọc háo
   hức sẽ chụp lại `appsettings.json` và **phớt lờ biến môi trường**.
6. **Mỗi upstream có named client riêng và resilience pipeline riêng.** State dùng chung
   giữa các breaker cho phép một hệ thống lỗi làm suy giảm hệ thống kia.
7. **Tuyệt đối không retry một `4xx` không-tạm-thời.** Upstream **đang trả lời đúng**.
8. **Tuyệt đối không cache một thất bại.** Nó biến một cú hụt hơi một giây thành một sự cố
   dài bằng cả TTL.
9. **`TargetFramework` được khai báo đúng một lần**, trong `Directory.Build.props`. Đó
   chính là thứ làm cho việc chuyển sang .NET 10 LTS là **thay đổi một dòng**.
10. **Khẳng định về độ trễ và chạy test song song không đi cùng nhau được.** Các test
    collection của integration chạy **tuần tự** (`xunit.runner.json`); có năm test đo
    thời gian trôi qua.

---

## III.12. Xử lý sự cố

| Triệu chứng | Nguyên nhân có thể | Xem ở đâu |
|---|---|---|
| `/health/ready` trả `503` | Connection string đã được cấu hình nhưng database không với tới được, hoặc cú bắt tay chậm hơn `HealthChecks:DatabaseTimeoutMs` | dòng log của health check `postgres`; thử kết nối trực tiếp bằng `dotnet ef migrations list` |
| Mọi thứ chạy được, nhưng **không nguồn nào từng là `stale`** | Không cấu hình database, hoặc các lệnh ghi cache đang thất bại | grep log tìm `Cache upsert failed` / `Cache writes … exceeded` |
| Tài liệu trả về rỗng từ một nguồn vốn trả `200` | Tên trường trong payload không khớp contract, nên **mọi bản ghi đều bị bỏ qua** | test của normaliser; chính lỗi này từng làm **chín test xanh** trên các response rỗng |
| Một integration test chỉ fail khi chạy cùng các test khác | Một khẳng định về độ trễ gặp tranh chấp CPU | `xunit.runner.json`; thêm một request warm-up trước khi bấm đồng hồ |
| Một test **xanh dù có hay không có bản fix** | Khẳng định đang khớp với giá trị mặc định của thư viện, hoặc chế độ lỗi bị **từ chối ngay lập tức** thay vì treo | tự hỏi **"thay đổi gì ở production sẽ làm test này fail?"** — nếu không có câu trả lời, **viết lại test** |
| Các lời gọi upstream đi tới chỗ không ngờ tới | Cấu hình được đọc lúc đăng ký thay vì mỗi lần tạo client | `DependencyInjection.BaseAddress(sp, …)` |
| Console đầy output trace xen lẫn | Không set `OTEL_EXPORTER_OTLP_ENDPOINT`, nên exporter console dùng chung stdout với Serilog | `TelemetrySetup`; điểm thô đã biết, đã ghi là một minor được hoãn |

---

## III.13. Các quyết định được ghi ở đâu

| Tài liệu | Nội dung |
|---|---|
| [`system-design.md`](../system-design.md) §10 (= §I.10 ở trên) | 11 giả định — mọi chỗ nhập nhằng trong đề bài, và quyết định đã chọn |
| [`system-design.md`](../system-design.md) §9 (= §I.9 ở trên) | 6 phạm vi loại trừ — những gì cố ý không làm, và vì sao |
| [`ai-collaboration/review-log.md`](../ai-collaboration/review-log.md) | 22 entry: 7 ở giai đoạn thiết kế, 15 ở giai đoạn hiện thực — mọi defect tìm ra, đã sửa gì, vì sao nó quan trọng |
| [`ai-collaboration/implementation-ledger.md`](../ai-collaboration/implementation-ledger.md) | Mọi phán quyết đưa ra trong lúc chạy plan, kèm giá phải trả nếu nó sai |
| [`superpowers/plans/`](../superpowers/plans/) | Bản plan theo từng task mà phần hiện thực đã đi theo |

Nếu anh đang định đổi một thứ gì mà **không tìm thấy lý lẽ**, hãy mặc định rằng **đã từng
có một lý lẽ** và tìm trong review log **trước khi** gạt bỏ nó.

---
---

## Phụ lục — Thuật ngữ đối chiếu

| Tiếng Anh | Dùng trong tài liệu này | Ghi chú |
|---|---|---|
| upstream / system of record | hệ thống upstream / hệ thống nguồn | Sales System và Service System |
| fan-out | fan-out (gọi song song) | Giữ nguyên vì là tên kỹ thuật |
| partial result | kết quả một phần | Cờ `partial` trong response |
| stale | dữ liệu cũ (`stale`) | Giữ nguyên giá trị enum |
| degraded | suy giảm | Trạng thái vẫn phục vụ được nhưng không đầy đủ |
| circuit breaker | circuit breaker | Giữ nguyên |
| budget | ngân sách (thời gian) | Giới hạn thời gian cho một thao tác |
| correlation ID | correlation ID | Giữ nguyên |
| PII | dữ liệu cá nhân / PII | VIN được coi là PII |
| fire-and-forget | phóng rồi quên (fire-and-forget) | Ghi audit không chờ kết quả |
| seam | đường may | Điểm chèn abstraction để test/mở rộng |
| invariant | bất biến | Điều kiện không được phá |
| normaliser | normaliser / bộ chuẩn hóa | Chuyển schema upstream về mô hình hợp nhất |
