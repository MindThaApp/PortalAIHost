# Portal AI Host: hướng dẫn sử dụng

Portal AI Host chạy một mô hình AI trên máy tính của bạn và chia sẻ nó cho các ứng dụng Portal của bạn. Câu hỏi và tệp của bạn
luôn ở trên chính các thiết bị của bạn. Hướng dẫn này giải thích từng phần của ứng dụng, các cài đặt làm gì, và phải làm gì khi có sự cố.

Những chỗ cần có hình ảnh được đánh dấu `[screenshot: ...]` (ảnh chụp màn hình: ...).

## Mục lục

1. Bắt đầu nhanh
2. Yêu cầu hệ thống, cài đặt, cập nhật, gỡ cài đặt
3. Làm quen với ứng dụng
4. Mô hình
5. Ứng dụng và quyền
6. Thử
7. Cài đặt hiệu năng
8. Thông báo
9. Cho phép thiết bị khác dùng máy tính này
10. Thiết bị trên máy tính này
11. Điều khiển mô hình và hai mô hình cùng lúc
12. Dùng máy tính ở nhà từ laptop (chế độ máy khách)
13. Ở xa nhà: relay và khóa truy cập relay
14. Quyền riêng tư và báo cáo sự cố
15. Dữ liệu được lưu ở đâu, sao lưu và gỡ bỏ
16. Phím tắt
17. Khắc phục sự cố và câu hỏi thường gặp
18. Sắp tới
19. Hỗ trợ

---

## 1. Bắt đầu nhanh

1. Cài đặt Portal AI Host từ Microsoft Store rồi mở ứng dụng.
2. Cửa sổ chào mừng sẽ kiểm tra máy tính của bạn (bộ nhớ, card đồ họa, dung lượng đĩa trống) và đề xuất một mô hình. Chọn
   **Bắt đầu với cài đặt được đề xuất**, hoặc **Tải xuống** để tự chọn mô hình. **Chọn sau** sẽ bỏ qua bước này.
3. Tìm biểu tượng gần đồng hồ trong khay hệ thống. Portal AI Host chờ ở đó một cách lặng lẽ. Nhấp vào biểu tượng để xem trạng thái nhanh.
4. Mở **Thử** và đặt một câu hỏi ngắn để kiểm tra mô hình hoạt động.
5. Lần đầu tiên một ứng dụng Portal muốn dùng AI, một thông báo sẽ hỏi bạn cho phép hay từ chối.

[screenshot: cửa sổ chào mừng với mô hình được đề xuất]

Trên laptop cần dùng AI của máy tính ở nhà, chọn **Dùng máy tính ở nhà** trong cửa sổ chào mừng. Xem
mục 12.

**Miễn phí hay trả phí:** Portal AI Host miễn phí. Không cần tài khoản, đăng nhập hay mua hàng cho bất kỳ tính năng nào trong hướng dẫn này.

---

## 2. Yêu cầu hệ thống, cài đặt, cập nhật, gỡ cài đặt

### Yêu cầu

| | Tối thiểu | Khuyến nghị |
|---|---|---|
| Windows | Windows 11 (21H2, bản dựng 22000) trở lên, x64 hoặc Arm64 | Giống nhau |
| Bộ nhớ | 8 GB | 16 GB trở lên (32 GB trở lên cho các mô hình lớn) |
| Đồ họa | Không có (mô hình chạy trên bộ xử lý, chậm hơn) | Card đồ họa hỗ trợ Vulkan với 8 GB bộ nhớ video trở lên (AMD, NVIDIA hoặc Intel) |
| Dung lượng đĩa trống | 5 GB | 25 GB |

Mỗi mô hình có kích thước từ 2 đến 20 GB. Máy Arm64 chạy mô hình trên bộ xử lý. Chỉ cần kết nối internet để tải mô hình và cho relay;
khi mô hình đã có trên máy tính, nó hoạt động mà không cần mạng.

### Cài đặt

Cài đặt từ Microsoft Store. Không cần thêm gì khác: không Python, không dòng lệnh, không phần mềm khác. Động cơ AI là một phần của ứng dụng.

### Khởi động cùng Windows

**Cài đặt > Chung > Khởi động cùng Windows** (mặc định: tắt, ngoại trừ nút "Bắt đầu với cài đặt được đề xuất" trong cửa sổ chào mừng,
nút này sẽ bật tùy chọn này). Khi bật, Portal AI Host chờ trong khay hệ thống sau khi bạn đăng nhập, để các ứng dụng có thể dùng ngay.
- Nếu công tắc ghi rằng nó chỉ có sẵn trong ứng dụng đã cài đặt, bạn đang chạy bản phát triển.
- Nếu bạn đã tắt nó trong Trình quản lý tác vụ (ứng dụng khởi động), hãy bật lại ở đó.
- Nếu tổ chức của bạn đặt tùy chọn này, công tắc sẽ bị khóa.

### Cập nhật

Store cập nhật ứng dụng. Sau khi cập nhật, **Có gì mới** sẽ mở một lần. Bạn có thể mở lại từ menu ứng dụng.
Các mô hình vẫn giữ nguyên vị trí sau khi cập nhật.

### Gỡ cài đặt

Gỡ ứng dụng trong **Cài đặt Windows > Ứng dụng**. Các mô hình trong thư mục mặc định được lưu trong dữ liệu riêng của ứng dụng và
sẽ bị xóa cùng ứng dụng. Thư mục mô hình tùy chỉnh mà bạn đã chọn sẽ không bị ảnh hưởng. Xem
mục 15 trước khi gỡ cài đặt nếu bạn muốn giữ lại thứ gì đó.

---

## 3. Làm quen với ứng dụng

Bên trái có năm trang.

| Trang | Dùng để | Phím tắt |
|---|---|---|
| Trang chủ | Trạng thái, mô hình đang hoạt động, mức sử dụng bộ nhớ, ứng dụng đang dùng AI | Ctrl+1 |
| Mô hình | Tải xuống, sử dụng và xóa mô hình | Ctrl+2 |
| Thiết bị và ứng dụng | Cho phép hoặc xóa ứng dụng Portal, tạm dừng, đặt ưu tiên | Ctrl+3 |
| Thử | Đặt cho mô hình một câu hỏi ngắn | Ctrl+4 |
| Cài đặt | Mọi thứ có thể cấu hình | Ctrl+, |

