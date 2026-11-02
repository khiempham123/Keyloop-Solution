# Hướng dẫn đọc mã nguồn

**Bản tiếng Việt** · Keyloop Technical Assessment — Scenario D · Tác giả: Pham Gia Khiem

| | |
|---|---|
| **Bản gốc tiếng Anh** | [`code-guide.md`](../../docs/code-guide.md) — **đây là bản thẩm quyền** |
| **Mục lục bộ tiếng Việt** | [`tai-lieu-tieng-viet.md`](../tai-lieu-tieng-viet.md) |
| **Nội dung** | Định hướng — tour mã nguồn 10 phút |

> Nếu bản tiếng Việt và bản tiếng Anh khác nhau, **bản tiếng Anh là bản đúng**. Tên lớp,
> tên file, tên test, khóa cấu hình và khối code giữ nguyên tiếng Anh vì chúng là định
> danh trong mã nguồn.
>
> Các tham chiếu dạng **§I.x**, **§III.x**… trỏ sang phần tương ứng trong bộ tài liệu này:
> I = thiết kế, II = tour mã nguồn, III = tra cứu, IV = README, V = nhật ký review,
> VI = prompt, VII = phán quyết, VIII = plan. Mục lục ở link trên.

---

Một tour mã nguồn dành cho người vừa clone repo này về, trong khoảng mười phút. Nó giải
thích **vì sao từng mảnh tồn tại và điều gì sẽ vỡ nếu thiếu nó**.

Phần tra cứu đầy đủ — bề mặt công khai của từng file, luồng request truy qua các lời gọi
hàm thật, mọi ngân sách và giới hạn gom trong một bảng, cùng các "công thức" mở rộng
service — nằm ở **Phần III**. Lý lẽ thiết kế ở **Phần I**. Bản ghi những gì AI làm sai
nằm trong [`ai-collaboration/review-log.md`](../ai-collaboration/review-log.md).

Đọc theo đúng thứ tự dưới đây. Mỗi mục nói rõ **xem ở đâu** và **cần để ý gì**.

---

## II.0. Một ý tưởng duy nhất giải thích tất cả phần còn lại

Mọi thứ trong codebase này đều suy ra từ một quyết định duy nhất: **một thất bại của
upstream là một giá trị trả về, không phải một exception.**

`src/DocumentViewer.Domain/UpstreamResult.cs`

```csharp
public abstract record UpstreamResult<T>
{
    public sealed record Ok(T Value, TimeSpan Duration) : UpstreamResult<T>;
    public sealed record Failed(UpstreamFailure Failure, TimeSpan Duration) : UpstreamResult<T>;
}
```

Cách hiện thực hiển nhiên của "gọi hai hệ thống song song" là
`await Task.WhenAll(sales, service)`. Nó **ném lại exception của task đầu tiên** và
**ném đi kết quả đã hoàn thành của task còn lại**. Vì toàn bộ scenario này nói về việc
**vẫn hữu ích khi một hệ thống chết**, đúng một dòng đó sẽ phá hủy cả tính năng — và chỉ
phá khi một upstream thực sự chết, tức là **không bao giờ xảy ra trong lúc demo**.

Vì mỗi source trả về một `UpstreamResult` thay vì throw, `Task.WhenAll` **không bao giờ
có thể throw**, và bộ tổng hợp luôn nhận đúng một kết cục cho mỗi nguồn. Hai test giữ
điều này: một test nơi source trả về `Failed`, và một test nơi source **throw dù hợp
đồng không cho phép**.

---

## II.1. Domain — `src/DocumentViewer.Domain/`

Không phụ thuộc vào bất cứ thứ gì. Nếu một type ở đây cần database hay `HttpClient`, nó
đang nằm sai project.

| File | Cần để ý |
|---|---|
| `Vin.cs` | Là một **value object**, không phải `string`. `TryParse` trim và chuyển chữ hoa **trước khi** validate, bắt buộc 17 ký tự, loại bỏ `I`/`O`/`Q`, và kiểm tra check digit theo ISO 3779 ở vị trí 9 — bao gồm cả nhánh `X` khi số dư bằng 10. **`ToString()` trả về dạng đã che (masked)**, nên một lần lỡ viết `$"…{vin}…"` trong câu log **không thể làm lộ dữ liệu cá nhân**. `Hash12` và `Masked` là hai thứ log được phép mang. |
| `VehicleDocument.cs` | Mô hình hợp nhất mà cả hai hệ thống chuẩn hóa về. `IssuedAt` và `SizeBytes` là nullable vì **một upstream hợp lý là không có chúng**, và bỏ một tài liệu vì thiếu ngày sẽ tệ hơn là hiển thị nó không có ngày. |
| `SourceSystem.cs`, `DocumentType.cs` | `DocumentType.Unknown = 0` là **có chủ ý**: một loại lạ từ upstream map về `Unknown` và tài liệu **vẫn được trả về**. Ẩn một tài liệu chỉ vì phân loại của nó mới là một thất bại tệ hơn việc hiển thị nó với loại "chưa biết". |
| `UpstreamResult.cs`, `UpstreamFailure.cs` | Xem §II.0. `UpstreamFailureReason` chính là thứ làm cho trường `sources[].error.reason` trong response có ý nghĩa. |

