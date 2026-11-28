# Portal AI Host: kullanıcı kılavuzu

Portal AI Host, bilgisayarınızda bir yapay zeka modeli çalıştırır ve bunu Portal uygulamalarınızla paylaşır. Sorularınız ve
dosyalarınız kendi cihazlarınızda kalır. Bu kılavuz uygulamanın her bölümünü, ayarların ne işe yaradığını ve bir sorun
çıktığında ne yapmanız gerektiğini açıklar.

Bir resmin yer alması gereken yerler `[screenshot: ...]` (ekran görüntüsü: ...) olarak işaretlenmiştir.

## İçindekiler

1. Hızlı başlangıç
2. Sistem gereksinimleri, kurulum, güncelleme, kaldırma
3. Uygulamada gezinme
4. Modeller
5. Uygulamalar ve izinler
6. Dene
7. Performans ayarları
8. Bildirimler
9. Diğer cihazların bu bilgisayarı kullanmasına izin vermek
10. Bu bilgisayardaki cihazlar
11. Model kontrolü ve aynı anda iki model
12. Ev bilgisayarınızı dizüstü bilgisayardan kullanmak (istemci modu)
13. Evin dışında: aktarıcı ve aktarıcı erişim anahtarı
14. Gizlilik ve çökme raporları
15. Verilerin nerede saklandığı, yedekleme ve kaldırma
16. Klavye kısayolları
17. Sorun giderme ve sık sorulan sorular
18. Daha sonra gelecekler
19. Destek

---

## 1. Hızlı başlangıç

1. Portal AI Host'u Microsoft Store'dan yükleyin ve açın.
2. Karşılama penceresi bilgisayarınızı (bellek, ekran kartı, boş disk alanı) denetler ve bir model önerir. **Önerilen
   ayarlarla başla** seçeneğini veya modeli kendiniz seçmek için **İndir** seçeneğini kullanın. **Daha sonra seç** bu adımı
   atlar.
3. Saatin yanındaki bildirim alanında simgeyi arayın. Portal AI Host orada sessizce bekler. Kısa bir durum bilgisi için
   simgeye tıklayın.
4. **Dene** sayfasını açın ve modelin çalıştığını doğrulamak için kısa bir soru sorun.
5. Bir Portal uygulaması ilk kez yapay zeka kullanmak istediğinde, izin verip vermeyeceğinizi soran bir istem çıkar.

[screenshot: önerilen modelin gösterildiği karşılama penceresi]

Yapay zekayı ev bilgisayarınızdan kullanması gereken bir dizüstü bilgisayarda, karşılama penceresinde **Ev bilgisayarımı
kullan** seçeneğini seçin. Bkz. bölüm 12.

**Ücretsiz mi, ücretli mi:** Portal AI Host ücretsizdir. Bu kılavuzdaki hiçbir özellik için hesap, oturum açma veya satın alma
gerekmez.

---

## 2. Sistem gereksinimleri, kurulum, güncelleme, kaldırma

### Gereksinimler

| | Minimum | Önerilen |
|---|---|---|
| Windows | Windows 11 (21H2, derleme 22000) veya üstü, x64 veya Arm64 | Aynısı |
| Bellek | 8 GB | 16 GB veya daha fazla (büyük modeller için 32 GB veya daha fazla) |
| Grafik | Yok (model işlemci üzerinde çalışır, daha yavaştır) | Vulkan destekli, 8 GB veya daha fazla video belleğine sahip ekran kartı (AMD, NVIDIA veya Intel) |
| Boş disk alanı | 5 GB | 25 GB |

Her model 2 ila 20 GB'tır. Arm64 bilgisayarlar modeli işlemci üzerinde çalıştırır. İnternet bağlantısı yalnızca modelleri
indirmek ve aktarıcı için gerekir; bir model bilgisayarınızdaysa çevrimdışı çalışır.

### Kurulum

Uygulamayı Microsoft Store'dan yükleyin. Başka bir şey gerekmez: Python, komut satırı ya da başka bir yazılım yok. Yapay zeka
motoru uygulamanın bir parçasıdır.

### Windows ile birlikte başlat

**Ayarlar > Genel > Windows ile birlikte başlat** (varsayılan: kapalı; karşılama penceresindeki "Önerilen ayarlarla başla"
seçeneği hariç, bu seçenek bunu açar). Açık olduğunda Portal AI Host, oturum açtıktan sonra bildirim alanında bekler, böylece
uygulamalar hemen kullanabilir.
- Anahtar yalnızca yüklü uygulamada kullanılabilir diyorsa, bir geliştirme kopyası çalıştırıyorsunuz demektir.
- Görev Yöneticisi'nde (Başlangıç uygulamaları) kapattıysanız, orada yeniden açın.
- Kuruluşunuz bunu ayarlamışsa anahtar kilitlidir.

### Güncelleme

Store uygulamayı günceller. Güncellemeden sonra **Yenilikler** bir kez açılır. Uygulama menüsünden tekrar açabilirsiniz.
Modeller güncellemelerden sonra da yerinde kalır.

### Kaldırma

**Windows Ayarları > Uygulamalar** bölümünden uygulamayı kaldırın. Varsayılan klasördeki modeller uygulamanın kendi verilerinde
saklanır ve onunla birlikte silinir. Seçtiğiniz özel model klasörüne dokunulmaz. Bir şeyi saklamak istiyorsanız kaldırmadan önce
bölüm 15'e bakın.

---

## 3. Uygulamada gezinme

Solda beş sayfa vardır.

| Sayfa | Ne için | Kısayol |
|---|---|---|
| Ana sayfa | Durum, etkin model, bellek kullanımı, şu an yapay zekayı kullanan uygulama | Ctrl+1 |
| Modeller | Modelleri indirme, kullanma ve kaldırma | Ctrl+2 |
| Cihazlar ve uygulamalar | Portal uygulamalarına izin verme veya kaldırma, duraklatma, öncelik belirleme | Ctrl+3 |
| Dene | Modele tek kısa bir soru sorma | Ctrl+4 |
| Ayarlar | Yapılandırılabilen her şey | Ctrl+, |

Altıncı bir sayfa olan **Bu bilgisayarın cihazları**, bu bilgisayarla eşlenmiş dizüstü bilgisayarları ve telefonları listeler
(bkz. bölüm 10). Uygulama menüsünde Ayarlar, Klavye kısayolları, Yenilikler ve Hakkında
bulunur.