Trang thứ sáu, **Thiết bị của máy tính này**, liệt kê các laptop và điện thoại đã ghép nối với máy tính này (xem
mục 10). Menu ứng dụng gồm Cài đặt, Phím tắt, Có gì mới và Giới thiệu.

**Trang chủ** cho biết AI đang Sẵn sàng, Đang trả lời, Đang chuẩn bị mô hình, Đang nghỉ (mô hình sẽ thức dậy khi một ứng dụng yêu cầu),
hoặc chưa có mô hình. Mục **HIỆN TẠI** cho biết ứng dụng nào đang nhận câu trả lời và khoảng bao nhiêu từ mỗi giây. Nó cũng hiển thị
mô hình đang dùng bao nhiêu bộ nhớ và bộ nhớ đồ họa, cùng nút **Tải ngay** / **Gỡ ngay**. Mục **HÔM NAY** đếm số yêu cầu của mỗi ứng dụng.
Thẻ tình trạng hiển thị "Mọi thứ ổn" hoặc liệt kê những việc cần chú ý, mỗi việc có một cách sửa chỉ với một cú nhấp như Tải mô hình,
Khởi động lại động cơ, Kiểm tra tệp hoặc Mở cài đặt hiệu năng.

[screenshot: Trang chủ khi một ứng dụng đang dùng mô hình]

Biểu tượng khay hệ thống mở một cửa sổ nhỏ: mô hình, bộ nhớ, yêu cầu gần nhất và hàng đợi, với nút **Mở ứng dụng** và **Gỡ mô hình**.

[screenshot: cửa sổ khay hệ thống]

Nếu chương trình máy chủ không chạy, Trang chủ sẽ cung cấp nút **Khởi động Portal AI Host**. Nếu nó đóng đột ngột nhiều hơn một lần,
một thanh **Chế độ an toàn** sẽ cho biết ứng dụng đã khởi động với cài đặt thận trọng. Các ứng dụng của bạn vẫn hoạt động; nếu lỗi
tiếp tục xảy ra, hãy khởi động lại máy tính.

---

## 4. Mô hình

Mô hình là AI thực hiện công việc. **Mô hình** (Ctrl+2) liệt kê những mô hình bạn có và những mô hình bạn có thể thêm.

### Mô hình được đề xuất

Lần chạy đầu tiên và Trang chủ đề xuất "mô hình tốt nhất cho máy tính này", kèm lý do ngắn gọn, dựa trên bộ nhớ, bộ nhớ đồ họa
và dung lượng đĩa trống của bạn. Khi có mô hình tốt hơn, một thanh **Có mô hình tốt hơn** sẽ hiển thị. Mô hình hiện tại của bạn vẫn
được giữ cho đến khi mô hình mới sẵn sàng, và bạn quyết định lúc nào gỡ mô hình cũ.

### Tải xuống mô hình

1. Mở **Mô hình**. Chọn một mô hình trong mục **Mô hình bạn có thể thêm**.
2. Nhấn **Tải xuống**. Một thanh sẽ hiển thị tiến trình và thời gian còn lại.
3. **Tạm dừng** và **Tiếp tục** dùng được bất cứ lúc nào. **Dừng** sẽ bỏ phần đã tải (sau khi xác nhận).
4. Máy chủ kiểm tra các tệp đã tải trước khi dùng.
5. Khi xong, nhấn **Dùng mô hình này**.

Các lần tải có thể tiếp tục sau khi khởi động lại và được kiểm tra từng tệp. Việc tải cần đủ dung lượng đĩa trống; nếu không đủ,
trang sẽ cho biết còn thiếu bao nhiêu và đề nghị **Đổi thư mục trong Cài đặt**. Máy tính được giữ thức trong khi tải
(Cài đặt > Hiệu năng, mặc định bật).

[screenshot: trang Mô hình với một lần tải đang diễn ra]

Mô hình được đánh dấu "Mô hình này có thể quá lớn cho máy tính này" vẫn có thể tải về, nhưng có thể chạy chậm hoặc không vừa.

### Chi tiết, kiểm tra tệp, tải lại, gỡ

- **Chi tiết** cho biết mô hình phù hợp với việc gì, cần lưu ý điều gì, bộ nhớ cần thiết, giấy phép, lần dùng gần nhất và dung lượng đang chiếm.
- **Kiểm tra tệp** kiểm tra lại mô hình trên đĩa. Không xóa gì cả. Mô hình bị hỏng sẽ được đánh dấu **Hỏng**.
- **Tải lại** (đối với mô hình hỏng) xóa bản sao bị hỏng và tải lại. Không có gì khác bị xóa.
- **Gỡ** loại bỏ mô hình khỏi máy tính này sau khi xác nhận, thông báo sẽ giải phóng bao nhiêu dung lượng. Nếu mô hình đang được dùng,
  máy chủ sẽ gỡ nó trước rồi hỏi bạn muốn dùng mô hình nào tiếp theo. Chỉ các tệp do máy chủ tải về mới bị xóa.

### Thêm mô hình của riêng bạn

**Thêm mô hình của tôi...** cho phép bạn chọn một tệp `.gguf`. Máy chủ tạo bản sao riêng trong thư mục mô hình. Tệp gốc của bạn không bao giờ
bị thay đổi hay di chuyển. Dòng đó được đánh dấu **Do bạn thêm**, và máy chủ không thể ước tính bộ nhớ cần thiết nên hãy thử và xem.
**Gỡ** trên mô hình như vậy chỉ xóa bản sao của máy chủ.

### Giải phóng dung lượng

**Giải phóng dung lượng** liệt kê những phần còn sót lại do chính ứng dụng tạo ra: các lần tải dở dang không được tiếp tục, bản sao cũ
còn lại sau khi di chuyển mô hình, và các mô hình bị kiểm tra phát hiện hỏng. Bạn xác nhận trước khi bất cứ thứ gì bị xóa. Mô hình bạn đang
dùng và các tệp do bạn tự thêm không bao giờ nằm trong danh sách. Các lần tải dở dang vẫn có thể tiếp tục sẽ được giữ trong một tuần.

### Thư mục mô hình và di chuyển mô hình

