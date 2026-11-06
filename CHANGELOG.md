# Nhật ký thay đổi

Mỗi bản phát hành có một mục `## [X.Y.Z]`. Khi tạo tag `vX.Y.Z`, workflow `release.yml` lấy đúng mục này làm ghi chú của GitHub Release, và nút **Xem thay đổi** trong plugin mở trang đó.

## [3.13.0] - 2026-11-06

### Thay đổi
- Thông báo cập nhật hiện thành **cửa sổ popup** thay cho thanh chèn vào đầu bảng; có **Cập nhật**, **Để sau**, **Bỏ qua bản này**.
- Nội dung thay đổi (changelog) hiện ngay trong popup, không cần mở trình duyệt; vẫn có liên kết "Xem trên GitHub".
- Tab Cài đặt: thêm nút **Xem thay đổi**; dấu ● trên tab Cài đặt khi có bản mới.

### Thêm
- Nút **—** ở góc phải thanh tab: thu nhỏ bảng vào cạnh màn hình (rê chuột vào để mở lại), bấm **□** để giữ bảng luôn mở. L= vẫn tự cập nhật khi thu nhỏ.
- Lệnh `LLMTOGGLE` để ẩn/hiện bảng.

### Sửa
- Tiêu đề bảng luôn hiện đúng số phiên bản (AutoCAD từng giữ lại tiêu đề cũ đã lưu).
- Ghi log `plugin.log` khi không tạo được biểu tượng ở thanh trạng thái hoặc popup, để dễ tìm lỗi.

## [3.12.0] - 2026-11-06

### Kiểm tra
- Bản thử luồng tự cập nhật: chỉ tăng số phiên bản, không thay đổi tính năng so với 3.11.0.

## [3.11.0] - 2026-11-06

### Thêm
- Tự cập nhật qua GitHub Releases: loader nhỏ (`LiveLengthManager.Loader.dll`) nạp bản mới nhất; palette báo khi có bản mới, bấm **Cập nhật** để tải (kiểm tra SHA-256), dùng từ lần mở AutoCAD sau. Bản nạp lỗi tự bị bỏ qua.
- Tab Cài đặt: bật/tắt tự kiểm tra bản mới, nút **Kiểm tra ngay**.
- Cột **Kiểu chữ** và menu chuột phải **Đổi kiểu chữ** TNR (Unicode) ↔ VNROMANS (TCVN3), tự đổi bảng mã nội dung.
- Biểu tượng **L** ở thanh trạng thái AutoCAD để ẩn/hiện bảng; L= vẫn tự cập nhật khi ẩn.
- Tiêu đề palette hiện số phiên bản.

### Sửa
- Chip lọc trạng thái không còn nhảy vị trí khi bấm.
- Cột Đơn vị không còn lựa chọn trống.
- Cột Nội dung không còn hiện mã định dạng MText (`{\fArial|b0…;}`).
- Lỗi văng AutoCAD khi nạp bản thử đầu tiên của 3.11.0.

## [3.10.0] - 2026-11-06

### Thay đổi
- Làm lại tab Chiều dài: nút tạo một cú bấm, chip lọc trạng thái, chấm màu trạng thái, menu chuột phải theo dòng, ẩn/hiện cột, phím tắt F5 / Ctrl+F / Enter, dòng vừa đổi sáng nhẹ.

## [3.9.9] - 2026-11-06

### Thêm
- Mũi tên MLeader bám theo nguồn khi nguồn đổi kích thước hoặc di chuyển (bật/tắt trong Cài đặt).
- Nguồn ARC, SPLINE, ELLIPSE.

### Thay đổi
- Đồng bộ tăng dần và palette chỉ cập nhật các dòng thay đổi: nhanh hơn với bản vẽ lớn.
- Thông báo dòng lệnh dùng tiếng Việt có dấu.

### Sửa
- Xuất CSV mở đúng tiếng Việt và đúng cột trong Excel.

## [3.9.8]

### Thêm
- Liên kết chiều dài cho MText và đồng bộ dòng đang chọn giữa bản vẽ và palette.

## [3.9.7]

- Mã nguồn đầu tiên của LiveLength Manager 2027.