**Ana sayfa**, yapay zekanın Hazır, Yanıt veriyor, Model hazırlanıyor, Dinleniyor (bir uygulama istediğinde model uyanır) veya
henüz modelin olmadığını gösterir. **ŞU AN** bölümü hangi uygulamanın yanıt aldığını ve saniyede yaklaşık kaç kelime
üretildiğini gösterir. Ayrıca modelin ne kadar bellek ve ekran belleği kullandığını ve **Şimdi yükle** / **Şimdi boşalt**
düğmesini gösterir. **BUGÜN** bölümü her uygulamanın isteklerini sayar. Sağlık kartı "Her şey yolunda" der ya da dikkat
gerektiren noktaları listeler; her birinin yanında tek tıkla yapılabilen bir düzeltme bulunur: Modeli yükle, Motoru yeniden
başlat, Dosyaları denetle veya Performans ayarlarını aç.

[screenshot: bir uygulama modeli kullanırken Ana sayfa]

Bildirim alanındaki simge küçük bir pencere açar: model, bellek, son istek ve kuyruk, ayrıca **Uygulamayı aç** ve **Modeli
boşalt** düğmeleri.

[screenshot: bildirim alanı penceresi]

Barındırma programı çalışmıyorsa Ana sayfa **Portal AI Host'u başlat** düğmesini sunar. Beklenmedik biçimde birden fazla kez
kapandıysa, **Güvenli mod** çubuğu uygulamanın dikkatli ayarlarla başladığını belirtir. Uygulamalarınız yine çalışır; sorun
tekrarlarsa bilgisayarı yeniden başlatın.

---

## 4. Modeller

Model, işi yapan yapay zekadır. **Modeller** (Ctrl+2), sahip olduklarınızı ve ekleyebileceklerinizi listeler.

### Önerilen model

İlk çalıştırma ve Ana sayfa, belleğinize, ekran belleğinize ve boş disk alanınıza göre, nedeniyle birlikte "bu bilgisayar için en
iyi model" önerir. Daha iyi bir model kullanılabilir olduğunda **Daha iyi bir model var** şeridi görünür. Yeni model hazır
olana kadar mevcut modeliniz kalır ve eski modeli ne zaman kaldıracağınıza siz karar verirsiniz.

### Model indirme

1. **Modeller** sayfasını açın. **Eklenebilecek modeller** altından bir model seçin.
2. **İndir** düğmesine basın. Bir şerit ilerlemeyi ve kalan süreyi gösterir.
3. İstediğiniz zaman **Duraklat** ve **Devam et** kullanabilirsiniz. **Durdur** zaten indirilmiş kısmı atar (onaydan sonra).
4. Host, indirilen dosyaları kullanmadan önce denetler.
5. İndirme bittiğinde **Bu modeli kullan** düğmesine basın.

İndirmeler yeniden başlatmadan sonra devam ettirilebilir ve dosyalar tek tek denetlenir. İndirme için yeterli boş disk alanı
gerekir; yoksa sayfa ne kadar eksik olduğunu söyler ve **Ayarlardan klasörü değiştir** seçeneğini sunar. İndirme sürerken
bilgisayar uyanık tutulur (Ayarlar > Performans, varsayılan olarak açık).

[screenshot: indirmesi süren Modeller sayfası]

"Bu model bu bilgisayar için çok büyük olabilir" işaretli bir model yine de indirilebilir, ancak yavaş çalışabilir veya
sığmayabilir.

### Ayrıntılar, dosya denetimi, yeniden indirme, kaldırma

- **Ayrıntılar**, modelin neler için iyi olduğunu, nelere dikkat etmeniz gerektiğini, ihtiyaç duyduğu belleği, lisansını, en son
  ne zaman kullanıldığını ve ne kadar yer kapladığını gösterir.
- **Dosyaları denetle**, diskteki modeli yeniden denetler. Hiçbir şeyi silmez. Bozuk bir model **Bozuk** olarak işaretlenir.
- **Yeniden indir** (bozuk bir model için) bozuk kopyayı kaldırır ve modeli yeniden indirir. Başka hiçbir şey silinmez.
- **Kaldır**, onaydan sonra modeli bu bilgisayardan çıkarır; onay ne kadar alan boşalacağını söyler. Model kullanımdaysa host
  önce modeli boşaltır ve bundan sonra hangisinin kullanılacağını sorar. Yalnızca host tarafından indirilen dosyalar silinir.

### Kendi modelinizi ekleyin

**Kendi modelimi ekle...** bir `.gguf` dosyası seçmenizi sağlar. Host, model klasöründe kendi kopyasını oluşturur. Orijinal
dosyanız hiçbir zaman değiştirilmez ya da taşınmaz. Satır **Sizin tarafınızdan eklendi** olarak işaretlenir ve host modelin ne
kadar bellek gerektirdiğini tahmin edemez; bu yüzden deneyip görün. Bu tür bir modelde **Kaldır**, yalnızca hostun kendi
kopyasını siler.

### Yer açma

**Yer aç**, uygulamanın kendi oluşturduğu artıkları listeler: devam ettirilmeyen yarım kalmış indirmeler, modelleri taşıdıktan
sonra kalan eski kopyalar ve bir denetimin bozuk bulduğu modeller. Kaldırılmadan önce onay verirsiniz. Kullandığınız modeller ve
kendi eklediğiniz dosyalar hiçbir zaman listelenmez. Devam ettirebileceğiniz yarım kalmış indirmeler bir hafta saklanır.

### Model klasörü ve modellerinizi taşıma

**Ayarlar > Model klasörü**, yeni indirmelerin nereye gideceğini belirler. Varsayılan klasör için boş bırakın ya da `D:\Modeller`
gibi tam bir yol yazıp Enter'a basın. Daha önce indirilen modeller taşınmaz ya da silinmez; bulundukları yerde kalır ve listede
görünmeye devam eder. Onları taşımak için **Modellerimi oraya taşı** seçeneğini kabul edin. Kopyalanır, denetlenir ve ancak bundan
sonra eski kopyalar kaldırılır. İşlem sırasında kullanılan model boşaltılır ve **Taşımayı iptal et** her şeyi eski yerinde bırakır.

