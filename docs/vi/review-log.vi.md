# Nhật ký review AI

**Bản tiếng Việt** · Keyloop Technical Assessment — Scenario D · Tác giả: Pham Gia Khiem

| | |
|---|---|
| **Bản gốc tiếng Anh** | [`review-log.md`](../../docs/ai-collaboration/review-log.md) — **đây là bản thẩm quyền** |
| **Mục lục bộ tiếng Việt** | [`tai-lieu-tieng-viet.md`](../tai-lieu-tieng-viet.md) |
| **Nội dung** | Bằng chứng — 22 entry: AI đề xuất gì, đổi gì, vì sao |

> Nếu bản tiếng Việt và bản tiếng Anh khác nhau, **bản tiếng Anh là bản đúng**. Tên lớp,
> tên file, tên test, khóa cấu hình và khối code giữ nguyên tiếng Anh vì chúng là định
> danh trong mã nguồn.
>
> Các tham chiếu dạng **§I.x**, **§III.x**… trỏ sang phần tương ứng trong bộ tài liệu này:
> I = thiết kế, II = tour mã nguồn, III = tra cứu, IV = README, V = nhật ký review,
> VI = prompt, VII = phán quyết, VIII = plan. Mục lục ở link trên.

---

Bản ghi chạy liên tục về **GenAI đề xuất gì, đã làm gì với đề xuất đó, và vì sao**. Các
entry được thêm vào **TRONG LÚC** công việc diễn ra, không phải dựng lại sau — điểm chính
của nhật ký này là **làm bằng chứng**, và bằng chứng viết lại từ ký ức vào lúc cuối thì
không phải bằng chứng.

Mỗi entry trả lời ba câu hỏi: **mô hình sinh ra gì**, **tôi đã làm gì với nó**, và **điều
gì sẽ xảy ra sai nếu tôi chỉ đơn giản chấp nhận nó**.

> **Ghi chú của người dịch:** bản gốc trình bày các entry giai đoạn hiện thực dưới dạng
> một bảng với các ô rất dài. Ở đây em chuyển thành từng mục nhỏ cho dễ đọc, và **sắp theo
> thứ tự số I-1 → I-15** (bản gốc xếp theo thứ tự chúng được thêm vào, nên I-8 nằm trước
> I-1). Nội dung không thay đổi.

---

## V.A. Giai đoạn thiết kế — 2026-10-29

### D-1 · Chọn scenario bằng cách chấm điểm theo một rubric tường minh

**Đề xuất.** Cho đề bài, mô hình chấm điểm cả bốn scenario theo độ phức tạp hiện thực,
nguy cơ phình phạm vi, và mỗi cái để lại bao nhiêu chất liệu cho tài liệu thiết kế. Nó xếp
Scenario D thứ nhất về **tín hiệu trên mỗi đơn vị công sức** và khuyên không chọn
Scenario A.

**Quyết định.** **Chấp nhận**, nhưng là chấp nhận sau khi kiểm tra **lý lẽ**, không phải
kiểm tra thứ hạng. Scenario A đạt điểm cao nhất về độ sâu kỹ thuật — booking với tài nguyên
giới hạn là một bài toán concurrency thực sự — nhưng nó cũng là chỗ mà **một giải pháp sai
tinh vi rất dễ ship và chắc chắn sẽ bị chọc vào**. Các lo ngại về độ tin cậy của Scenario D
là **nội tại** của bài toán chứ không phải gắn thêm vào, mà đó mới đúng là điều mà cụm từ
"scalability, performance, reliability, maintainability, observability" của đề bài đang
hỏi tới.

**Nếu chấp nhận một cách mù quáng.** Ở đây thì ít hại — nhưng **chính cái rubric mới là
đầu ra giá trị, không phải câu trả lời**. Có thể nói ra được **vì sao** chọn D thay vì A
chính là điều mà tiêu chí đánh giá thứ nhất đang hỏi.

---

### D-2 · Một yêu cầu bắt buộc mà Scenario D không tự nhiên thỏa mãn

**Đề xuất.** Mô hình được yêu cầu đọc lại Phần 2 của đề bài **theo kiểu đối kháng** và báo
cáo bất cứ yêu cầu nào mà Scenario D **không thỏa mãn về mặt cấu tạo**. Nó tìm ra rằng
*"Nếu bạn chọn Backend: Expose một RESTful API và **dùng một database bền vững**"* là
**bắt buộc**, trong khi Scenario D là tổng hợp chỉ-đọc thuần, **không sinh ra dữ liệu của
riêng nó**.