**Bắt đầu từ đây:** `Vin.cs` và 13 test của nó là ví dụ nhỏ rõ ràng nhất cho thấy
codebase này được test theo cách nào.

---

## II.2. Application — `src/DocumentViewer.Application/`

Nghiệp vụ, không hề biết gì về HTTP, SQL hay retry.

### `Sources/IDocumentSource.cs`

```csharp
public interface IDocumentSource
{
    SourceSystem System { get; }
    Task<UpstreamResult<IReadOnlyList<VehicleDocument>>> FetchAsync(Vin vin, CancellationToken ct);
}
```

Đây là **đường may** làm cho "thêm hệ thống nguồn thứ ba" chỉ còn là việc đăng ký thêm
một client, và làm cho các luật xử lý lỗi một phần unit-test được mà không cần mạng. Để
ý kiểu trả về: **một hiện thực mà throw là đang vi phạm hợp đồng của nó.**

### `Aggregation/DocumentAggregationService.cs`

Trái tim của service. Đọc nó theo **bốn lượt**:

1. **Fan-out.** Mỗi source đã đăng ký được chiếu thành một task **đã chứa `try`/`catch`
   của chính nó**, rồi `Task.WhenAll`. Một source throw dù hợp đồng không cho phép sẽ bị
   bắt ở đây và chuyển thành `Failed(Unknown)` — "dây lưng cộng dây treo" cho đúng cái
   quyết định mà cả thiết kế đặt lên.

2. **Nhánh thất bại.** Với mỗi source lỗi, cache được tra. Có dữ liệu → thành `Stale`
   kèm tài liệu từ cache; không có → vẫn là `Failed`. Đây chính là thứ làm một hệ thống
   nguồn bệnh **suy giảm một cách duyên dáng** thay vì biến mất.

3. **Các ngân sách.** Hai con số đáng hiểu:
   - `CacheOperationBudget` (**1 giây**) giới hạn **đường đi của response** chờ cache bao
     lâu. Quá đó, phần đọc hoàn tất và việc xử lý cache tự chạy nốt. Từng có lúc một
     database **nhận kết nối TCP rồi không bao giờ trả lời** đã bắt giữ toàn bộ lệnh đọc
     tài liệu làm con tin.
   - `TryCacheUpsertAsync` truyền **`CancellationToken.None`**, không phải token của
     request. Nhìn thì **tưởng là sai**, mà lại là **dòng quan trọng nhất trong file**:
     token của request bị hủy ngay khoảnh khắc response được gửi, và một lệnh ghi liên
     vùng mất nhiều thời gian hơn phần đời còn lại của request. Truyền `ct` vào có nghĩa
     là **không có gì được cache cả**, nên nhánh stale ở trên **không bao giờ có thể kích
     hoạt** — trong khi **mọi test vẫn xanh**. Repository tự áp ngân sách ghi riêng của
     nó thay thế.

4. **Sắp xếp và cờ partial.** Tài liệu được sắp theo `IssuedAt` giảm dần, các giá trị
   null xuống cuối, phá thế bằng `Id` để kết quả **ổn định**. `Partial` là true khi **bất
   kỳ** báo cáo nguồn nào không phải `Ok` — bao gồm cả `Stale`, vì dữ liệu cũ là thứ
   client **xứng đáng được biết**.

### `Aggregation/AggregationResult.cs`, `SourceReport.cs`

`AllSourcesFailed` là thứ tầng API map sang `503`. `SourceReport` là lý do client **không
bao giờ phải đoán**: một dòng cho mỗi nguồn, kèm trạng thái, số lượng, thời gian và lỗi.

### `Persistence/`, `Observability/`

