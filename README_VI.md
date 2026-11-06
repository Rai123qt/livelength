# LiveLength Manager 2027

Mã nguồn plugin AutoCAD 2027 (.NET 10, C#) để tạo và quản lý MLeader có chiều dài tự cập nhật từ LINE, PLINE, ARC, SPLINE, ELLIPSE, MLINE hoặc DIM. Bảng quản lý có tab ghi chú chiều dài, tab giật cấp GC và tab cài đặt.

Phiên bản mã nguồn: **3.12.0**. Chạy lệnh `LLMGR` trong AutoCAD để mở bảng quản lý.

## Cấu trúc dự án

- `LiveLengthManager/`: mã nguồn C# và file dự án `.csproj` của plugin.
- `LiveLengthManager.Loader/`: DLL khởi động nhỏ mà AutoCAD nạp; nó chọn và nạp bản plugin mới nhất (xem phần Cập nhật).
- `.github/workflows/`: `build.yml` kiểm tra build cho mỗi PR; `release.yml` tự phát hành khi đẩy tag `vX.Y.Z`.
- `LiveLengthManager2027.bundle/PackageContents.xml`: khai báo plugin cho AutoCAD.
- `FIND_AUTOCAD_2027.ps1`: tìm thư mục cài AutoCAD 2027 trên máy.
- `BUILD.cmd`, `BUILD_AND_INSTALL.cmd`: build, hoặc build rồi cài vào tài khoản Windows hiện tại.
- `INSTALL_AUTOSTART.cmd`, `INSTALL_PORTABLE.ps1`: cài bản DLL đã build hoặc bộ cài đã đóng gói.

`OUT/`, `RELEASE/`, `obj/` và `ErrorReports/` là dữ liệu tạo trên máy, không cần đưa lên GitHub.

## Build và chạy

Yêu cầu Windows 64-bit, AutoCAD 2027 và .NET 10 SDK. Các DLL Autodesk được tham chiếu từ thư mục cài AutoCAD trên máy, không nằm trong repository.

1. Chạy `BUILD.cmd`. Script tự tìm AutoCAD 2027. Nếu cần, truyền đường dẫn: `BUILD.cmd "D:\DuongDan\AutoCAD 2027"`.
2. DLL được tạo tại `OUT/net10.0-windows/LiveLengthManager.dll` và loader tại `OUT/loader/LiveLengthManager.Loader.dll`.
3. Để thử thủ công, trong AutoCAD gõ `NETLOAD`, chọn DLL trên, sau đó gõ `LLMGR`.
4. Để tự nạp ở lần mở AutoCAD tiếp theo, đóng AutoCAD rồi chạy `BUILD_AND_INSTALL.cmd`.

Không nạp hai bản LiveLength trong cùng một phiên AutoCAD. Hãy lưu bản vẽ và đóng AutoCAD trước khi thay DLL đang dùng.

## Ghi chú sử dụng

- Ghi `L=` trong nội dung MLeader để gắn chiều dài; không có `L=` thì đó là ghi chú thường.
- Có thể chọn đơn vị `m` hoặc `mm` riêng cho từng ghi chú. `mm` hiển thị số nguyên.
- Ghi chú có hai chiều dài dùng hai vị trí `L=` và gắn nguồn thứ hai trong bảng quản lý.
- Nội dung GC được quản lý từ nguồn DIM, LINE, PLINE, ARC, SPLINE, ELLIPSE hoặc MLINE đã đăng ký.
- Ghi chú chỉ tự cập nhật trên máy có cài plugin; bản vẽ vẫn hiển thị giá trị đã lưu khi gửi cho người không cài plugin.
- Khi chuyển MText có `L=` sang MLeader, plugin dò nguồn tại mũi tên; nếu chưa thấy, người dùng chọn LINE/PLINE/ARC/SPLINE/ELLIPSE/MLINE/DIM làm nguồn.
- Khi chọn MLeader LiveLength trong bản vẽ, palette chuyển tới và đánh dấu dòng ghi chú tương ứng.

- VNROMANS dùng bảng mã TCVN3 (ABC), TNR dùng Unicode. Bảng quản lý luôn hiển thị và cho sửa nội dung bằng Unicode; chuột phải vào dòng → **Đổi kiểu chữ** để chuyển qua lại, nội dung được đổi bảng mã tương ứng. TCVN3 không có chữ hoa có dấu nên các chữ đó thành chữ thường khi đổi sang VNROMANS.
- Biểu tượng **L** ở thanh trạng thái AutoCAD dùng để ẩn/hiện bảng. Ẩn hoặc đóng bảng không làm dừng việc tự cập nhật `L=`.

## Cập nhật

AutoCAD nạp `Contents\LiveLengthManager.Loader.dll` trong bundle. Loader chọn bản `LiveLengthManager.dll` mới nhất trong:

- `Contents\app\` (bản do bộ cài chép vào);
- `%AppData%\LiveLengthManager2027\versions\<phiên bản>\` (bản tự tải từ GitHub Releases).

Bản nào nạp lỗi sẽ bị ghi vào `versions\bad.txt` và bỏ qua ở lần sau; loader nạp bản kế trước. Nhật ký ở `%AppData%\LiveLengthManager2027\loader.log`.

Plugin kiểm tra GitHub Releases tối đa 1 lần/ngày (tắt được trong tab Cài đặt). Khi có bản mới, bảng quản lý hiện thông báo; bấm **Cập nhật** để tải về (kiểm tra SHA-256 và số phiên bản), bản mới được dùng từ lần mở AutoCAD sau. Người đang dùng bản trước 3.11 cần cài tay một lần bằng bộ cài 3.11 để có loader.

### Phát hành bản mới

1. Tăng `Version` trong `LiveLengthManager/LiveLengthManager.csproj` và hai chỗ `Version` trong `LiveLengthManager2027.bundle/PackageContents.xml`.
   Thêm mục `## [X.Y.Z]` vào đầu `CHANGELOG.md`; nội dung mục này thành ghi chú của Release.
2. Merge vào `main`.
3. Tạo release với tag trùng phiên bản, ví dụ `v3.12.0` (trang Releases → *Draft a new release*, hoặc `git tag v3.12.0 && git push origin v3.12.0`).
4. Workflow `release.yml` tự build, kiểm tra tag khớp phiên bản và có mục trong `CHANGELOG.md`, đính kèm bộ cài ZIP, `LiveLengthManager.dll` và `.sha256` vào release. Người dùng sẽ thấy thông báo cập nhật.

Không commit file ZIP hay DLL build vào lịch sử Git.