**Quyết định.** **Chấp nhận phát hiện; nhưng quyết định thiết kế là của tôi.** Thay vì
thêm một bảng cho đủ ô tick, hai bảng được chọn mà mỗi bảng trả lời một câu hỏi vận hành
thật: `search_audit` (ai đã truy cập tài liệu của xe nào, và họ có nhận được câu trả lời
đầy đủ — **truy cập có thể kiểm toán là một kỳ vọng thật** trong bán lẻ ô tô) và
`document_metadata_cache` (thứ làm cho hành vi stale-fallback ở §I.4.3 **khả thi ngay từ
đầu**). Được ghi lại thành giả định #1 trong tài liệu thiết kế.

**Nếu chấp nhận mù quáng — hoặc bỏ sót.** Nộp một backend **không có database** sẽ **fail
một yêu cầu tường minh** của đề bài. Đây là **thứ giá trị nhất mà AI đóng góp** trong giai
đoạn thiết kế.

---

### D-3 · `Task.WhenAll` âm thầm phá hủy kết quả một phần

**Đề xuất.** Bản nháp thiết kế fan-out được đưa lại cho mô hình kèm một chỉ thị duy nhất:
**tìm chế độ lỗi**. Nó phát hiện rằng `await Task.WhenAll(sales, service)` ném lại exception
của task fault đầu tiên và **ném đi kết quả đã hoàn thành của task song song**.

**Quyết định.** Chấp nhận và thiết kế vòng quanh nó. Mọi lời gọi source đều được bọc để
thất bại là một **giá trị trả về**, không phải exception (`UpstreamResult<T>` với hai nhánh
`Ok` / `Failed`); `IDocumentSource.FetchAsync` bắt exception tại biên của chính nó. Với
thiết kế đó, `Task.WhenAll` **không thể throw** và bộ tổng hợp luôn nhận đúng một kết cục
cho mỗi nguồn. Hai test ghim nó: một test source trả về `Failed`, và một test source
**throw dù hợp đồng không cho phép**.

**Nếu chấp nhận mù quáng.** Bug **chỉ xuất hiện khi một upstream chết** — nghĩa là **không
bao giờ trong lúc demo và luôn luôn ở production**. Nó sẽ phá vỡ tiêu chí nghiệm thu #3
(danh sách hợp nhất) và làm metric tiêu biểu `response.partial` **trở nên không đo được**.

---

### D-4 · Một dependency có licence không thể ship

**Đề xuất.** Gợi ý test stack đầu tiên của mô hình có FluentAssertions.

**Quyết định.** **Từ chối** và thay bằng Shouldly. FluentAssertions v8 chuyển sang licence
thương mại; **một test dependency mà một công ty không thể hợp pháp dùng trong sản phẩm
thương mại thì không phải là một dependency dùng được**, dù nó quen tay đến mấy.

**Nếu chấp nhận mù quáng.** Một vấn đề licence trong bài nộp cho **một nhà tuyển dụng
thương mại** — một ấn tượng đầu tiên xấu mà **chẳng liên quan gì tới năng lực kỹ thuật**.

---

### D-5 · Status code cho một kết quả một phần

**Đề xuất.** Ban đầu mô hình với tay tới một response kiểu `5xx`/`207` khi một upstream
lỗi.

**Quyết định.** **Từ chối.** Endpoint trả `200` với `partial: true` và một báo cáo
`sources[]` theo từng nguồn; **chỉ** trường hợp tất cả nguồn đều lỗi mới trả `503`. Lý lẽ:
mục tiêu của người dùng là thấy những tài liệu **đang có**, và **nhiều HTTP client bỏ luôn
response body khi nhận `5xx`** — một lựa chọn status code sẽ ném đi chính dữ liệu mà
service đã lấy được thành công. `207 Multi-Status` là một ngữ nghĩa của WebDAV về kết cục
**theo từng tài nguyên** và nó **không mô tả đúng** tình huống này; khối `sources[]` mang
thông tin đó mà **không lạm dụng một status code**. Ghi lại thành giả định #2.

**Nếu chấp nhận mù quáng.** Client sẽ **không có cách nào an toàn** để render một câu trả
lời suy giảm, mà đó lại chính là **hành vi duy nhất mà scenario này tồn tại để trình diễn**.

---

### D-6 · Chọn database, và test nên trỏ vào đâu

**Đề xuất.** Mô hình đưa ra đánh đổi Postgres-so-với-SQLite: SQLite chạy mọi nơi không cần
Docker, Postgres giữ câu chuyện index và concurrency đáng tin.