**Cài đặt > Thư mục mô hình** đặt nơi các lần tải mới được lưu. Để trống để dùng thư mục mặc định, hoặc nhập đường dẫn đầy đủ như
`D:\Mo_hinh` rồi nhấn Enter. Các mô hình đã tải sẵn không bị di chuyển hoặc xóa. Chúng vẫn ở nơi cũ và vẫn được liệt kê. Để chuyển
chúng, chấp nhận **Chuyển mô hình của tôi về đó**. Chúng được sao chép, kiểm tra, và chỉ sau đó bản cũ mới bị xóa. Mô hình đang dùng sẽ được
gỡ trong lúc này, và **Hủy chuyển** sẽ giữ mọi thứ ở vị trí cũ.

### Ngân sách bộ nhớ và các thanh

Các thanh **Ngân sách bộ nhớ** trên trang Mô hình hiển thị RAM và bộ nhớ đồ họa: các mô hình đã tải dùng bao nhiêu, bao nhiêu được
dành riêng trong khi tải, và bao nhiêu còn trống so với giới hạn của bạn (xem mục 7). Một lần tải vượt giới hạn
bộ nhớ sẽ bị từ chối với lý do rõ ràng trước khi bất cứ thứ gì bị dừng.

[screenshot: các thanh Ngân sách bộ nhớ khi có hai mô hình được tải]

### Vị trí chạy: card đồ họa, bộ nhớ hoặc chỉ bộ xử lý

Nơi một mô hình chạy được quyết định bởi **Cài đặt > Hiệu năng > Chạy trên** (Tự động, Card đồ họa, Bộ xử lý). Một mô hình không vừa
card đồ họa sẽ chạy một phần trên bộ xử lý (chậm hơn). Xem thêm vị trí cho từng mô hình trong
mục 11, có tùy chọn **Chỉ bộ xử lý**.

### Giới hạn và miễn phí so với trả phí

- Tất cả mô hình trong danh sách đều miễn phí để dùng thương mại (giấy phép Apache 2.0 hoặc MIT); mỗi mô hình hiển thị giấy phép riêng.
- Các mô hình được tải từ trang của nhà phát hành trên Hugging Face. Máy chủ chỉ chấp nhận danh sách mô hình đáng tin cậy và đã ký.
  Nếu không tải được danh sách đó, một thanh **Danh sách mô hình không khả dụng** sẽ hiện; các mô hình đã có trên máy tính vẫn hoạt động.

### Khắc phục sự cố

- *Không có mô hình nào để tải:* kiểm tra kết nối internet và nhấn **Thử lại** trên Trang chủ.
- *Tải bị dừng:* nhấn **Tiếp tục**; nếu thiếu dung lượng, hãy giải phóng dung lượng hoặc đổi thư mục.
- *Mô hình "có thể quá lớn":* chọn mô hình nhỏ hơn, hoặc giảm tải trong Cài đặt > Hiệu năng.

---

## 5. Ứng dụng và quyền

Mở **Thiết bị và ứng dụng** (Ctrl+3).

- **Đang chờ bạn trả lời:** một ứng dụng yêu cầu dùng AI sẽ xuất hiện ở đây và dưới dạng thông báo ("Cho phép Voice Portal dùng AI trên máy tính này?").
  Chọn **Cho phép**, **Không cho phép** hoặc **Quyết định sau**. Thông báo cho biết ứng dụng là ứng dụng Portal đã được xác minh, một ứng dụng
  đã cài nhưng không phải ứng dụng Portal, hay chưa được xác minh (khi đó nó cho biết ứng dụng chạy từ đâu; chỉ cho phép nếu chính bạn đã
  khởi động nó).
- **Các ứng dụng có thể dùng máy chủ AI của bạn:** mỗi ứng dụng được cho phép hiển thị số yêu cầu hôm nay và lần dùng gần nhất.
  - **Tạm dừng** từ chối các yêu cầu mới cho đến khi bạn tiếp tục; ứng dụng vẫn giữ quyền truy cập.
  - **Ưu tiên** (Cao, Thường, Thấp) quyết định ai được xử lý trước khi nhiều ứng dụng yêu cầu cùng lúc. Yêu cầu tương tác đi trước công việc
    hàng loạt, và một yêu cầu chờ lâu sẽ được đẩy lên, nên một ứng dụng ưu tiên thấp không bao giờ bị bỏ đói.
  - **Xóa quyền truy cập** ngừng ứng dụng cho đến khi bạn cho phép lại. **Hoàn tác** xuất hiện trong chốc lát.

Các ứng dụng phải hỏi trước khi có thể dùng máy chủ. Máy chủ chỉ lắng nghe trên máy tính này trừ khi bạn bật chia sẻ với thiết bị khác
(mục 9).

[screenshot: trang Thiết bị và ứng dụng với một ứng dụng đang chờ và một ứng dụng đã được cho phép]

Dành cho nhà phát triển: máy chủ cung cấp một API cục bộ tương thích OpenAI tại `http://127.0.0.1:41870` (hoàn thành hội thoại có truyền
trực tiếp, embedding, danh sách mô hình). Xem `api-v1.md`.

**Nơi lưu trữ:** các ứng dụng được cho phép và token của chúng được giữ trong thư mục dữ liệu của máy chủ, được bảo vệ cho tài khoản Windows
của bạn. Xóa quyền truy cập của một ứng dụng sẽ xóa token của nó.

---

## 6. Thử

**Thử** (Ctrl+4) kiểm tra xem mô hình có hoạt động không. Nhập một câu hỏi ngắn và nhấn **Hỏi** (hoặc Ctrl+Enter). **Dừng** kết thúc câu trả lời,
**Xóa** làm trống trang. Có gợi ý các câu hỏi. Trang hiển thị thời gian để những từ đầu tiên xuất hiện và tốc độ tính theo token mỗi giây.
Không có gì bạn nhập ở đây được lưu lại.

[screenshot: Thử với câu trả lời và dòng thời gian]

---

## 7. Cài đặt hiệu năng

**Cài đặt > Hiệu năng** giúp AI không làm chậm máy tính của bạn. Thay đổi có hiệu lực ngay; một mô hình không còn vừa sẽ được tải lại với
giới hạn mới hoặc được gỡ (vẫn còn trên đĩa). **Khôi phục mặc định** sẽ đặt lại mục này.

