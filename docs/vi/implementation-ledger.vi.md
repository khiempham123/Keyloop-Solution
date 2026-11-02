# Sổ phán quyết hiện thực

**Bản tiếng Việt** · Keyloop Technical Assessment — Scenario D · Tác giả: Pham Gia Khiem

| | |
|---|---|
| **Bản gốc tiếng Anh** | [`implementation-ledger.md`](../../docs/ai-collaboration/implementation-ledger.md) — **đây là bản thẩm quyền** |
| **Mục lục bộ tiếng Việt** | [`tai-lieu-tieng-viet.md`](../tai-lieu-tieng-viet.md) |
| **Nội dung** | Phán quyết — mọi quyết định khi chạy plan |

> Nếu bản tiếng Việt và bản tiếng Anh khác nhau, **bản tiếng Anh là bản đúng**. Tên lớp,
> tên file, tên test, khóa cấu hình và khối code giữ nguyên tiếng Anh vì chúng là định
> danh trong mã nguồn.
>
> Các tham chiếu dạng **§I.x**, **§III.x**… trỏ sang phần tương ứng trong bộ tài liệu này:
> I = thiết kế, II = tour mã nguồn, III = tra cứu, IV = README, V = nhật ký review,
> VI = prompt, VII = phán quyết, VIII = plan. Mục lục ở link trên.

---

Bản ghi theo từng task được giữ **trong lúc** plan được thực thi: lần quét sơ bộ tìm xung
đột giữa các task, **mọi phán quyết** được đưa ra ở những chỗ plan và spec bất đồng — mỗi
cái kèm lý lẽ và **giá phải trả nếu nó sai** — cùng với lệnh test và kết quả đã đóng từng
task.

Nó được đưa vào vì **một người review nên thấy được những quyết định đã đưa ra thay cho
mình, không chỉ thấy đoạn code đi ra**.

> **Ghi chú của người dịch:** phần này giữ nguyên các chuỗi output test bằng tiếng Anh, vì
> chúng là **đầu ra máy đã ghi lại** — dịch chúng sẽ làm mất tính bằng chứng.

---

## VII.1. Quét sơ bộ các interface

| Bên sinh ra → Bên tiêu thụ | Interface / Hợp đồng | Phát hiện & Phán quyết |
|---|---|---|
| Task 1 → Task 3, 4, 5, 6, 7 | `Vin`, `VinError`, `SourceSystem`, `DocumentType`, `VehicleDocument`, `UpstreamResult<T>`, `UpstreamFailure`, `UpstreamFailureReason` | Khớp sạch trên mọi task và spec §I.4.2, §I.5.2. |
| Task 2 → Task 3, 5 | Schema của `MockSalesApi` (`:5101`) & `MockServiceApi` (`:5102`) + `/admin/chaos` | Khớp sạch với bảng spec §I.5.3. |
| Task 4 ↔ Task 5, 6, 7 | `IDocumentCacheRepository` (Task 6) và `IAggregationMetrics` (Task 7) được `DocumentAggregationService` (Task 4) và DI host (Task 5) tiêu thụ | **Phán quyết:** Định nghĩa `IDocumentCacheRepository` (`CachedSourceDocuments`) và `IAggregationMetrics` trong `DocumentViewer.Application` ngay ở Task 4, dùng **đúng signature của Task 6/7**, và đăng ký các mặc định no-op ở Task 5 (`TryAddSingleton`) cho tới khi Task 6 & 7 thay thế chúng — **vì sao:** `Application` phải compile được ở Task 4 và `Api` phải chạy được end-to-end ở Task 5 — **giá nếu sai:** bằng không, signature giống hệt Task 6 & 7. |
| Task 4 → Task 5 | Biểu diễn JSON của `SourceStatus` trong `DocumentsResponse` | **Phán quyết:** Chú thích các thành viên enum `SourceStatus` bằng `[JsonStringEnumMemberName("ok" / "stale" / "failed")]` trong khi giữ `JsonStringEnumConverter` PascalCase cho `SourceSystem`, `DocumentType`, và `UpstreamFailureReason` — **vì sao:** khớp **nguyên văn** ví dụ JSON ở spec §I.4.3 & §I.5.2 — **giá nếu sai:** một lần điều chỉnh attribute không đáng kể. |
| Spec §8.4 ↔ Task 6 & 7 | Kiểu cột `search_audit.correlation_id` (`uuid` trong sơ đồ §8.4 so với `string CorrelationId` ở Task 6 và `X-Correlation-Id` bất kỳ / `TraceId` W3C 32-hex ở §7.1 & Task 7) | **Phán quyết:** Dùng `varchar(128)` cho `search_audit.correlation_id` thay vì `uuid` của Postgres — **vì sao:** các header `X-Correlation-Id` từ ngoài vào và giá trị `TraceId` W3C 32 ký tự **không phải UUID theo RFC 4122** và sẽ **fail khi insert** vào cột `uuid` — **giá nếu sai:** khoảng 100 byte mỗi dòng audit, so với việc ghi audit **bị vỡ** khi có correlation ID tùy biến. |

