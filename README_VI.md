# Community Script Hook V .NET Core

# [English](README.md) | Tiếng Việt

## Giới thiệu
- Được phát triển dựa trên [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) và [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced), Community Script Hook V .NET Core là một thiết kế hoàn toàn mới mang đến sự hỗ trợ mod tốt hơn bao giờ hết trên nền tảng .NET Core hiện đại.
- Các thành phần .NET được xây dựng trên C# 14 và F# 10 mới nhất nhằm đảm bảo tính tương thích lâu dài nhất có thể với các bản phát hành .NET Core trong tương lai.
- Đảm bảo không có bản build không an toàn (unsafe).

## Các thành phần
### CoreCLRHostLoader (Trình tải máy chủ CoreCLR của Script Hook V)
- Nền tảng cốt lõi dành cho .NET Core.
- Khả năng mở rộng .NET Core (bao gồm cả các phiên bản .NET Core Preview).
- Hỗ trợ đầy đủ Visual Basic, F# và C# (dựa trên Runtime có sẵn trên máy tính của bạn). Đối với các modder sử dụng F#, cần phải có `FSharp.Core` để chạy.
- Có khả năng thay thế cho bộ não trung tâm mà không cần viết lại (trong trường hợp thay thế Script Hook V .NET Core).

### CommunityScriptHookVDotNetCore (Script Hook V .NET Core)
- Chịu trách nhiệm về vòng đời của các bản mod, tần suất tick (tickrates), v.v. Nghe có vẻ quen thuộc phải không? Nhưng giờ đây nó không còn chứa mọi thứ gộp chung như trước nữa.

### Alloc8orStandardNatives (Các tệp thực thi Native chuẩn của Alloc8or)
- Dựa trên trang web chứa các tệp thực thi native của Alloc8or.
- Bạn không cần phải liệt kê thủ công tất cả các mã chỉ để cập nhật danh mục native; chỉ cần chạy tệp PowerShell được tạo sẵn và mọi thứ sẽ được hoàn tất. Nó hoàn toàn đồng bộ với trang web.
- Mã 64-bit Native Executable được nén với thuật toán Brotli nhằm tiết kiệm kích thước sau cùng.

### ScriptHookInput (Đầu vào Script Hook V)
- Dựa trên trang web của FiveM, bao gồm 2 loại đầu vào: đầu vào trong game (game input) và đầu vào từ thiết bị (device input).
- Đầu vào trong game như `INPUT_TALK`, `INPUT_CONTEXT`, v.v. là một phần của game, bạn chỉ cần vào cài đặt để thay đổi.
- Đầu vào từ thiết bị như tay cầm (controller), bàn phím và chuột.

### Script4Reload (Công cụ tải lại của Script4)
- Vẫn giữ nguyên khả năng tải lại (reload) mà các modder .NET Framework đã quen thuộc. Tuy nhiên, có 2 chế độ: 1 là Thủ công (Manual) như trước đây, 2 là Đồng bộ hóa (Synchronized) — trong đó bạn không thể sử dụng phím tải lại thủ công vì quá trình này được thực hiện hoàn toàn tự động.
- Không còn hiện tượng treo game (game freeze), do việc tải lại giờ đây đã được chuyển sang kiểu bất đồng bộ (asynchronous).
- Không còn tình trạng vét cạn (brute-force) và tải lại toàn bộ cùng một lúc. Đối với các modder, việc giảm bớt khối lượng công việc tải lại sẽ giúp trò chơi hoạt động bền bỉ hơn thay vì bị văng game ngẫu nhiên từ sớm.

## Yêu cầu
### Dành cho Người dùng cuối
- [FSharp.Core](https://www.nuget.org/packages/fsharp.core).
- [.NET Core Runtime](https://dotnet.microsoft.com/en-us/download/dotnet) (tùy thuộc vào bản mod, các Targeting Version có thể khác nhau).

### Dành cho Đồng phát triển (Khuyên dùng vì tui luôn bận rộn)
- [Visual Studio 2026](https://visualstudio.microsoft.com) hoặc [Visual Studio Insiders](https://visualstudio.microsoft.com/insiders).
- [.NET Core SDK](https://dotnet.microsoft.com/en-us/download/dotnet) (chỉ dành cho bản Preview, còn bản Release đã được tích hợp sẵn trong trình cài đặt Visual Studio).

## Question: Tôi có khả năng tiếp tục modding ở SHVDN3 hay không?
- Có, hoàn toàn có thể, nhưng chỉ khi không gặp lỗi IO Exception do trùng lặp nội dung.