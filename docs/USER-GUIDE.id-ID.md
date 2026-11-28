# Portal AI Host: panduan pengguna

Portal AI Host menjalankan model AI di PC Anda dan membagikannya ke aplikasi Portal Anda. Pertanyaan dan file Anda tetap
berada di perangkat Anda sendiri. Panduan ini menjelaskan setiap bagian aplikasi, fungsi setiap pengaturan, dan apa yang
harus dilakukan jika terjadi masalah.

Tempat yang seharusnya berisi gambar ditandai dengan `[screenshot: ...]` (tangkapan layar: ...).

## Daftar isi

1. Mulai cepat
2. Persyaratan sistem, pemasangan, pembaruan, pencopotan
3. Mengenal aplikasi
4. Model
5. Aplikasi dan izin
6. Coba
7. Pengaturan performa
8. Notifikasi
9. Membiarkan perangkat lain menggunakan PC ini
10. Perangkat di PC ini
11. Kontrol model dan dua model sekaligus
12. Menggunakan PC rumah dari laptop (mode klien)
13. Di luar rumah: relay dan kunci akses relay
14. Privasi dan laporan crash
15. Tempat data disimpan, pencadangan, dan penghapusan
16. Pintasan keyboard
17. Pemecahan masalah dan FAQ
18. Yang akan datang
19. Dukungan

---

## 1. Mulai cepat

1. Pasang Portal AI Host dari Microsoft Store dan buka aplikasinya.
2. Jendela selamat datang akan memeriksa PC Anda (memori, kartu grafis, ruang disk kosong) dan merekomendasikan satu model.
   Pilih **Mulai dengan pengaturan yang direkomendasikan**, atau **Unduh** untuk memilih modelnya sendiri. **Pilih nanti**
   akan melewati langkah ini.
3. Cari ikon di area notifikasi dekat jam. Portal AI Host menunggu di sana dengan tenang. Klik ikon itu untuk melihat status singkat.
4. Buka **Coba** dan ajukan pertanyaan singkat untuk memastikan modelnya berfungsi.
5. Saat pertama kali aplikasi Portal ingin menggunakan AI, akan muncul permintaan untuk mengizinkan atau menolaknya.

[screenshot: jendela selamat datang dengan model yang direkomendasikan]

Pada laptop yang seharusnya menggunakan AI PC rumah Anda, pilih **Gunakan PC rumah saya** di jendela selamat datang. Lihat
bagian 12.

**Gratis atau berbayar:** Portal AI Host gratis. Tidak diperlukan akun, masuk, atau pembelian untuk fitur apa pun dalam panduan ini.

---

## 2. Persyaratan sistem, pemasangan, pembaruan, pencopotan

### Persyaratan

| | Minimum | Direkomendasikan |
|---|---|---|
| Windows | Windows 11 (21H2, build 22000) atau lebih baru, x64 atau Arm64 | Sama |
| Memori | 8 GB | 16 GB atau lebih (32 GB atau lebih untuk model besar) |
| Grafis | Tidak ada (model berjalan di prosesor, lebih lambat) | Kartu grafis Vulkan dengan memori video 8 GB atau lebih (AMD, NVIDIA, atau Intel) |
| Ruang disk kosong | 5 GB | 25 GB |

Setiap model berukuran 2 hingga 20 GB. PC Arm64 menjalankan model di prosesor. Koneksi internet hanya diperlukan untuk mengunduh
model dan untuk relay; setelah model ada di PC Anda, model itu berfungsi tanpa internet.

### Pemasangan

Pasang dari Microsoft Store. Tidak ada yang perlu dipasang lagi: tanpa Python, tanpa baris perintah, tanpa perangkat lunak lain.
Mesin AI sudah menjadi bagian dari aplikasi.

### Mulai bersama Windows

**Pengaturan > Umum > Mulai bersama Windows** (default: mati, kecuali tombol "Mulai dengan pengaturan yang direkomendasikan" di
jendela selamat datang, yang akan menyalakannya). Saat aktif, Portal AI Host menunggu di area notifikasi setelah Anda masuk,
sehingga aplikasi bisa langsung menggunakannya.
- Jika sakelar menyebutkan hanya tersedia di aplikasi yang terpasang, berarti Anda menjalankan salinan pengembangan.
- Jika Anda mematikannya di Pengelola Tugas (aplikasi startup), aktifkan kembali di sana.
- Jika organisasi Anda mengaturnya, sakelar terkunci.

### Pembaruan

Store memperbarui aplikasi. Setelah pembaruan, **Yang baru** akan terbuka sekali. Anda bisa membukanya lagi dari menu aplikasi.
Model tetap berada di tempatnya setelah pembaruan.

### Pencopotan

Hapus aplikasi di **Pengaturan Windows > Aplikasi**. Model di folder default disimpan di dalam data aplikasi dan ikut terhapus
bersamanya. Folder model khusus yang Anda pilih tidak tersentuh. Lihat bagian 15
sebelum mencopot jika Anda ingin menyimpan sesuatu.

---

## 3. Mengenal aplikasi

Di sisi kiri ada lima halaman.

| Halaman | Fungsinya | Pintasan |
|---|---|---|
| Beranda | Status, model aktif, penggunaan memori, aplikasi yang sedang menggunakan AI | Ctrl+1 |
| Model | Mengunduh, menggunakan, dan menghapus model | Ctrl+2 |
| Perangkat dan aplikasi | Mengizinkan atau menghapus aplikasi Portal, menjeda, mengatur prioritas | Ctrl+3 |
| Coba | Mengajukan satu pertanyaan singkat ke model | Ctrl+4 |
| Pengaturan | Semua yang bisa dikonfigurasi | Ctrl+, |

