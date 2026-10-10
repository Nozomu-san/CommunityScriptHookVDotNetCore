# Community Script Hook V .NET Core

# [English](README.md) | Tiếng Việt

## Giới thiệu
- Được phát triển dựa trên [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) và [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced), Community Script Hook V .NET Core là một thiết kế hoàn toàn mới mang đến sự hỗ trợ mod tốt hơn bao giờ hết trên nền tảng .NET Core hiện đại.
- Đảm bảo không có bản build không an toàn (unsafe).

## Vai trò
- Community Script Hook V .Net Core được phát triển với mô hình một chiều gồm 6 cơ sở hạ tầng: *Host (Máy chủ) - Central Brain (Bộ não trung tâm) - Extended Contents (Nội dung mở rộng) - Dynamic Library (Thư viện động) - Inherited Class (Lớp kế thừa) - Human Readables (Dữ liệu con người đọc được)*.
- Dựa trên vai trò, không dựa trên ngôn ngữ. Điều này có nghĩa là nếu bạn viết cho Community Script Hook V .Net Core bằng bất kỳ ngôn ngữ nào, bạn chỉ cần các hợp đồng (contracts) và runtime phù hợp để được nhận diện.
- Các cơ sở hạ tầng phía sau hoàn toàn không biết về sự tồn tại của các cơ sở hạ tầng phía trước, nhưng phần phía trước lại tạo điều kiện để phần phía sau tồn tại. Điều này có nghĩa là, việc sửa đổi từ bất kỳ cơ sở hạ tầng nào thường sẽ không ảnh hưởng đến các phần đứng trước nó.
- Bạn chỉ cần các thành phần bắt buộc để vận hành.
- Vì là mô hình một chiều, các cơ sở hạ tầng phía sau có nhiều yêu cầu khác nhau đối với phần phía trước. Nếu không, hệ sinh thái sẽ không thể hoạt động.
- Sụp đổ Domino: Để ngăn chặn mã zombie hoặc các thực thi không xác định, thiết kế nhánh được tạo ra để vận hành. Khi các gốc thuộc về một cơ sở hạ tầng sụp đổ, bất kỳ phần nào ở phía sau có yêu cầu phần bị sụp đổ đó sẽ bị ảnh hưởng. Tuy nhiên, nếu các phần phía sau không yêu cầu phần bị sụp đổ, chúng vẫn hoạt động bình thường.
- Thiết kế dựa trên hợp đồng: Để nhận diện các cơ sở hạ tầng khác, các hợp đồng được tạo ra để nhận diện thay vì thông qua việc đặt tên, do đó việc đặt tên không còn quan trọng nữa.

### Host (Máy chủ)
- Độ quan trọng: Bắt buộc
- Số lượng tối đa: 1
- Vai trò: Hoạt động như một Powerhouse, cung cấp sức mạnh cho .NET Core và một số lệnh thực thi cấp thấp từ Script Hook V gốc, phục vụ mục đích viết một lần, tái sử dụng nhiều lần. Vai trò chính ở cấp độ này chỉ là gọi các thực thi cấp thấp và lưu trữ .NET Core.

### Central Brain (Bộ não trung tâm)
- Độ quan trọng: Bắt buộc
- Số lượng tối đa: 1
- Vai trò: Sử dụng tần suất tick (tickrate) để quản lý vòng đời của các lớp kế thừa. Nếu các lớp gặp lỗi, chúng sẽ bị gỡ bỏ và không còn khả dụng trong vòng đời. Bắt đầu với thư mục scripts4, bất kỳ tệp .dll nào không có lớp kế thừa nào sẽ được nhận diện là thư viện, 1 hoặc nhiều lớp sẽ được nhận diện là lớp kế thừa. Tuy nhiên, hệ sinh thái sụp đổ được quản lý dựa trên thiết kế lớp, thay vì toàn bộ nội dung từ tệp .dll bị lỗi. Nếu các lớp bị lỗi, bất kỳ lớp nào khác không yêu cầu từ lớp bị sụp đổ vẫn hoạt động bình thường. Tương tự với extension tại thư mục extensions.

### Extended Contents (Nội dung mở rộng)
- Độ quan trọng: Dựa trên mã code
- Số lượng tối đa: Không giới hạn
- Vai trò: Các nội dung mở rộng trước đây từng thấy ở [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) và [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced), giờ đây đã được chia thành nhiều nội dung và loại khác nhau để vận hành.

### Dynamic Library (Thư viện động)
- Độ quan trọng: Dựa trên mã code
- Số lượng tối đa: Dựa trên mã code
- Vai trò: Vẫn như trước đây với [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) và [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced), đây là nơi bạn muốn sử dụng chung các nội dung.

