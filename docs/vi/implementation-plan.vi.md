# Implementation plan

**Bản tiếng Việt** · Keyloop Technical Assessment — Scenario D · Tác giả: Pham Gia Khiem

| | |
|---|---|
| **Bản gốc tiếng Anh** | [`2026-10-29-scenario-d-document-viewer.md`](../../docs/superpowers/plans/2026-10-29-scenario-d-document-viewer.md) — **đây là bản thẩm quyền** |
| **Mục lục bộ tiếng Việt** | [`tai-lieu-tieng-viet.md`](../tai-lieu-tieng-viet.md) |
| **Nội dung** | Plan — 9 task TDD, kèm 9 chỗ plan khác với code cuối |

> Nếu bản tiếng Việt và bản tiếng Anh khác nhau, **bản tiếng Anh là bản đúng**. Tên lớp,
> tên file, tên test, khóa cấu hình và khối code giữ nguyên tiếng Anh vì chúng là định
> danh trong mã nguồn.
>
> Các tham chiếu dạng **§I.x**, **§III.x**… trỏ sang phần tương ứng trong bộ tài liệu này:
> I = thiết kế, II = tour mã nguồn, III = tra cứu, IV = README, V = nhật ký review,
> VI = prompt, VII = phán quyết, VIII = plan. Mục lục ở link trên.

---

Bản plan theo từng task mà phần hiện thực đã đi theo. Đây là **một tài liệu lịch sử**: nó
được viết **trước khi** code tồn tại, nên có vài chỗ nó **khác với sản phẩm cuối cùng** —
mỗi chỗ như vậy em đánh dấu bằng một ghi chú. Chính những chỗ lệch đó là bằng chứng rằng
plan được **thực thi và kiểm chứng**, không phải được viết lại sau cho khớp.

> **Ghi chú của người dịch:** các khối code, tên test và lệnh shell **giữ nguyên tiếng
> Anh** — chúng là định danh và đầu ra máy. Chỉ phần văn xuôi được dịch.

**Mục tiêu.** Một backend .NET mà, cho một VIN, fan-out **song song** tới hai hệ thống
nguồn đã mock và trả về **một danh sách tài liệu hợp nhất, có ghi nguồn**, vẫn hữu ích khi
một trong hai upstream chết.

**Kiến trúc.** Solution bốn project phân tầng (`Api` / `Application` / `Infrastructure` /
`Domain`) cộng hai mock upstream service độc lập. **Thất bại được mô hình hóa thành một
giá trị trả về** (`UpstreamResult<T>`), **không bao giờ là exception**, nên `Task.WhenAll`
**không bao giờ có thể ném đi công việc đã hoàn thành của một source song song**. Một cache
Postgres theo từng `(vin, source)` cho phép một nguồn khỏe được phục vụ sống trong khi một
nguồn bệnh được phục vụ dữ liệu cũ **trong cùng một response**.

**Tech stack.** .NET 9 (`net9.0`, SDK 9.0.320), ASP.NET Core Minimal APIs,
`Microsoft.Extensions.Http.Resilience` (Polly v8), EF Core + Npgsql trên Supabase
PostgreSQL, Serilog, OpenTelemetry, xUnit + Shouldly + NSubstitute, WireMock.Net,
Testcontainers for PostgreSQL.

**Spec.** [`docs/system-design.md`](../system-design.md) (= Phần I)

**Deadline.** Thứ Tư 2026-11-04. **Task 1–5 là sản phẩm tối thiểu nộp được**; Task 6–9 mỗi
cái có giá trị độc lập. Nếu hết thời gian, **cắt từ dưới lên** trong §Non-Goals của spec,
**tuyệt đối không cắt Task 1–5, 8 hay 9**.

---

## VIII.1. Ràng buộc toàn cục

Những ràng buộc này **ngầm áp dụng cho yêu cầu của MỌI task**.

- Target framework `net9.0` (SDK 9.0.320, cài tại `/Volumes/240GB/dotnet`; chạy
  `source ./env.sh` trước, trong **mọi** shell). Nullable reference type và
  `TreatWarningsAsErrors` được bật trong `Directory.Build.props`.