Chỉ có interface (`IDocumentCacheRepository`, `ISearchAuditRepository`,
`IAggregationMetrics`). Chúng nằm ở đây, **không** nằm trong Infrastructure, vì
`Application` **định nghĩa những gì nó cần** và Infrastructure **cung cấp**.

---

## II.3. Infrastructure — `src/DocumentViewer.Infrastructure/`

### `Sources/Sales/`, `Sources/Service/`

Mỗi upstream có một bộ contract (`*Contracts.cs`), một normaliser và một typed client.

Hai schema **bất đồng có chủ ý**, bởi chuẩn hóa chỉ có nghĩa nếu có gì đó để chuẩn hóa:

| Trường hợp nhất | Sales | Service |
|---|---|---|
| định danh | `documentId` — string `"PO-88213"` | `id` — số nguyên `88213` |
| loại | `docType` `"PURCHASE_ORDER"` | `category` `"job-card"` |
| ngày phát hành | `createdDate` `"2026-03-04"` (ISO date) | `timestamp` `1772582400000` (epoch **ms**) |
| kích thước | `fileSizeKb` — **kilobyte** | `bytes` |
| liên kết | `url` (phẳng) | `links.self` (lồng) |
| phong bì | `{ documents: [...] }` | `{ data: { items: [...] }, meta: {...} }` |

**Mỗi dòng là một bug đang chờ xảy ra, và mỗi dòng đều có test.** `id` được đặt tiền tố
khi đi ra (`sales:` / `service:`) vì hai hệ thống cấp định danh độc lập và không gian
khóa có thể trùng.

### `Sources/UpstreamClientExecutor.cs`

Nơi luật "không bao giờ throw" được **thực thi**. Mỗi loại exception map sang một lý do:
`TimeoutRejectedException` và `TaskCanceledException` → `Timeout`;
`BrokenCircuitException` → `CircuitOpen`; status không thành công → `HttpError` kèm mã;
`JsonException` → `InvalidPayload`. **Việc người gọi hủy request thì được ném lại
(rethrow)**, nhờ đó một client ngắt kết nối **phân biệt được** với một upstream lỗi — đó
không phải lỗi của upstream và không được tính vào tài khoản của nó.

### `DependencyInjection.cs`

Hai điều cần để ý:

- **Mỗi client đều được đặt tên** (`"SalesSystem"`, `"ServiceSystem"`). Một
  `AddHttpClient<IDocumentSource, T>()` **không tên** sẽ khóa cả hai registration dưới
  cùng khóa `IDocumentSource`, nên chúng **dùng chung một base address và chung MỘT
  circuit breaker** — âm thầm phá hủy chính sự cô lập theo upstream mà thiết kế đặt lên.
- **Cấu hình được đọc từ `IServiceProvider`, bên trong các delegate** — tuyệt đối không
  chụp lại lúc đăng ký. Một lần đọc "háo hức" sẽ chụp lại đúng những gì
  `appsettings.json` nói và **phớt lờ mọi nguồn cấu hình thêm vào sau đó**, nên một lần
  triển khai đặt địa chỉ upstream qua biến môi trường sẽ **bị bỏ qua trong im lặng**.

Resilience cho mỗi client: **2 giây** mỗi lần thử, **5 giây** tổng, **2 lần retry** với
backoff lũy thừa và jitter, chỉ với điều kiện tạm thời, và circuit breaker ở tỉ lệ lỗi
**0,5** trong cửa sổ **30 giây** với thời gian mở **15 giây**. Độ trễ retry được set
**tường minh 200 ms**: mức mặc định 2 giây cộng với timeout 2 giây mỗi lần thử **không
thể nhồi nổi lần retry thứ hai** vào trong ngân sách 5 giây.

### `Persistence/`

