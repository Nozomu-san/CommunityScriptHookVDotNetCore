# Community Script Hook V .NET Core

# [English](README.md) | Tiếng Việt

## Giới thiệu
- Được phát triển dựa trên [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) và [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced), Community Script Hook V .NET Core là một thiết kế hoàn toàn mới mang đến sự hỗ trợ mod tốt hơn bao giờ hết trên nền tảng .NET Core hiện đại.
- Các thành phần .NET được xây dựng trên C++ 26, C# 15, F# 11 và C# 15 Nhúng trên PowerShell 7.7 Preview, nhằm đảm bảo tính tương thích lâu dài nhất có thể với các bản phát hành .NET Core trong tương lai.
- Đảm bảo không có bản build không an toàn (unsafe).

## Vai trò
- Community Script Hook V .Net Core được phát triển với mô hình một chiều gồm 6 cơ sở hạ tầng: *Chủ nhà - Bộ não trung tâm - Nội dung mở rộng - Thư viện động - Lớp kế thừa - Dữ liệu con người đọc được*.
- Dựa trên vai trò, không dựa trên ngôn ngữ. Điều này có nghĩa là nếu bạn viết cho Community Script Hook V .Net Core bằng bất kỳ ngôn ngữ nào, bạn chỉ cần các hợp đồng và runtime phù hợp để được nhận diện.
- Các cơ sở hạ tầng phía sau hoàn toàn không biết về sự tồn tại của các cơ sở hạ tầng phía trước, nhưng phần phía trước lại tạo điều kiện để phần phía sau tồn tại. Điều này có nghĩa là, việc sửa đổi từ bất kỳ cơ sở hạ tầng nào thường sẽ không ảnh hưởng đến các phần đứng trước nó.
- Host và Central Brain mỗi phần chỉ có 1 thành viên, nhưng như vậy là đủ để vận hành toàn bộ hệ sinh thái .NET Core trên GTA V.
- Vì là mô hình một chiều, các cơ sở hạ tầng phía sau có nhiều yêu cầu khác nhau đối với phần phía trước. Nếu không, hệ sinh thái sẽ không thể hoạt động.
- Sụp đổ Domino: Để ngăn chặn mã zombie hoặc các thực thi không xác định, thiết kế nhánh được tạo ra để vận hành. Khi các gốc thuộc về một cơ sở hạ tầng sụp đổ, bất kỳ phần nào ở phía sau có yêu cầu phần bị sụp đổ đó sẽ bị ảnh hưởng. Tuy nhiên, nếu các phần phía sau không yêu cầu phần bị sụp đổ, chúng vẫn hoạt động bình thường.
- Thiết kế dựa trên hợp đồng: Để nhận diện các cơ sở hạ tầng khác, các hợp đồng được tạo ra để nhận diện thay vì thông qua việc đặt tên, do đó việc đặt tên không còn quan trọng nữa.

### Máy chủ
- Độ quan trọng: Bắt buộc
- Vai trò: Hoạt động như một cỗ máy cung cấp sức mạnh cho .NET Core và một số thực thi cấp thấp từ Script Hook V gốc, phục vụ mục đích viết một lần, tái sử dụng nhiều lần. Vai trò chính ở cấp độ này chỉ là gọi các thực thi cấp thấp và lưu trữ .NET Core.

### Bộ não trung tâm
- Độ quan trọng: Bắt buộc
- Vai trò: Sử dụng tần suất tick để quản lý vòng đời của các lớp kế thừa. Nếu các lớp gặp lỗi, chúng sẽ bị gỡ bỏ và không còn khả dụng trong vòng đời. Bắt đầu với thư mục scripts4, bất kỳ tệp .dll nào không có lớp kế thừa nào sẽ được nhận diện là thư viện; 1 hoặc nhiều lớp sẽ được nhận diện là lớp kế thừa. Tuy nhiên, hệ sinh thái sụp đổ được quản lý dựa trên thiết kế lớp, thay vì toàn bộ nội dung từ tệp .dll bị lỗi. Nếu các lớp bị lỗi, bất kỳ lớp nào khác không yêu cầu từ lớp bị sụp đổ vẫn hoạt động bình thường.

### Nội dung mở rộng
- Độ quan trọng: Dựa trên mã code
- Vai trò: Các nội dung mở rộng trước đây từng thấy ở [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) và [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced), giờ đây đã được chia thành nhiều nội dung và loại khác nhau để vận hành.

### Thư viện động
- Độ quan trọng: Dựa trên mã code
- Vai trò: Vẫn như trước đây với [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) và [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced), đây là nơi bạn muốn sử dụng chung các nội dung.

### Lớp kế thừa
- Độ quan trọng: Dựa trên mã code
- Vai trò: Nội dung chính trong modding, vẫn như trước đây với [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) và [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced).

### Dữ liệu con người đọc được
- Độ quan trọng: Tùy chọn
- Vai trò: Các tệp có thể đọc được như json, log, ini, v.v... mà mod có thể đọc để vận hành hoặc chỉ ở dạng chỉ đọc.

## Các thành phần

