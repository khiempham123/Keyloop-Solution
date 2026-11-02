# Tài liệu tiếng Việt — mục lục

**Keyloop Technical Assessment — Scenario D (Domain: Operate)** · Tác giả: Pham Gia Khiem

Mỗi tài liệu tiếng Anh của repo có **một file tiếng Việt kèm bên cạnh**, để mở song song
hai bản mà theo dõi. Trước đây tất cả nằm trong một file 3.260 dòng; giờ đã tách ra, và
**mỗi bản dịch chỉ tồn tại ở đúng một chỗ** nên không có hai bản lệch nhau.

| # | Bản tiếng Việt | Bản gốc tiếng Anh | Nội dung |
|---|---|---|---|
| I | [Tài liệu thiết kế hệ thống](vi/system-design.vi.md) | [`system-design.md`](system-design.md) | **Vì sao** — kiến trúc, đánh đổi, 11 giả định, 6 phạm vi loại trừ |
| II | [Hướng dẫn đọc mã nguồn](vi/code-guide.vi.md) | [`code-guide.md`](code-guide.md) | **Định hướng** — tour mã nguồn 10 phút |
| III | [Sổ tay tra cứu cho lập trình viên](vi/developer-guide.vi.md) | [`developer-guide.md`](developer-guide.md) | **Tra cứu** — file, luồng, mọi con số, cách mở rộng |
| IV | [README: chạy và xem nó suy giảm](vi/README.vi.md) | [`../README.md`](../README.md) | **Chạy nó** — quickstart, cấu hình, API, bộ test, tường thuật AI |
| V | [Nhật ký review AI](vi/review-log.vi.md) | [`ai-collaboration/review-log.md`](ai-collaboration/review-log.md) | **Bằng chứng** — 22 entry: AI đề xuất gì, đổi gì, vì sao quan trọng |
| VI | [Bản ghi prompt](vi/prompts.vi.md) | [`ai-collaboration/prompts.md`](ai-collaboration/prompts.md) | **Chiến lược** — 8 giai đoạn prompt và vì sao theo thứ tự đó |
| VII | [Sổ phán quyết hiện thực](vi/implementation-ledger.vi.md) | [`ai-collaboration/implementation-ledger.md`](ai-collaboration/implementation-ledger.md) | **Phán quyết** — mọi quyết định khi chạy plan, kèm giá nếu sai |
| VIII | [Implementation plan](vi/implementation-plan.vi.md) | [`superpowers/plans/2026-10-29-scenario-d-document-viewer.md`](superpowers/plans/2026-10-29-scenario-d-document-viewer.md) | **Plan** — 9 task TDD, kèm 9 chỗ plan khác với code cuối |

**Cả 8 tài liệu tiếng Anh của repo đều đã được dịch.**

---

## Nên đọc theo thứ tự nào

| Anh đang ở đâu | Đọc gì |
|---|---|
| Vừa clone repo về, muốn chạy thử | **IV** (README) rồi **II** (tour mã nguồn) |
| Muốn hiểu vì sao kiến trúc lại như vậy | **I** (thiết kế) |
| Sắp sửa đổi code | **III** (tra cứu) — có bảng mọi con số và 10 bất biến không được phá |
| Muốn biết AI đã sai ở đâu và bị bắt thế nào | **V** (nhật ký review) — hai entry nặng nhất là I-8 và I-14 |
| Muốn kiểm tra các quyết định đã đưa ra thay mình | **VII** (phán quyết) |

---

## Hai quy ước xuyên suốt

**Bản tiếng Anh là bản thẩm quyền.** Bộ tiếng Anh là bài nộp cho Keyloop. Bản tiếng Việt
để đọc hiểu nhanh — nếu hai bản khác nhau, bản tiếng Anh là bản đúng.

**Định danh giữ nguyên tiếng Anh.** Tên lớp, tên file, tên test, khóa cấu hình, lệnh shell
và khối code không dịch, vì chúng là định danh trong mã nguồn — dịch `DocumentAggregationService`
sẽ làm tài liệu không grep được vào code. Output test của máy cũng giữ nguyên, vì dịch nó
thì nó thôi làm bằng chứng.

---

## Phụ lục — Thuật ngữ đối chiếu

| Tiếng Anh | Dùng trong bộ tài liệu này | Ghi chú |
|---|---|---|
| upstream / system of record | hệ thống upstream / hệ thống nguồn | Sales System và Service System |
| fan-out | fan-out (gọi song song) | Giữ nguyên vì là tên kỹ thuật |
| partial result | kết quả một phần | Cờ `partial` trong response |
| stale | dữ liệu cũ (`stale`) | Giữ nguyên giá trị enum |
| degraded | suy giảm | Vẫn phục vụ được nhưng không đầy đủ |
| circuit breaker | circuit breaker | Giữ nguyên |
| budget | ngân sách (thời gian) | Giới hạn thời gian cho một thao tác |
| correlation ID | correlation ID | Giữ nguyên |
| PII | dữ liệu cá nhân / PII | VIN được coi là PII |
| fire-and-forget | phóng rồi quên (fire-and-forget) | Ghi audit không chờ kết quả |
| seam | đường may | Điểm chèn abstraction để test/mở rộng |
| invariant | bất biến | Điều kiện không được phá |
| normaliser | normaliser / bộ chuẩn hóa | Chuyển schema upstream về mô hình hợp nhất |