---

## VII.2. Các phán quyết và kết quả theo từng task

**Task 1 · Phán quyết.** Đoạn `VinTests` trong plan có **12 case** trong khi **tuyên bố là
13** và **bỏ sót nhánh số-dư-10 (`'X'`) của ISO 3779**; đã thêm `1M8GDM9AXKP042788` làm
case thứ 13 — **vì sao:** nó thực thi nhánh `mod 11 == 10` và khớp với kỳ vọng 13 test —
**giá nếu sai:** không gì cả, đây là một VIN ISO 3779 hợp lệ.

**Task 1 · Hoàn thành** (commit `1fed2a3..403f43d`, test: `dotnet test tests/DocumentViewer.UnitTests` → `Passed! Failed: 0, Passed: 13, Total: 13, Duration: 31 ms`)

**Task 2 · Hoàn thành** (commit `403f43d..3c99371`, test: `dotnet test KeyloopDocumentViewer.sln` → `No test is available in …DocumentViewer.IntegrationTests.dll` — project integration test lúc đó **chưa có test nào**)

**Task 3 · Hoàn thành** (commit `3c99371..a3b0c7f`, test: `dotnet test tests/DocumentViewer.UnitTests` → `Passed! Failed: 0, Passed: 36, Total: 36, Duration: 168 ms`)

**Task 4 · Hoàn thành** (commit `a3b0c7f..84406b1`, test: `dotnet test tests/DocumentViewer.UnitTests` → `Passed! Failed: 0, Passed: 47, Total: 47, Duration: 501 ms`)

**Task 5 · Hoàn thành** (commit `84406b1..717e717`, test: `dotnet test KeyloopDocumentViewer.sln` → `Passed! Failed: 0, Passed: 9, Total: 9, Duration: 1 s`)

**Task 6 · Hoàn thành** (commit `717e717..f9086e0`, test: `dotnet test … --filter FullyQualifiedName~Persistence --no-build` → `Passed! Failed: 0, Passed: 8, Total: 8, Duration: 3 s`)

**Phán quyết (giữa Task 6 và 7).** Tiếp tục commit lên `main` thay vì cắt một branch —
**vì sao:** cả 8 commit trước của plan này **đã nằm trên `main`**, đây là một repo bài nộp
**một tác giả**, và Task 9 kiểm chứng từ **một clone sạch của branch mặc định** — **giá nếu
sai:** một lần rebase sang branch, **không đổi code**.

**Task 7 · Hoàn thành** (commit `f9086e0..358567b`, test: `dotnet test … --filter FullyQualifiedName~ObservabilityTests` → `Passed! Failed: 0, Passed: 9`)

**Bản fix defect · Phán quyết 1.** Ngân sách readiness được thực thi bằng cách **đua** probe
thay vì **hủy** nó, và được làm cấu hình được (mặc định 5 giây) — **vì sao:** Npgsql **không
quan sát một `CancellationToken` một cách đáng tin trong lúc đang mở kết nối vật lý**, nên
một ngân sách 200 ms **bị phớt lờ** và readiness chạy hết trọn **30 giây** connect timeout
đối với Supabase thật — **giá nếu sai:** một task probe bị bỏ dở cho mỗi lần poll readiness
bị timeout, mà nó **được quan sát và bị loại bỏ**.

**Bản fix defect · Phán quyết 2.** Lệnh ghi cache dùng `CancellationToken.None` thay vì
token của request, với ngân sách ghi 8 giây ở repository và ngân sách 1 giây trên đường
response — **vì sao:** token của request **bị hủy khi response được gửi**, nên các lệnh ghi
liên vùng **luôn bị hủy giữa lúc bắt tay** và cache **vĩnh viễn rỗng**, làm cho nhánh stale
fallback ở spec §4.3 **thành code chết** — **giá nếu sai:** một lệnh ghi cache có thể sống
lâu hơn request của nó tới 8 giây và **client không hủy được nó**.