### CoreCLRHostLoader (Trình tải máy chủ CoreCLR của Script Hook V)
- Hoàn toàn dựa trên runtime, nghĩa là hỗ trợ đầy đủ Visual Basic, F# và C# (dựa trên những gì có sẵn trên máy tính của bạn). Đối với các modder sử dụng F#, cần phải có FSharp.Core để chạy. Cũng có thể sử dụng với các phiên bản preview.
- Có khả năng thay thế bộ não trung tâm trong tương lai mà không cần viết lại (trong trường hợp thay thế Central Brain).

### CommunityScriptHookVDotNetCore (Script Hook V .NET Core)
- Chịu trách nhiệm về vòng đời của các bản mod, tần suất tick (tickrates), v.v.

### Alloc8orStandardNatives (Các tệp thực thi Native chuẩn của Alloc8or)
- Dựa trên trang web chứa các tệp thực thi native của Alloc8or. Giờ bạn có thể phát triển các tệp thực thi native của mình dựa trên website ở [Legacy](https://alloc8or.re/gta5/nativedb) hoặc [Enhanced](https://alloc8or.re/gta5/nativedb/enhanced), do đó không còn chuyện tự khai báo thủ công hay sử dụng trực tiếp nữa.
- Bạn không cần phải liệt kê tất cả các mã chỉ để cập nhật danh mục native; chạy tệp PowerShell đã build và mọi thứ sẽ hoàn tất. Nó hoàn toàn đồng bộ với trang web.
- Mã 64-bit Native Executable được nén với thuật toán Brotli nhằm tiết kiệm kích thước bản build.
- Để cập nhật danh mục, bạn cần có [PowerShell](https://apps.microsoft.com/detail/9mz1snwt0n5d) hoặc (PowerShell Preview)[https://apps.microsoft.com/detail/9p95zzktnrn4] để thực thi.

### ScriptHookInput (Đầu vào Script Hook V)
- Dựa trên trang web của FiveM, bao gồm 2 loại đầu vào: đầu vào game (game input) và đầu vào thiết bị (device input).
- Đầu vào trong game như `INPUT_TALK`, `INPUT_CONTEXT`, v.v. là một phần của game, bạn chỉ cần vào cài đặt để thay đổi.
- Đầu vào thiết bị như tay cầm (controller), bàn phím và chuột.

### Script4Reload (Công cụ tải lại của Script4)
- Vẫn giữ nguyên khả năng tải lại (reload) từ [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) và [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced) mà modder đã quen thuộc. Tuy nhiên, có 2 chế độ. 1 là Thủ công (Manual) như trước đây, 2 là Đồng bộ hóa (Synchronized) — trong đó bạn không thể sử dụng phím tải lại thủ công. Quá trình này được thiết kế để thực hiện tự động.
- Không còn hiện tượng treo game (game freeze), do việc tải lại giờ đây đã được chuyển sang kiểu bất đồng bộ (asynchronous).
- Không còn tình trạng vét cạn (brute-force) và tải lại toàn bộ cùng một lúc. Đối với các modder, việc giảm bớt khối lượng công việc tải lại sẽ giúp trò chơi hoạt động bền bỉ hơn thay vì bị văng ngẫu nhiên từ sớm.

### LocalNativeMemories
- Cung cấp câu trả lời cho những gì có trong game, chẳng hạn như số lượng object, entity, vehicle và ped.

### CEventGenerator
- Cơ chế đồng bộ hóa tickrate hoặc độ trễ (latency) được thay thế hoàn toàn bằng cơ chế dựa trên sự kiện (event-based), vận hành thông qua mã máy, điều này giúp loại bỏ đáng kể nguyên nhân gây nghẽn hiệu năng.

### LowLevelEvents
- Trích xuất và chuyển đổi các CEvent cấp thấp từ mã máy sang các chuẩn trung gian mà các dự án .NET Core sử dụng. Đó cũng là nơi trả lời cho việc event thuộc về đâu.

### StandardGameOperations
- Cách dễ nhất để viết mã, bằng cách sử dụng công cụ này, bạn sẽ bỏ qua được rất nhiều thao tác viết, chẳng hạn như khai báo thủ công các native call, khai báo cục bộ cơ sở dữ liệu game, v.v., thậm chí cả các chuỗi thao tác native call phức tạp, tất cả đều có ở đây.

## Yêu cầu

### Dành cho Người dùng cuối
- [FSharp.Core](https://www.nuget.org/packages/fsharp.core) (dành cho project F#, ở đây có ScriptHookInput & Script4Reload).
- [.NET Core Runtime](https://dotnet.microsoft.com/en-us/download/dotnet) (tùy thuộc vào target, dựa trên yêu cầu).
- [.NET Host](https://www.nuget.org/packages/Microsoft.NETCore.App.Host.win-x64).

### Dành cho Đồng phát triển (Khuyên dùng vì tôi luôn bận rộn)
- [Visual Studio 2026](https://visualstudio.microsoft.com) hoặc [Visual Studio Insiders](https://visualstudio.microsoft.com/insiders).
- [.NET Core SDK](https://dotnet.microsoft.com/en-us/download/dotnet) (chỉ yêu cầu với bản Preview, còn bản Release đã được tích hợp sẵn trong Visual Studio Installer).

## Câu hỏi: Tôi có thể tiếp tục mod trên SHVDN gốc từ một trong hai phía không?
- Có. Bạn có thể, miễn là bạn không làm xáo trộn với lỗi IO Exception do sự trùng lặp.