- `TargetFramework` được set **MỘT LẦN** trong `Directory.Build.props` và được mọi project
  kế thừa — **không bao giờ lặp lại trong một `.csproj`**. Đây chính là thứ làm cho lộ
  trình chuyển sang .NET 10 LTS đã ghi trong tài liệu trở thành **thay đổi một dòng**.
- **KHÔNG dùng FluentAssertions** (licence v8+ không miễn phí cho mục đích thương mại).
  Dùng Shouldly.
- **KHÔNG có VIN thô trong bất kỳ câu log hay tag metric nào.** Chỉ log `VinHash` (12 hex
  đầu của SHA-256) và `VinMasked` (6 ký tự cuối). VIN đầy đủ **chỉ** xuất hiện trong bảng
  `search_audit`.
- Mọi method `async` **đều nhận một `CancellationToken`** và truyền nó xuống; **không
  `.Result`, không `.Wait()`, không `async void`**.
- Mọi mốc thời gian là `DateTimeOffset` ở UTC. **Inject `TimeProvider`**; không bao giờ gọi
  `DateTime.Now` hay `DateTimeOffset.UtcNow` trực tiếp trong code test được.
- Ngân sách timeout, **giá trị chính xác**: mỗi lần thử mỗi upstream **2 s**; tổng mỗi
  upstream gồm cả retry **5 s**; toàn request **6 s**.
- Retry: **2** lần, backoff lũy thừa có jitter, **chỉ** lỗi tạm thời (`5xx`, `408`, `429`,
  timeout, socket). **Không bao giờ retry các `4xx` khác.**
- Circuit breaker: **mỗi upstream một cái**, tỉ lệ lỗi **0,5**, cửa sổ lấy mẫu **30 s**,
  thời gian mở **15 s**.
- Cache TTL **15 phút**. **Thất bại không bao giờ được cache.**
- `id` của tài liệu được đặt tiền tố: `"sales:{externalId}"` / `"service:{externalId}"`.
- Supabase: kết nối runtime dùng pooler chế độ transaction (cổng **6543**); migration của
  EF Core dùng kết nối trực tiếp (cổng **5432**). Connection string đến từ cấu hình / biến
  môi trường và **không bao giờ được commit**.
- Conventional Commits cho **mọi** commit message.

---

## VIII.2. Review Focus — năm lớp đầu vào mà không happy path nào chạm tới

Spec hàm ý năm lớp đầu vào này, nhưng **không đường đi thuận lợi của task nào thực thi
chúng**. Mỗi cái được gán test vào đúng task sở hữu đoạn code đó.

1. **VIN được dán kèm khoảng trắng hai bên hoặc viết chữ thường** — người dùng copy từ
   email và bảng tính. `" 1hgcm82633a004352 "` **phải được trim, chuyển hoa và chấp nhận**,
   không bị từ chối bằng một `400`. *(Task 1)*
2. **Postgres không với tới được** — cache và audit **không bao giờ được nằm trên đường
   tới hạn** của lệnh đọc chính. Với database chết, một request mà **cả hai upstream đều
   khỏe** vẫn phải trả `200` với **đủ tài liệu**. *(Task 6)*
3. **Upstream trả `200` với payload lỗi định dạng hoặc không đầy đủ** — thiếu tiêu đề,
   ngày null, kích thước không phải số. Nguồn **phải** được báo là `failed` với lý do
   `InvalidPayload`, **hoặc** tài liệu xấu bị bỏ qua, nhưng **tuyệt đối không bao giờ là
   một `500` từ service này**. *(Task 3)*
4. **Hai request đồng thời cho cùng một VIN** — cả hai đều cache miss và cả hai đều cố
   upsert cùng khóa chính `(vin, source)`. **Lệnh ghi thứ hai không được lộ ra thành một
   `500`.** *(Task 6)*
5. **Client ngắt kết nối giữa request** — việc hủy **không được** log ở mức `Error`,
   **không được** phóng một lệnh ghi audit, và **không được** tính là một thất bại upstream
   trong metric. *(Task 7)*

---

## VIII.3. Task 1 — Bộ khung solution và các primitive của Domain