**Bản fix defect · Hoàn thành** (commit `358567b..a4985d7`, test: `dotnet test KeyloopDocumentViewer.sln` → `Passed! 56 unit + 29 integration = 85, Failed: 0`)

**Task 8 · Phán quyết 1.** Sửa `AddDocumentSources` để phân giải cấu hình upstream **lười
từ `IServiceProvider`** thay vì chụp `IConfiguration` lúc đăng ký, và bỏ tham số
`IConfiguration` của nó — **vì sao:** lần đọc háo hức **phớt lờ mọi nguồn cấu hình thêm vào
sau khi đăng ký**, nên các test gọi vào **container mock thật** và **bất kỳ lần triển khai
nào đặt địa chỉ upstream qua biến môi trường sẽ bị âm thầm phớt lờ** — **giá nếu sai:** một
lần tra `IConfiguration` thêm cho mỗi lần tạo `HttpClient`.

**Task 8 · Phán quyết 2.** Tắt parallel test collection cho project integration test —
**vì sao:** bốn test khẳng định về thời gian trôi qua và tranh chấp CPU từ các class song
song đã **đẩy một upstream khỏe mạnh vượt quá timeout 2 giây mỗi lần thử**, tạo ra một
`503` **hoàn toàn đúng** mà test **không parse được** — **giá nếu sai:** bộ integration
chạy ~2 phút 11 thay vì ~1 phút 44.

**Task 8 · Hoàn thành** (commit `a4985d7..9cdb6a3`, test: `dotnet test KeyloopDocumentViewer.sln` → `Passed! 56 unit + 39 integration = 95, Failed: 0`)

**Task 9 · Phán quyết 1.** Readiness báo `Healthy` khi **hoàn toàn không cấu hình**
database, và chỉ báo `Unhealthy` khi **đã cấu hình mà không với tới được** — **vì sao:**
chạy không cache và audit là một **cấu hình được hỗ trợ** và service vẫn trả lời truy vấn
tài liệu, nên định nghĩa "tôi làm được việc của tôi" ở spec §7.4 được áp dụng; nếu không,
trải nghiệm clone-sạch sẽ **mở ra bằng một `503`** — **giá nếu sai:** một người vận hành
quên cấu hình database sẽ thấy một service khỏe mạnh với persistence bị tắt trong im lặng,
mà **chính response của health check nói rõ điều đó**.

**Task 9 · Phán quyết 2.** Deliverable video được **hoãn theo yêu cầu tường minh của người
dùng** — **vì sao:** người dùng yêu cầu hoàn thành công việc của hôm đó **trừ video** —
**giá nếu sai:** một trong ba artefact bắt buộc của bài nộp **còn treo**; nó là **hạng mục
duy nhất còn lại**.

**Task 9 · Hoàn thành** (commit `9cdb6a3..bea60ed`, test: `dotnet test` từ **một clone sạch
không có database và không `docker compose up`** → `Passed! 56 unit + 40 integration = 96, Failed: 0`)

---

## VII.3. Review cuối

**Review cuối:** một người review **với ngữ cảnh mới** (Opus, effort cao) trên
`review-f9086e0..bea60ed.diff` — **1 Critical, 7 Important, 13 Minor.**

**Phán quyết · Nâng hạng minor #8** (vệ sinh bài nộp) lên **Important** — **vì sao:**
README **link tới workspace `.superpowers/sdd/` vốn bị git-ignore**, tức là một **link chết
trong mọi bản clone**, và `TODO.md`/`env.sh` là **scaffolding nội bộ đã bị commit** —
**giá nếu sai:** không gì cả; sổ phán quyết được **công bố** thành
`docs/ai-collaboration/implementation-ledger.md` và hai file kia **ở lại trên đĩa nhưng
không được track**.

**Phán quyết · Nâng hạng minor #4** (các stub observability dùng tên trường mà normaliser
**không đọc**) lên **Important** — **vì sao:** **cả chín test trong class đó đang chạy trên
các response ZERO tài liệu và vẫn pass**, nên cả nhóm **không ghim được điều nó tuyên bố** —
**giá nếu sai:** một test guard thêm vào.