| Cài đặt | Chức năng | Mặc định |
|---|---|---|
| Hiệu năng | Một cấu hình sẵn: **Êm** (tốc độ khoảng một nửa, máy tính vẫn phản hồi tốt), **Cân bằng** (tốc độ tốt, chương trình của bạn được ưu tiên), **Nhanh** (phản hồi nhanh nhất, chương trình khác có thể chậm lại), **Tùy chỉnh** | Cân bằng |
| Bộ nhớ tối đa cho AI | AI không bao giờ dùng nhiều hơn mức này | 80% bộ nhớ máy tính |
| Luôn chừa trống | Bộ nhớ được giữ trống cho các chương trình khác | Theo cấu hình sẵn |
| Chạy trên | **Tự động** (card đồ họa khi có ích), **Card đồ họa** (nhanh nhất, nếu mô hình vừa), **Bộ xử lý** (chậm hơn, card đồ họa để trống cho trò chơi) | Tự động |
| Bộ nhớ đồ họa cho AI | Giới hạn bộ nhớ đồ họa mà AI được dùng | Theo cấu hình sẵn |
| Nhân bộ xử lý | Ít nhân hơn giúp máy tính phản hồi tốt | Theo cấu hình sẵn |
| Ưu tiên | **Thấp** (công việc của bạn trước) hoặc **Thường** | Thấp |
| Mức dùng bộ xử lý tối đa | Mức trần cho việc dùng bộ xử lý | Không giới hạn (100%) |
| Giảm tải khi | AI chạy với mức ưu tiên thấp nhất, dùng tối đa một nửa bộ xử lý và trở lại bình thường khi kết thúc. Bạn có thể chọn bất kỳ mục nào sau: có trò chơi hoặc chương trình toàn màn hình đang chạy, máy tính đang dùng pin, một ứng dụng Portal đang bận với công việc trực tiếp như đọc chính tả | Bật: chương trình toàn màn hình, pin và ứng dụng đang bận |

Trong **Cài đặt > Chung**, phần **Giải phóng bộ nhớ khi rảnh**, bạn chọn **Giữ mô hình được tải**, **Sau 5 phút** hoặc **Sau 10 phút**
(mặc định 10 phút, 5 phút khi dùng pin). Mô hình sẽ được tải lại vào lần tiếp theo ứng dụng yêu cầu. Giữ mô hình được tải giúp trả lời
nhanh hơn nhưng luôn chiếm bộ nhớ.

Các gợi ý trên thanh trượt cho biết bộ nhớ đã chọn có đủ để đưa mô hình hoàn toàn lên card đồ họa, một phần lên bộ xử lý, hay chỉ trên bộ xử lý,
và đề nghị mô hình nhỏ hơn khi mô hình hiện tại không còn vừa.

[screenshot: Cài đặt > Hiệu năng]

**Khắc phục sự cố:** trả lời chậm trên máy có card đồ họa thường có nghĩa là mô hình chỉ vừa một phần với card đồ họa. Hãy chọn mô hình nhỏ hơn,
hoặc tăng **Bộ nhớ đồ họa cho AI**. Nếu máy tính bị giật khi đang dùng, hãy chọn Êm hoặc giảm **Mức dùng bộ xử lý tối đa**.

---

## 8. Thông báo

**Cài đặt > Thông báo** điều khiển các thông báo Windows ngắn hiện ra khi có việc cần bạn chú ý và cửa sổ đã đóng. Chúng im lặng khi một chương trình
toàn màn hình đang ở phía trước, và không lặp lại quá một lần trong vài phút.

| Công tắc | Ý nghĩa |
|---|---|
| Một lần tải mô hình hoàn tất hoặc thất bại | Mặc định bật |
| Một ứng dụng yêu cầu chấp thuận | Mặc định bật |
| Động cơ AI dừng hoặc một kiểm tra thất bại | Mặc định bật |
| Một thiết bị ghép nối hoặc kết nối từ xa | Mặc định bật (các cảnh báo trong mục 10) |

Mỗi công tắc có thể tắt riêng. **Khôi phục mặc định** trên cùng trang sẽ đặt lại tất cả.

---

## 9. Cho phép thiết bị khác dùng máy tính này

**Cài đặt > Thiết bị khác.** Mặc định tắt. Khi bật, laptop của bạn hoặc một máy tính khác trong mạng gia đình có thể yêu cầu dùng AI
của máy tính này. Thiết bị phải được ghép nối bằng mã trước, và mỗi ứng dụng trên thiết bị đó vẫn cần bạn chấp thuận.

### Ghép nối một thiết bị

1. Bật **Cho phép thiết bị khác dùng AI của máy tính này**. Windows có thể hỏi bạn cho phép một quy tắc mạng.
2. Nhấn **Ghép nối thiết bị**. Một mã và mã QR sẽ hiện ra, và **Sao chép liên kết** sẽ sao chép liên kết ghép nối.
3. Trên thiết bị kia, mở ứng dụng Portal của nó (hoặc Portal AI Host ở chế độ máy khách, mục 12),
   chọn kết nối tới một máy tính, rồi quét mã, nhập mã hoặc dán liên kết.
4. Thiết bị kiểm tra rằng nó đang nói chuyện với đúng máy tính này trước khi gửi bất cứ thứ gì, rồi hiển thị tên máy tính và dấu vân tay để bạn xác nhận.

Mã chỉ dùng được một lần, trong 5 phút, cho một thiết bị. Nhập sai sẽ làm mã hết lượt; hãy tạo mã mới bằng
**Ghép nối thiết bị**. **Hủy ghép nối** sẽ kết thúc quy trình.

[screenshot: mã ghép nối và mã QR]

### Thiết bị đã ghép nối

Mỗi thiết bị đã ghép nối được liệt kê cùng thời gian dùng gần nhất và nút **Gỡ**. Gỡ một thiết bị sẽ dừng quyền truy cập của nó ngay,
và các yêu cầu đang xử lý sẽ kết thúc. Nó phải ghép nối lại để quay lại. Tối đa **16** thiết bị có thể được ghép nối.

### Cài đặt khác trong thẻ này

- **Cũng truy cập máy tính này khi ở xa nhà** (relay), xem mục 13. Mặc định: bật sau khi thiết bị khác được bật.
- **Khóa truy cập relay**, xem mục 13. Mặc định: tắt.
- **Thiết bị được thay thế mô hình**, xem mục 11. Mặc định: tắt.

**Những gì đi qua mạng:** các yêu cầu và câu trả lời được truyền mã hóa giữa các thiết bị của bạn. Kết nối mạng gia đình dùng một chứng chỉ
mà thiết bị đã ghim khi ghép nối. Không có gì được gửi đến máy chủ của chúng tôi, ngoại trừ relay mù tùy chọn (mục 13).