**Quyết định.** **Supabase PostgreSQL cho ứng dụng đang chạy** — có quản lý, Postgres
thật, không phải cài gì cục bộ. Nhưng **integration test trỏ vào một Postgres CỤC BỘ** qua
Testcontainers, **không trỏ vào Supabase**: test phụ thuộc mạng và phụ thuộc state remote
dùng chung thì **flaky, chậm, và có khả năng làm hỏng chính dữ liệu đang dùng cho video
demo**. Cùng một provider Npgsql ở cả hai nơi, nên không có phân kỳ hành vi. Hai hệ quả
thực tiễn được ghi vào plan: migration **phải** dùng kết nối trực tiếp của Supabase (5432)
vì pooler chế độ transaction (6543) không hỗ trợ tính năng mức session, và **không
connection string nào được commit**.

**Nếu chấp nhận mù quáng.** Trỏ bộ test vào Supabase sẽ tạo ra một build **đỏ bất thường
từng lúc** — **thứ tệ nhất có thể đưa cho một người review**.

---

### D-7 · Nơi AI không được cấp quyền gì

**Cố ý KHÔNG** giao phó, và được ghi lại kèm lý lẽ trong tài liệu thiết kế để người review
có thể tranh luận với **lập luận** thay vì với **đầu ra**: hợp đồng API và ngữ nghĩa status
code của nó (§I.5.1), ngân sách timeout (§I.8.1), việc coi VIN là dữ liệu cá nhân và hash
nó trong log (§I.7.1), việc loại upstream khỏi `/health/ready` (§I.7.4), và **mọi dòng
trong bảng giả định** (§I.10).

---

## V.B. Giai đoạn hiện thực

Được điền vào trong lúc Task 1–8 được thực thi. Định dạng: mô hình sinh ra gì, đã đổi gì,
vì sao nó quan trọng. **Một dòng cho mỗi defect THẬT** — một mục trống còn tốt hơn một mục
bị nhồi cho dày.

---

### I-1 · Task 1 — Phiên bản package trôi nổi

**AI sinh ra.** Script scaffolding chạy
`dotnet add package Microsoft.AspNetCore.Mvc.Testing --no-restore`, ghi `Version="*"` trôi
nổi vào `.csproj` và phân giải ra `10.0.14` (**chỉ dành cho `net10.0`**), làm restore thất
bại với `NU1202` và `NU1604` dưới `TreatWarningsAsErrors`.

**Đã đổi.** Bỏ `--no-restore` / phiên bản wildcard và **ghim** các phiên bản package tất
định tương thích `.NET 9` (`9.0.*` cho `Microsoft.AspNetCore.*` / `EF Core`, phiên bản
tường minh cho `Shouldly` và `NSubstitute`). Cũng ghi chú cho §I.6 rằng chuyển sang
`.NET 10 LTS` chỉ đổi `TargetFramework` **một lần** trong `Directory.Build.props` cộng
với việc ghim lại package `9.x` theo framework.

**Vì sao quan trọng.** Phiên bản `*` trôi nổi **phá vỡ build tái lập được**
(`TreatWarningsAsErrors` từ chối `NU1604`) và kéo về các package **sai major version ngay
khoảnh khắc** một bản .NET major mới lên NuGet.

---

### I-2 · Task 1 & 2 — Đếm sai test và một VIN sai check digit

**AI sinh ra.** Plan do AI sinh ra có đặc tả validate check digit ISO 3779 trong
`Vin.TryParse`, nhưng **đếm 12 unit test thành 13** trong khi **bỏ sót nhánh
`mod 11 == 10 → 'X'`**, và chọn VIN Audi châu Âu `WAUZZZ8V7JA003421` ở Task 2 cho test
case `200 OK` không-có-tài-liệu.

**Đã đổi.** Thêm `1M8GDM9AXKP042788` (check digit `X`, số dư 10) làm test case thứ 13 của
`VinTests`, và **tự tính tổng có trọng số ISO 3779** cho `WAUZZZ8V7JA003421`
(`309 ≡ 1 mod 11`, **không phải `7`**), thay VIN seed rỗng bằng `WAUZZZ8V1JA003421` hợp lệ
(và giữ `WAUZZZ8V7JA003421` chỉ như một ghi chú cảnh báo).

**Vì sao quan trọng.** Các VIN đệm kiểu `WAUZZZ` của châu Âu **không thực thi check digit
Bắc Mỹ**; dùng `WAUZZZ8V7JA003421` cho tình huống kết quả rỗng sẽ **fail ở validate biên
với `400 InvalidCheckDigit`** thay vì test được `200` + `documents: []`.

