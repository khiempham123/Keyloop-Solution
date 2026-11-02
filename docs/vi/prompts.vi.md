# Bản ghi prompt

**Bản tiếng Việt** · Keyloop Technical Assessment — Scenario D · Tác giả: Pham Gia Khiem

| | |
|---|---|
| **Bản gốc tiếng Anh** | [`prompts.md`](../../docs/ai-collaboration/prompts.md) — **đây là bản thẩm quyền** |
| **Mục lục bộ tiếng Việt** | [`tai-lieu-tieng-viet.md`](../tai-lieu-tieng-viet.md) |
| **Nội dung** | Chiến lược — 8 giai đoạn prompt và thứ tự của chúng |

> Nếu bản tiếng Việt và bản tiếng Anh khác nhau, **bản tiếng Anh là bản đúng**. Tên lớp,
> tên file, tên test, khóa cấu hình và khối code giữ nguyên tiếng Anh vì chúng là định
> danh trong mã nguồn.
>
> Các tham chiếu dạng **§I.x**, **§III.x**… trỏ sang phần tương ứng trong bộ tài liệu này:
> I = thiết kế, II = tour mã nguồn, III = tra cứu, IV = README, V = nhật ký review,
> VI = prompt, VII = phán quyết, VIII = plan. Mục lục ở link trên.

---

Những prompt **thực sự định hình** bài nộp này, theo thứ tự, kèm những gì nhận lại được và
đã làm gì với chúng. Prompt được viết bằng **tiếng Việt** trong một session Claude Code;
bản gốc tiếng Anh diễn giải lại **ý định** của từng prompt.

Khuôn mẫu mà chúng tuân theo là **có chủ ý** và chính là chiến lược được mô tả ở §IV.7.1:
**không bao giờ hỏi code trước.** Mỗi giai đoạn yêu cầu mô hình **trích xuất**, **chấm
điểm**, **tấn công**, hoặc **biện giải** — và chỉ sau đó mới **sản xuất**. Công việc được
sinh ra trước khi bài toán được chốt là công việc sẽ phải ném đi.

---

## Giai đoạn 1 — Trích xuất, không diễn giải

> Đọc đề bài assessment và rút ra các yêu cầu. **Đừng bắt đầu thiết kế.**

Sinh ra một bản kiểm kê yêu cầu, và — **quan trọng hơn** — **tách biệt** tiêu chí nghiệm thu
*tường minh* của từng scenario khỏi các yêu cầu *xuyên suốt* trong "The Challenge Structure"
và "Deliverables" — **nhóm thứ hai chính là chỗ mà yêu cầu database-bền-vững đang ẩn mình**.
Giữ việc trích xuất tách khỏi việc thiết kế chính là thứ làm cho việc tìm ra nó **trở nên
khả thi**.

## Giai đoạn 2 — Chấm điểm các phương án theo rubric, kèm một khuyến nghị

> Phân tích cả bốn scenario và đánh giá cái nào phù hợp với trình độ của tôi. **Đưa lý lẽ,
> không đưa phán quyết.**

Sinh ra bản so sánh dẫn tới Scenario D: độ phức tạp hiện thực, nguy cơ phình phạm vi, và
mỗi scenario để lại bao nhiêu chất liệu cho tài liệu thiết kế hệ thống. Xem nhật ký review
**D-1**.

## Giai đoạn 3 — Tấn công chính lựa chọn của mình

> Đọc lại Phần 2 **theo kiểu đối kháng**. Scenario D **không thỏa mãn được điều gì về mặt
> cấu tạo?**

**Prompt giá trị nhất của cả quy trình.** Sinh ra phát hiện rằng một database bền vững là
**bắt buộc** cho bài nộp backend trong khi Scenario D **không lưu gì của riêng nó**. Xem
nhật ký review **D-2**.

## Giai đoạn 4 — Lập kế hoạch dựa trên một ràng buộc thật

> Tôi có sáu ngày, và đây là ba deliverable. **Thứ tự công việc là gì, và cái gì bị cắt đầu
> tiên nếu tôi bị trễ?**

Sinh ra kế hoạch theo từng ngày, danh sách **"không được cắt"** (fan-out song song, lỗi một
phần, test, database, tài liệu thiết kế, tường thuật AI, video — **mỗi cái map tới một dòng
trong khung đánh giá**), và thứ tự cắt cho mọi thứ còn lại. Danh sách cắt đó **trở thành §9
Non-Goals** trong tài liệu thiết kế: **một lựa chọn được nêu kèm lý do thì đọc ra là phán
đoán, trong khi cùng khoảng trống đó mà im lặng thì đọc ra là sơ suất.**

## Giai đoạn 5 — Tìm chế độ lỗi trong chính thiết kế của mình

> Đây là thiết kế fan-out. **Tìm bug.**

Sinh ra phát hiện `Task.WhenAll`. Xem nhật ký review **D-3** — nó làm đổi **chính kiểu dữ
liệu cốt lõi** (`UpstreamResult<T>`) chứ không phải một dòng code.

## Giai đoạn 6 — Biện giải mọi công nghệ, kể cả những cái bị loại

> Với **mỗi** công nghệ, hãy nêu phương án anh **đã loại** và **vì sao**.

Sinh ra bảng công nghệ ở §I.6. Việc **bắt buộc** phải nêu một phương án bị loại cho **từng
dòng** chính là thứ làm lộ ra vấn đề licence của FluentAssertions (**D-4**) và câu hỏi
integration test nên trỏ vào đâu (**D-6**) — **không cái nào trong hai cái đó sẽ xuất hiện
từ một prompt chỉ hỏi "nên dùng gì"**.

## Giai đoạn 7 — Tài liệu thiết kế, rồi plan, rồi code

> Viết tài liệu thiết kế hệ thống. **Rồi** một implementation plan với các test được viết
> rõ ra theo từng task.

**Trình tự có chủ ý.** Viết thiết kế trước plan, và plan trước code, nghĩa là mô hình hiện
thực **dựa trên một đặc tả mà TÔI sở hữu** thay vì tự bịa ra một cái — và đó là thứ cho
phép **từng task trong plan được kiểm chứng độc lập**, thay vì phải review một đống code
sinh ra **mờ đục như một khối**.

## Giai đoạn 8 — Hiện thực

Prompt được thêm vào khi Task 1–8 được thực thi, **mỗi cái đi kèm bước kiểm chứng** đã kiểm
tra đầu ra của nó.