**Khắc phục sự cố:** *Thiết bị không tìm thấy máy tính này:* kiểm tra cả hai cùng mạng, máy tính này đang thức và máy chủ đang chạy.
*Mã "không đúng" hoặc "đã hết":* tạo mã mới. *"Không trong mạng gia đình":* ghép nối một lần ở nhà trước.

---

## 10. Thiết bị trên máy tính này

Trang **Thiết bị của máy tính này** hiển thị một thẻ cho mỗi laptop đã ghép nối. Đây là nơi bạn xem mỗi laptop đang làm gì và giới hạn nó.

[screenshot: Thiết bị trên máy tính này với hai thẻ thiết bị]

### Thẻ hiển thị gì

- Trạng thái (Trực tuyến, Rảnh, Ngoại tuyến), tuyến kết nối (Mạng gia đình hoặc Relay (ở xa)) và lần thấy gần nhất.
- Thứ đang chạy và thứ đang chờ.
- Yêu cầu và token hôm nay, cùng thanh 7 ngày, theo từng ứng dụng.
- Mô hình đang dùng, ai đã tải nó, và tốc độ tính bằng token mỗi giây cùng thời gian đến byte đầu tiên.

### Điều khiển

| Điều khiển | Chức năng |
|---|---|
| Tạm dừng / Tiếp tục | Tạm dừng toàn bộ thiết bị, hoặc một trong các ứng dụng của nó. Laptop nhận câu trả lời "đã tạm dừng" rõ ràng và dùng phương án dự phòng của chính nó nếu bạn đã đặt. |
| Đổi tên | Đổi tên hiển thị của thiết bị |
| Giới hạn | Ưu tiên, giới hạn theo ngày, giờ yên tĩnh và các mô hình được phép (bên dưới) |
| Điều khiển mô hình (công tắc) | Cho phép thiết bị này tải và gỡ mô hình. Xem mục 11 |
| Lấy lại | Gỡ mô hình mà thiết bị này đã tải. Lựa chọn của máy tính này được khôi phục. Không tệp mô hình nào bị động đến. |
| Quên | Gỡ thiết bị. Quyền truy cập của nó dừng ngay và lịch sử sử dụng bị xóa. Mô hình không bị ảnh hưởng. |

**Giới hạn** (dưới nút **Giới hạn** trên thẻ, rồi **Lưu**):

- **Cùng ưu tiên với các ứng dụng của máy tính này.** Tắt (mặc định): các ứng dụng của máy tính này đi trước. Bật: thiết bị xếp chung hàng đợi.
- **Yêu cầu mỗi ngày** và **Token mỗi ngày.** Mặc định: không giới hạn. Khi đã dùng hết, thiết bị được báo quay lại sau nửa đêm theo giờ của máy tính này.
- **Giờ yên tĩnh**, với **Từ** và **Đến** (theo giờ của máy tính này). Thiết bị không nhận câu trả lời trong khoảng thời gian đó. Mặc định: tắt.
- **Các mô hình thiết bị này được dùng.** Mặc định: tất cả mô hình.

### Chế độ trò chơi

Ở đầu trang. **Mặc định: tắt.** Khi bật, các yêu cầu từ thiết bị khác sẽ được báo rằng máy tính này đang bận khi bạn chơi game, để game
giữ card đồ họa. Máy tính này được coi là "đang chơi" khi Windows báo có game toàn màn hình, hoặc khi một chương trình khác giữ động cơ 3D
của card đồ họa ở mức 40% trở lên trong khoảng 10 giây, nên game chạy trong cửa sổ hoặc cửa sổ không viền cũng được tính. Chế độ này ngừng
tính khoảng 30 giây sau khi tải kết thúc. Nếu bộ đếm đồ họa không khả dụng trên máy tính của bạn, chỉ kiểm tra toàn màn hình được dùng. Khi đó
laptop sẽ dùng phương án dự phòng theo cài đặt của chính nó.

### Cảnh báo

Một thông báo Windows xuất hiện khi có thiết bị mới được ghép nối, và khi một thiết bị kết nối qua relay từ xa nhà. Tắt bằng
**Cài đặt > Thông báo > Một thiết bị ghép nối hoặc kết nối từ xa**.

### Dữ liệu

Các số liệu sử dụng chỉ là số đếm (yêu cầu và token theo từng ứng dụng theo ngày, giữ 7 ngày). Không có nội dung câu lệnh hoặc câu trả lời nào được lưu.
**Quên** xóa tệp sử dụng của thiết bị đó và không gì khác.

---

## 11. Điều khiển mô hình và hai mô hình cùng lúc

Mặc định, một laptop đã ghép nối sẽ dùng bất kỳ mô hình nào máy tính này đã tải. Bạn có thể cấp thêm quyền cho thiết bị.

### Điều khiển mô hình

**Cài đặt > Thiết bị khác**, trên dòng của thiết bị: **Điều khiển mô hình** (mặc định: tắt). Khi bật, laptop có thể:
- tải và gỡ các mô hình đã cài trên máy tính này,
- chọn nơi mô hình chạy (Tự động, Card đồ họa, Bộ nhớ hệ thống hoặc Chỉ bộ xử lý),
- chọn mô hình nào trong các mô hình của máy tính này mỗi ứng dụng của nó dùng.

Laptop không bao giờ có thể tải về, xóa, kiểm tra hay nhập mô hình, thay đổi cài đặt hoặc động đến tệp của bạn, dù có quyền này hay không.
Tắt nó đi và yêu cầu tiếp theo từ thiết bị sẽ bị từ chối. Gỡ thiết bị sẽ thu hồi quyền, và ghép nối lại không khôi phục quyền đó.

Máy tính cho biết ai đã tải gì: "Được tải bởi <thiết bị>: <mô hình> (card đồ họa)". **Lấy lại** (trên thẻ và trong Cài đặt) gỡ mô hình
mà thiết bị đã tải. Lựa chọn của máy tính này được khôi phục và mô hình sẽ được tải ở yêu cầu tiếp theo.

### Hai mô hình cùng lúc

Khi có Điều khiển mô hình, một laptop có thể giữ một mô hình thứ hai được tải trên máy tính này, ví dụ một mô hình trên card đồ họa và một mô hình
trong bộ nhớ hệ thống.
- Mô hình của bạn vẫn ở khe thứ nhất. Mô hình của thiết bị vào khe thứ hai, nên thiết bị không bao giờ đẩy mô hình của bạn ra chỉ để lấy chỗ cho
  mình.