---

### I-3 · Task 3 — Ba cái bẫy im lặng trong mặc định của Polly v8

**AI sinh ra.** `AddHttpClient<IDocumentSource, TClient>()` + mặc định của
`AddStandardResilienceHandler()` trong .NET / Polly v8 mang theo **ba cái bẫy im lặng**:

1. Bỏ qua `name` tường minh của client khiến **cả hai** registration bị khóa dưới
   `"IDocumentSource"`, **dùng chung một `BaseAddress` và một circuit breaker**;
2. Mặc định `Retry.Delay = 2s` cộng `AttemptTimeout = 2s` **vượt** `TotalRequestTimeout =
   5s` **trước khi** lần retry thứ hai có thể chạy;
3. Mặc định `CircuitBreaker.MinimumThroughput = 100` **không bao giờ trip** dưới lưu lượng
   burst vừa phải hay trong integration test.

**Đã đổi.** Đặt tên hai pipeline `IHttpClientFactory` là `"SalesSystem"` và
`"ServiceSystem"`, set `Retry.Delay = 200ms` (lũy thừa + jitter), và làm
`CircuitBreaker.MinimumThroughput` cấu hình được (mặc định `5`).

**Vì sao quan trọng.** Giữ được **sự cô lập lỗi theo từng upstream thực sự** (§I.2 nguyên
tắc 3) và làm cho ngân sách retry + breaker **nhất quán về mặt toán học** với hệ phân cấp
timeout 2s / 5s / 6s.

---

### I-4 · Task 5 — Shouldly so sánh chuỗi không phân biệt hoa thường theo mặc định

**AI sinh ra.** Trong `Response_body_never_contains_a_lowercase_or_untrimmed_vin`, code
test được sinh ra gọi `raw.ShouldNotContain("1hgcm82633a004352")`, **giả định** ngữ nghĩa
phân biệt hoa thường của `string.Contains` / FluentAssertions. `ShouldContain` /
`ShouldNotContain` của Shouldly **mặc định là `Case.Insensitive`**, nên nó khớp với chuỗi
đã chuẩn hóa đúng `"1HGCM82633A004352"` và **fail**.

**Đã đổi.** Truyền `Case.Sensitive` tường minh (`ShouldNotContain(..., Case.Sensitive)`)
và kiểm tra cả khoảng trắng chưa trim.

**Vì sao quan trọng.** Hệ quả trực tiếp của việc chuyển từ FluentAssertions sang Shouldly
(D-4); nó cũng **ngăn các khẳng định xanh-giả về hoa thường** (`ShouldContain` khớp với
đầu ra sai hoa thường).

---

### I-5 · Task 6 — Ba điểm tinh vi của tầng persistence

**AI sinh ra.** Ba điểm tinh vi lộ ra khi đối chiếu với bản nháp plan/spec:

1. §I.8.4 vẽ `search_audit.correlation_id` là `uuid`, trong khi §I.7.1 **nhận chuỗi
   `X-Correlation-Id` bất kỳ** và `TraceId` W3C dạng 32-hex;
2. `AddDbContext` theo phạm vi request tiêu chuẩn **fail khi `UpsertAsync` gọi đồng thời**
   (`"A second operation was started on this context instance"`) và throw
   `ObjectDisposedException` bên trong các lệnh ghi audit fire-and-forget trong `Task.Run`
   **sau khi** HTTP response hoàn tất (**âm thầm làm mất mọi dòng audit** sau `try/catch`);
3. Rẽ nhánh theo `builder.Configuration.GetConnectionString("Default")` **trước**
   `builder.Build()` sẽ **phớt lờ** các override của
   `WebApplicationFactory.ConfigureAppConfiguration`, làm `DatabaseOutageTests` chạm vào
   một repository no-op thay vì một DB bị hỏng.

**Đã đổi.** Map `correlation_id` thành `varchar(128)`, chuyển sang
`AddDbContextFactory<DocumentViewerDbContext>` với việc tạo `DbContext` **cho mỗi thao
tác**, phân giải `IConfiguration` **lười (lazily)** bên trong các delegate factory của DI,
và khẳng định `ShouldBeOfType<DocumentCacheRepository>()` trong `DatabaseOutageTests`.

**Vì sao quan trọng.** Giữ cho các correlation ID **không-phải-UUID** vẫn hợp lệ, làm cho
upsert đồng thời và ghi audit fire-and-forget **an toàn ở production**, và **ngăn một
integration test về sự cố database bị xanh-giả**.

---

### I-6 · Task 7 — VIN lọt vào log qua một object `Uri`