### Inherited Class (Lớp kế thừa)
- Độ quan trọng: Dựa trên mã code
- Số lượng tối đa: Không giới hạn
- Vai trò: Nội dung chính trong modding, vẫn như trước đây với [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) và [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced).

### Human Readables (Dữ liệu con người đọc được)
- Độ quan trọng: Tùy chọn
- Số lượng tối đa: Dựa trên mã code
- Vai trò: Các tệp có thể đọc được như json, log, ini, v.v... mà mod có thể đọc để vận hành hoặc chỉ ở dạng chỉ đọc.

## Các thành phần

### CoreCLRHostLoader (Trình tải máy chủ CoreCLR của Script Hook V)
- Ngôn ngữ mục tiêu: C++ 23
- Mô tả:
* Hoàn toàn dựa trên runtime, nghĩa là hỗ trợ đầy đủ Visual Basic, F# và C# (dựa trên những gì có sẵn trên máy tính của bạn). Đối với các modder sử dụng F#, cần phải có `FSharp.Core` để chạy. Cũng có thể sử dụng với các phiên bản preview.
* Có sẵn khả năng thay thế bộ não trung tâm trong tương lai mà không cần viết lại (trong trường hợp thay thế Central Brain).

### CommunityScriptHookVDotNetCore (Script Hook V .NET Core)
- Ngôn ngữ mục tiêu: C# 15 Preview
- Mô tả: Chịu trách nhiệm quản lý vòng đời của các bản mod, tần suất tick (tickrate), v.v.