### Bellek bütçesi ve çubuklar

Modeller sayfasındaki **Bellek bütçesi** çubukları RAM ve ekran belleğini gösterir: yüklü modellerin ne kadar kullandığını, bir
model yüklenirken ne kadarın ayrıldığını ve sınırlarınıza göre ne kadarın boş olduğunu (bkz. bölüm 7).
Bellek sınırınızı aşacak bir yükleme, hiçbir şey durdurulmadan önce açık bir nedenle reddedilir.

[screenshot: iki model yüklüyken Bellek bütçesi çubukları]

### Yerleşim: ekran kartı, bellek veya yalnızca işlemci

Bir modelin nerede çalışacağına **Ayarlar > Performans > Çalıştır** (Otomatik, Ekran kartı, İşlemci) karar verir. Ekran kartına
sığmayan bir model kısmen işlemci üzerinde çalışır (daha yavaş). Model bazında yerleşim için bölüm 11'e
bakın; orada **Yalnızca işlemci** seçeneği de bulunur.

### Sınırlar ve ücretsiz ya da ücretli

- Listedeki tüm modeller ticari kullanım için ücretsizdir (Apache 2.0 veya MIT lisansları); her model kendi lisansını gösterir.
- Modeller, yayıncılarının Hugging Face sayfalarından indirilir. Host yalnızca güvenilen, imzalı model listesini kabul eder.
  Bu listeyi yükleyemezse **Model listesi kullanılamıyor** şeridi görünür; bu bilgisayarda zaten bulunan modeller çalışmaya
  devam eder.

### Sorun giderme

- *İndirilecek model listelenmiyor:* internet bağlantınızı denetleyin ve Ana sayfada **Yeniden dene** düğmesine basın.
- *İndirme duruyor:* **Devam et** düğmesine basın; boş alan azsa yer açın ya da klasörü değiştirin.
- *Model "çok büyük olabilir" diyor:* daha küçük bir model seçin veya Ayarlar > Performans bölümünden yükü azaltın.

---

## 5. Uygulamalar ve izinler

**Cihazlar ve uygulamalar** (Ctrl+3) sayfasını açın.