**File:**
- Tạo: `KeyloopDocumentViewer.sln`, `Directory.Build.props`, `.gitignore`, `.editorconfig`
- Tạo: `src/DocumentViewer.Domain/Vin.cs`, `VinError.cs`, `SourceSystem.cs`,
  `DocumentType.cs`, `VehicleDocument.cs`, `UpstreamResult.cs`, `UpstreamFailure.cs`
- Test: `tests/DocumentViewer.UnitTests/Domain/VinTests.cs`

**Interface — Tiêu thụ:** không gì cả. **Sinh ra:**

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

**Bước 1.** Tạo solution, bốn project `src`, hai project test, `Directory.Build.props`
(net9.0, nullable, cảnh báo thành lỗi), `.gitignore` (template dotnet) và `.editorconfig`;
nối project reference `Api → Application → Domain`, `Infrastructure → Application`,
`Api → Infrastructure`. **Kiểm chứng bằng `dotnet build` TRƯỚC KHI viết bất kỳ test nào.**

**Bước 2.** Viết các test VIN đỏ (xem khối code ở bản gốc / §V.I-2 về case thứ 13).

**Bước 3.** Chạy để xác nhận chúng **fail**.
Chạy: `dotnet test tests/DocumentViewer.UnitTests --filter FullyQualifiedName~VinTests`
Mong đợi: **FAIL** — `Vin` chưa tồn tại.

**Bước 4.** Hiện thực `Vin.TryParse` và các type Domain còn lại. `TryParse` **trim và
chuyển chữ hoa trước khi** validate. Thuật toán check digit (ISO 3779), vì các test không
xác định nó: chuyển mỗi ký tự thành số (`0-9` → giá trị bề mặt; `A..Z` →
`1,2,3,4,5,6,7,8,_,1,2,3,4,5,_,7,_,9,2,3,4,5,6,7,8,9` với `I`,`O`,`Q` là **không hợp lệ**),
nhân với trọng số theo vị trí `8,7,6,5,4,3,2,10,0,9,8,7,6,5,4,3,2`, cộng lại, lấy
`mod 11`; chữ số mong đợi **là chính số dư đó, hoặc `X` khi nó bằng 10**. So với vị trí 9.

**Bước 5.** Chạy để xác nhận **pass**. Mong đợi: **PASS, 13 test**.

**Bước 6.** Commit: `feat(domain): add Vin value object, document model and UpstreamResult`

---

## VIII.4. Task 2 — Mock upstream với schema khác nhau

**File:** tạo `mocks/MockSalesApi/{Program,SeedData}.cs`,
`mocks/MockServiceApi/{Program,SeedData}.cs`, `docker-compose.yml` (**chỉ hai mock** — DB
là Supabase).

**Sinh ra** hai hợp đồng HTTP cho Task 3 tiêu thụ. Hình dạng payload **BẮT BUỘC** phải
khác nhau **đúng y như** bảng §I.5.3 của spec.

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

Điều khiển chaos, trên cả hai: `POST /admin/chaos` với
`{ "mode": "normal" | "slow" | "fail" | "garbage", "delayMs": 8000 }`, và
`GET /admin/chaos` để đọc trạng thái hiện tại. `slow` delay mọi response; `fail` trả `503`;
`garbage` trả `200` với payload **thiếu trường bắt buộc** (đây là thứ Review Focus #3 được
test đối chiếu với).

**Bước 1–2.** Hiện thực hai mock dưới dạng minimal API, đọc từ seed trong bộ nhớ khóa theo
VIN, cộng hai endpoint chaos và một `/health`. Seed **ít nhất 4 tài liệu** cho VIN
`1HGCM82633A004352` và **kết quả rỗng** cho `WAUZZZ8V7JA003421`. MockServiceApi seed **3
tài liệu** cho cùng VIN đó, **bao gồm một cái có `category` là giá trị mà phần map của Task
3 sẽ KHÔNG nhận ra**.

> **Chỗ plan sai so với thực tế:** VIN `WAUZZZ8V7JA003421` dùng cho kết quả rỗng **có check
> digit sai** và sẽ bị chặn ở `400 InvalidCheckDigit` thay vì test được `200` +
> `documents: []`. Đã thay bằng `WAUZZZ8V1JA003421`. Xem §V.I-2.

