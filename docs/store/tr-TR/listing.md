# Mağaza kaydı (Türkçe, tr-tr)

Bu metinleri Partner Center > gönderim > Store listings > Türkçe bölümüne yapıştırın. Bu, listing-en.md dosyasının çevirisidir;
gönderimden önce ana dili Türkçe olan biri tarafından gözden geçirilmelidir. Sertifikasyon notları, yaş derecelendirmesi ve
runFullTrust yetki gerekçesi İngilizce kalır (yalnızca listing-en.md içinde).

## Uygulama adı

Portal AI Host

## Kısa açıklama (en fazla 1000 karakter)

Bilgisayarınızda yerel bir yapay zeka modeli çalıştırın ve bunu Portal uygulamalarınızla paylaşın. Tek tıkla donanımınıza en uygun
modeli seçer, indirir ve hazır tutar. Metniniz bu bilgisayarda kalır: hesap yok, bulut yok, abonelik yok.

## Açıklama

Portal AI Host, bilgisayarınıza kendi yapay zeka modelini verir ve diğer uygulamalarınızın bu modeli kullanmasını sağlar.

Her uygulamanın kendi modelini indirmesi ya da metninizi bir bulut hizmetine göndermesi yerine, Portal AI Host bilgisayarınızda tek
bir model tutar ve bunu izin verdiğiniz uygulamalarla paylaşır. Voice Portal ve diğer Portal uygulamaları, tek başlarına taşıyamayacakları
daha büyük ve daha iyi bir modelle özet, toplantı notu ve yanıt yazabilir.

Kurulumu kolay
- İlk çalıştırmada uygulama bilgisayarınızı (bellek, ekran kartı, boş disk alanı) denetler ve üzerinde iyi çalışan en iyi modeli,
  açık bir açıklamayla önerir.
- Tek tıkla indirir. İndirmeler duraklatılabilir ve devam ettirilebilir; her dosya kullanılmadan önce denetlenir.
- Başka bir şey kurmanız gerekmez: motor uygulamanın içinde hazırdır. Python yok, komut satırı yok, ek araç yok.

Tasarımı gereği özel
- Model bu bilgisayarda çalışır. İstemler ve yanıtlar hiçbir zaman bilgisayarınızdan çıkmaz.
- Hesap ve oturum açma gerekmez.
- Analitik yoktur. İsteğe bağlı çökme raporları dosyalarınızı ya da yazdıklarınızı hiçbir zaman içermez ve bunları kapatabilirsiniz.
  İstemleriniz asla kaydedilmez veya günlüğe yazılmaz.
- Ana bilgisayar yalnızca bu bilgisayarı dinler (loopback). Ağınızdaki diğer bilgisayarlar ona erişemez.

Kontrol sizde
- Bir uygulamanın ana bilgisayarı kullanabilmesi için önce izin istemesi gerekir ve kararı siz verirsiniz: izin ver, reddet veya
  daha sonra karar ver. Bir uygulamanın erişimini istediğiniz zaman kaldırabilirsiniz.
- Ana bilgisayar, bir uygulamanın doğrulanmış (yayıncısı belli) olup olmadığını gösterir; böylece kimin istediğini bilirsiniz.
- Modellerin nerede saklanacağını seçin ve artık ihtiyacınız olmayan modelleri kaldırın. Ana bilgisayar yalnızca kendisinin indirdiği
  dosyaları siler.
- Windows ile birlikte başlatma isteğe bağlıdır ve siz açana kadar kapalıdır.

Donanımınızla çalışır
- Vulkan üzerinden ekran kartınızı kullanır (AMD, NVIDIA ve Intel) ve uygun bir ekran kartı yoksa işlemciye geçer.
- Model boştayken belleği boşaltır veya isterseniz modeli yüklü tutar.

Geliştiriciler için
- Ana bilgisayar, 127.0.0.1 adresinde standart bir OpenAI uyumlu API sunar (akışlı sohbet tamamlama, gömmeler ve model listesi);
  bu yüzden mevcut istemci kitaplıkları çalışır. Her uygulamanın önce kullanıcının izni gerekir.

Modeller, yayıncılarının Hugging Face sayfalarından indirilir. Her modelin kendi lisansı vardır; uygulama yalnızca lisansı ticari
kullanıma izin veren modelleri (Apache 2.0 veya MIT) sunar.

## Özellikler (Partner Center "Product features", en fazla 20, her biri 200 karakter)

1. Bilgisayarınızda yerel olarak bir yapay zeka modeli çalıştırır, bulut gerekmez
2. Tüm Portal uygulamalarınız için tek bir model
3. Donanımınız için en iyi modeli önerir
4. Duraklatma, devam ettirme ve dosya denetimiyle tek tıkla indirme
5. Ekran kartınızı (Vulkan) kullanır ve otomatik olarak işlemciye geçer
6. Hesap yok, oturum açma yok, abonelik yok
7. Analitik yok; çökme raporları isteğe bağlı ve temizlenmiş; istemler asla kaydedilmez veya günlüğe yazılmaz
8. Yalnızca bu bilgisayarı dinler (loopback)
9. Uygulamalar önce izin ister; siz izin verir, reddeder veya erişimi kaldırırsınız
10. İzin isteyen uygulamanın doğrulanmış olup olmadığını gösterir
11. Boşken belleği boşaltır veya modeli yüklü tutar
12. Modeller için klasör seçimi
13. Bildirim alanı simgesiyle isteğe bağlı Windows ile başlatma
14. Modelin çalıştığını denemek için "Dene" kutusu
15. Geliştiriciler için OpenAI uyumlu yerel API
16. Birden fazla uygulama aynı anda istediğinde adil paylaşım
17. Motor durursa kendini kendiliğinden onarır

## Arama terimleri (anahtar kelimeler, 7)

1. yerel yapay zeka
2. çevrimdışı yapay zeka
3. özel yapay zeka
4. LLM
5. yapay zeka modeli
6. dil modeli
7. yapay zeka sunucusu

Anahtar kelimelerde başka ürünlerin adları kullanılmaz (Store ilkesi 10.1.3).

## Bu sürümdeki yenilikler (1.0)

İlk sürüm. Yerel bir yapay zeka modelini tek tıkla indirin ve Portal uygulamalarınızla, bilgisayarınızda özel olarak paylaşın.

## Sistem gereksinimleri (liste metni)

Minimum (Partner Center > Özellikler > Sistem gereksinimleri bölümünde ayarlanır):
- Windows 11 (21H2 sürümü, 22000 derlemesi) veya üstü, x64 veya Arm64.
- Bellek: 8 GB.
- Boş disk alanı: 5 GB (uygulamanın kendisi küçüktür; modeller 2 ila 20 GB, daha büyük isteğe bağlı modeller yaklaşık 140 GB'a kadar).

Önerilen:
- Bellek: 16 GB veya daha fazla (büyük modeller için 32 GB veya daha fazla).
- Grafik: 8 GB veya daha fazla video belleğine sahip, Vulkan destekli bir ekran kartı (AMD, NVIDIA veya Intel).
- Boş disk alanı: 25 GB.

Ek sistem gereksinimleri (açıklamanın altındaki metin):
> En iyi sonucu 16 GB bellek ve 8 GB video belleğine sahip bir ekran kartıyla verir. Uygun bir ekran kartı yoksa model işlemci
> üzerinde çalışır; bu daha yavaştır. Arm64 bilgisayarlar modeli işlemci üzerinde çalıştırır. Modeller için 2 ila 20 GB boş
> disk alanı gerekir.