- Mỗi khe có hàng đợi riêng, nên hai mô hình có thể trả lời cùng lúc. Yêu cầu nêu tên mô hình thứ hai sẽ được gửi đến nó; mọi yêu cầu khác được gửi đến mô hình của bạn.
- Trước khi tải, máy chủ tính xem khe kia đã dùng bao nhiêu so với giới hạn của bạn. Mô hình thứ hai không vừa sẽ bị từ chối
  ("không đủ bộ nhớ"). Không có gì bị dừng âm thầm.
- Lựa chọn của bạn luôn được ưu tiên. Nếu bạn chọn mô hình khác, gỡ một trong hai, hoặc mô hình của bạn cần bộ nhớ, mô hình của thiết bị sẽ bị dừng và thiết bị được thông báo.
- Mô hình thứ hai của thiết bị không bao giờ được giữ tải mãi; nó được gỡ khi rảnh như mọi mô hình khác.
- **Chỉ bộ xử lý** chạy mô hình không dùng các lớp đồ họa, sử dụng khoảng một nửa bộ xử lý, ví dụ một mô hình phụ trên bộ xử lý trong khi mô hình khác dùng card đồ họa.

[screenshot: trang Mô hình với hai mô hình được tải bởi các chủ sở hữu khác nhau]

### Thiết bị được thay thế mô hình

**Cài đặt > Thiết bị khác > Thiết bị được thay thế mô hình** (mặc định: tắt). Tắt: thiết bị không thể gỡ mô hình mà ứng dụng của bạn đang dùng. Bật: có thể,
và ứng dụng của bạn sẽ tải lại mô hình của chúng ở yêu cầu tiếp theo. Với hai thiết bị, thiết bị tải trước sẽ giữ mô hình của nó.

---

## 12. Dùng máy tính ở nhà từ laptop (chế độ máy khách)

Chế độ máy khách khiến Portal AI Host trên laptop dùng AI của một máy tính khác của bạn thay vì tự chạy mô hình. Các ứng dụng trên laptop
hoạt động như bình thường.

### Thiết lập

1. Trên máy tính ở nhà, bật **Cho phép thiết bị khác dùng AI của máy tính này** và nhấn **Ghép nối thiết bị** (mục 9). Nhấn **Sao chép liên kết** và gửi liên kết cho laptop.
2. Trên laptop, mở **Cài đặt > Máy tính ở nhà** (hoặc chọn **Dùng máy tính ở nhà** trong cửa sổ chào mừng).
3. Trong **Nơi AI chạy**, chọn **Dùng máy tính ở nhà**. Một cửa sổ **Dùng máy tính ở nhà** sẽ mở. Dán liên kết bằng **Dán liên kết**, nhấn **Tiếp**, kiểm tra tên máy tính và dấu vân tay, rồi **Ghép nối**. Đặt tên cho laptop.
4. Trang chủ giờ hiển thị tên máy tính và trạng thái: **Đã kết nối - mạng gia đình**, **Đã kết nối - qua relay**, **Đang kết nối...** hoặc **Không truy cập được**.

[screenshot: cửa sổ ghép nối trên laptop]

### Cài đặt (Cài đặt > Máy tính ở nhà)

| Cài đặt | Lựa chọn | Mặc định |
|---|---|---|
| Nơi AI chạy | Máy tính này / Dùng máy tính ở nhà | Máy tính này |
| Nếu máy tính ở nhà không truy cập được | **Không có** (ứng dụng dùng AI tích hợp sẵn của chúng) / **Dùng mô hình của laptop này** (tải khi cần) | Không có |
| Kết nối khi ở xa nhà | **Tự động** / **Thủ công** (khi đó công tắc **Dùng relay** chỉ áp dụng cho phiên này và đặt lại khi ứng dụng đóng) | Tự động |

- Lần đầu tiên máy tính ở nhà không truy cập được, bạn sẽ được hỏi một lần về cách xử lý; bạn có thể thay đổi ở đây bất cứ lúc nào.
- **Ghép nối lại máy tính** ghép nối lần nữa; **Hủy ghép nối** quên máy tính (mô hình và các lần tải giữ nguyên); **Đổi máy tính** (trên Trang chủ)
  mở các cài đặt này; **Chạy AI ở đây** (trên Trang chủ) thoát chế độ máy khách và giữ lại cặp ghép nối đã lưu.
- Khi máy tính ở nhà đã cấp Điều khiển mô hình, mục **Mô hình trên <tên máy tính>** sẽ xuất hiện với các nút Tải, Gỡ và vị trí chạy, cùng với
  **Mô hình máy tính cho từng ứng dụng**. Nếu không có quyền này, trang hiển thị: "Hãy nhờ chủ sở hữu <tên máy tính> bật Điều khiển mô hình cho laptop này".
- Laptop ở chế độ máy khách không nhận kết nối từ thiết bị khác và không thể làm điểm trung gian tới máy tính ở nhà của bạn.

### Điều gì xảy ra với mô hình của chính laptop này

Khi vào chế độ máy khách, mô hình đã tải trên laptop sẽ được gỡ để giải phóng bộ nhớ. Các tệp mô hình, lần tải và thư mục mô hình không bị ảnh hưởng.
Khi thoát chế độ này, mô hình sẽ được tải lại khi có yêu cầu.

### Khắc phục sự cố

- **Không truy cập được:** kiểm tra máy tính ở nhà đang bật và cùng mạng, hoặc bật relay.
- **"Laptop này đã bị gỡ khỏi máy tính":** máy tính ở nhà đã gỡ nó. Chọn **Ghép nối lại**.
- **"Danh tính của <máy tính> đã thay đổi":** chứng chỉ của máy tính không còn khớp với bản laptop đã lưu. Nếu bạn không mong đợi điều này, đừng ghép nối lại. Nếu không, hãy hủy ghép nối rồi ghép nối lại.
- **Ghép nối từ xa thất bại:** hãy ghép nối một lần ở nhà trước; liên kết ghép nối phải trỏ tới một địa chỉ trong mạng gia đình của bạn.
- **Đang tạm dừng, giờ yên tĩnh, có trò chơi đang chạy hoặc đã hết giới hạn ngày:** máy tính cho laptop biết lý do; laptop dùng phương án dự phòng.