**Bước 3.** Thêm `docker-compose.yml` expose MockSalesApi ở `:5101` và MockServiceApi ở
`:5102`.

**Bước 4–5.** Kiểm chứng **bằng tay** cả hai mock, và kiểm chứng chaos hoạt động (set
`fail` → mong đợi `503`, rồi reset về `normal`).

**Bước 6.** Commit:
`feat(mocks): add Sales and Service mock APIs with divergent schemas and chaos injection`

---

## VIII.5. Task 3 — Normalizer và typed HTTP client có resilience

**File:** tạo `IDocumentSource.cs` trong Application; `Sources/Sales/*` và
`Sources/Service/*` (contract, normalizer, client) cùng `DependencyInjection.cs` trong
Infrastructure; hai file test normalizer.

**Sinh ra:**

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

> **Chỗ plan sai so với thực tế:** tham số `IConfiguration config` **đã bị bỏ**. Đọc cấu
> hình **háo hức lúc đăng ký** chính là defect **I-11** — nó chụp lại `appsettings.json` và
> phớt lờ mọi nguồn cấu hình thêm vào sau. Signature cuối là `AddDocumentSources(this
> IServiceCollection services)`, phân giải cấu hình từ `IServiceProvider` cho mỗi client.

**Bước 1.** Viết các test normalizer **đỏ** — 8 test cho Sales, 7 cho Service:

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

**Bước 2.** Chạy để xác nhận **fail** — normalizer chưa tồn tại.

**Bước 3.** Hiện thực các record contract và cả hai normalizer. Kilobyte → byte dùng
`kb * 1024`. Chuỗi type/category không nhận ra map về `DocumentType.Unknown` và **tài liệu
vẫn được trả về**; một tài liệu **thiếu tiêu đề thì bị bỏ qua**. Deserialise dùng
`System.Text.Json` với `PropertyNameCaseInsensitive = true` và **không exception nào được
lọt ra khỏi `Normalize`**.

> **Chỗ plan thiếu so với thực tế:** `kb * 1024` **không kiểm tra tràn** chính là defect
> **I-15**: một giá trị trên `long.MaxValue / 1024` quấn vòng và trả cho client một số byte
> **âm**. Code cuối báo kích thước **không biết** và vẫn trả tài liệu. Bản gốc ghi "PASS, 15
> test"; con số cuối là **18** sau khi thêm các test của I-15.

**Bước 5.** Hiện thực `SalesSystemClient` và `ServiceSystemClient` dưới dạng typed client
implement `IDocumentSource`. Mỗi cái bọc **toàn bộ** thao tác của nó trong `try/catch` và
trả về `UpstreamResult<T>.Failed` — **không bao giờ throw**. Map
`TimeoutRejectedException`/`TaskCanceledException` (khi `ct` **chưa** bị hủy) → `Timeout`;
`BrokenCircuitException` → `CircuitOpen`; status không thành công → `HttpError` kèm mã;
`JsonException` → `InvalidPayload`; **người gọi hủy → ném lại `OperationCanceledException`**
để endpoint phân biệt được một client ngắt kết nối. Ghi thời gian trôi qua bằng
`Stopwatch.GetTimestamp`.

**Bước 6.** Đăng ký cả hai client trong `AddDocumentSources` với
`AddStandardResilienceHandler`, dùng **đúng** các giá trị ngân sách ở §VIII.1 và base
address từ khóa cấu hình `Upstreams:SalesSystem:BaseAddress` và
`Upstreams:ServiceSystem:BaseAddress`.

**Bước 7.** Chạy toàn bộ unit suite và commit:
`feat(infrastructure): add resilient typed clients and per-source normalizers`

---

## VIII.6. Task 4 — Service tổng hợp song song

**File:** tạo `Aggregation/{IDocumentAggregationService,AggregationResult,SourceReport,DocumentAggregationService}.cs`
và file test tương ứng.

**Sinh ra:**

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

Constructor: `DocumentAggregationService(IEnumerable<IDocumentSource> sources,
IDocumentCacheRepository cache, IAggregationMetrics metrics, TimeProvider clock,
ILogger<DocumentAggregationService> logger)`. `IDocumentCacheRepository` và
`IAggregationMetrics` được định nghĩa ở Task 6 và 7; **tới khi đó**, stub chúng bằng hiện
thực no-op trong project test để task này **vẫn test được độc lập**.