### Alloc8orStandardNatives (Các tệp thực thi Native chuẩn của Alloc8or)
- Ngôn ngữ mục tiêu: C# 15 Preview & Embedded C# 15 Preview trên PowerShell
- Mô tả:
* Dựa trên trang web chứa các tệp thực thi native của Alloc8or. Giờ bạn có thể phát triển các tệp thực thi native của mình dựa trên website ở [Legacy](https://alloc8or.re/gta5/nativedb) hoặc [Enhanced](https://alloc8or.re/gta5/nativedb/enhanced), do đó không còn chuyện tự khai báo thủ công hay sử dụng trực tiếp nữa.
* Bạn không cần phải liệt kê tất cả các mã chỉ để cập nhật danh mục native; chạy tệp PowerShell đã build và mọi thứ sẽ hoàn tất. Nó hoàn toàn đồng bộ với trang web.
* Mã 64-bit Native Executable được nén với thuật toán Brotli nhằm tiết kiệm kích thước bản build.
* Để cập nhật danh mục, bạn cần có [PowerShell](https://apps.microsoft.com/detail/9mz1snwt0n5d) hoặc [PowerShell Preview](https://apps.microsoft.com/detail/9p95zzktnrn4) để thực thi.

### ScriptHookInput (Đầu vào Script Hook V)
- Ngôn ngữ mục tiêu: F# 7
- Mô tả:
* Dựa trên trang web của FiveM, bao gồm 2 loại đầu vào: đầu vào game (game input) và đầu vào thiết bị (device input).
* Đầu vào trong game như `INPUT_TALK`, `INPUT_CONTEXT`, v.v. là một phần của game, bạn chỉ cần vào cài đặt để thay đổi.
* Đầu vào thiết bị như tay cầm (controller), bàn phím và chuột.

### Script4Reload (Công cụ tải lại của Script4)
- Ngôn ngữ mục tiêu: F# 7
- Mô tả:
* Vẫn giữ nguyên khả năng tải lại (reload) quen thuộc từ [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) và [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced). Tuy nhiên, có 2 chế độ: 1 là Thủ công (Manual) như trước đây, 2 là Đồng bộ hóa (Synchronized) — trong đó bạn không thể sử dụng phím tải lại thủ công mà quá trình này được thực hiện hoàn toàn tự động.
* Không còn hiện tượng treo game (game freeze), do việc tải lại giờ đây đã được chuyển sang kiểu bất đồng bộ (asynchronous).
* Không còn tình trạng vét cạn (brute-force) và tải lại toàn bộ cùng một lúc. Đối với các modder, việc giảm bớt khối lượng công việc tải lại sẽ giúp trò chơi hoạt động bền bỉ hơn thay vì bị văng ngẫu nhiên từ sớm.

### LocalNativeMemories (Bộ nhớ Pool & Trình giải quyết Native cấp thấp)
- Ngôn ngữ mục tiêu: C# 15 Preview
- Mô tả: Trả lời cho những gì có trong game, chẳng hạn như số lượng object, entity, vehicle và ped, đồng thời là phương án dự phòng nếu native call không tồn tại.

### CEventGenerator (Trình xuất sự kiện mã máy)
- Ngôn ngữ mục tiêu: C++ 23
- Mô tả: Cơ chế đồng bộ hóa tickrate hoặc độ trễ (latency) dạng polling được thay thế hoàn toàn bằng cơ chế dựa trên sự kiện (event-based), vận hành thông qua mã máy, giúp loại bỏ chuyện gây nghẽn hiệu năng.

### LowLevelEvents (Trình giải quyết sự kiện cấp thấp)
- Ngôn ngữ mục tiêu: C# 15 Preview
- Mô tả: Trích xuất và chuyển đổi các CEvent cấp thấp từ mã máy sang các chuẩn trung gian mà các dự án .NET Core sử dụng, đồng thời xác định rõ sự kiện đó thuộc về đâu.

### StandardGameOperations (Bảng mã chuẩn)
- Ngôn ngữ mục tiêu: C# 15 Preview
- Mô tả: Cách dễ nhất để viết mã. Bằng cách sử dụng công cụ này, bạn sẽ bỏ qua được rất nhiều thao tác viết rườm rà như khai báo thủ công các native call, khai báo cục bộ cơ sở dữ liệu game, v.v., thậm chí cả các chuỗi thao tác native call phức tạp.

### LocalUserDebug (Trình log trong game)
- Ngôn ngữ mục tiêu: C# 15 Preview
- Mô tả: Vẫn là log nội dung, nhưng ở trong game, thay vì phải đọc liên tục file bên ngoài liên tục.

## Yêu cầu

### Dành cho Người dùng cuối
- Bạn chỉ cần lựa chọn 1 trong 2 giải pháp sau để các thành phần bắt buộc hoạt động:

1. Giải pháp thành phần nhỏ:
* [FSharp.Core](https://www.nuget.org/packages/fsharp.core) (dành cho dự án F#, bao gồm ScriptHookInput & Script4Reload).
* [.NET Host](https://www.nuget.org/packages/Microsoft.NETCore.App.Host.win-x64).
* [.NET Core Runtime](https://dotnet.microsoft.com/en-us/download/dotnet) (tùy thuộc vào target dựa trên yêu cầu).

2. Giải pháp tất cả trong một (All-in-one): [.NET SDK](https://dotnet.microsoft.com/en-us/download/dotnet) (chỉ yêu cầu đối với các bản Preview, đối với bản Release thì đã được tích hợp sẵn trong Visual Studio Installer).

### Dành cho Đồng phát triển
- [Visual Studio 2026](https://visualstudio.microsoft.com) hoặc [Visual Studio Insiders](https://visualstudio.microsoft.com/insiders).
- [.NET SDK](https://dotnet.microsoft.com/en-us/download/dotnet) (chỉ yêu cầu đối với bản Preview, đối với bản Release đã có sẵn trong Visual Studio Installer).

## Cài đặt

### Dành cho Người dùng cuối
- Giải pháp thành phần nhỏ:
1. Cài đặt [.NET Core Runtime](https://dotnet.microsoft.com/en-us/download/dotnet).
2. Tải `FSharp.Core.dll` từ [FSharp.Core](https://www.nuget.org/packages/fsharp.core).
3. Tải `nethost.dll` từ [.NET Host](https://www.nuget.org/packages/Microsoft.NETCore.App.Host.win-x64).
4. Di chuyển `FSharp.Core.dll` và `nethost.dll` vào thư mục gốc của tựa game GTA V.
- Giải pháp tất cả trong một: Chỉ cần cài đặt [.NET SDK](https://dotnet.microsoft.com/en-us/download/dotnet). SDK đã bao gồm sẵn `FSharp.Core.dll`, `nethost.dll` và runtime cần thiết để tải.

### Dành cho Đồng phát triển
1. Cài đặt [Visual Studio 2026](https://visualstudio.microsoft.com) hoặc [Visual Studio Insiders](https://visualstudio.microsoft.com/insiders).
2. Tích chọn ".NET Desktop development" và chọn ít nhất 2 thành phần bắt buộc: *Development tools for .NET* & *F# desktop language support*.
3. Tích chọn "Desktop development with C++" và chọn ít nhất 2 thành phần bắt buộc: *MSVC Build Tools for x64/x86* & *Windows 11 SDK* (tùy theo lựa chọn của bạn).
4. Mở phần *Individual components* -> *Code tools* -> *Git for Windows*.
5. Tiến hành cài đặt IDE.
6. Mở IDE.
7. Chọn `Clone a repository`, dán đường dẫn sau vào ô *Repository location*:
```text
https://github.com/Nozomu-san/CommunityScriptHookVDotNetCore
```
8. Nhấn `Clone`.
9. Mở tệp `CommunityScriptHookVDotNetCore.slnx`.
10. Hoàn tất. Bạn đã sẵn sàng bắt đầu!

## Câu hỏi: Tôi có thể tiếp tục mod trên [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) hoặc [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced) không?
- Có. Bạn hoàn toàn có thể tiếp tục mod trên các API đó.