- **Yanıtınızı bekliyor:** yapay zekayı kullanmak isteyen bir uygulama burada ve bir istem olarak görünür ("Voice Portal bu
  bilgisayarda yapay zekayı kullansın mı?"). **İzin ver**, **İzin verme** veya **Daha sonra karar ver** seçeneğini seçin. İstem,
  uygulamanın Doğrulanmış bir Portal uygulaması mı, Portal olmayan yüklü bir uygulama mı, yoksa doğrulanmamış mı olduğunu gösterir
  (doğrulanmamışsa nereden çalıştığını da belirtir; yalnızca kendiniz başlattıysanız izin verin).
- **Yapay zeka hostunuzu kullanabilen uygulamalar:** izin verilen her uygulama bugünkü isteklerini ve en son ne zaman kullanıldığını
  gösterir.
  - **Duraklat**, siz devam ettirene kadar yeni istekleri reddeder; uygulama erişimini korur.
  - **Öncelik** (Yüksek, Normal, Düşük), birkaç uygulama aynı anda istek yaptığında kimin önce geçeceğini belirler. Etkileşimli
    istekler toplu işlerden önce gelir ve uzun süre bekleyen bir istek öne çıkar; böylece düşük öncelikli bir uygulama hiçbir
    zaman aç bırakılmaz.
  - **Erişimi kaldır**, siz tekrar izin verene kadar uygulamayı durdurur. **Geri al** kısa bir süre görünür.

Uygulamalar hostu kullanmadan önce izin istemek zorundadır. Diğer cihazlarla paylaşımı açmadığınız sürece host yalnızca bu
bilgisayarı dinler (bölüm 9).

[screenshot: bir bekleyen ve bir izinli uygulamanın olduğu Cihazlar ve uygulamalar sayfası]

Geliştiriciler için: host, `http://127.0.0.1:41870` adresinde OpenAI uyumlu bir yerel API sunar (akışlı sohbet tamamlama,
gömmeler, model listesi). Bkz. `api-v1.md`.

**Nerede saklanır:** izin verilen uygulamalar ve belirteçleri hostun veri klasöründe tutulur ve Windows kullanıcınız için
korunur. Bir uygulamanın erişimini kaldırmak, onun belirtecini de kaldırır.

---

## 6. Dene

**Dene** (Ctrl+4), modelin çalıştığını denetler. Kısa bir soru yazın ve **Sor** (veya Ctrl+Enter) düğmesine basın. **Durdur**
yanıtı bitirir, **Temizle** sayfayı boşaltır. Önerilen sorular sunulur. İlk kelimelerin ne kadar sürdüğünü ve saniyedeki
belirteç (token) hızını gösterir. Buraya yazdıklarınız kaydedilmez.

[screenshot: yanıt ve süre satırı olan Dene sayfası]

---

## 7. Performans ayarları

**Ayarlar > Performans**, yapay zekanın bilgisayarınızı yavaşlatmasını önler. Değişiklikler hemen uygulanır; artık sığmayan bir
model yeni sınırlarla yeniden yüklenir ya da boşaltılır (diskte kalır). **Varsayılana sıfırla** bölümü eski haline getirir.

| Ayar | Ne yapar | Varsayılan |
|---|---|---|
| Performans | Bir hazır ayar: **Sessiz** (yaklaşık yarı hız, bilgisayar duyarlı kalır), **Dengeli** (iyi hız, programlarınız öncelikli), **Hızlı** (en hızlı yanıtlar, diğer programlar yavaşlayabilir), **Özel** | Dengeli |
| Yapay zeka için en fazla bellek | Yapay zeka bundan fazla bellek kullanmaz | Bilgisayarın belleğinin %80'i |
| Her zaman boş bırak | Diğer programlarınız için ayrılmış bellek | Hazır ayara göre |
| Çalıştır | **Otomatik** (yardımcı olduğunda ekran kartı), **Ekran kartı** (model sığarsa en hızlısı), **İşlemci** (daha yavaş, oyunlar için ekran kartı boş kalır) | Otomatik |
| Yapay zeka için ekran belleği | Yapay zekanın kullanabileceği ekran belleği sınırı | Hazır ayara göre |
| İşlemci çekirdekleri | Az çekirdek bilgisayarınızı duyarlı tutar | Hazır ayara göre |
| Öncelik | **Düşük** (önce kendi işiniz) veya **Normal** | Düşük |
| En fazla işlemci kullanımı | İşlemci kullanımı için bir üst sınır | Sınır yok (%100) |
| Şu durumda yavaşlat | Yapay zeka en düşük öncelikte çalışır, işlemcinin en fazla yarısını kullanır ve bittiğinde normale döner. Şunlardan birini ya da birkaçını seçebilirsiniz: bir oyun veya tam ekran program çalışıyor, bilgisayar pilde, bir Portal uygulaması diktasyon gibi canlı bir işle meşgul | Tam ekran program, pil ve meşgul uygulama açık |

**Ayarlar > Genel** içindeki **Boştayken belleği serbest bırak** bölümünde **Modeli yüklü tut**, **5 dakika sonra** veya
**10 dakika sonra** seçeneklerinden birini seçersiniz (varsayılan 10 dakika, pildeyken 5 dakika). Bir uygulama bir sonraki
istekte bulunduğunda model yeniden yüklenir. Modeli yüklü tutmak yanıtları daha hızlı verir ama belleği sürekli kullanır.

Kaydırıcı ipuçları, seçilen belleğin bir modeli tamamen ekran kartına mı, kısmen işlemciye mi yoksa yalnızca işlemciye mi
sığdırdığını gösterir ve mevcut model artık sığmıyorsa daha küçük bir model önerir.

[screenshot: Ayarlar > Performans]

**Sorun giderme:** ekran kartlı bir bilgisayarda yavaş yanıtlar genellikle modelin ekran kartına yalnızca kısmen sığdığı anlamına
gelir. Daha küçük bir model seçin veya **Yapay zeka için ekran belleğini** artırın. Kullanım sırasında bilgisayarınız takılıyorsa
Sessiz'i seçin ya da **En fazla işlemci kullanımını** düşürün.

---

## 8. Bildirimler

**Ayarlar > Bildirimler**, bir şeyin dikkatinizi gerektirdiğinde ve pencere kapalıyken gösterilen kısa Windows mesajlarını
denetler. Tam ekran bir program önde olduğunda sessiz kalırlar ve birkaç dakikada bir kereden fazla tekrarlanmazlar.

| Anahtar | Anlamı |
|---|---|
| Bir model indirmesi biter veya başarısız olur | Varsayılan olarak açık |
| Bir uygulama onay ister | Varsayılan olarak açık |
| Yapay zeka motoru durur veya bir denetim başarısız olur | Varsayılan olarak açık |
| Bir cihaz eşleşir veya uzaktan bağlanır | Varsayılan olarak açık (bölüm 10'daki uyarılar) |

Her biri ayrı ayrı kapatılabilir. Aynı sayfadaki **Varsayılana sıfırla** bunları eski haline getirir.

---

## 9. Diğer cihazların bu bilgisayarı kullanmasına izin vermek

**Ayarlar > Diğer cihazlar.** Varsayılan olarak kapalıdır. Açıldığında, kendi dizüstü bilgisayarınız ya da ev ağınızdaki başka bir
bilgisayar bu bilgisayarın yapay zekasını kullanmak isteyebilir. Bir cihazın önce bir kodla eşleşmesi gerekir ve cihazdaki her
uygulama yine sizin onayınızı ister.

### Bir cihaz eşleştirin

1. **Diğer cihazların bu bilgisayarın yapay zekasını kullanmasına izin ver** seçeneğini açın. Windows bir ağ kuralına izin vermeniz
   için sorabilir.
2. **Bir cihaz eşleştir** düğmesine basın. Bir kod ve QR kodu belirir; **Bağlantıyı kopyala** eşleştirme bağlantısını kopyalar.
3. Diğer cihazda Portal uygulamasını (veya bölüm 12'deki istemci
   modunda Portal AI Host'u) açın, bir bilgisayara bağlanmayı seçin, sonra kodu tarayın, yazın veya bağlantıyı yapıştırın.
4. Herhangi bir şey göndermeden önce bu bilgisayarla konuştuğunu denetler, ardından bilgisayarın adını ve onaylamak için bir parmak
   izi gösterir.

Kod yalnızca bir kez, 5 dakika boyunca ve tek bir cihaz için çalışır. Yanlış denemeler kodu tüketir; **Bir cihaz eşleştir**
ile yenisini oluşturun. **Eşleştirmeyi iptal et** işlemi sonlandırır.

[screenshot: eşleştirme kodu ve QR kodu]

### Eşleştirilmiş cihazlar

Her eşleştirilmiş cihaz, son kullanım zamanı ve **Kaldır** düğmesiyle listelenir. Bir cihazı kaldırmak erişimini hemen durdurur ve
devam eden istekleri sonlandırır. Geri dönmek için cihazın yeniden eşleşmesi gerekir. En fazla **16** cihaz eşleştirilebilir.

### Bu karttaki diğer ayarlar

- **Bu bilgisayara evin dışından da ulaş** (aktarıcı), bkz. bölüm 13.
  Diğer cihazlar etkinleştirildikten sonra varsayılan olarak açıktır.
- **Aktarıcı erişim anahtarı**, bkz. bölüm 13. Varsayılan olarak kapalı.
- **Cihazlar modelleri değiştirebilir**, bkz. bölüm 11. Varsayılan olarak kapalı.

**Ağdan ne geçer:** istekler ve yanıtlar, kendi cihazlarınız arasında şifreli olarak iletilir. Ev ağı bağlantısı, eşleşirken
cihazın sabitlediği bir sertifika kullanır. Opsiyonel kör aktarıcı (bölüm 13) dışında hiçbir şey sunucularımıza gönderilmez.

**Sorun giderme:** *Bir cihaz bu bilgisayarı bulamıyor:* ikisinin de aynı ağda olduğunu, bu bilgisayarın uyanık ve hostun çalışıyor
olduğunu denetleyin. *Kod "doğru değil" veya "süresi doldu" diyor:* yenisini oluşturun. *"Ev ağınızda değil":* önce evde bir kez
eşleştirin.

---

## 10. Bu bilgisayardaki cihazlar

**Bu bilgisayarın cihazları** sayfası, eşleştirilmiş her dizüstü bilgisayar için bir kart gösterir. Her dizüstü bilgisayarın ne
yaptığını görmek ve sınırlamak için burası kullanılır.

[screenshot: iki cihaz kartı olan Bu bilgisayardaki cihazlar]

### Bir kartın gösterdikleri

- Durum (Çevrimiçi, Boşta, Çevrimdışı), rota (Ev ağı veya Aktarıcı (evin dışında)) ve en son görülme zamanı.
- Şu an çalıştırdığı şey ve bekleyenler.
- Bugünkü istekler ve belirteçler; uygulama başına 7 günlük bir çubukla.
- Kullanılan model, modeli kimin yüklediği, saniyedeki belirteç hızı ve ilk bayta kadar geçen süre.

### Denetimler

| Denetim | Ne yapar |
|---|---|
| Duraklat / Devam et | Tüm cihazı ya da uygulamalarından birini duraklatır. Dizüstü bilgisayar açık bir "duraklatıldı" yanıtı alır ve ayarladıysanız kendi yedek yolunu kullanır. |
| Yeniden adlandır | Cihazın görünen adını değiştirir |
| Sınırlar | Öncelik, günlük sınırlar, sessiz saatler ve izinli modeller (aşağıda) |
| Model kontrolü (anahtar) | Bu cihazın modelleri yükleyip boşaltmasına izin verir. Bkz. bölüm 11 |
| Geri al | Bu cihazın yüklediği modeli boşaltır. Bu bilgisayarın kendi seçimi geri gelir. Hiçbir model dosyasına dokunulmaz. |
| Unut | Cihazı kaldırır. Erişimi hemen durur ve kullanım geçmişi silinir. Modellere dokunulmaz. |

**Sınırlar** (kartta **Sınırlar** düğmesinin altında, sonra **Kaydet**):

- **Bu bilgisayarın uygulamalarıyla aynı öncelik.** Kapalı (varsayılan): bu bilgisayarın kendi uygulamaları önce gelir. Açık:
  cihaz aynı sırada bekler.
- **Günlük istek** ve **Günlük belirteç.** Varsayılan: sınır yok. Biri tükendiğinde cihaza bu bilgisayarın saatine göre gece
  yarısından sonra tekrar gelmesi söylenir.
- **Sessiz saatler**, **Başlangıç** ve **Bitiş** alanlarıyla (bu bilgisayarın saati). Cihaz bu saatler arasında yanıt almaz.
  Varsayılan: kapalı.
- **Bu cihazın kullanabileceği modeller.** Varsayılan: tüm modeller.

### Oyun modu

Sayfanın üstündedir. **Varsayılan: kapalı.** Açıkken, siz oynarken diğer cihazlardan gelen çağrılara bu bilgisayarın meşgul olduğu
söylenir; böylece oyunlar ekran kartını korur. Windows tam ekran bir oyun bildirdiğinde ya da başka bir program ekran kartının 3D
motorunu yaklaşık 10 saniye boyunca %40 veya daha fazla meşgul tuttuğunda bu bilgisayar "oyunda" sayılır. Pencere içinde veya
kenarlıksız çalışan oyunlar da sayılır. Yük bittikten yaklaşık 30 saniye sonra durur. Bilgisayarınızda ekran kartı sayaçları
kullanılamıyorsa yalnızca tam ekran denetimi kullanılır. Dizüstü bilgisayar bu durumda kendi ayarlarındaki yedek yolu izler.

### Uyarılar

Yeni bir cihaz eşleştiğinde ve bir cihaz evin dışından aktarıcı üzerinden bağlandığında Windows bildirimi görünür. Bunları
**Ayarlar > Bildirimler > Bir cihaz eşleşir veya evin dışından bağlanır** seçeneğinden kapatabilirsiniz.

### Veriler

Kullanım sayıları yalnızca sayılardır (uygulama başına günlük istek ve belirteçler; 7 gün saklanır). Hiçbir istem veya yanıt metni
saklanmaz. **Unut** yalnızca o cihazın kullanım dosyasını siler.

---

## 11. Model kontrolü ve aynı anda iki model

Varsayılan olarak eşleştirilmiş bir dizüstü bilgisayar, bu bilgisayarda yüklü olan modeli kullanır. Bir cihaza daha fazlasını
verebilirsiniz.

### Model kontrolü

**Ayarlar > Diğer cihazlar**, cihazın satırında: **Model kontrolü** (varsayılan: kapalı). Açıkken dizüstü bilgisayar şunları yapabilir:
- bu bilgisayarda kurulu modelleri yükleyip boşaltabilir,
- bir modelin nerede çalışacağını seçebilir (Otomatik, Ekran kartı, Sistem belleği veya Yalnızca işlemci),
- bu bilgisayarın hangi modelini her uygulamasının kullanacağını seçebilir.

Bu izinle de olsa, bu izin olmadan da olsa, modelleri indiremez, kaldıramaz, denetleyemez veya içe aktaramaz; ayarları ve dosyalarınızı
değiştiremez. Kapatırsanız cihazdan gelen bir sonraki çağrı reddedilir. Cihazı kaldırmak izni de kaldırır; yeniden eşleşmek izni geri
getirmez.

Bilgisayar kimin neyi yüklediğini gösterir: "<cihaz> tarafından yüklendi: <model> (ekran kartı)". Kartta ve Ayarlarda bulunan **Geri
al**, bir cihazın yüklediği modeli boşaltır. Bu bilgisayarın kendi seçimi geri gelir ve model bir sonraki istekte yüklenir.

### Aynı anda iki model

Model kontrolü açıkken bir dizüstü bilgisayar bu bilgisayarda ikinci bir modeli yüklü tutabilir; örneğin biri ekran kartında,
biri sistem belleğinde.
- Kendi modeliniz birinci yuvada kalır. Bir cihazın modeli ikinci yuvaya gider; böylece cihaz kendi modeli için sizin modelinizi
  asla dışarı itmez.
- Her yuvanın kendi kuyruğu vardır; bu yüzden iki model aynı anda yanıt verebilir. İkinci modeli adıyla belirten bir istek ona
  gider; diğer her şey sizin modelinize gider.
- Yüklemeden önce host, diğer yuvanın zaten ne kadar kullandığını sınırlarınıza göre hesaplar. Sığmayan ikinci bir model reddedilir
  ("yetersiz bellek"). Hiçbir şey sessizce durdurulmaz.
- Kendi seçimleriniz her zaman öncelikli olur. Başka bir model seçerseniz, birini boşaltırsanız veya kendi modeliniz belleğe ihtiyaç
  duyarsa, cihazın modeli durdurulur ve cihaza bildirilir.
- Bir cihazın ikinci modeli sonsuza dek yüklü tutulmaz; diğerleri gibi boşta kaldığında boşaltılır.
- **Yalnızca işlemci**, grafik katmanları olmadan, işlemcinin yaklaşık yarısını kullanarak bir modeli çalıştırır. Örneğin, başka bir
  model ekran kartında çalışırken yardımcı bir modeli işlemcide çalıştırmak gibi.

[screenshot: farklı sahiplerce yüklenmiş iki modelin olduğu Modeller sayfası]

### Cihazlar modelleri değiştirebilir

**Ayarlar > Diğer cihazlar > Cihazlar modelleri değiştirebilir** (varsayılan: kapalı). Kapalıyken bir cihaz, uygulamalarınızın
kullandığı bir modeli boşaltamaz. Açıkken boşaltabilir ve uygulamalarınız bir sonraki istekte modellerini yeniden yükler. İki
cihaz varsa, önce yükleyen cihaz modelini tutar.

---

## 12. Ev bilgisayarınızı dizüstü bilgisayardan kullanmak (istemci modu)

İstemci modu, dizüstü bilgisayardaki Portal AI Host'un modeli kendisi çalıştırmak yerine başka bir bilgisayarınızın yapay zekasını
kullanmasını sağlar. Dizüstü bilgisayardaki uygulamalar eskisi gibi çalışır.

### Kurulum

1. Ev bilgisayarında **Diğer cihazların bu bilgisayarın yapay zekasını kullanmasına izin ver** seçeneğini açın ve **Bir cihaz
   eşleştir** düğmesine basın (bölüm 9). **Bağlantıyı kopyala**
   düğmesine basın ve bağlantıyı dizüstü bilgisayara gönderin.
2. Dizüstü bilgisayarda **Ayarlar > Ev bilgisayarı** bölümünü açın (veya karşılama penceresinde **Ev bilgisayarımı kullan**
   seçeneğini seçin).
3. **Yapay zeka nerede çalışır** bölümünde **Ev bilgisayarımı kullan** seçeneğini seçin. **Ev bilgisayarımı kullan** penceresi
   açılır. **Bağlantıyı yapıştır** ile bağlantıyı yapıştırın, **İleri** düğmesine basın, bilgisayar adını ve parmak izini denetleyin,
   sonra **Eşleştir** düğmesine basın. Dizüstü bilgisayarınıza bir ad verin.
4. Ana sayfa artık bilgisayarın adını ve bir durumu gösterir: **Bağlı - ev ağı**, **Bağlı - aktarıcı üzerinden**, **Bağlanıyor...**
   veya **Ulaşılamıyor**.

[screenshot: dizüstü bilgisayardaki eşleştirme penceresi]

### Ayarlar (Ayarlar > Ev bilgisayarı)

| Ayar | Seçenekler | Varsayılan |
|---|---|---|
| Yapay zeka nerede çalışır | Bu bilgisayar / Ev bilgisayarımı kullan | Bu bilgisayar |
| Ev bilgisayarına ulaşılamazsa | **Yok** (uygulamalar kendi yerleşik yapay zekasını kullanır) / **Bu dizüstü bilgisayarın modelini kullan** (istek üzerine yüklenir) | Yok |
| Evin dışındaki bağlantı | **Otomatik** / **El ile** (bu durumda **Aktarıcıyı kullan** anahtarı yalnızca bu oturum için geçerlidir ve uygulama kapanınca sıfırlanır) | Otomatik |

- Ev bilgisayarına ilk kez ulaşılamadığında bir kez ne yapacağınız sorulur; bunu istediğiniz zaman buradan değiştirebilirsiniz.
- **Bilgisayarı eşleştir** yeniden eşleşir; **Eşleşmeyi kaldır** bilgisayarı unutur (modeller ve indirmeler olduğu gibi kalır);
  **Bilgisayarı değiştir** (Ana sayfada) bu ayarları açar; **Burada yapay zekayı çalıştır** (Ana sayfada) istemci modundan çıkar ve
  kayıtlı eşleşmeyi tutar.
- Ev bilgisayarı Model kontrolü izni verdiyse, Yükle, Boşalt ve yerleşim düğmeleriyle birlikte **<bilgisayar adındaki> modeller**
  bölümü ve **Her uygulama için bilgisayar modeli** görünür. İzin yoksa sayfa şunu söyler: "Bu dizüstü bilgisayar için Model
  kontrolünü açması amacıyla <bilgisayar adının> sahibinden isteyin."
- İstemci modundaki bir dizüstü bilgisayar diğer cihazlardan bağlantı kabul etmez ve ev bilgisayarınıza giden bir ara nokta
  olamaz.

### Bu dizüstü bilgisayarın kendi modellerine ne olur

İstemci moduna girildiğinde dizüstü bilgisayarın yüklü modeli bellek boşaltmak için boşaltılır. Model dosyaları, indirmeler ve model
klasörü değişmez. Çıkıldığında model istendiğinde yeniden yüklenir.

### Sorun giderme

- **Ulaşılamıyor:** ev bilgisayarının açık ve aynı ağda olduğunu denetleyin veya aktarıcıyı açın.
- **"Bu dizüstü bilgisayar bilgisayardan kaldırıldı":** ev bilgisayarı onu kaldırdı. **Yeniden eşleştir** seçeneğini seçin.
- **"<bilgisayarın> kimliği değişti":** bilgisayarın sertifikası, dizüstü bilgisayarın kaydettiğiyle artık eşleşmiyor. Bunu
  beklemiyorsanız yeniden eşleştirmeyin. Aksi halde eşleşmeyi kaldırın, sonra yeniden eşleştirin.
- **Evin dışından eşleştirme başarısız:** önce evde bir kez eşleştirin; eşleştirme bağlantısı ev ağınızdaki bir adrese işaret etmelidir.
- **Duraklatılmış, sessiz saatler, çalışan bir oyun veya tükenmiş bir günlük sınır:** bilgisayar dizüstü bilgisayara nedenini söyler;
  dizüstü bilgisayar yedek yolunu kullanır.

**Nerede saklanır:** dizüstü bilgisayar eşleşmesini hostun veri klasöründe `client\` altında tutar. Cihaz anahtarı ve belirteçler
Windows kullanıcınız için korunur. **Eşleşmeyi kaldır** bunları siler. Bu klasörün yedekleri başka bir Windows kullanıcısında
geri yüklenemez.

---

## 13. Evin dışında: aktarıcı ve aktarıcı erişim anahtarı

Aktarıcı, eşleştirilmiş bir cihazın dünyanın her yerinden bilgisayarınıza ulaşmasını sağlar. **Ayarlar > Diğer cihazlar > Bu
bilgisayara evden uzakta da ulaş.**

- Bilgisayarınız küçük bir aktarıcı hizmetine bağlanır. Aktarıcı yalnızca okuyamadığı karıştırılmış baytları iletir ve hiçbir içerik
  tutmaz. Her cihaz yine de eşleştirilmiş olmalıdır ve her isteği imzalar.
- Durum satırı **Evin dışından erişilebilir**, **Yalnızca ev ağı**, **Aktarıcıya bağlanıyor...** veya **Aktarıcıya bağlanılamıyor**
  gösterir.
- Normal web bağlantı noktası üzerinden çalışır, bu yüzden çoğu ofis ağı buna izin verir.
- **Şirket cihazları:** iş içerikleri kişisel bilgisayarınıza gidecektir. Önce işvereninizin kurallarını kontrol edin.

### Aktarıcı erişim anahtarı

**Ayarlar > Diğer cihazlar > Aktarıcı erişim anahtarı** (varsayılan: kapalı). Açıkken, yalnızca eşleştirme kodundaki anahtarla
eşleşmiş cihazlar aktarıcı üzerinden bu bilgisayara ulaşabilir; bu, yabancıların bilgisayarınızın aktarıcı yuvalarını tüketmesini
engeller.

- Bunu **eşleştirmeden önce** açın. Daha önce eşleşmiş cihazlar aktarıcıyı kullanmak için yeniden eşleşmelidir (ev ağı çalışmaya
  devam eder).
- **Yeni anahtar** onaydan sonra yeni bir anahtar oluşturur. Eşleşmiş her cihazın aktarıcıyı kullanmak için yeniden eşleşmesi gerekir;
  evde hiçbir şey değişmez. Bir cihazı kaldırmak anahtarı değiştirmez; bu yüzden güvenmediğiniz bir cihazı kaldırdıktan sonra
  **Yeni anahtar** seçeneğini kullanın.
- Anahtar dosyası kullanılamazsa aktarıcı açık değil, kapalı kalır ve ev ağı çalışmaya devam eder. Bozuk bir anahtar dosyası
  `.bad` adıyla kenara alınır ve asla üzerine yazılmaz.
- Anahtar açık kalmıyorsa, aktarıcı hizmetinin kendi tarafında güncellenmesi gerekebilir. Anahtarı yeniden kapatın.

**Sınırlar:** aktarıcı, bir cihazın ne kadar hızlı ve ne kadar veri gönderebileceğini sınırlar; böylece tek bir cihaz hizmeti
boğamaz. Normal kullanım bu sınırların çok altındadır.

---

## 14. Gizlilik ve çökme raporları

- **Analitik ve kullanım takibi yoktur.** Bilgisayarınızdan yalnızca bir çökme raporu çıkabilir ve bu yalnızca açık bırakırsanız olur.
- **Metniniz sizin cihazlarınızda kalır.** İstemler ve yanıtlar host tarafından hiçbir zaman kaydedilmez veya günlüğe yazılmaz.
  Günlükler yalnızca sayıları, süreleri ve durumları içerir.
- **Çökme raporları varsayılan olarak açıktır ve kapatabilirsiniz.** Pencere veya arka plandaki host çökerse, kısa bir rapor bunu
  düzeltmemize yardımcı olur. **Ayarlar > Gizlilik > Çökme raporlarını gönder** seçeneğinden istediğiniz zaman kapatabilirsiniz;
  hemen durur. Karşılama penceresinde, hiçbir şey gönderilmeden önce, varsayılan olarak işaretli aynı seçenek bulunur.
  - **Gönderilir:** hata türü ve nerede oluştuğu (işlev ve modül adları), uygulama ve Windows sürümleri, Mağaza derlemesi olup
    olmadığı ve hatanın pencereden mi yoksa hostan mı geldiği.
  - **Asla gönderilmez:** istemler veya yanıtlar, yazdığınız her şey, dosya adları ve klasör yolları, bilgisayar veya kullanıcı adınız,
    hostu hangi uygulamaların kullandığı ya da IP adresiniz. Hata metni atılır.
  - **90 gün saklanır**, sonra silinir.
  - **Neler gönderiliyor** (Ayarlar > Gizlilik) gizlilik sayfasını açar.
- Bu bilgisayardan kendi diğer cihazlarınızdan birine gönderilirken **her şey şifrelenir**.
- **Gizlilik politikası:** Hakkında > **Gizlilik politikası**.
- Sorular veya bir raporun silinmesi talebi: mindthaapp@gmail.com.

[screenshot: çökme raporu anahtarının olduğu Ayarlar > Gizlilik]

---

## 15. Verilerin nerede saklandığı, yedekleme ve kaldırma

Her şey uygulamanın kendi veri klasöründe bulunur. **Hakkında > Veri klasörünü aç** bunu açar.

| Ne | Nerede (veri klasörünün içinde) | Yedeklenmeli mi? |
|---|---|---|
| İndirilen modeller | `models\` veya Ayarlarda seçtiğiniz klasörün içindeki `Portal AI Host Models\` | İsteğe bağlı. Yeniden indirilebilirler. Büyüklerdir. |
| Ayarlar | `settings.json` | Evet |
| İzin verilen uygulamalar ve belirteçleri | Hostun uygulama deposu | Taşınamaz (Windows kullanıcınız için korunur) |
| Eşleşmiş cihazlar, sınırlar, kullanım istatistikleri | Hostun cihaz deposu ve cihaz başına küçük bir istatistik dosyası | Taşınamaz |
| Dizüstü bilgisayar eşleşmesi (istemci modu) | `client\` | Taşınamaz. Yeni bir bilgisayarda yeniden eşleştirin. |
| Günlükler | `logs\` (döner, 10 MB ile sınırlı) | Hayır |
| Uygulamalar için keşif dosyası | `discovery\host.json` | Hayır |

**Yedekleme:** Portal AI Host'u kapatın, sonra veri klasörünü kopyalayın. Ayarlar ve modeller, geri kopyalanarak
geri yüklenebilir. Uygulama izinleri, cihaz eşleşmeleri ve belirteçler Windows kullanıcınız ve bilgisayarınız için korunur; bu
yüzden yeni bir bilgisayarda uygulamalara yeniden izin vermeniz ve cihazları yeniden eşleştirmeniz gerekir.

**Kaldırma:**
- *Tek bir model:* **Modeller > Kaldır**. Yalnızca host tarafından indirilen dosyalar silinir.
- *Artıklar:* **Modeller > Yer aç**.
- *Bir cihaz:* **Unut** (Bu bilgisayarın cihazları) veya **Kaldır** (Ayarlar).
- *Bir uygulamanın erişimi:* **Erişimi kaldır** (Cihazlar ve uygulamalar).
- *Her şey:* uygulamayı kaldırın. Seçtiğiniz özel model klasörü yerinde kalır; tamamen silmek isterseniz `Portal AI Host Models`
  klasörünü kendiniz silin.

Uygulama kendi oluşturmadığı hiçbir şeyi asla silmez, dosyalarınızın üzerine asla yazmaz; kendi eklediğiniz bir model yalnızca
hostun kendi kopyası olarak kaldırılır (orijinal dosyanıza dokunulmaz).

---

## 16. Klavye kısayolları

| Tuşlar | İşlem |
|---|---|
| Ctrl+1 | Ana sayfa |
| Ctrl+2 | Modeller |
| Ctrl+3 | Cihazlar ve uygulamalar |
| Ctrl+4 | Dene |
| Ctrl+, | Ayarlar |
| F1 | Hakkında |
| Ctrl+Enter | Dene sayfasında sor |
| Esc | Yenilikler'i veya bir iletişim kutusunu kapat |

Liste ayrıca uygulama menüsü > **Klavye kısayolları** altında da bulunur.

Görünüm: **Ayarlar > Görünüm** şunları sunar: Tema (varsayılan: Windows ayarınız), Pencere malzemesi (Mica), Köşeler (Yuvarlatılmış),
Yoğunluk (Standart), Metin boyutu (Normal) ve Hareket (Windows'u izle). Windows yüksek karşıtlık modu her zaman düz sistem renklerini kullanır.

---

## 17. Sorun giderme ve sık sorulan sorular

**Host çalışmıyor diyor.** Ana sayfada **Portal AI Host'u başlat** düğmesine basın. Başlatılamazsa bilgisayarı yeniden başlatın; o da
işe yaramazsa uygulamayı Store'dan yeniden yükleyin.

**"Bu bilgisayardaki başka bir Windows kullanıcısı Portal AI Host'u zaten çalıştırıyor."** O kullanıcıdan uygulamayı kapatmasını isteyin
veya onun hesabıyla oturum açın.

**Yanıtlar yavaş.** Bölüm 7'ye bakın. Ekran belleğiniz için çok büyük bir model kısmen işlemci üzerinde çalışır.

**Bir uygulama yapay zeka olmadığını söylüyor.** Uygulamanın izinli olduğunu (Cihazlar ve uygulamalar), duraklatılmadığını ve bir modelin
kurulu olduğunu ve bilgisayar için çok büyük olmadığını denetleyin.

**İlk yanıt yavaş, sonrakiler hızlı.** Model dinleniyordu. İlk istekte yüklenir. Bunu önlemek için Ayarlar > Genel bölümünde
**Modeli yüklü tut** seçeneğini seçin.

**Verilerim bir yere gönderiliyor mu?** Hayır. İstemler ve yanıtlar cihazlarınızda kalır. Yalnızca çökme raporları çıkabilir
(bölüm 14) ve bunları kapatabilirsiniz.

**İnternet olmadan kullanabilir miyim?** Evet, bir model indirildikten sonra.

**Bir maliyeti var mı?** Hayır.

**Hangi modeli seçmeliyim?** Önerilen modeli. Bilgisayarınız yavaş hissettiriyorsa daha küçük, daha iyi yanıtlar istiyorsanız ve
belleğiniz varsa daha büyük bir model seçin.

**İndirme "İndirme denetleniyor" yazısında takıldı.** Bekleyin. Büyük dosyaların denetlenmesi bir dakika sürer.

**Eşleştirme "çok fazla cihaz" diyor.** Sınır 16 cihazdır. Birini kaldırın.

**Bildirim alanındaki simge nerede?** Gizli simgeleri göstermek için saatin yanındaki oka tıklayın.

Hâlâ çözemediyseniz **Hakkında > Sürümü kopyala** seçeneğini kullanın, sonra mindthaapp@gmail.com adresine yazın ve sürümü
mesajınıza yapıştırın.

---

## 18. Daha sonra gelecekler

Bunlar planlanmıştır ve bugün uygulamada yoktur.

- **Modeller sayfasında model kategorileri:** görevlere göre gruplanmış (sohbet ve yazı, kodlama, akıl yürütme, görme, gömmeler,
  konuşma) filtreler ve aramayla birlikte.
- **Bu bilgisayarda görüntü ve video üretimi**; sahneleri bir videoda birleştiren bir "yönetmen" dahil. Video modelleri, motor var
  olana kadar "Yakında" olarak görünecek.
- **AI Portal'da hangi modelin yanıt vereceğini seçme** ve birbirlerinden yardım isteyen modeller.
- **macOS sürümü.**

---

## 19. Destek

- **Geri bildirim ve fikirler:** Hakkında > **Geri bildirim gönder veya bir fikir öner**.
- **Destek ve gizlilik soruları:** mindthaapp@gmail.com.
- **Sürüm:** Hakkında'da görünür; **Sürümü kopyala** mesajınız için panoya kopyalar.
- **Lisanslar:** Portal AI Host, modelleri bilgisayarınızda çalıştıran llama.cpp'yi (MIT lisansı) içerir. Modeller kendi lisanslarıyla
  indirilir (Apache 2.0 veya MIT). Hakkında bölümüne bakın.