**Bước 1.** Viết các test tổng hợp **đỏ**. Hai test quan trọng nhất:

```csharp
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
```

Cùng với: merge tài liệu từ mọi nguồn; sắp theo `issuedAt` giảm dần với null xuống cuối;
báo `ok` cho mỗi nguồn thành công và `partial` là false; một nguồn lỗi **vẫn trả tài liệu
của nguồn kia**; mọi nguồn lỗi thì set `AllSourcesFailed`; nguồn lỗi **có** cache được báo
`Stale` **và tài liệu cache được đưa vào**; kết quả của nguồn thành công **được ghi** vào
cache; kết quả của nguồn lỗi **không bao giờ được ghi** vào cache; mọi nguồn rỗng cho ra
danh sách rỗng và `partial` false.

**Bước 3.** Hiện thực `GetDocumentsAsync`. Chiếu mỗi source thành một task **đã chứa
`try/catch` của chính nó** để `Task.WhenAll` **không thể throw** — một source throw dù hợp
đồng không cho phép thì bị bắt ở đây và chuyển thành `Failed(Unknown)`. Với mỗi nguồn
`Failed`, tra cache: hit → `Stale` kèm tài liệu cache, miss → vẫn `Failed`. `Partial` là
true khi **bất kỳ** báo cáo nào không phải `Ok`. Sắp theo `IssuedAt` giảm dần với null
xuống cuối, rồi theo `Id` để **ổn định**.

> **Chỗ plan thiếu so với thực tế:** bản gốc ghi "PASS, 11 test"; con số cuối là **14**.
> Ba test được thêm sau là những test mà plan **không thể lường tới** vì chúng đến từ việc
> chạy đối chiếu hạ tầng thật: lệnh ghi cache sống lâu hơn request (**I-8**), một lần tra
> cache vượt ngân sách được ghi là `timeout` chứ không phải `hit` (review cuối **I5**), và
> **một database bị treo không thể ném đi tài liệu của một nguồn khỏe mạnh** (**I-14**,
> Critical).

**Bước 5.** Commit:
`feat(application): add parallel document aggregation with partial-result semantics`

---

## VIII.7. Task 5 — Endpoint HTTP, map status và OpenAPI

**File:** tạo `Program.cs`, `Endpoints/VehicleDocumentsEndpoint.cs`,
`Contracts/DocumentsResponse.cs`, `appsettings.json`, `appsettings.Development.json`,
`requests.http`, và file test endpoint.

**Sinh ra:** `GET /api/v1/vehicles/{vin}/documents`, cùng các response DTO khớp **nguyên
văn** spec §I.5.2 (`vin`, `retrievedAt`, `partial`, `documents[]`, `sources[]`). JSON dùng
camelCase; enum serialise thành string.

**Bước 1.** Viết các test endpoint **đỏ** dùng `WebApplicationFactory` với
`IDocumentAggregationService` được thay thế:

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

**Bước 3.** Hiện thực `Program.cs` và endpoint. Ghép: `AddProblemDetails`,
`AddDocumentSources`, sinh OpenAPI, các option JSON. Endpoint parse VIN qua `Vin.TryParse`
và trả `TypedResults.Problem` với `type` là
`https://keyloop.example/problems/invalid-vin` nếu thất bại; ngược lại gọi service tổng
hợp và map `AllSourcesFailed` → `503` kèm `Retry-After: 30`, còn lại → `200`. **Thực thi
ngân sách 6 giây toàn request bằng một `CancellationTokenSource` liên kết.**

**Bước 4.** Chạy để xác nhận pass (**9 test**), rồi xác nhận end-to-end đối chiếu các mock
đang chạy.

> **Chỗ plan sai so với thực tế:** plan ghi `curl -s localhost:5000/...`. Cổng cuối cùng
> là **5080** — chỗ lệch này chính là finding **I4** của review cuối (`requests.http` trỏ
> 5000 trong khi README chạy 5080) và đã được đồng bộ về 5080.

