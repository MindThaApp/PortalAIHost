# Daftar Store (Bahasa Indonesia, id-id)

Tempel teks-teks ini di Partner Center > pengiriman > Store listings > Bahasa Indonesia. Ini terjemahan dari listing-en.md;
sebelum dikirim, teksnya harus diperiksa oleh penutur asli. Catatan sertifikasi, rating usia, dan pembenaran kapabilitas
runFullTrust tetap dalam bahasa Inggris (hanya di listing-en.md).

## Nama aplikasi

Portal AI Host

## Deskripsi singkat (maks. 1.000 karakter)

Jalankan model AI lokal di PC Anda sendiri dan bagikan ke aplikasi Portal Anda. Satu klik memilih model terbaik untuk perangkat
keras Anda, mengunduhnya, dan menyiapkannya. Teks Anda tetap di PC ini: tanpa akun, tanpa cloud, tanpa langganan.

## Deskripsi

Portal AI Host memberi PC Anda model AI sendiri, dan membiarkan aplikasi Portal lainnya menggunakannya.

Alih-alih setiap aplikasi mengunduh modelnya sendiri, atau mengirim teks Anda ke layanan cloud, Portal AI Host menyimpan satu model
di PC Anda dan membagikannya ke aplikasi yang Anda izinkan. Voice Portal dan aplikasi Portal lainnya kemudian dapat membuat ringkasan,
notulen, dan jawaban dengan model yang lebih besar dan lebih baik daripada yang bisa mereka bawa sendiri.

Mudah diatur
- Saat pertama dijalankan, aplikasi memeriksa PC Anda (memori, kartu grafis, ruang disk kosong) dan merekomendasikan model terbaik
  yang berjalan baik di PC tersebut, dengan penjelasan yang sederhana.
- Satu klik untuk mengunduh. Unduhan dapat dijeda dan dilanjutkan, dan setiap file diperiksa sebelum digunakan.
- Tidak ada yang perlu dipasang lagi: mesinnya sudah termasuk. Tanpa Python, tanpa baris perintah, tanpa alat tambahan.

Privat sejak dari desainnya
- Model berjalan di PC ini. Permintaan dan jawaban tidak pernah meninggalkannya.
- Tanpa akun dan tanpa masuk.
- Tanpa analitik. Laporan crash yang bersifat opsional tidak pernah menyertakan file Anda atau apa yang Anda ketik, dan Anda dapat
  mematikannya. Permintaan Anda tidak pernah disimpan atau dicatat.
- Host hanya mendengarkan di PC ini (loopback). Komputer lain di jaringan Anda tidak dapat menjangkaunya.

Anda tetap memegang kendali
- Aplikasi harus meminta izin sebelum dapat menggunakan host, dan Andalah yang memutuskan: izinkan, tolak, atau putuskan nanti. Anda
  dapat mencabut akses aplikasi kapan saja.
- Host menunjukkan apakah aplikasi yang meminta sudah terverifikasi (penerbitnya) atau belum, sehingga Anda tahu siapa yang meminta.
- Pilih tempat model disimpan, dan hapus model yang tidak lagi Anda perlukan. Host hanya menghapus file yang diunduhnya sendiri.
- Mulai saat Windows berjalan bersifat opsional dan mati sampai Anda menyalakannya.

Bekerja dengan perangkat keras Anda
- Menggunakan kartu grafis Anda melalui Vulkan (AMD, NVIDIA, dan Intel), dan beralih ke prosesor jika tidak ada GPU yang sesuai.
- Membebaskan memori saat model menganggur, atau tetap menyimpannya dalam memori jika Anda lebih suka.

Untuk pengembang
- Host menawarkan API standar yang kompatibel dengan OpenAI di 127.0.0.1 (penyelesaian obrolan dengan streaming, embedding, dan daftar
  model), sehingga pustaka klien yang sudah ada dapat digunakan. Setiap aplikasi memerlukan izin pengguna terlebih dahulu.

Model diunduh dari halaman penerbitnya di Hugging Face. Setiap model memiliki lisensinya sendiri; aplikasi hanya menawarkan model
yang lisensinya mengizinkan penggunaan komersial (Apache 2.0 atau MIT).

## Fitur (Partner Center "Product features", maks. 20, 200 karakter masing-masing)

1. Menjalankan model AI secara lokal di PC Anda, tanpa perlu cloud
2. Satu model dibagikan ke semua aplikasi Portal Anda
3. Merekomendasikan model terbaik untuk perangkat keras Anda
4. Unduh satu klik dengan jeda, lanjutkan, dan pemeriksaan file
5. Menggunakan kartu grafis Anda (Vulkan) dengan cadangan CPU otomatis
6. Tanpa akun, tanpa masuk, tanpa langganan
7. Tanpa analitik; laporan crash bersifat opsional dan dibersihkan; permintaan tidak pernah disimpan atau dicatat
8. Hanya mendengarkan di PC ini (loopback)
9. Aplikasi harus meminta dulu; Anda mengizinkan, menolak, atau mencabut akses
10. Menampilkan apakah aplikasi yang meminta sudah terverifikasi
11. Membebaskan memori saat menganggur, atau tetap menyimpan model dalam memori
12. Pilih folder untuk model Anda
13. Mulai bersama Windows (opsional), dengan ikon area notifikasi
14. Kotak "Coba" untuk memastikan model berfungsi
15. API lokal yang kompatibel dengan OpenAI untuk pengembang
16. Pembagian yang adil ketika beberapa aplikasi meminta bersamaan
17. Pulih sendiri jika mesin berhenti

## Kata kunci pencarian (7)

1. AI lokal
2. AI offline
3. AI privat
4. LLM
5. model AI
6. model bahasa
7. server AI

Tidak ada nama produk pihak lain dalam kata kunci (kebijakan Store 10.1.3).

## Yang baru di versi ini (1.0)

Rilis pertama. Unduh model AI lokal dengan satu klik dan bagikan ke aplikasi Portal Anda, secara privat, di PC Anda sendiri.

## Persyaratan sistem (teks daftar)

Minimum (diatur di Partner Center > Properti > Persyaratan sistem):
- Windows 11 (versi 21H2, build 22000) atau lebih baru, x64 atau Arm64.
- Memori: 8 GB.
- Ruang disk kosong: 5 GB (aplikasinya sendiri kecil; model berukuran 2 hingga 20 GB, model opsional yang lebih besar hingga sekitar 140 GB).

Direkomendasikan:
- Memori: 16 GB atau lebih (32 GB atau lebih untuk model besar).
- Grafis: kartu grafis dengan dukungan Vulkan dan memori video 8 GB atau lebih (AMD, NVIDIA, atau Intel).
- Ruang disk kosong: 25 GB.

Persyaratan sistem tambahan (teks di bawah deskripsi):
> Paling baik dengan memori 16 GB dan kartu grafis dengan memori video 8 GB. Tanpa kartu grafis yang sesuai, model berjalan di
> prosesor, yang lebih lambat. PC Arm64 menjalankan model di prosesor. Model membutuhkan ruang disk kosong 2 hingga 20 GB.