**AI sinh ra.** Dù các logger của ứng dụng đã chỉ log `VinHash` / `VinMasked` và một
Serilog enricher đã làm sạch các property dạng string (sửa được scope `RequestPath` ngầm
của `Microsoft.AspNetCore.Hosting`), test `No_log_line_contains_the_raw_vin` **vẫn fail**
trên một lời gọi upstream suy giảm `503`, bởi vì `Microsoft.Extensions.Http.Resilience`
(logger execution-attempt của Polly) **lưu URL gửi đi dưới dạng một `ScalarValue` bọc một
object `System.Uri`** chứ không phải `string`, **làm lộ VIN thô vào `@m`**. Riêng việc
khác: `OpenTelemetry.* 1.11.2` do mô hình gợi ý **fail restore** dưới
`TreatWarningsAsErrors` với cảnh báo bảo mật `NU1902`.

**Đã đổi.** Mở rộng `VinRedactingLogEnricher` để **đệ quy** làm sạch `ScalarValue`
(`string` **và** `Uri`), `StructureValue`, `SequenceValue`, và `DictionaryValue` (cộng một
`BaseProcessor<Activity>` tương ứng cho tag URL của span OpenTelemetry), truyền
`preserveStaticLogger: true` vào `UseSerilog` để cô lập test song song, và nâng
`OpenTelemetry.*` lên bản đã vá `1.20.1`.

**Vì sao quan trọng.** **Ngăn một vụ rò rỉ PII âm thầm** vào JSON log ở production trong
lúc upstream retry — tức **đúng cái nhánh degraded-mode mà log cảnh báo của Polly phát ra**
— và giữ cho cây dependency sạch khỏi các CVE đã biết.

---

### I-7 · Task 7 — Hai lệnh ghi cache bị tuần tự hóa, phát hiện từ waterfall của chính mình

**AI sinh ra.** Khi kiểm tra ảnh chụp waterfall Jaeger **thật** đầu tiên đối chiếu với
Supabase thật (`eu-west-1`), thấy rằng **tuy hai lời gọi HTTP upstream có chồng lấn**
(`354 ms` và `384 ms`), `DocumentAggregationService` lại **`await TryCacheUpsertAsync`
tuần tự** bên trong vòng `foreach` trên `outcomes`, **tuần tự hóa hai vòng
`ON CONFLICT DO UPDATE` liên vùng** sau khi fan-out đã xong.

**Đã đổi.** Thu các task `TryCacheUpsertAsync` vào `cacheUpsertTasks` và `await` chúng
**đồng thời** qua `Task.WhenAll(cacheUpsertTasks)` (giữ nguyên ngữ nghĩa cache
read-after-write tất định trong khi vẫn chồng lấn hai lệnh ghi DB).

**Vì sao quan trọng.** **Giảm một nửa** độ trễ ghi cache ở trạng thái ổn định khi cả hai
nguồn đều hit — và nó được phát hiện **trực tiếp bằng cách kiểm tra waterfall OpenTelemetry
của chính mình**, không phải bằng mock tổng hợp.

---

### I-8 · Bản fix defect — Toàn xanh ở local, chết ở production

> **Đây là entry quan trọng nhất của cả giai đoạn hiện thực.**

**AI sinh ra.** Mọi thứ tới thời điểm đó **đều xanh**: 73 test pass và endpoint demo đúng
đối chiếu với các mock. Nhưng đối chiếu với **Supabase THẬT**, service vẫn **hỏng theo ba
cách mà không test nào có thể thấy**:

1. **`/health/ready` trả `503`**: health check giới hạn probe của nó ở mức **hardcode 2
   giây**, nhưng một kết nối liên vùng lạnh cần TLS + tra cứu tenant Supavisor + SASL, và
   **Npgsql không quan sát cancellation token một cách đáng tin trong lúc đang mở kết nối
   vật lý**, nên cái giới hạn đó **bị phớt lờ** và probe chạy hết trọn **30 giây** connect
   timeout.
2. **Nhánh stale-cache của §I.4.3 chưa bao giờ chạy** — mọi nguồn lỗi đều báo `failed`,
   **không bao giờ là `stale`**. Lệnh ghi cache chạy trên cancellation token **của
   request**, mà token đó bị hủy ngay khoảnh khắc response được gửi, nên một lệnh ghi liên
   vùng **luôn bị hủy giữa lúc bắt tay** và cache **vĩnh viễn rỗng**. Ngân sách ghi 1500 ms
   của chính repository cũng **không thể** hoàn tất một kết nối lạnh.