| Mã | Đã sửa gì | Bằng chứng |
|---|---|---|
| **C1** | Một database bị treo **ném đi tài liệu của một nguồn khỏe mạnh**, biến `200`-partial thành `503`-rỗng | `A_hung_database_cannot_discard_a_healthy_source_when_the_request_budget_expires` ĐỎ→XANH, suite 101/101 |
| **I1** | `Parallel_fan_out` fail 3/3 khi chạy một mình; `--filter Resilience` flaky 1/4 | Thêm một request **warm-up trước khi bấm đồng hồ** + các vòng breaker **fail ồn ào**; kiểm chứng 3/3 và 4/4 xanh, suite 101/101 |
| **I2** | `openapi.json` đã commit **đánh dấu `required`** cho các thành viên bị-bỏ-khi-null, nên một client được generate **từ chối đường đi thuận lợi** | `OpenApiContractTests` ĐỎ→XANH, suite 101/101 |
| **I3** | Tài liệu thiết kế §8.4 **vẫn vẽ `correlation_id` là `uuid`** trong khi code dùng `varchar(128)` | Đã sửa tài liệu |
| **I4** | `requests.http` **trỏ vào cổng 5000** trong khi README chạy 5080 | `requests.http` và `launchSettings` được đồng bộ về 5080 |
| **I5** | `cache.lookup` **ghi nhận một hit từ một lần tra đã bị bỏ dở**, sau khi response đã đi ra mà không có nó | `A_cache_lookup_that_exceeds_its_budget_is_recorded_as_a_timeout_not_a_hit` ĐỎ→XANH, suite 101/101 |
| **I6** | Timeout toàn request **không ghi metric và không ghi dòng audit nào**, làm mù SLO khả dụng ở §7.5 | `TimeoutAccountingTests` ĐỎ→XANH, suite 101/101 |
| **I7** | Hai trong ba thiết lập hardening **chỉ khớp đúng mặc định của Npgsql**, nên test **vẫn pass dù `Harden` trả về nguyên đầu vào** | `Pins_every_setting_it_depends_on_instead_of_inheriting_an_Npgsql_default` giờ **khẳng định việc phát ra giá trị**; **kiểm chứng bằng đột biến (mutation)**: trả về nguyên đầu vào làm **fail 2 test**, suite 101/101 |

### VII.3.1 Các minor được hoãn

Bảy minor được ghi lại và **cố ý không sửa** ở lượt đó:

1. Health check gọi `Task.Delay(budget, cancellationToken)` báo *"did not answer within
   5000ms"* khi **người gọi hủy**, đó là **một thông báo sai**.
2. Lệnh đọc cache cho các nguồn lỗi được `await` **tuần tự**, nên hai nguồn lỗi có thể tốn
   **2 giây** trên đường đi của response.
3. `Task.Delay` không có token để lại **một timer 1 giây cho mỗi thao tác cache** kể cả khi
   thao tác đó thắng cuộc đua.
4. `NullAggregationMetrics` là **code chết** vì `AddDocumentSources` **luôn** thay thế
   `IAggregationMetrics`.
5. Không có OTLP endpoint, console exporter **xen trace và metric lẫn với JSON của Serilog**
   trên cùng một stdout.
6. `fileSizeKb * 1024` của `SalesDocumentNormalizer` **không kiểm tra tràn**, nên một giá
   trị upstream phi lý tạo ra `sizeBytes` **âm**. → **Đã sửa sau đó, xem bên dưới.**
7. Response body của upstream được đọc **không giới hạn kích thước** và **ngoài ngân sách
   Polly** (`HttpCompletionOption.ResponseHeadersRead`). → **Đã sửa sau đó, xem bên dưới.**

### VII.3.2 Sau review — minor #6 và #7

**Phán quyết.** Xử lý các minor được hoãn **#6 và #7 theo yêu cầu tường minh của người
dùng**, mỗi cái **đỏ-sang-xanh** — **vì sao:** người dùng yêu cầu cụ thể hai cái đó, và
**việc viết test cho #7 cho thấy phát hiện đó đã bị mô tả nhẹ đi** (một phép đọc body không
giới hạn **không chỉ phình bộ nhớ, nó không bao giờ trả về**; tiến trình test phải **bị giết
sau 70 giây**) — **giá nếu sai:** một body upstream lớn hơn 4 MiB hoặc chậm hơn 2 giây sẽ
**bị báo là một thất bại của nguồn** thay vì được đọc.

- **Đã sửa minor #6** (kilobyte × 1024 không kiểm tra tràn tạo ra `sizeBytes` âm) —
  `Reports_a_size_it_cannot_represent_as_unknown_rather_than_a_negative_number` ĐỎ→XANH,
  suite **108/108**.
- **Đã sửa minor #7** (đọc body upstream không giới hạn kích thước và ngoài ngân sách
  Polly) — `Reports_a_body_that_never_stops_arriving` /
  `Reports_a_body_that_trickles_past_its_budget` **ĐỎ (treo, bị giết ở 70 giây)** →XANH,
  suite **108/108**.