Halaman keenam, **Perangkat PC ini**, menampilkan laptop dan ponsel yang dipasangkan dengan PC ini (lihat
bagian 10). Menu aplikasi berisi Pengaturan, Pintasan keyboard, Yang baru, dan Tentang.

**Beranda** menunjukkan apakah AI sedang Siap, Menjawab, Menyiapkan model, Beristirahat (model akan bangun saat aplikasi memintanya),
atau belum ada model. **SEKARANG** menunjukkan aplikasi mana yang sedang menerima jawaban dan kira-kira berapa kata per detik.
Juga ditampilkan berapa memori dan memori grafis yang dipakai model, serta tombol **Muat sekarang** / **Bongkar sekarang**.
**HARI INI** menghitung permintaan setiap aplikasi. Kartu kesehatan menampilkan "Semua baik" atau daftar hal yang perlu
perhatian, masing-masing dengan perbaikan satu klik seperti Muat model, Mulai ulang mesin, Periksa file, atau Buka pengaturan
performa.

[screenshot: halaman Beranda saat aplikasi sedang menggunakan model]

Ikon area notifikasi membuka jendela kecil: model, memori, permintaan terakhir dan antrean, dengan tombol **Buka aplikasi** dan
**Bongkar model**.

[screenshot: jendela kecil dari area notifikasi]

Jika program host tidak berjalan, Beranda menawarkan **Mulai Portal AI Host**. Jika host tertutup tak terduga lebih dari sekali,
bilah **Mode aman** menyatakan bahwa aplikasi dimulai dengan pengaturan yang hati-hati. Aplikasi Anda tetap berfungsi; mulai ulang
PC jika hal ini terus terjadi.

---

## 4. Model

Model adalah AI yang melakukan pekerjaan. **Model** (Ctrl+2) menampilkan model yang Anda miliki dan yang bisa Anda tambahkan.

### Model yang direkomendasikan

Saat pertama kali dijalankan dan di Beranda, aplikasi merekomendasikan "model terbaik untuk PC ini", dengan alasan singkat yang
didasarkan pada memori, memori grafis, dan ruang disk kosong Anda. Jika ada model yang lebih baik, muncul bilah
**Model yang lebih baik tersedia**. Model Anda saat ini tetap digunakan sampai model baru siap, dan Anda yang memutuskan kapan
menghapus model lama.

### Mengunduh model

1. Buka **Model**. Pilih model di bagian **Model yang bisa Anda tambahkan**.
2. Tekan **Unduh**. Sebuah bilah menampilkan kemajuan dan perkiraan waktu tersisa.
3. **Jeda** dan **Lanjutkan** dapat digunakan kapan saja. **Hentikan** membuang bagian yang sudah diunduh (setelah konfirmasi).
4. Host memeriksa file yang diunduh sebelum menggunakannya.
5. Setelah selesai, tekan **Gunakan model ini**.

Unduhan dapat dilanjutkan setelah restart dan diperiksa file demi file. Unduhan membutuhkan ruang disk kosong yang cukup; jika
tidak cukup, halaman akan menyebutkan kekurangannya dan menawarkan **Ubah folder di Pengaturan**. PC tetap terjaga selama unduhan
berjalan (Pengaturan > Performa, default aktif).

[screenshot: halaman Model dengan unduhan yang sedang berjalan]

Model yang ditandai "Model ini mungkin terlalu besar untuk PC ini" tetap bisa diunduh, tetapi mungkin berjalan lambat atau tidak muat.

### Detail, periksa file, unduh ulang, hapus

- **Detail** menunjukkan kegunaan model, hal yang perlu diperhatikan, memori yang dibutuhkan, lisensinya, kapan terakhir digunakan,
  dan berapa ruang yang dipakainya.
- **Periksa file** memeriksa ulang model di disk. Tidak menghapus apa pun. Model yang rusak ditandai **Rusak**.
- **Unduh ulang** (untuk model yang rusak) menghapus salinan yang rusak dan mengunduhnya lagi. Tidak ada yang lain yang dihapus.
- **Hapus** menghapus model dari PC ini setelah konfirmasi yang menyebutkan berapa ruang yang akan dibebaskan. Jika model sedang
  dipakai, host membongkarnya terlebih dahulu dan menanyakan model apa yang dipakai berikutnya. Hanya file yang diunduh host yang dihapus.

### Menambahkan model sendiri

**Tambahkan model saya sendiri...** memungkinkan Anda memilih file `.gguf`. Host membuat salinannya sendiri di folder model. File
asli Anda tidak pernah diubah atau dipindahkan. Barisnya ditandai **Ditambahkan oleh Anda**, dan host tidak dapat memperkirakan
berapa memori yang dibutuhkan, jadi cobalah dan lihat hasilnya. **Hapus** pada model seperti ini hanya menghapus salinan milik host.

### Membebaskan ruang

**Bebaskan ruang** menampilkan sisa-sisa yang dibuat oleh aplikasi itu sendiri: unduhan yang belum selesai dan tidak sedang dilanjutkan,
salinan lama yang tersisa setelah model dipindahkan, dan model yang oleh pemeriksaan dinyatakan rusak. Anda harus mengonfirmasi sebelum
apa pun dihapus. Model yang Anda gunakan dan file yang Anda tambahkan sendiri tidak pernah masuk daftar. Unduhan yang belum selesai
dan masih bisa dilanjutkan disimpan selama seminggu.

### Folder model dan memindahkan model

