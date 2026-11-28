# Trang Store (Tiếng Việt, vi-vn)

Dán các văn bản này vào Partner Center > bản gửi > Store listings > Tiếng Việt. Đây là bản dịch của listing-en.md; trước khi gửi,
người bản ngữ nên xem lại. Ghi chú chứng nhận, xếp hạng độ tuổi và lý do quyền runFullTrust vẫn giữ bằng tiếng Anh (chỉ có trong listing-en.md).

## Tên ứng dụng

Portal AI Host

## Mô tả ngắn (tối đa 1.000 ký tự)

Chạy một mô hình AI cục bộ trên máy tính của bạn và chia sẻ với các ứng dụng Portal của bạn. Chỉ một cú nhấp, ứng dụng chọn mô hình tốt nhất
cho phần cứng của bạn, tải xuống và giữ sẵn sàng. Văn bản của bạn ở lại trên máy tính này: không tài khoản, không đám mây, không đăng ký.

## Mô tả

Portal AI Host mang đến cho máy tính của bạn một mô hình AI riêng, và cho phép các ứng dụng Portal khác dùng nó.

Thay vì mỗi ứng dụng tự tải mô hình hoặc gửi văn bản của bạn lên một dịch vụ đám mây, Portal AI Host giữ một mô hình trên máy tính
của bạn và chia sẻ nó cho các ứng dụng bạn cho phép. Voice Portal và các ứng dụng Portal khác khi đó có thể viết bản tóm tắt, biên bản
và câu trả lời bằng một mô hình lớn và tốt hơn nhiều so với khả năng riêng của chúng.

Thiết lập đơn giản
- Lần chạy đầu tiên, ứng dụng kiểm tra máy tính của bạn (bộ nhớ, card đồ họa, dung lượng đĩa trống) và đề xuất mô hình tốt nhất chạy tốt
  trên máy, kèm giải thích rõ ràng.
- Một cú nhấp để tải xuống. Có thể tạm dừng và tiếp tục tải, và mọi tệp đều được kiểm tra trước khi dùng.
- Không cần cài thêm gì: động cơ đã được tích hợp sẵn. Không Python, không dòng lệnh, không công cụ bổ sung.

Riêng tư theo thiết kế
- Mô hình chạy trên máy tính này. Câu lệnh và câu trả lời không bao giờ rời khỏi máy.
- Không tài khoản và không đăng nhập.
- Không phân tích dữ liệu. Báo cáo sự cố tùy chọn không bao giờ chứa tệp hay nội dung bạn gõ, và bạn có thể tắt chúng.
  Câu lệnh của bạn không bao giờ được lưu hay ghi nhật ký.
- Máy chủ chỉ lắng nghe trên máy tính này (loopback). Các máy khác trong mạng của bạn không thể truy cập được.

Bạn luôn kiểm soát
- Một ứng dụng phải hỏi trước khi có thể dùng máy chủ, và bạn quyết định: cho phép, từ chối hoặc quyết định sau. Bạn có thể thu hồi quyền truy cập
  của một ứng dụng bất cứ lúc nào.
- Máy chủ cho biết ứng dụng đang hỏi đã được xác minh (nhà phát hành của nó) hay chưa, để bạn biết ai đang yêu cầu.
- Chọn nơi lưu mô hình và gỡ những mô hình bạn không còn cần. Máy chủ chỉ xóa các tệp do chính nó tải xuống.
- Khởi động cùng Windows là tùy chọn và tắt cho đến khi bạn bật lên.

Hoạt động với phần cứng của bạn
- Dùng card đồ họa qua Vulkan (AMD, NVIDIA và Intel), và chuyển sang bộ xử lý khi không có GPU phù hợp.
- Giải phóng bộ nhớ khi mô hình không hoạt động, hoặc giữ mô hình được tải nếu bạn thích.