**Bước 5.** Sinh và commit `openapi.json`, và viết `requests.http` phủ đường đi thuận lợi,
lỗi một phần, cả hai chết, VIN không hợp lệ và kết quả rỗng.

> **Chỗ plan thiếu so với thực tế:** `openapi.json` được sinh ra **đánh dấu `required` cho
> các thành viên bị-bỏ-khi-null**, nên một client được generate sẽ **từ chối chính đường đi
> thuận lợi** — finding **I2** của review cuối. Đã sửa bằng
> `OptionalPropertySchemaTransformer` cộng `OpenApiContractTests`.

**Bước 6.** Commit — **đây là mốc nộp được đầu tiên**:
`feat(api): expose unified vehicle documents endpoint with partial-result status mapping`

---

## VIII.8. Task 6 — Persistence: audit và cache trên Supabase

**File:** tạo `Persistence/DocumentViewerDbContext.cs`, `Entities/SearchAuditEntity.cs`,
`Entities/DocumentCacheEntity.cs`, `DocumentCacheRepository.cs`,
`SearchAuditRepository.cs`, `Persistence/Migrations/` (sinh tự động); sửa
`DependencyInjection.cs` và `DocumentAggregationService.cs`; ba file test.

**Sinh ra:**

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

Schema **đúng y như** spec §I.8.4, bao gồm index `(vin, searched_at desc)` trên
`search_audit` và `(expires_at)` trên `document_metadata_cache`.

**Bước 1.** Viết các test persistence **đỏ** đối chiếu một instance PostgreSQL của
Testcontainers. Hai test đáng chú ý:

