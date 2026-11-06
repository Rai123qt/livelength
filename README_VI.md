# LiveLength Manager 2027

Mã nguồn plugin AutoCAD 2027 (.NET 10, C#) để tạo và quản lý MLeader có chiều dài tự cập nhật từ LINE, PLINE, ARC, SPLINE, ELLIPSE, MLINE hoặc DIM. Bảng quản lý có tab ghi chú chiều dài, tab giật cấp GC và tab cài đặt.

Phiên bản mã nguồn: **3.9.9**. Chạy lệnh `LLMGR` trong AutoCAD để mở bảng quản lý.

## Cấu trúc dự án

- `LiveLengthManager/`: mã nguồn C# và file dự án `.csproj`.
- `LiveLengthManager2027.bundle/PackageContents.xml`: khai báo plugin cho AutoCAD.
- `FIND_AUTOCAD_2027.ps1`: tìm thư mục cài AutoCAD 2027 trên máy.
- `BUILD.cmd`, `BUILD_AND_INSTALL.cmd`: build, hoặc build rồi cài vào tài khoản Windows hiện tại.
- `INSTALL_AUTOSTART.cmd`, `INSTALL_PORTABLE.ps1`: cài bản DLL đã build hoặc bộ cài đã đóng gói.

`OUT/`, `RELEASE/`, `obj/` và `ErrorReports/` là dữ liệu tạo trên máy, không cần đưa lên GitHub.

## Build và chạy

Yêu cầu Windows 64-bit, AutoCAD 2027 và .NET 10 SDK. Các DLL Autodesk được tham chiếu từ thư mục cài AutoCAD trên máy, không nằm trong repository.

1. Chạy `BUILD.cmd`. Script tự tìm AutoCAD 2027. Nếu cần, truyền đường dẫn: `BUILD.cmd "D:\DuongDan\AutoCAD 2027"`.
2. DLL được tạo tại `OUT/net10.0-windows/LiveLengthManager.dll`.
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

## Lưu trữ trên GitHub

Repository này chứa mã nguồn và script build. Bộ cài ZIP có thể được tải lên mục Releases của GitHub khi cần phát hành cho người dùng; không cần commit các file ZIP build vào lịch sử Git.