**Nơi lưu trữ:** laptop giữ ghép nối trong thư mục dữ liệu của máy chủ, dưới `client\`. Khóa thiết bị và token được bảo vệ cho tài khoản Windows của bạn.
**Hủy ghép nối** sẽ xóa chúng. Bản sao lưu của thư mục này không thể khôi phục trên một tài khoản Windows khác.

---

## 13. Ở xa nhà: relay và khóa truy cập relay

Relay cho phép một thiết bị đã ghép nối truy cập máy tính của bạn từ bất cứ đâu. **Cài đặt > Thiết bị khác > Cũng truy cập máy tính này khi ở xa nhà.**

- Máy tính của bạn kết nối ra một dịch vụ relay nhỏ. Relay chỉ chuyển tiếp các byte đã được xáo trộn mà nó không thể đọc, và không giữ nội dung nào.
  Mỗi thiết bị vẫn phải được ghép nối và ký mọi yêu cầu.
- Dòng trạng thái hiển thị **Truy cập được từ xa nhà**, **Chỉ mạng gia đình**, **Đang kết nối tới relay...** hoặc **Không kết nối được relay**.
- Nó hoạt động qua cổng web thông thường, nên hầu hết mạng văn phòng đều cho phép.
- **Thiết bị công ty:** nội dung công việc sẽ đi tới máy tính cá nhân của bạn. Hãy kiểm tra quy định của nhà tuyển dụng trước.

### Khóa truy cập relay

**Cài đặt > Thiết bị khác > Khóa truy cập relay** (mặc định: tắt). Khi bật, chỉ các thiết bị đã ghép nối bằng khóa trong mã ghép nối mới có thể
truy cập máy tính này qua relay, điều này ngăn người lạ dùng hết các khe relay của máy tính bạn.

- Bật **trước khi ghép nối**. Các thiết bị đã ghép nối trước đó phải ghép nối lại để dùng relay (mạng gia đình vẫn hoạt động).
- **Khóa mới** tạo một khóa mới sau khi xác nhận. Mọi thiết bị đã ghép nối phải ghép nối lại để dùng relay; ở nhà thì không có gì thay đổi. Gỡ một thiết bị không làm đổi khóa,
  vì vậy hãy dùng **Khóa mới** sau khi gỡ thiết bị bạn không tin cậy.
- Nếu tệp khóa không dùng được, relay vẫn tắt chứ không mở, và mạng gia đình vẫn hoạt động. Tệp khóa bị hỏng được chuyển sang một tên có đuôi `.bad` và không bao giờ bị ghi đè.
- Nếu công tắc không giữ được trạng thái bật, dịch vụ relay có thể cần được cập nhật ở phía họ. Hãy tắt công tắc.

**Giới hạn:** relay giới hạn tốc độ và lượng dữ liệu một thiết bị có thể gửi, để một thiết bị không làm quá tải dịch vụ. Dùng bình thường thì thấp hơn nhiều so với các giới hạn này.

---

## 14. Quyền riêng tư và báo cáo sự cố

- **Không có phân tích hay theo dõi sử dụng.** Chỉ báo cáo sự cố mới có thể rời khỏi máy tính của bạn, và chỉ khi bạn để nó bật.
- **Văn bản của bạn ở lại trên các thiết bị của bạn.** Câu lệnh và câu trả lời không bao giờ được máy chủ lưu hay ghi nhật ký. Nhật ký chỉ chứa số đếm, thời gian và trạng thái.
- **Báo cáo sự cố được bật mặc định và bạn có thể tắt.** Nếu cửa sổ hoặc máy chủ nền bị sự cố, một báo cáo ngắn giúp chúng tôi sửa lỗi. Tắt bất cứ lúc nào trong
  **Cài đặt > Quyền riêng tư > Gửi báo cáo sự cố**; có hiệu lực ngay. Cửa sổ chào mừng có cùng lựa chọn, được đánh dấu sẵn, trước khi bất cứ thứ gì được gửi.
  - **Được gửi:** loại lỗi và nơi xảy ra (tên hàm và mô-đun), phiên bản ứng dụng và Windows, có phải bản từ Store hay không, và lỗi đến từ cửa sổ hay máy chủ.
  - **Không bao giờ được gửi:** câu lệnh hay câu trả lời, bất cứ thứ gì bạn nhập, tên tệp hoặc đường dẫn thư mục, tên máy tính hoặc tên người dùng của bạn,
    ứng dụng nào dùng máy chủ, hoặc địa chỉ IP của bạn. Nội dung thông báo lỗi bị loại bỏ.
  - **Lưu 90 ngày**, sau đó bị xóa.
  - **Những gì được gửi** (Cài đặt > Quyền riêng tư) mở trang quyền riêng tư.
- **Mọi thứ được mã hóa** khi rời máy tính này để tới một thiết bị khác của bạn.
- **Chính sách quyền riêng tư:** Giới thiệu > **Chính sách quyền riêng tư**.
- Câu hỏi hoặc yêu cầu xóa báo cáo: mindthaapp@gmail.com.

[screenshot: Cài đặt > Quyền riêng tư với công tắc báo cáo sự cố]

---

## 15. Dữ liệu được lưu ở đâu, sao lưu và gỡ bỏ

Mọi thứ nằm trong thư mục dữ liệu riêng của ứng dụng. **Giới thiệu > Mở thư mục dữ liệu** sẽ mở nó.

| Thứ gì | Ở đâu (trong thư mục dữ liệu) | Có nên sao lưu? |
|---|---|---|
| Mô hình đã tải | `models\`, hoặc `Portal AI Host Models\` bên trong thư mục bạn đã chọn trong Cài đặt | Tùy chọn. Có thể tải lại. Chúng lớn. |
| Cài đặt | `settings.json` | Có |
| Ứng dụng được cho phép và token | Kho ứng dụng của máy chủ | Không di chuyển được (được bảo vệ cho tài khoản Windows của bạn) |
| Thiết bị đã ghép nối, giới hạn, thống kê sử dụng | Kho thiết bị của máy chủ và một tệp thống kê nhỏ cho mỗi thiết bị | Không di chuyển được |
| Ghép nối laptop (chế độ máy khách) | `client\` | Không di chuyển được. Ghép nối lại trên máy tính mới. |
| Nhật ký | `logs\` (luân phiên, giới hạn 10 MB) | Không |
| Tệp khám phá cho ứng dụng | `discovery\host.json` | Không |

**Sao lưu:** đóng Portal AI Host, rồi sao chép thư mục dữ liệu. Cài đặt và mô hình có thể được khôi phục bằng cách sao chép lại. Quyền ứng dụng,
ghép nối thiết bị và token được bảo vệ cho tài khoản Windows và máy tính của bạn, nên trên máy tính mới bạn cần cho phép ứng dụng và ghép nối thiết bị lại.

**Gỡ bỏ:**
- *Một mô hình:* **Mô hình > Gỡ**. Chỉ các tệp do máy chủ tải về mới bị xóa.
- *Phần còn sót lại:* **Mô hình > Giải phóng dung lượng**.
- *Một thiết bị:* **Quên** (Thiết bị của máy tính này) hoặc **Gỡ** (Cài đặt).
- *Quyền truy cập của một ứng dụng:* **Xóa quyền truy cập** (Thiết bị và ứng dụng).
- *Tất cả:* gỡ cài đặt ứng dụng. Thư mục mô hình tùy chỉnh bạn đã chọn được giữ nguyên; hãy tự xóa thư mục `Portal AI Host Models` nếu bạn muốn xóa hẳn.

Ứng dụng không bao giờ xóa thứ gì nó không tự tạo, không bao giờ ghi đè tệp của bạn, và một mô hình bạn tự thêm chỉ bị gỡ như bản sao của máy chủ
(tệp gốc của bạn không bị động đến).

---

## 16. Phím tắt

| Phím | Hành động |
|---|---|
| Ctrl+1 | Trang chủ |
| Ctrl+2 | Mô hình |
| Ctrl+3 | Thiết bị và ứng dụng |
| Ctrl+4 | Thử |
| Ctrl+, | Cài đặt |
| F1 | Giới thiệu |
| Ctrl+Enter | Hỏi, trên trang Thử |
| Esc | Đóng Có gì mới hoặc hộp thoại |

Danh sách cũng có trong menu ứng dụng > **Phím tắt**.

Giao diện: **Cài đặt > Giao diện** cung cấp Chủ đề (mặc định: cài đặt Windows của bạn), Chất liệu cửa sổ (Mica), Góc (Bo tròn),
Mật độ (Tiêu chuẩn), Cỡ chữ (Bình thường) và Chuyển động (Theo Windows). Chế độ tương phản cao của Windows luôn dùng màu hệ thống đặc.

---

## 17. Khắc phục sự cố và câu hỏi thường gặp

**Máy chủ báo không chạy.** Nhấn **Khởi động Portal AI Host** trên Trang chủ. Nếu không khởi động được, hãy khởi động lại máy tính; nếu vẫn không được, cài lại từ Store.

**"Một người dùng Windows khác trên máy tính này đang chạy Portal AI Host."** Yêu cầu người đó thoát ứng dụng, hoặc đăng nhập bằng tài khoản của họ.

**Câu trả lời chậm.** Xem mục 7. Một mô hình quá lớn so với bộ nhớ đồ họa của bạn sẽ chạy một phần trên bộ xử lý.

**Một ứng dụng nói không có AI nào.** Kiểm tra ứng dụng đã được cho phép (Thiết bị và ứng dụng), không bị tạm dừng, và có mô hình đã cài không quá lớn cho máy tính này.

**Câu trả lời đầu tiên chậm, các câu sau nhanh.** Mô hình đang nghỉ. Nó được tải ở yêu cầu đầu tiên. Chọn **Giữ mô hình được tải** trong Cài đặt > Chung để tránh điều này.

**Dữ liệu của tôi có được gửi đi đâu không?** Không. Câu lệnh và câu trả lời ở lại trên các thiết bị của bạn. Chỉ báo cáo sự cố mới có thể rời đi (mục 14), và bạn có thể tắt chúng.

**Tôi có thể dùng mà không có internet không?** Có, sau khi đã tải mô hình.

**Có tốn phí không?** Không.

**Nên chọn mô hình nào?** Mô hình được đề xuất. Chọn mô hình nhỏ hơn nếu máy tính của bạn chạy chậm, hoặc mô hình lớn hơn nếu bạn muốn câu trả lời tốt hơn và có đủ bộ nhớ.

**Tải bị kẹt ở "Đang kiểm tra bản tải".** Hãy chờ. Các tệp lớn cần một phút để kiểm tra.

**Ghép nối báo "quá nhiều thiết bị".** Giới hạn là 16 thiết bị. Hãy gỡ bớt một thiết bị.

**Biểu tượng khay hệ thống ở đâu?** Nhấp vào mũi tên gần đồng hồ để hiển thị các biểu tượng ẩn.

Nếu vẫn chưa giải quyết được, hãy dùng **Giới thiệu > Sao chép phiên bản**, rồi viết thư đến mindthaapp@gmail.com và dán phiên bản vào thư.

---

## 18. Sắp tới

Đây là các tính năng đã được lên kế hoạch và chưa có trong ứng dụng hiện tại.

- **Danh mục mô hình trên trang Mô hình:** nhóm theo tác vụ (trò chuyện và viết, lập trình, suy luận, thị giác, embedding, giọng nói) với bộ lọc và tìm kiếm.
- **Tạo hình ảnh và video** trên máy tính này, gồm một "đạo diễn" ghép các cảnh thành video. Các mô hình video sẽ hiển thị là "Sắp ra mắt" cho đến khi động cơ có sẵn.
- **Chọn mô hình nào trả lời**, và các mô hình nhờ nhau giúp đỡ, trong AI Portal.
- **Phiên bản macOS.**

---

## 19. Hỗ trợ

- **Phản hồi và ý tưởng:** Giới thiệu > **Gửi phản hồi hoặc đề xuất ý tưởng**.
- **Câu hỏi về hỗ trợ và quyền riêng tư:** mindthaapp@gmail.com.
- **Phiên bản:** Giới thiệu hiển thị phiên bản, và **Sao chép phiên bản** đưa nó vào bộ nhớ tạm để dán vào thư của bạn.
- **Giấy phép:** Portal AI Host bao gồm llama.cpp (giấy phép MIT), phần chạy các mô hình trên máy tính của bạn. Các mô hình được tải theo giấy phép riêng của chúng (Apache 2.0 hoặc MIT). Xem Giới thiệu.