```csharp
[Fact]
public async Task Concurrent_upserts_for_same_key_do_not_throw()   // Review Focus #4
{
    var tasks = Enumerable.Range(0, 8).Select(_ =>
        Repository.UpsertAsync(TestVin, SourceSystem.SalesSystem, Docs(2), TimeSpan.FromMinutes(15), default));
    await Should.NotThrowAsync(() => Task.WhenAll(tasks));
}

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

Cùng với: upsert rồi get round-trip được; get trả null cho entry **đã hết hạn**; get trả
null cho VIN lạ; upsert **ghi đè** entry cũ cho cùng vin và source; audit append **lưu VIN
đầy đủ** và source report; các dòng audit **truy vấn được theo vin, mới nhất trước**.

**Bước 3.** Hiện thực `DbContext`, hai entity với map table/column **snake_case**, và hai
repository. `UpsertAsync` dùng `ON CONFLICT (vin, source_system) DO UPDATE` qua **SQL thô**
thay vì change tracker của EF, nên các writer đồng thời **hội tụ thay vì xung đột**. Tài
liệu được lưu dạng `jsonb`. **Cả hai repository tự nuốt và log lỗi của mình ở mức
`Warning`** rồi trả `null` / hoàn tất trong im lặng — **không cái nào được phép đẩy một
exception vào đường đọc**.

**Bước 4.** Sinh migration đầu tiên và áp lên Supabase bằng **kết nối trực tiếp**
(cổng 5432).

**Bước 5.** Nối lệnh ghi audit vào endpoint dưới dạng **fire-and-forget** sau một
`try/catch`, và chuyển connection string runtime sang **pooler (6543)**.

> **Chỗ plan thiếu so với thực tế — đây là chỗ thiếu NẶNG NHẤT của cả plan.** Plan không
> lường được rằng **một pooler có quản lý liên vùng hành xử khác hẳn một container cục bộ**.
> Ba thứ phải thêm sau: tách lệnh ghi khỏi token của request (`CancellationToken.None`),
> tách một ngân sách thành **đọc 1500 ms / ghi 8 giây**, và giữ **một kết nối ấm**
> (`NpgsqlConnectionHardening` + `DatabaseWarmupService`). Xem §V.I-8 — defect quan trọng
> nhất của cả dự án, và **không test nào trong plan có thể thấy nó**.

**Bước 6–7.** Chạy để xác nhận pass (**8 test**) rồi commit:
`feat(persistence): add search audit and per-source document cache on Supabase`

---

## VIII.9. Task 7 — Observability

**File:** tạo `Middleware/CorrelationIdMiddleware.cs`,
`Observability/{LoggingSetup,TelemetrySetup,HealthChecks}.cs` trong Api;
`Observability/{AggregationMetrics,CorrelationPropagationHandler}.cs` trong
Infrastructure; `Observability/IAggregationMetrics.cs` trong Application; sửa `Program.cs`;
file test.

**Sinh ra:**

```csharp
public interface IAggregationMetrics
{
    void RecordAggregation(string outcome, TimeSpan duration);
    void RecordUpstream(SourceSystem system, SourceStatus status, TimeSpan duration);
    void RecordPartial();
    void RecordCacheLookup(string result);   // "hit" | "miss" | "error"
}
```

Tên meter `DocumentViewer`; tên instrument **đúng y như** spec §I.7.2. Tên `ActivitySource`
là `DocumentViewer`.

> **Chỗ plan thiếu so với thực tế:** `RecordCacheLookup` cuối cùng nhận **bốn** giá trị —
> `"hit" | "miss" | "error" | "timeout"`. Giá trị `timeout` được thêm vào do finding **I5**
> của review cuối: một lần tra cache **đã bị bỏ dở** vẫn ghi nhận một `hit` **sau khi**
> response đã đi ra mà không có nó.

**Bước 1.** Viết các test observability **đỏ**:

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

Test về span chồng lấn khẳng định rằng hai activity upstream **chia sẻ cùng span cha của
request** và rằng
`salesSpan.StartTime < serviceSpan.StartTime + serviceSpan.Duration && serviceSpan.StartTime < salesSpan.StartTime + salesSpan.Duration`
— nghĩa là **khoảng thời gian của chúng giao nhau**.

**Bước 3.** Hiện thực Serilog JSON ra stdout với enricher cho `CorrelationId`, `TraceId`,
`VinHash`; `CorrelationIdMiddleware` tôn trọng `traceparent` rồi `X-Correlation-Id` rồi mới
tự sinh; và `CorrelationPropagationHandler` gắn vào **cả hai** typed client.

> **Chỗ plan thiếu so với thực tế:** "enricher làm sạch property dạng string" là **không
> đủ**. Logger retry của Polly lưu URL dưới dạng **object `Uri`**, nên VIN thô **vẫn lọt
> vào log** trên nhánh suy giảm. Enricher cuối cùng phải **đệ quy** qua `ScalarValue` /
> `StructureValue` / `SequenceValue` / `DictionaryValue`. Xem §V.I-6.

**Bước 4.** Hiện thực `TelemetrySetup`: OpenTelemetry tracing với instrumentation cho
ASP.NET Core, `HttpClient` và Npgsql, **một span con cho mỗi lời gọi upstream**, và metric
cho **mọi** instrument ở spec §I.7.2. Exporter OTLP khi `OTEL_EXPORTER_OTLP_ENDPOINT` được
set, console exporter nếu không.

**Bước 5.** Hiện thực `HealthChecks`: `/health/live` **chỉ chính nó**; `/health/ready`
**chỉ database**; `/health/upstream` tham khảo, **luôn `200`** với body theo từng nguồn.

> **Chỗ plan thiếu so với thực tế:** test `Health_ready_returns_503_when_the_database_is_unreachable`
> **không đủ chặt**. Ngân sách readiness bị **hardcode 2 giây** và bị Npgsql phớt lờ
> (**I-8**), còn test trỏ vào một **cổng đóng** nên nó **xanh cả trước và sau bản fix**
> (**I-9**). Code cuối thực thi ngân sách bằng cách **đua** probe, cấu hình được qua
> `HealthChecks:DatabaseTimeoutMs`, và plan **không hề đề cập** phân biệt "database vắng mặt
> → `Healthy`" (giả định #11).

**Bước 7.** Chụp ảnh waterfall của trace cho thấy hai span upstream song song vào
`docs/images/trace-parallel-fanout.png` và tham chiếu nó từ spec §I.7.3.

**Bước 8.** Commit:
`feat(observability): add correlation ids, structured logging, OpenTelemetry and health checks`

---

## VIII.10. Task 8 — Integration test về resilience với upstream được dàn dựng

**File:** tạo `Resilience/UpstreamResilienceTests.cs`, `Fixtures/WireMockUpstreamFixture.cs`.

**Tiêu thụ:** **toàn bộ ứng dụng đã ghép** (Task 5–7) với base address của cả hai upstream
trỏ vào các server WireMock.Net. **Sinh ra:** không gì được tiêu thụ xuôi dòng.

**Bước 1.** Viết các test resilience **đỏ**:

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

**Bước 3.** Hiện thực `WireMockUpstreamFixture` (hai server, các helper để dàn dựng delay,
status, payload, và **để đếm số request nhận được**) và sửa bất cứ thứ gì mà các test làm
lộ ra.

> **Dòng quan trọng nhất của cả plan:** *"Thất bại ở đây được KỲ VỌNG là bug thật trong
> Task 3–6, không phải bug của test — hãy đối xử với từng cái như vậy."* Đúng y như vậy:
> **cả mười test fail giống nhau** và nguyên nhân là **một defect production thật**
> (**I-11**, cấu hình đọc háo hức), không phải một bất tiện của test.

**Bước 4–5.** Chạy toàn bộ suite (mong đợi **PASS, mọi project**) rồi commit:
`test(resilience): prove parallel fan-out, timeout budget, retry policy and breaker isolation`

---

## VIII.11. Task 9 — Các artefact của bài nộp

**File:** tạo `README.md`, `docs/ai-collaboration/prompts.md`,
`docs/ai-collaboration/review-log.md`; sửa `docs/system-design.md` (v2 — **đối chiếu với
những gì thực sự đã được xây**).

**Bước 1.** Viết `README.md`: yêu cầu cài trước, quickstart `docker compose up` +
`dotnet run`, cấu hình connection string Supabase qua biến môi trường, `dotnet test`, các
ví dụ cURL từ `requests.http`, một lượt đi qua hình dạng response, và một bản tóm tắt kiến
trúc link tới tài liệu thiết kế.

**Bước 2.** Viết mục **Tường thuật cộng tác với AI** trong README. Cấu trúc nó thành:
**chiến lược**, rồi **quy trình kiểm chứng**, rồi **một bảng defect với một dòng cho mỗi
vấn đề do AI sinh ra mà THỰC SỰ bị bắt**, mỗi dòng nêu rõ triệu chứng và vì sao nó quan
trọng. Các dòng mồi, **tất cả đều là thật**: `Task.WhenAll` ném đi kết quả song song;
retry `4xx`; cache response lỗi; `DateTime.Now` thay vì một `TimeProvider` được inject;
FluentAssertions được đề xuất dù có vấn đề licence. Thêm phần còn lại **khi chúng xảy ra**
trong Task 1–8 — **ghi lại TRONG LÚC hiện thực, không phải từ ký ức vào lúc cuối.**

**Bước 3.** Copy các prompt thực sự đã dùng vào `prompts.md` và các ghi chú review vào
`review-log.md`.

**Bước 4.** Cập nhật `docs/system-design.md` lên **v2** — đối chiếu **mọi** sơ đồ và
interface với code đã xây, thêm ảnh chụp trace từ Task 7, và chuyển mọi thứ đã bị cắt vào
§9 Non-Goals **kèm lý do**.

**Bước 5.** Kiểm chứng README **từ một clone sạch**.
Chạy: `git clone <repo> /tmp/keyloop-verify && cd /tmp/keyloop-verify && docker compose up -d && dotnet test`
Mong đợi: test pass khi **chỉ đi theo README**, không có bước nào mà README không nhắc tới.
**Sửa README, đừng sửa lịch sử shell.**

**Bước 6.** Quay video (5–10 phút): giới thiệu và lựa chọn scenario; đi qua thiết kế; câu
chuyện cộng tác với AI (1–2 phút); demo trực tiếp **bao gồm một upstream bị tiêm chaos để
cho thấy response một phần**; bài học và thách thức.

> **Trạng thái:** Bước 1–5 và 7 đã hoàn thành. **Bước 6 — video — là hạng mục duy nhất của
> cả plan còn chưa làm.** Xem §VII.2, phán quyết của Task 9.

**Bước 7.** Commit:
`docs: add README with AI collaboration narrative and finalise system design v2`