| File | Vì sao nó tồn tại |
|---|---|
| `DocumentViewerDbContext.cs`, `Entities/` | Hai bảng. `search_audit` ghi ai đã xem tài liệu của xe nào và họ có thấy câu trả lời đầy đủ hay không. `document_metadata_cache` khóa theo **`(vin, source_system)`** — không phải theo request — đó là thứ cho phép nguồn khỏe được phục vụ sống trong khi nguồn bệnh được phục vụ từ cache **trong cùng một response**. |
| `DocumentCacheRepository.cs` | Upsert dùng `ON CONFLICT (vin, source_system) DO UPDATE` bằng SQL thô thay vì change tracker của EF, nên các writer đồng thời **hội tụ** thay vì xung đột. **Hai ngân sách, không phải một:** **1500 ms** cho *đọc* vì đọc nằm trên đường đi của response, và **8 giây** cho *ghi* vì ghi xảy ra sau đó và phải sống nổi qua một kết nối liên vùng lạnh. Cả hai thao tác **tự nuốt lỗi của mình** và log ở mức `Warning` — cache là một tối ưu hóa, **không bao giờ là lý do để một lệnh đọc thất bại**. |
| `SearchAuditRepository.cs` | Fire-and-forget từ endpoint, bên trong `try`/`catch` riêng. Observability không bao giờ được là lý do request của người dùng thất bại. |
| `NpgsqlConnectionHardening.cs` | Áp `Minimum Pool Size=1`, `Connection Idle Lifetime=300`, `Timeout=15` vào connection string của người vận hành — **chỉ điền chỗ trống, không bao giờ ghi đè giá trị họ đã set**. Một kết nối nóng nghĩa là **không request nào của người dùng phải trả giá bắt tay TLS + tra cứu tenant của pooler + SASL**. Để ý chỗ tinh vi mà nó phải lách qua: `NpgsqlConnectionStringBuilder.ContainsKey` trả lời câu hỏi *"đây có phải một keyword tôi biết không"*, **không phải** *"người vận hành có set nó không"*, nên các khóa đã-được-cấp phải đọc từ một `DbConnectionStringBuilder` thuần. |
| `DatabaseWarmupService.cs` | Là một `BackgroundService`, **có chủ ý** — một thao tác warm-up chặn khởi động sẽ biến "database không với tới được" thành "service không boot nổi", trong khi service này **bắt buộc phải tiếp tục phục vụ tài liệu từ cache khi database chết**. Lỗi chỉ được log, không làm gì thêm. |

### `Observability/`

`AggregationMetrics` sở hữu sáu instrument. `CorrelationPropagationHandler` là một
`DelegatingHandler` gắn trên **cả hai** typed client, nhờ đó **một correlation ID trải
suốt** dòng log của API, cả hai lời gọi upstream, và dòng audit.

---

## II.4. API — `src/DocumentViewer.Api/`

### `Endpoints/VehicleDocumentsEndpoint.cs`

**Mỏng có chủ ý.** Nó parse VIN, gọi service tổng hợp, và map một kết cục sang một status
code:

| Kết cục | Status |
|---|---|
| `Vin.TryParse` thất bại | `400` + `ProblemDetails`, `type: …/invalid-vin` |
| `AllSourcesFailed` | `503` + `Retry-After: 30` |
| mọi trường hợp khác | `200`, với `partial` lấy từ các báo cáo nguồn |

Nó cũng thực thi ngân sách **6 giây** cho toàn request bằng một `CancellationTokenSource`
liên kết, và phóng lệnh ghi audit với `CancellationToken.None` để nó **không bị giết khi
response hoàn tất**.

Để ý thứ **không có** ở đây: không hề biết có bao nhiêu upstream, hay chúng lỗi kiểu gì.

### `Observability/`

| File | Cần để ý |
|---|---|
| `LoggingSetup.cs` | Serilog ra stdout dạng JSON. `VinRedactingLogEnricher` **đệ quy** qua `ScalarValue`, `StructureValue`, `SequenceValue` và `DictionaryValue` — bởi vì một logger retry của thư viện thứ ba đã lưu URL gửi đi dưới dạng **object `Uri`**, không phải string, và **làm lộ một VIN thô vào log trên nhánh suy giảm**. Có một test khẳng định không dòng log nào chứa VIN thô. |
| `TelemetrySetup.cs` | Trace và metric. Span cha cộng hai span upstream **ngang hàng** là bằng chứng rõ nhất rằng fan-out là thật. |
| `HealthChecks.cs` | Ba phân biệt đáng đọc kỹ. `/health/ready` **chỉ** kiểm tra database — nếu nó kiểm tra cả upstream, việc cả hai hệ thống nguồn chết sẽ rút mọi instance khỏi vòng phục vụ, và service sẽ ngừng phục vụ chính những tài liệu trong cache mà nó vẫn hoàn toàn đủ sức phục vụ. Một database **vắng mặt** do cấu hình là `Healthy` (chạy không persistence là được hỗ trợ); một database **đã cấu hình mà không với tới được** thì không. Và ngân sách readiness được thực thi bằng cách **đua** probe chứ không phải hủy nó, vì Npgsql không quan sát cancellation token đáng tin trong lúc đang mở kết nối vật lý. |
| `CorrelationIdMiddleware.cs` | Tôn trọng `traceparent` (W3C), rồi `X-Correlation-Id`, rồi mới tự sinh; dội lại trên response. |

### `Program.cs`