3. **`await Task.WhenAll(cacheUpsertTasks)` nằm trên đường đi của response**, nên một
   database **nhận kết nối TCP rồi không bao giờ trả lời** đã **giữ toàn bộ lệnh đọc** cho
   tới khi ngân sách request hết.

**Đã đổi.** Thực thi ngân sách readiness bằng cách **đua** probe thay vì hủy nó, và làm nó
cấu hình được (`HealthChecks:DatabaseTimeoutMs`, mặc định 5 giây). Tách lệnh ghi cache khỏi
token của request (`CancellationToken.None`) và chia ngân sách 1500 ms duy nhất của
repository thành ngân sách **đọc 1500 ms** trên đường response và ngân sách **ghi 8 giây**
sau đó; nâng ghi audit lên 8 giây vì cùng lý do. Cấp cho việc xử lý cache một ngân sách
**1 giây trên đường response**, quá đó thì phần đọc hoàn tất và phần ghi tự chạy nền. Thêm
`NpgsqlConnectionHardening` để giữ một kết nối ấm (`Minimum Pool Size=1`,
`Connection Idle Lifetime=300`, `Timeout=15`) **mà không ghi đè bất cứ gì người vận hành
đã set**, cộng một `DatabaseWarmupService` mở một kết nối sau khi khởi động và **không bao
giờ chặn boot**. **Đã kiểm chứng đối chiếu Supabase thật**: readiness `200`, và một nguồn
lỗi giờ trả `stale` với đủ 7 tài liệu.

**Vì sao quan trọng.** **Toàn bộ điểm chính của Scenario D là cư xử tốt khi một dependency
cư xử tệ**, và cái tính năng chứng minh điều đó **đã chết ở production trong khi mọi test
nói ngược lại**. Test không thể bắt được nó vì Testcontainers chạy PostgreSQL **cục bộ** và
kết nối trong **dưới một milli giây**, nên ngân sách 1500 ms là **rộng rãi ở đó** và **phi
lý** đối với một managed pooler liên vùng. **Bài học rút ra: một bộ test xanh chỉ mô tả cái
môi trường mà bộ test đó đang chạy trong đó.** Vì vậy ba test thay thế trỏ database vào
một **TCP listener hố đen** nhận kết nối rồi không bao giờ trả lời — một cổng đóng bị từ
chối ngay lập tức và **sẽ chẳng chứng minh được gì**.

---

### I-9 · Bản fix defect — Một test không thể fail

**AI sinh ra.** Trong lúc viết test hồi quy cho ngân sách readiness, bản đầu tiên trỏ
connection string vào **một cổng đóng** (`127.0.0.1:59999`) và khẳng định readiness trả lời
trong dưới 1,5 giây.

**Đã đổi.** Thay cổng đóng bằng một `TcpListener` **hố đen** nhận kết nối rồi không bao giờ
trả lời, và thêm một test thứ ba khẳng định rằng một ngân sách **rộng rãi 3 giây** cũng
được tôn trọng (mà cái trần hardcode 2 giây cũ sẽ **fail**).

**Vì sao quan trọng.** Test đó sẽ **xanh cả trước VÀ sau** bản fix: một cổng đóng bị từ
chối ngay lập tức, nên nó **chẳng đo gì cả**. **Một test không thể fail còn tệ hơn là
không có test, bởi nó báo cáo một sự an toàn mà nó chưa từng kiểm tra.** Nó được bắt bằng
cách hỏi **"thay đổi gì ở production sẽ làm test này fail?"** trước khi tin nó.

---

### I-10 · Bản fix defect — `ContainsKey` trả lời một câu hỏi khác

**AI sinh ra.** `NpgsqlConnectionHardening` dùng
`NpgsqlConnectionStringBuilder.ContainsKey` để quyết định xem người vận hành đã set
`Timeout` / `Minimum Pool Size` chưa, nên nó **chưa bao giờ áp dụng bất kỳ mặc định nào**.

**Đã đổi.** Đọc các khóa đã được cấp từ một `DbConnectionStringBuilder` **thuần** dựng từ
chuỗi thô.

**Vì sao quan trọng.** `NpgsqlConnectionStringBuilder.ContainsKey` trả lời **"đây có phải
một keyword tôi biết không"**, **không phải** "người vận hành có set nó không", nên nó
`true` với **mọi** option của Npgsql. Unit test bắt được ngay lập tức — `MinPoolSize` trả
về `0` — mà **đó chính là công dụng của một test được viết trước phần hiện thực**.

---

### I-11 · Task 8 — Cấu hình đọc háo hức, một defect production thật