**Pengaturan > Folder model** menentukan ke mana unduhan baru disimpan. Biarkan kosong untuk folder default, atau ketik path lengkap
seperti `D:\Model` lalu tekan Enter. Model yang sudah diunduh tidak dipindahkan atau dihapus. Model tetap berada di tempatnya dan tetap
tercantum. Untuk memindahkannya, terima **Pindahkan model saya ke sana**. Model disalin, diperiksa, dan hanya setelah itu salinan lama
dihapus. Model yang sedang dipakai dibongkar selama proses ini, dan **Batalkan pemindahan** membiarkan semuanya di tempat semula.

### Anggaran memori dan bilah

Bilah **Anggaran memori** di halaman Model menampilkan RAM dan memori grafis: berapa yang dipakai model yang dimuat, berapa yang
dicadangkan saat model sedang dimuat, dan berapa yang masih bebas dibandingkan batas Anda (lihat bagian 7).
Pemuatan yang akan melewati batas memori Anda ditolak dengan alasan yang jelas sebelum apa pun dihentikan.

[screenshot: bilah Anggaran memori dengan dua model dimuat]

### Penempatan: kartu grafis, memori, atau prosesor saja

Tempat model berjalan ditentukan oleh **Pengaturan > Performa > Jalankan di** (Otomatis, Kartu grafis, Prosesor). Model yang tidak muat
di kartu grafis akan berjalan sebagian di prosesor (lebih lambat). Lihat juga penempatan per model di
bagian 11, yang mencakup **Hanya prosesor**.

### Batasan dan gratis versus berbayar

- Semua model dalam daftar gratis untuk penggunaan komersial (lisensi Apache 2.0 atau MIT); setiap model menampilkan lisensinya sendiri.
- Model diunduh dari halaman penerbitnya di Hugging Face. Host hanya menerima daftar model tepercaya yang ditandatangani. Jika tidak
  dapat memuat daftar itu, bilah **Daftar model tidak tersedia** akan muncul; model yang sudah ada di PC ini tetap berfungsi.

### Pemecahan masalah

- *Tidak ada model yang tercantum untuk diunduh:* periksa koneksi internet Anda dan tekan **Coba lagi** di Beranda.
- *Unduhan berhenti:* tekan **Lanjutkan**; jika ruang disk kurang, kosongkan ruang atau ubah folder.
- *Model "mungkin terlalu besar":* pilih model yang lebih kecil, atau turunkan beban di Pengaturan > Performa.

---

## 5. Aplikasi dan izin

Buka **Perangkat dan aplikasi** (Ctrl+3).