Dành cho nhà phát triển
- Máy chủ cung cấp một API tiêu chuẩn tương thích OpenAI tại 127.0.0.1 (hoàn thành hội thoại có truyền trực tiếp, embedding và danh sách
  mô hình), vì vậy các thư viện máy khách hiện có vẫn hoạt động. Mỗi ứng dụng cần sự chấp thuận của người dùng trước.

Các mô hình được tải từ trang của nhà phát hành trên Hugging Face. Mỗi mô hình có giấy phép riêng; ứng dụng chỉ cung cấp các mô hình
có giấy phép cho phép sử dụng thương mại (Apache 2.0 hoặc MIT).

## Tính năng (Partner Center "Product features", tối đa 20, mỗi mục 200 ký tự)

1. Chạy mô hình AI cục bộ trên máy tính của bạn, không cần đám mây
2. Một mô hình dùng chung cho tất cả ứng dụng Portal của bạn
3. Đề xuất mô hình tốt nhất cho phần cứng của bạn
4. Tải xuống một cú nhấp với tạm dừng, tiếp tục và kiểm tra tệp
5. Dùng card đồ họa (Vulkan) với tự động chuyển sang CPU khi cần
6. Không tài khoản, không đăng nhập, không đăng ký
7. Không phân tích dữ liệu; báo cáo sự cố là tùy chọn và đã được làm sạch; câu lệnh không bao giờ được lưu hoặc ghi nhật ký
8. Chỉ lắng nghe trên máy tính này (loopback)
9. Ứng dụng phải hỏi trước; bạn cho phép, từ chối hoặc thu hồi quyền truy cập
10. Hiển thị ứng dụng đang hỏi có được xác minh hay không
11. Giải phóng bộ nhớ khi không hoạt động, hoặc giữ mô hình được tải
12. Chọn thư mục cho các mô hình
13. Khởi động tùy chọn cùng Windows, với biểu tượng trong khay hệ thống
14. Ô "Thử" để kiểm tra mô hình hoạt động
15. API cục bộ tương thích OpenAI cho nhà phát triển
16. Chia sẻ công bằng khi nhiều ứng dụng yêu cầu cùng lúc
17. Tự phục hồi nếu động cơ dừng

## Từ khóa tìm kiếm (7)

1. AI cục bộ
2. AI ngoại tuyến
3. AI riêng tư
4. LLM
5. mô hình AI
6. mô hình ngôn ngữ
7. máy chủ AI

Không dùng tên sản phẩm của bên thứ ba trong từ khóa (chính sách Store 10.1.3).

## Có gì mới trong phiên bản này (1.0)

Bản phát hành đầu tiên. Tải một mô hình AI cục bộ chỉ với một cú nhấp và chia sẻ nó với các ứng dụng Portal của bạn, riêng tư, ngay trên máy tính của bạn.

## Yêu cầu hệ thống (văn bản trên trang)

Tối thiểu (đặt trong Partner Center > Thuộc tính > Yêu cầu hệ thống):
- Windows 11 (phiên bản 21H2, bản dựng 22000) trở lên, x64 hoặc Arm64.
- Bộ nhớ: 8 GB.
- Dung lượng đĩa trống: 5 GB (bản thân ứng dụng nhỏ; mô hình có kích thước 2 đến 20 GB, các mô hình tùy chọn lớn hơn lên đến khoảng 140 GB).

Khuyến nghị:
- Bộ nhớ: 16 GB trở lên (32 GB trở lên cho các mô hình lớn).
- Đồ họa: card đồ họa hỗ trợ Vulkan với 8 GB bộ nhớ video trở lên (AMD, NVIDIA hoặc Intel).
- Dung lượng đĩa trống: 25 GB.

Yêu cầu hệ thống bổ sung (văn bản bên dưới mô tả):
> Hoạt động tốt nhất với 16 GB bộ nhớ và card đồ họa có 8 GB bộ nhớ video. Nếu không có card đồ họa phù hợp, mô hình chạy trên
> bộ xử lý, chậm hơn. Máy Arm64 chạy mô hình trên bộ xử lý. Mô hình cần 2 đến 20 GB dung lượng đĩa trống.