**AI sinh ra.** **Cả mười** test resilience fail **y như nhau** ở lần chạy đầu: service cứ
gọi các container mock thật ở cổng 5101/5102 thay vì các stub WireMock mà test đã trỏ nó
tới. `AddDocumentSources` đọc `Upstreams:*:BaseAddress` **háo hức, lúc đăng ký**, nên nó
chụp lại đúng những gì `appsettings.json` nói và **phớt lờ mọi nguồn cấu hình thêm vào sau
đó** — bao gồm cả nguồn của `WebApplicationFactory`.

**Đã đổi.** Chuyển các lần đọc cấu hình **vào trong** các delegate cấu hình client để
chúng phân giải từ `IServiceProvider` cho mỗi client (`BaseAddress(sp, …)`,
`MinimumThroughput(sp)`), và bỏ tham số `IConfiguration` giờ đã vô dụng.

**Vì sao quan trọng.** Cùng họ với I-5, và là **một defect production THẬT** chứ không phải
một bất tiện của test: **bất kỳ lần triển khai nào đặt địa chỉ upstream qua biến môi trường
hoặc một file config được mount — tức cách làm bình thường — sẽ bị âm thầm phớt lờ** để
dùng mặc định đã nướng vào trong. **Phải có một test trỏ đi chỗ khác mới nhận ra được.**

---

### I-12 · Task 8 — Test flaky vì tranh chấp CPU, không phải vì bug sản phẩm

**AI sinh ra.** Sau khi sửa cấu hình, chín trên mười test pass và
`Repeated_failures_open_the_circuit_and_subsequent_calls_fail_fast` fail với collection
`Sources` bị null — **nhưng pass ba trên ba lần khi chạy một mình**.

**Đã đổi.** Set `parallelizeTestCollections: false` cho project integration test qua
`xunit.runner.json`, và làm các vòng lặp breaker **bỏ qua** một response không-`200` thay
vì cố parse nó.

**Vì sao quan trọng.** Bốn integration test khẳng định về thời gian trôi qua — fan-out song
song, ngân sách timeout, và hai ngân sách readiness. Khi chạy cùng các test class khác,
tranh chấp CPU đẩy một upstream **cố ý khỏe mạnh** vượt quá timeout 2 giây mỗi lần thử, nên
cả hai nguồn đều lỗi và endpoint **trả lời `503` một cách hoàn toàn đúng** với một body
`ProblemDetails` không có `sources`. **Sản phẩm thì đúng và cái sai là bộ khung chạy test**;
truy nó như một bug sản phẩm sẽ là công sức bỏ đi. **Khẳng định về độ trễ và chạy test song
song không đi cùng nhau được.**

---

### I-13 · Task 9 — Tài liệu không sống nổi qua việc bị chạy thật

**AI sinh ra.** Bản nháp đầu tiên của mục "Seeing it degrade" trong README được viết từ **ý
định thiết kế**: làm hỏng một nguồn, rồi làm nó chậm, và quan sát một timeout ~5 giây. Chạy
các lệnh đã ghi theo đúng thứ tự thì ra **0,04 giây** và lý do **`CircuitOpen`** thay vào
đó, còn bước `garbage` báo nguồn là **`ok`** chứ không phải `InvalidPayload`.

**Đã đổi.** Viết lại cả mục **đối chiếu với đầu ra đo được**: thêm bước circuit-breaker như
**một tính năng để quan sát** thay vì một sự cố tình cờ, thêm **lệnh chờ 16 giây** để các
breaker đóng lại trước khi đo một timeout (**5,9 giây, không phải 5**), và sửa lại mô tả
`garbage` — mock trả về JSON **đúng cú pháp nhưng thiếu tiêu đề**, nên bản ghi bị bỏ qua và
nguồn **vẫn `ok`**, mà đó là **giả định #7 đang hoạt động** chứ không phải một payload
không hợp lệ.

**Vì sao quan trọng.** **Tài liệu không sống nổi qua việc bị chạy thật thì còn tệ hơn là
không có tài liệu**: một người review đi theo nó sẽ kết luận rằng service bị hỏng, hoặc
rằng **tác giả chưa bao giờ chạy chính hướng dẫn của mình**. Hai lỗi có **cùng một gốc**:
ghi lại điều mà **thiết kế nói** thay vì điều mà **hệ thống làm**. Giờ **mọi con số trong
mục đó đều là con số đã được đo**.

---

### I-14 · Review cuối — Một Critical mà 96 test không thấy

> **Đây là entry biện minh cho toàn bộ quy trình.**