- **Menunggu jawaban Anda:** aplikasi yang meminta menggunakan AI akan muncul di sini dan sebagai permintaan ("Izinkan Voice Portal
  menggunakan AI di PC ini?"). Pilih **Izinkan**, **Jangan izinkan**, atau **Putuskan nanti**. Permintaan menampilkan apakah aplikasi
  adalah aplikasi Portal yang Terverifikasi, aplikasi terpasang yang bukan aplikasi Portal, atau belum terverifikasi (lalu ditampilkan dari
  mana aplikasi itu berjalan; izinkan hanya jika Anda sendiri yang menjalankannya).
- **Aplikasi yang bisa menggunakan host AI Anda:** setiap aplikasi yang diizinkan menampilkan permintaan hari ini dan kapan terakhir
  digunakan.
  - **Jeda** menolak permintaan baru sampai Anda melanjutkan; aplikasi tetap mempertahankan aksesnya.
  - **Prioritas** (Tinggi, Normal, Rendah) menentukan siapa yang didahulukan ketika beberapa aplikasi meminta bersamaan. Permintaan
    interaktif didahulukan sebelum pekerjaan batch, dan permintaan yang menunggu lama naik ke atas, sehingga aplikasi berprioritas
    rendah tidak pernah dibiarkan kelaparan.
  - **Hapus akses** menghentikan aplikasi sampai Anda mengizinkannya lagi. **Urungkan** muncul sebentar.

Aplikasi harus meminta izin sebelum dapat menggunakan host. Host hanya mendengarkan di PC ini kecuali Anda mengaktifkan berbagi
dengan perangkat lain (bagian 9).

[screenshot: halaman Perangkat dan aplikasi dengan satu aplikasi menunggu dan satu aplikasi diizinkan]

Untuk pengembang: host menawarkan API lokal yang kompatibel dengan OpenAI di `http://127.0.0.1:41870` (penyelesaian obrolan dengan
streaming, embedding, daftar model). Lihat `api-v1.md`.

**Tempat penyimpanannya:** aplikasi yang diizinkan dan tokennya disimpan di folder data host, dilindungi untuk pengguna Windows Anda.
Menghapus akses aplikasi juga menghapus tokennya.

---

## 6. Coba

**Coba** (Ctrl+4) memeriksa bahwa model berfungsi. Ketik pertanyaan singkat dan tekan **Tanya** (atau Ctrl+Enter). **Hentikan**
mengakhiri jawaban, **Bersihkan** mengosongkan halaman. Pertanyaan yang disarankan ditawarkan. Halaman menampilkan berapa lama kata-kata
pertama muncul dan kecepatan dalam token per detik. Apa pun yang Anda ketik di sini tidak disimpan.

[screenshot: Coba dengan jawaban dan baris waktu]

---

## 7. Pengaturan performa

**Pengaturan > Performa** mencegah AI memperlambat PC Anda. Perubahan langsung berlaku; model yang tidak lagi muat akan dimuat ulang
dengan batas baru atau dibongkar (tetap ada di disk). **Atur ulang ke default** mengembalikan bagian ini.

| Pengaturan | Fungsinya | Default |
|---|---|---|
| Performa | Preset: **Senyap** (sekitar setengah kecepatan, PC tetap responsif), **Seimbang** (kecepatan baik, program Anda diutamakan), **Cepat** (balasan tercepat, program lain bisa melambat), **Kustom** | Seimbang |
| Memori maksimum untuk AI | AI tidak pernah memakai memori lebih dari ini | 80% memori PC |
| Selalu sisakan | Memori yang tetap bebas untuk program Anda yang lain | Diatur oleh preset |
| Jalankan di | **Otomatis** (kartu grafis jika membantu), **Kartu grafis** (tercepat, jika model muat), **Prosesor** (lebih lambat, kartu grafis tetap bebas untuk game) | Otomatis |
| Memori grafis untuk AI | Batas memori grafis yang boleh dipakai AI | Diatur oleh preset |
| Inti prosesor | Lebih sedikit inti membuat PC tetap responsif | Diatur oleh preset |
| Prioritas | **Rendah** (pekerjaan Anda didahulukan) atau **Normal** | Rendah |
| Penggunaan prosesor maksimum | Batas atas penggunaan prosesor | Tanpa batas (100%) |
| Kurangi beban saat | AI berjalan dengan prioritas terendah dan memakai paling banyak setengah prosesor Anda, lalu kembali normal saat selesai. Pilih salah satu atau beberapa: game atau program layar penuh sedang berjalan, PC menggunakan baterai, aplikasi Portal sedang sibuk dengan pekerjaan langsung seperti dikte | Program layar penuh, baterai, dan aplikasi sibuk aktif |

Di bawah **Bebaskan memori saat menganggur** pada **Pengaturan > Umum**, Anda memilih **Biarkan model tetap dimuat**, **Setelah 5 menit**,
atau **Setelah 10 menit** (default 10 menit, 5 menit saat menggunakan baterai). Model akan dimuat lagi saat aplikasi memintanya
berikutnya. Membiarkan model tetap dimuat membuat jawaban lebih cepat, tetapi memakai memori sepanjang waktu.

Petunjuk pada penggeser menunjukkan apakah memori yang dipilih cukup untuk menampung model sepenuhnya di kartu grafis, sebagian di
prosesor, atau hanya di prosesor, dan menawarkan model yang lebih kecil jika model saat ini sudah tidak muat.

[screenshot: Pengaturan > Performa]

**Pemecahan masalah:** jawaban yang lambat pada PC dengan kartu grafis biasanya berarti model hanya sebagian muat di kartu grafis.
Pilih model yang lebih kecil, atau naikkan **Memori grafis untuk AI**. Jika PC Anda tersendat saat digunakan, pilih Senyap atau turunkan
**Penggunaan prosesor maksimum**.

---

## 8. Notifikasi

**Pengaturan > Notifikasi** mengatur pesan Windows singkat yang muncul ketika ada yang membutuhkan perhatian Anda dan jendela tertutup.
Pesan-pesan itu tetap senyap saat program layar penuh berada di depan, dan tidak diulang lebih dari sekali setiap beberapa menit.

| Sakelar | Artinya |
|---|---|
| Unduhan model selesai atau gagal | Aktif secara default |
| Aplikasi meminta persetujuan | Aktif secara default |
| Mesin AI berhenti atau pemeriksaan gagal | Aktif secara default |
| Perangkat dipasangkan atau tersambung dari luar | Aktif secara default (peringatan dalam bagian 10) |

Setiap sakelar dapat dimatikan sendiri-sendiri. **Atur ulang ke default** di halaman yang sama akan mengembalikannya.

---

## 9. Membiarkan perangkat lain menggunakan PC ini

**Pengaturan > Perangkat lain.** Mati secara default. Jika diaktifkan, laptop Anda sendiri atau PC lain di jaringan rumah dapat meminta
untuk menggunakan AI PC ini. Perangkat harus dipasangkan dengan kode terlebih dahulu, dan setiap aplikasi di perangkat itu tetap
memerlukan persetujuan Anda.

### Memasangkan perangkat

1. Aktifkan **Izinkan perangkat lain menggunakan AI PC ini**. Windows mungkin meminta izin untuk aturan jaringan.
2. Tekan **Pasangkan perangkat**. Kode dan kode QR akan muncul, dan **Salin tautan** menyalin tautan pemasangan.
3. Di perangkat lain, buka aplikasi Portal-nya (atau Portal AI Host dalam mode klien, bagian 12),
   pilih untuk terhubung ke PC, lalu pindai kode, ketik, atau tempel tautannya.
4. Perangkat memeriksa bahwa ia berbicara dengan PC ini sebelum mengirim apa pun, lalu menampilkan nama PC dan sidik jari untuk dikonfirmasi.

Kode hanya berlaku sekali, selama 5 menit, untuk satu perangkat. Percobaan yang salah akan menghabiskan kode; buat yang baru dengan
**Pasangkan perangkat**. **Batalkan pemasangan** mengakhiri proses.

[screenshot: kode pemasangan dan kode QR]

### Perangkat yang dipasangkan

Setiap perangkat yang dipasangkan tercantum dengan waktu terakhir digunakan dan tombol **Hapus**. Menghapus perangkat langsung menghentikan
aksesnya, dan permintaannya yang sedang berjalan akan berakhir. Perangkat harus dipasangkan ulang untuk kembali. Paling banyak **16**
perangkat dapat dipasangkan.

### Pengaturan lain di kartu ini

- **Juga jangkau PC ini dari luar rumah** (relay), lihat bagian 13. Default: aktif
  setelah perangkat lain diaktifkan.
- **Kunci akses relay**, lihat bagian 13. Default: mati.
- **Perangkat boleh mengganti model**, lihat bagian 11. Default: mati.

**Apa yang dilalui jaringan:** permintaan dan jawaban berpindah dengan terenkripsi di antara perangkat Anda sendiri. Sambungan jaringan
rumah menggunakan sertifikat yang disematkan perangkat saat pemasangan. Tidak ada yang dikirim ke server kami kecuali, jika Anda mau,
relay buta (bagian 13).

**Pemecahan masalah:** *Perangkat tidak dapat menemukan PC ini:* pastikan keduanya berada di jaringan yang sama, PC ini sedang aktif,
dan host berjalan. *Kode "tidak benar" atau "sudah habis":* buat yang baru. *"Bukan di jaringan rumah Anda":* pasangkan sekali di rumah
terlebih dahulu.

---

## 10. Perangkat di PC ini

Halaman **Perangkat PC ini** menampilkan satu kartu untuk setiap laptop yang dipasangkan. Di sinilah Anda melihat apa yang dilakukan
setiap laptop dan membatasinya.

[screenshot: Perangkat di PC ini dengan dua kartu perangkat]

### Yang ditampilkan kartu

- Status (Online, Menganggur, Offline), rute (Jaringan rumah atau Relay (di luar rumah)), dan terakhir terlihat.
- Yang sedang dijalankan sekarang, dan yang sedang menunggu.
- Permintaan dan token hari ini, dengan bilah 7 hari, per aplikasi.
- Model yang digunakan, siapa yang memuatnya, serta kecepatan dalam token per detik dan waktu hingga byte pertama.

### Kontrol

| Kontrol | Fungsinya |
|---|---|
| Jeda / Lanjutkan | Menjeda seluruh perangkat, atau salah satu aplikasinya. Laptop menerima jawaban "dijeda" yang jelas dan menggunakan cadangannya sendiri, jika Anda mengaturnya. |
| Ganti nama | Mengubah nama perangkat yang ditampilkan |
| Batas | Prioritas, batas harian, jam tenang, dan model yang diizinkan (di bawah) |
| Kontrol model (sakelar) | Memungkinkan perangkat ini memuat dan membongkar model. Lihat bagian 11 |
| Ambil kembali | Membongkar model yang dimuat perangkat ini. Pilihan PC ini kembali. Tidak ada file model yang tersentuh. |
| Lupakan | Menghapus perangkat. Aksesnya langsung berhenti dan riwayat penggunaannya dihapus. Model tidak tersentuh. |

**Batas** (di bawah tombol **Batas** pada kartu, lalu **Simpan**):

- **Prioritas sama seperti aplikasi PC ini.** Mati (default): aplikasi PC ini didahulukan. Aktif: perangkat ikut antre di barisan yang sama.
- **Permintaan per hari** dan **Token per hari.** Default: tanpa batas. Jika sudah habis, perangkat diberi tahu untuk kembali setelah
  tengah malam menurut waktu PC ini.
- **Jam tenang**, dengan **Dari** dan **Sampai** (waktu PC ini). Perangkat tidak menerima jawaban di antara jam tersebut. Default: mati.
- **Model yang boleh digunakan perangkat ini.** Default: semua model.

### Mode game

Berada di bagian atas halaman. **Default: mati.** Saat aktif, panggilan dari perangkat lain diberi tahu bahwa PC ini sedang sibuk
ketika Anda bermain, agar game tetap memegang kartu grafis. PC ini dianggap "sedang bermain" ketika Windows melaporkan game layar penuh,
atau ketika program lain membuat mesin 3D kartu grafis sibuk 40% atau lebih selama sekitar 10 detik, sehingga game dalam jendela atau
jendela tanpa bingkai juga dihitung. Mode ini berhenti sekitar 30 detik setelah beban berakhir. Jika penghitung kartu grafis tidak
tersedia di PC Anda, hanya pemeriksaan layar penuh yang digunakan. Laptop kemudian mengikuti cadangan sebagaimana diatur di pengaturannya sendiri.

### Peringatan

Notifikasi Windows muncul ketika perangkat baru dipasangkan, dan ketika perangkat terhubung lewat relay dari luar rumah. Matikan dengan
**Pengaturan > Notifikasi > Perangkat dipasangkan atau tersambung dari luar**.

### Data

Angka penggunaan hanya berupa jumlah (permintaan dan token per aplikasi per hari, disimpan 7 hari). Tidak ada teks permintaan atau jawaban
yang disimpan. **Lupakan** menghapus file penggunaan perangkat itu dan tidak ada yang lain.

---

## 11. Kontrol model dan dua model sekaligus

Secara default, laptop yang dipasangkan menggunakan model apa pun yang sedang dimuat PC ini. Anda dapat memberi perangkat lebih banyak.

### Kontrol model

**Pengaturan > Perangkat lain**, pada baris perangkat: **Kontrol model** (default: mati). Saat aktif, laptop dapat:
- memuat dan membongkar model yang terpasang di PC ini,
- memilih tempat model berjalan (Otomatis, Kartu grafis, Memori sistem, atau Hanya prosesor),
- memilih model PC ini yang digunakan setiap aplikasinya.

Laptop tidak pernah dapat mengunduh, menghapus, memeriksa, atau mengimpor model, mengubah pengaturan, atau menyentuh file Anda, dengan
atau tanpa izin ini. Matikan, dan panggilan berikutnya dari perangkat akan ditolak. Menghapus perangkat juga mencabut izin, dan memasangkan
ulang tidak mengembalikannya.

PC menunjukkan siapa yang memuat apa: "Dimuat oleh <perangkat>: <model> (kartu grafis)". **Ambil kembali** (pada kartu dan di Pengaturan)
membongkar model yang dimuat perangkat. Pilihan PC ini kembali dan model dimuat saat permintaan berikutnya.

### Dua model sekaligus

Dengan Kontrol model, laptop dapat menyimpan model kedua tetap dimuat di PC ini, misalnya satu di kartu grafis dan satu di memori sistem.
- Model Anda sendiri tetap berada di slot pertama. Model perangkat masuk ke slot kedua, sehingga perangkat tidak pernah mendorong keluar
  model Anda hanya untuk mendapatkan tempatnya sendiri.
- Setiap slot memiliki antrean sendiri, sehingga dua model dapat menjawab pada saat yang sama. Permintaan yang menyebut model kedua
  diarahkan ke sana; yang lain diarahkan ke model Anda.
- Sebelum memuat, host menghitung apa yang sudah dipakai slot lain terhadap batas Anda. Model kedua yang tidak muat akan ditolak
  ("memori tidak cukup"). Tidak ada yang dihentikan diam-diam.
- Pilihan Anda selalu didahulukan. Jika Anda memilih model lain, membongkar salah satunya, atau model Anda membutuhkan memori, model
  perangkat dihentikan dan perangkat diberi tahu.
- Model kedua perangkat tidak pernah dimuat selamanya; seperti model lainnya, ia dibongkar saat menganggur.
- **Hanya prosesor** menjalankan model tanpa lapisan grafis, menggunakan sekitar setengah prosesor, misalnya model pembantu di prosesor
  sementara model lain menggunakan kartu grafis.

[screenshot: halaman Model dengan dua model yang dimuat oleh pemilik berbeda]

### Perangkat boleh mengganti model

**Pengaturan > Perangkat lain > Perangkat boleh mengganti model** (default: mati). Mati: perangkat tidak dapat membongkar model yang sedang
dipakai aplikasi Anda. Aktif: perangkat dapat melakukannya, dan aplikasi Anda memuat ulang model mereka pada permintaan berikutnya.
Jika ada dua perangkat, perangkat yang pertama memuat akan tetap memegang modelnya.

---

## 12. Menggunakan PC rumah dari laptop (mode klien)

Mode klien membuat Portal AI Host di laptop menggunakan AI dari PC lain milik Anda, alih-alih menjalankan model sendiri. Aplikasi di laptop
bekerja seperti biasa.

### Pengaturan

1. Di PC rumah, aktifkan **Izinkan perangkat lain menggunakan AI PC ini** dan tekan **Pasangkan perangkat** (bagian 9).
   Tekan **Salin tautan** dan kirim tautannya ke laptop.
2. Di laptop, buka **Pengaturan > PC rumah** (atau pilih **Gunakan PC rumah saya** di jendela selamat datang).
3. Di **Tempat AI berjalan**, pilih **Gunakan PC rumah saya**. Jendela **Gunakan PC rumah saya** akan terbuka. Tempel tautan dengan
   **Tempel tautan**, tekan **Berikutnya**, periksa nama PC dan sidik jari, lalu **Pasangkan**. Beri nama laptop.
4. Beranda sekarang menampilkan nama PC dan statusnya: **Tersambung - jaringan rumah**, **Tersambung - lewat relay**,
   **Menyambungkan...**, atau **Tidak dapat dijangkau**.

[screenshot: jendela pemasangan di laptop]

### Pengaturan (Pengaturan > PC rumah)

| Pengaturan | Pilihan | Default |
|---|---|---|
| Tempat AI berjalan | PC ini / Gunakan PC rumah saya | PC ini |
| Jika PC rumah tidak dapat dijangkau | **Tidak ada** (aplikasi menggunakan AI bawaannya sendiri) / **Gunakan model laptop ini** (dimuat saat diperlukan) | Tidak ada |
| Koneksi di luar rumah | **Otomatis** / **Manual** (lalu sakelar **Gunakan relay** hanya berlaku untuk sesi ini dan diatur ulang saat aplikasi ditutup) | Otomatis |

- Saat pertama kali PC rumah tidak dapat dijangkau, Anda ditanya sekali apa yang harus dilakukan; Anda dapat mengubahnya di sini kapan saja.
- **Pasangkan PC** memasangkan ulang; **Putuskan pasangan** melupakan PC (model dan unduhan tetap seperti semula); **Ganti PC** (di Beranda)
  membuka pengaturan ini; **Jalankan AI di sini** (di Beranda) keluar dari mode klien dan tetap menyimpan pasangan.
- Dengan Kontrol model yang diberikan oleh PC rumah, muncul **Model di <nama PC>** dengan tombol Muat, Bongkar, dan penempatan,
  serta **Model PC untuk setiap aplikasi**. Tanpa itu, halaman menulis: "Mintalah pemilik <nama PC> untuk mengaktifkan Kontrol model untuk
  laptop ini".
- Laptop dalam mode klien tidak menerima koneksi dari perangkat lain dan tidak dapat menjadi perantara ke PC rumah Anda.

### Apa yang terjadi pada model laptop ini sendiri

Saat masuk mode klien, model yang dimuat di laptop dibongkar untuk membebaskan memori. File model, unduhan, dan folder model tidak
tersentuh. Saat keluar, model dimuat lagi ketika diminta.

### Pemecahan masalah

- **Tidak dapat dijangkau:** pastikan PC rumah aktif dan berada di jaringan yang sama, atau aktifkan relay.
- **"Laptop ini telah dihapus dari PC":** PC rumah menghapusnya. Pilih **Pasangkan lagi**.
- **"Identitas <PC> berubah":** sertifikat PC tidak lagi cocok dengan yang disimpan laptop. Jika Anda tidak mengharapkan ini, jangan
  pasangkan lagi. Jika tidak, putuskan pasangan, lalu pasangkan lagi.
- **Pemasangan dari luar gagal:** pasangkan sekali di rumah terlebih dahulu; tautan pemasangan harus menunjuk ke alamat di jaringan rumah Anda.
- **Dijeda, jam tenang, game sedang berjalan, atau batas harian sudah habis:** PC memberi tahu laptop alasannya; laptop menggunakan cadangannya.

**Tempat penyimpanannya:** laptop menyimpan pemasangannya di folder data host, di bawah `client\`. Kunci perangkat dan token dilindungi untuk
pengguna Windows Anda. **Putuskan pasangan** menghapusnya. Cadangan folder ini tidak dapat dipulihkan di pengguna Windows lain.

---

## 13. Di luar rumah: relay dan kunci akses relay

Relay memungkinkan perangkat yang dipasangkan menjangkau PC Anda dari mana saja. **Pengaturan > Perangkat lain > Juga jangkau PC ini
dari luar rumah.**

- PC Anda terhubung ke layanan relay kecil. Relay hanya meneruskan byte yang teracak yang tidak dapat dibacanya, dan tidak menyimpan
  konten apa pun. Setiap perangkat tetap harus dipasangkan dan menandatangani setiap permintaan.
- Baris status menampilkan **Dapat dijangkau dari luar rumah**, **Hanya jaringan rumah**, **Menghubungkan ke relay...**, atau
  **Tidak dapat terhubung ke relay**.
- Relay bekerja melalui port web biasa, sehingga sebagian besar jaringan kantor mengizinkannya.
- **Perangkat perusahaan:** konten kerja akan masuk ke PC pribadi Anda. Periksa aturan pemberi kerja Anda terlebih dahulu.

### Kunci akses relay

**Pengaturan > Perangkat lain > Kunci akses relay** (default: mati). Saat aktif, hanya perangkat yang dipasangkan dengan kunci dalam
kode pemasangan yang dapat menjangkau PC ini melalui relay, yang mencegah orang asing menghabiskan slot relay untuk PC Anda.

- Aktifkan **sebelum memasangkan**. Perangkat yang dipasangkan sebelumnya harus dipasangkan ulang untuk menggunakan relay (jaringan rumah tetap berfungsi).
- **Kunci baru** membuat kunci baru setelah konfirmasi. Setiap perangkat yang dipasangkan harus dipasangkan ulang untuk menggunakan relay; di rumah tidak
  ada yang berubah. Menghapus perangkat tidak mengubah kunci, jadi gunakan **Kunci baru** setelah menghapus perangkat yang tidak Anda percayai.
- Jika file kunci tidak dapat digunakan, relay tetap mati, bukan terbuka, dan jaringan rumah tetap berfungsi. File kunci yang rusak
  disingkirkan dengan nama berakhiran `.bad` dan tidak pernah ditimpa.
- Jika sakelar tidak mau tetap aktif, layanan relay mungkin perlu diperbarui di pihaknya. Matikan sakelar lagi.

**Batas:** relay membatasi seberapa cepat dan seberapa banyak sebuah perangkat dapat mengirim, agar satu perangkat tidak membanjiri layanan.
Penggunaan normal jauh di bawah batas ini.

---

## 14. Privasi dan laporan crash

- **Tidak ada analitik atau pelacakan penggunaan.** Hanya laporan crash yang dapat keluar dari PC Anda, dan hanya jika Anda membiarkannya aktif.
- **Teks Anda tetap berada di perangkat Anda.** Permintaan dan jawaban tidak pernah disimpan atau dicatat oleh host. Log hanya berisi
  jumlah, waktu, dan status.
- **Laporan crash aktif secara default dan Anda dapat mematikannya.** Jika jendela atau host latar belakang crash, laporan singkat
  membantu kami memperbaikinya. Matikan kapan saja di **Pengaturan > Privasi > Kirim laporan crash**; berhenti seketika. Jendela selamat
  datang memiliki pilihan yang sama, dicentang secara default, sebelum apa pun dikirim.
  - **Dikirim:** jenis kesalahan dan lokasinya (nama fungsi dan modul), versi aplikasi dan Windows, apakah ini build Store, dan apakah
    berasal dari jendela atau host.
  - **Tidak pernah dikirim:** permintaan atau jawaban, apa pun yang Anda ketik, nama file atau path folder, nama komputer atau pengguna Anda,
    aplikasi mana yang menggunakan host, atau alamat IP Anda. Teks kesalahan dibuang.
  - **Disimpan selama 90 hari**, lalu dihapus.
  - **Yang dikirim** (Pengaturan > Privasi) membuka halaman privasi.
- **Semuanya dienkripsi** saat meninggalkan PC ini menuju perangkat lain milik Anda.
- **Kebijakan privasi:** Tentang > **Kebijakan privasi**.
- Pertanyaan, atau permintaan untuk menghapus laporan: mindthaapp@gmail.com.

[screenshot: Pengaturan > Privasi dengan sakelar laporan crash]

---

## 15. Tempat data disimpan, pencadangan, dan penghapusan

Semuanya berada di folder data aplikasi. **Tentang > Buka folder data** membukanya.

| Apa | Lokasi (di dalam folder data) | Dicadangkan? |
|---|---|---|
| Model yang diunduh | `models\`, atau `Portal AI Host Models\` di dalam folder yang Anda pilih di Pengaturan | Opsional. Dapat diunduh lagi. Ukurannya besar. |
| Pengaturan | `settings.json` | Ya |
| Aplikasi yang diizinkan dan tokennya | Penyimpanan aplikasi host | Tidak dapat dipindahkan (dilindungi untuk pengguna Windows Anda) |
| Perangkat yang dipasangkan, batas, statistik penggunaan | Penyimpanan perangkat host dan satu file statistik kecil per perangkat | Tidak dapat dipindahkan |
| Pemasangan laptop (mode klien) | `client\` | Tidak dapat dipindahkan. Pasangkan lagi di PC baru. |
| Log | `logs\` (bergulir, dibatasi 10 MB) | Tidak |
| File penemuan untuk aplikasi | `discovery\host.json` | Tidak |

**Pencadangan:** tutup Portal AI Host, lalu salin folder data. Pengaturan dan model dapat dipulihkan dengan menyalinnya kembali. Izin aplikasi,
pemasangan perangkat, dan token dilindungi untuk pengguna Windows Anda dan PC Anda, jadi di PC baru Anda perlu mengizinkan aplikasi dan
memasangkan perangkat lagi.

**Penghapusan:**
- *Satu model:* **Model > Hapus**. Hanya file yang diunduh host yang dihapus.
- *Sisa-sisa:* **Model > Bebaskan ruang**.
- *Satu perangkat:* **Lupakan** (Perangkat PC ini) atau **Hapus** (Pengaturan).
- *Akses satu aplikasi:* **Hapus akses** (Perangkat dan aplikasi).
- *Semuanya:* copot aplikasinya. Folder model khusus yang Anda pilih dibiarkan di tempatnya; hapus folder `Portal AI Host Models`
  sendiri jika Anda ingin menghapusnya.

Aplikasi tidak pernah menghapus apa pun yang tidak dibuatnya, tidak pernah menimpa file Anda, dan model yang Anda tambahkan sendiri hanya
dihapus sebagai salinan milik host (file asli Anda tidak tersentuh).

---

## 16. Pintasan keyboard

| Tombol | Tindakan |
|---|---|
| Ctrl+1 | Beranda |
| Ctrl+2 | Model |
| Ctrl+3 | Perangkat dan aplikasi |
| Ctrl+4 | Coba |
| Ctrl+, | Pengaturan |
| F1 | Tentang |
| Ctrl+Enter | Tanya, di halaman Coba |
| Esc | Tutup Yang baru atau dialog |

Daftar ini juga ada di menu aplikasi > **Pintasan keyboard**.

Tampilan: **Pengaturan > Tampilan** menawarkan Tema (default: pengaturan Windows Anda), Material jendela (Mica), Sudut
(Membulat), Kepadatan (Standar), Ukuran teks (Normal), dan Gerakan (Ikuti Windows). Mode kontras tinggi Windows selalu menggunakan
warna sistem yang solid.

---

## 17. Pemecahan masalah dan FAQ

**Host mengatakan tidak berjalan.** Tekan **Mulai Portal AI Host** di Beranda. Jika tidak bisa dimulai, mulai ulang PC; jika itu gagal,
pasang ulang dari Store.

**"Pengguna Windows lain di PC ini sudah menjalankan Portal AI Host."** Minta pengguna itu untuk keluar, atau masuk sebagai dia.

**Jawaban lambat.** Lihat bagian 7. Model yang terlalu besar untuk memori grafis Anda berjalan sebagian di prosesor.

**Sebuah aplikasi mengatakan tidak ada AI yang tersedia.** Pastikan aplikasi diizinkan (Perangkat dan aplikasi), tidak dijeda, dan model
terpasang serta tidak terlalu besar untuk PC ini.

**Jawaban pertama lambat, berikutnya cepat.** Model sedang beristirahat. Model dimuat pada permintaan pertama. Pilih **Biarkan model tetap
dimuat** di Pengaturan > Umum untuk menghindarinya.

**Apakah data saya dikirim ke suatu tempat?** Tidak. Permintaan dan jawaban tetap berada di perangkat Anda. Hanya laporan crash yang dapat
keluar (bagian 14), dan Anda dapat mematikannya.

**Bisakah saya menggunakannya tanpa internet?** Bisa, setelah model diunduh.

**Apakah ini berbayar?** Tidak.

**Model mana yang sebaiknya saya pilih?** Yang direkomendasikan. Pilih yang lebih kecil jika PC Anda terasa lambat, atau yang lebih besar jika
Anda menginginkan jawaban yang lebih baik dan memorinya tersedia.

**Unduhan macet di "Memeriksa unduhan".** Tunggu. File besar membutuhkan waktu satu menit untuk diperiksa.

**Pemasangan mengatakan "terlalu banyak perangkat".** Batasnya 16 perangkat. Hapus salah satunya.

**Di mana ikon area notifikasi?** Klik panah di dekat jam untuk menampilkan ikon tersembunyi.

Jika Anda masih kesulitan, gunakan **Tentang > Salin versi**, lalu tulis ke mindthaapp@gmail.com dan tempel versinya di pesan Anda.

---

## 18. Yang akan datang

Ini sudah direncanakan dan belum ada di aplikasi saat ini.

- **Kategori model di halaman Model:** dikelompokkan berdasarkan tugas (obrolan dan penulisan, pemrograman, penalaran, visi, embedding,
  ucapan) dengan filter dan pencarian.
- **Pembuatan gambar dan video** di PC ini, termasuk "sutradara" yang merangkai bidikan menjadi video. Model video akan muncul sebagai
  "Segera hadir" sampai mesinnya tersedia.
- **Memilih model yang menjawab**, dan model yang saling meminta bantuan, di AI Portal.
- **Versi macOS.**

---

## 19. Dukungan

- **Masukan dan ide:** Tentang > **Kirim masukan atau usulkan ide**.
- **Pertanyaan dukungan dan privasi:** mindthaapp@gmail.com.
- **Versi:** Tentang menampilkannya, dan **Salin versi** menyalinnya ke papan klip untuk pesan Anda.
- **Lisensi:** Portal AI Host menyertakan llama.cpp (lisensi MIT), yang menjalankan model di PC Anda. Model diunduh berdasarkan
  lisensinya masing-masing (Apache 2.0 atau MIT). Lihat Tentang.