**Ba mươi dòng.** Nếu composition root phình quá một màn hình, nghĩa là có thứ gì đáng
thuộc về một module đã **rò rỉ** vào đó.

---

## II.5. Mock — `mocks/`

Hai minimal API độc lập, chạy bằng `docker compose`, **không phải** fake in-process. Đó
là cái giá có chủ ý: chỉ như vậy thì fan-out song song, timeout và circuit breaker mới
được thực thi qua socket thật, thay vì trên một stub **không thể chậm được**.

Mỗi mock expose `POST /admin/chaos` với `mode`: `normal`, `slow` (kèm `delayMs`), `fail`
(`503`), hoặc `garbage` (`200` với payload không đọc được). Đây là thứ làm cho sự suy
giảm **có thể trình diễn trực tiếp** thay vì chỉ được mô tả bằng văn xuôi — xem mục
*Seeing it degrade* trong README.

---

## II.6. Test — `tests/`

`DocumentViewer.UnitTests` nhanh và thuần: VIN, normaliser, map lỗi của client, luật tổng
hợp, hardening connection string. `DocumentViewer.IntegrationTests` chạy **ứng dụng
thật**.

Ba fixture đáng biết:

| Fixture | Nó mua được gì |
|---|---|
| `Fixtures/WireMockUpstreamFixture.cs` | Upstream dàn dựng được theo từng test — chậm, hỏng, hoặc trả lời bằng rác — **và nó đếm số request**, đó là cách duy nhất để chứng minh một `404` **đã không bị retry**. |
| `Persistence/PostgresContainerFixture.cs` | Một PostgreSQL thật cho mỗi lần chạy, qua Testcontainers. Không phụ thuộc mạng tới Supabase, không state dùng chung, và không có nguy cơ một test lỗi làm hỏng dữ liệu demo. |
| Black-hole listener trong `DatabaseWarmupTests` | Một `TcpListener` **nhận kết nối rồi không bao giờ trả lời**. Một cổng đóng thì bị từ chối ngay lập tức và **không chứng minh được gì** về timeout; cái này mới là hình ảnh của một pooler có quản lý đang lạnh, nhìn từ phía client. |

Và một quyết định cấu hình: `xunit.runner.json` đặt
`parallelizeTestCollections: false`. Có **năm test khẳng định về thời gian trôi qua**, và
sự tranh chấp CPU từ các test class chạy song song đã đẩy một upstream **cố ý khỏe mạnh**
vượt quá timeout 2 giây mỗi lần thử — **sản phẩm thì đúng, cái sai là bộ khung chạy
test**. Khẳng định về độ trễ và chạy song song **không đi cùng nhau được**.

---

## II.7. Nếu anh đổi một thứ, đây là test sẽ nói cho anh biết

| Thay đổi | Test phải fail |
|---|---|
| Làm fan-out thành tuần tự | `Parallel_fan_out_takes_roughly_one_upstream_latency_not_two` |
| Dùng `Task.WhenAll` trần trên các source | `A_throwing_source_does_not_lose_the_sibling_source_result` |
| Retry một `4xx` | `Client_error_responses_are_never_retried` |
| Dùng chung một resilience pipeline cho cả hai client | `Open_circuit_on_one_source_does_not_affect_the_other_source` |
| Truyền token của request vào lệnh ghi cache | `Cache_writes_outlive_the_request_and_never_hold_the_response` |
| Log VIN thô ở bất cứ đâu | `No_log_line_contains_the_raw_vin` |
| Thêm upstream vào `/health/ready` | `Health_ready_returns_200_when_both_upstreams_are_down` |
| `await` cache trên đường đi của response | `A_database_that_accepts_but_never_answers_does_not_hold_up_a_document_read` |
| Hardcode ngân sách readiness | `Readiness_budget_comes_from_configuration_not_a_hardcoded_two_seconds` |
| Đọc body upstream không giới hạn kích thước | `Reports_a_body_that_never_stops_arriving_as_an_invalid_payload` |
| Đọc body upstream không giới hạn thời gian | `Reports_a_body_that_trickles_past_its_budget_as_a_timeout` |
| Nhân kilobyte với 1024 không kiểm tra tràn | `Reports_a_size_it_cannot_represent_as_unknown_rather_than_a_negative_number` |
| Cache một lần fetch thất bại | `Failed_source_results_are_never_written_to_cache` |

Nếu anh tạo được một thay đổi mà **không test nào trong số này phát hiện ra**, đó là một
khoảng trống của bộ test và đáng được báo lại.