**AI sinh ra.** Một người review **với ngữ cảnh mới** trên toàn bộ branch tìm ra một defect
**Critical** mà **cả phần hiện thực lẫn 96 test đang pass đều không bắt được**. Lệnh đọc
cache chạy **SAU** fan-out — khoảng một giây trước ngân sách 6 giây của toàn request, mỗi
khi một nguồn chạm timeout 5 giây của chính nó. Đối chiếu với một database nhận kết nối rồi
không bao giờ trả lời, `OperationCanceledException` của cache **lọt ra khỏi
`GetDocumentsAsync`** và **ném đi những tài liệu đã hoàn thành của nguồn khỏe mạnh**: thành
một `503` **rỗng không có gì trong đó**. Người review **kiểm chứng nó bằng một harness**
chứ không phải bằng suy luận.

**Đã đổi.** Làm cho lệnh đọc cache **về mặt cấu trúc không có khả năng throw** — nó bắt mọi
thứ, **kể cả cancellation**, và báo cáo một kết cục thay vì throw. Thêm test còn thiếu
(nguồn lỗi + database treo) mà chính sự thiếu nó đã để defect này ship trong trạng thái
xanh. Người review còn tìm thấy: `openapi.json` đã commit **đánh dấu `required` cho các
thành viên bị bỏ-khi-null** (nên một client được generate sẽ **từ chối chính đường đi thuận
lợi**), metric `cache.lookup` **ghi nhận một hit từ một lần tra đã bị bỏ dở**, nhánh `503`
do timeout **không ghi metric và không ghi dòng audit nào**, `Parallel_fan_out` **fail mọi
lần chạy một mình**, và hai trong ba thiết lập connection-hardening **chỉ lặp lại đúng mặc
định của Npgsql** — làm cho test đó thành **một test nữa không thể fail**. **Cả bảy đều
được sửa trong một lượt, mỗi cái với một test được xem thấy đỏ trước.**

**Vì sao quan trọng.** Defect này là **CHÍNH XÁC cái chế độ lỗi mà `UpstreamResult<T>` tồn
tại để ngăn** — được tái tạo lại **qua đường cancellation**, trong **đúng cái môi trường
(managed Postgres bị treo) mà bản fix trước được viết ra để xử lý**, bởi **chính người kỹ
sư vừa viết bản fix đó**. **Tự review không thể tìm ra nó: cùng tác giả, cùng điểm mù.**
Bài học của I-8 là một bộ test xanh chỉ mô tả cái môi trường nó chạy trong đó; bài học ở
đây **hẹp hơn và sắc hơn** — **một nguyên tắc thiết kế chỉ đúng ở nơi có ai đó đi kiểm tra
rằng nó đúng, và chính người viết ra nó là người tệ nhất để đi kiểm.**

---

### I-15 · Sau review — Hai "Minor" mà hậu quả không minor

**AI sinh ra.** Hai phát hiện mà review toàn branch **xếp loại Minor**, nhưng được xử lý vì
**hậu quả của chúng không minor**:

1. `SalesDocumentNormalizer` nhân kilobyte của upstream với 1024 **không kiểm tra tràn**,
   nên một giá trị phi lý sẽ **quấn vòng** và API trao cho client một số byte **ÂM**.
2. Response body của upstream được đọc **không giới hạn kích thước** và — vì
   `HttpCompletionOption.ResponseHeadersRead` nghĩa là pipeline Polly **đã trả về rồi** —
   **nằm hoàn toàn ngoài ngân sách của upstream**.

**Đã đổi.** Kilobyte lớn hơn `long.MaxValue / 1024` giờ báo kích thước **không biết** và
tài liệu **vẫn được trả về**. Phần đọc body được thêm giới hạn **4 MiB thực thi TRONG LÚC
ĐỌC** (một response chunked **không khai báo `Content-Length`**) và ngân sách **2 giây**
riêng của nó, với body quá lớn báo là `InvalidPayload` và body nhỏ giọt báo là `Timeout`.

**Vì sao quan trọng.** **Việc viết test cho giới hạn body cho thấy phát hiện đó đã bị mô tả
nhẹ đi**: không có giới hạn, phép đọc **không chỉ phình bộ nhớ — nó KHÔNG BAO GIỜ TRẢ VỀ**
— tiến trình test phải **bị giết sau 70 giây**. §I.2 nguyên tắc 3 nói upstream là không
đáng tin, mà response body lại là **chỗ duy nhất trong codebase nơi chúng được tin**. Một
kích thước âm cũng tệ hơn là không có kích thước, bởi vì **client sẽ tin nó**.
