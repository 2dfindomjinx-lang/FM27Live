# FM27 Live

Windows 10/11 x64 için normal uygulama ve oyun üstü overlay içeren FM27 anlık editör.

## Kullanım

1. `FM27Live.exe` dosyasını yazılabilir bir klasöre koy. .NET kurulumu gerekmez.
2. FM27'de kariyerini yükle. Önce yedek kayıt oluştur; ilk denemeyi ayrı bir kayıtta yap.
3. Uygulamadaki **Oyunu yükle / Load Game** düğmesiyle bağlan. Oyuncular, personel, kulüpler ve diğer editör ekranları aynı oturumu paylaşır.
4. **Overlay / F8** ilk geçişte ekranın kenarında küçük bir **FM27 ›** düğmesi açar. Düğmeye tıklayarak veya tekrar F8 ile paneli aç. **F8/Esc**, **Küçült** veya oyuna tıklama paneli tekrar küçük düğmeye indirir. Düğmenin sol şeridinden, açık panelin başlığından sürükleyebilirsin.
   Açık panelde **↔ / F11** genişletir-daraltır; panel ekranın tamamını kaplamaz. Editör içeriği panele orantılı sığar; dış panelde sağa kaydırma gerekmez. Daha büyük yazılar için paneli genişlet. **Uygulama** masaüstü görünümüne döner; normal uygulamada F11 tam ekrandır.
5. Overlay için FM'de **kenarlıksız tam ekran** veya pencere modu kullan. Exclusive fullscreen üzerinde görünmesi garanti edilmez.
6. Küçük düğmeye sağ tıklayarak **Tamamen gizle** seçebilirsin; F8 veya bildirim alanı simgesinden geri açılır. **Çıkış** uygulamayı kapatır.

**Settings → Language → Türkçe** ile dili değiştir. Türkçe ve İngilizce arasında geçiş anında uygulanır; uygulama yeniden başlamaz, açık editörler ve kaydedilmemiş alanlar korunur. Eski Vietnamca tercihi Türkçeye taşınır.

Türkçe arayüzde teknik, zihinsel ve fiziksel özellikler oyunun Türkçe dizilişini izler; duran toplar teknik sütunun altında ayrı gösterilir. **Ayarlar → Özellik sıralaması → İngilizce sıralama** seçeneğiyle önceki sıralamayı kullanabilirsin. **Arayüz diline göre** varsayılandır. Seçim anında uygulanır, kaydedilmemiş değerleri korur ve sonraki açılışta hatırlanır. İngilizce arayüzün sıralaması değişmez.

Üstteki hızlı erişim düğmeleri normal uygulamada ve overlay içinde kullanılabilir:

- **FM’deki açık ekranı getir (Ctrl+Shift+F):** FM’de görüntülediğin kişi veya kulübü editörde açar. Ctrl+R mevcut editör sayfasını yenilemeye devam eder. İlk kullanımda kurulu FM dosyalarından ekran adresleri çözülür; sonraki kullanımlar aynı oturumda önbelleği kullanır. Desteklenmeyen veya belirsiz ekranlarda önceki kişiyi açmaz. Açık oyuncu sekmesindeki kaydedilmemiş alanlar korunur.

- **Benim kulübüm:** insan menajerin kulübünü açar. Birden fazla menajer varsa seçim sunar; bulunamazsa kulüp seçebilirsin.
- **Kadrom:** oyuncu filtrelerini temizler ve kulübündeki oyuncuları gösterir.
- **Ligimdeki kulüpler:** kulübün A takımının ligindeki, yüklenen verilerde bulunan kulüpleri seçmeyi sağlar. Lig okunamazsa bunu bildirir.
- **Oyuncunun kulübü:** seçili oyuncunun kulübünü açar.
- **Son kulüpler:** son açtığın en fazla 10 kulüp. **Kulübümü seç…** ile kendi kulübünü elle belirleyebilirsin.

Tercihler kulüp/menajer kimlikleriyle saklanır; oyun belleği adresleri kayıtlar arasında taşınmaz.

Oyun tarihi değişince listeler ve temiz editör sayfaları otomatik yenilenir. Tarih 3 saniyelik iki kontrolde sabit kalınca arka planda okuma başlar. Kaydedilmemiş sekmelerin kontrolleri, yarım yazılmış değerler dahil korunur; bu sayfalar kaydetme veya vazgeçme sonrasında yenilenir. Diğer listeler güncellenmeye devam eder. Otomatik yenileme oyuna yazmaz ve çakışma kontrollerini kaldırmaz. Yeni bir kayıt yüklenirse **Oyunu yükle** gerekir.

## Otomatik güncelleme

Program açılışta ve açıkken 30 dakikada bir `2dfindomjinx-lang/FM27Live` yayın kanalını kontrol eder.
Paketin ECDSA yayıncı imzası, SHA-256 özeti ve boyutu kontrol edilir. Oyun bağlıyken kurulum ertelenir.
İndirilen paket sonraki açılışta otomatik kurulur; **Güncelle ve aç** düğmesiyle daha erken kurulabilir.
Kaydedilmemiş değişiklik veya açık işlem varsa yeniden başlatma engellenir.
Yeni sürüm 60 saniyede açılış onayı vermezse önceki uygulama sürümü geri yüklenir; sorunlu sürüm yeniden denenmez.
İnternet yokken mevcut editör kullanılabilir. Güncellemeler menüsünden otomatik güncelleme kapatılabilir.

Ayarlar `%LOCALAPPDATA%\FM27Live` içinde tutulur; paket değiştirilirken bu klasör silinmez.
`.previous` dosyası önceki uygulamadır. Bu geri dönüş oyun kaydını geri almaz.
Windows Authenticode sertifikası yoktur; yayıncı doğrulaması uygulamanın imzalı güncelleme sistemi içindedir.

## Uyumluluk

Açık ekran algılama Windows Unity metadata 39 için salt okunurdur; adresler Steam/Game Pass adına göre sabitlenmez, kurulu oyundan çözülür. Steam 27.1.0+2505117 üzerinde Faruk Yankayış ve Beşiktaş geçişi doğrulandı; Game Pass, personel, ülke ve turnuva ekranları için canlı doğrulama henüz yapılmadı. Bu özellik oyuna yazma yetkisini veya desteklenen oyun sürümlerini genişletmez.

## Düzenleme görünümü ve kişisel çeviriler

Oyuncu bilgi ekranında özellikler üç geniş sütundadır; kişisel bilgiler açılır bölümdedir. Üstte mevki, topa sahip olma durumu ve rol seçerek temel (yeşil), tercih edilen (mavi) ve düşük olması önerilen (sarı) özellikleri vurgulayabilirsin. Bu yalnızca görsel rehberdir, oyuncuya rol atamaz veya değer değiştirmez. Rol eşlemeleri uygulamanın mevcut yerleşik rol kataloğundan gelir.

**Ayarlar → Türkçe çevirileri düzenle** ile ifadeleri ara, değiştir, **Kaydet ve uygula** ile yeniden başlatmadan uygula. Seçili ifadeyi varsayılana döndürebilirsin. Kişisel sözlük `%LOCALAPPDATA%\FM27Live\translations\tr.custom.json` dosyasında saklanır ve güncellemelerde korunur. Dosyayı dışarıdan değiştirirsen aynı penceredeki **Çeviri dosyasını yeniden oku** düğmesini kullan. `{0}` gibi değişken yerlerini koru. `@attribute:Balance` oyuncunun dengesini, normal `Balance` mali bakiyeyi ayrı düzenler.

Açık overlay hem üst şeridin hem panel başlığının boş alanından sürüklenir; düğmeler ve giriş alanları kendi işlevlerini korur.

Yazı tipi dağılımı: kişi isimleri, özellikler ve diğer metinler **Cabinet Grotesk Regular**, başlıklar **Cabinet Grotesk Bold**. Fontlar EXE içine gömülür; Windows'a ayrıca yüklenmez.

### Fontlarla yerel derleme

PowerShell 7 ile `./scripts/get-cabinet-fonts.ps1` resmi Fontshare paketinden gerekli dosyaları indirir ve sabit SHA-256 değerleriyle doğrular. CI aynı hazırlık adımını kullanır. Elle kurulum da mümkündür:

Font dosyaları kaynak deposuna eklenmez. [Cabinet Grotesk'i Fontshare'den](https://www.fontshare.com/fonts/cabinet-grotesk) edin. İndirilen masaüstü paketinden `Fonts/OTF/CabinetGrotesk-Regular.otf`, `Fonts/OTF/CabinetGrotesk-Bold.otf` dosyalarını `re/local-fonts/` içine; `License/FFL.txt` belgesini aynı klasöre `Cabinet-FFL.txt` adıyla koy. Farklı klasör için `LocalFontDirectory` MSBuild özelliği kullanılabilir.

Cabinet Grotesk fontları kendi Fontshare Free Font License koşulları altında, uygulama içine gömülü olarak dağıtılır. Lisans metni EXE içinde `FM27Live.Licenses.CabinetGrotesk` kaynağında bulunur; uygulamanın kaynak kodu lisansı fontların lisansını değiştirmez.

Altyapıdaki Windows profilleri Steam ve Game Pass için `27.1.0+2505117` dahil sürümler içerir.
Tam liste `offsets/` altında. Bunlar upstream profil doğrulamalarıdır; bu dağıtımın oyun içi testi ayrıca yapılmalıdır.
Epic, kadın futbolu ve profili bulunmayan yeni FM sürümleri için düzenleme desteği iddia edilmez.
Tanınmayan sürümün adresleri tahmin edilmez; yeni profil doğrulanana kadar bağlantı/yazma engellenir.

## Yeni sürüm yayınlamak

Önce FM uyumluluğunu doğrula; upstream `AGENTS.md` içindeki yeni build kontrol sürecini izle.
İmzalama özel anahtarı depo dışında saklanır. Anahtarı kaybetme; GitHub'a veya arkadaşına gönderme.

```powershell
pwsh scripts/package-live.ps1 -Version 0.1.1 -PrivateKey C:\path\update-private.pem -Output release-live -Notes 'Yeni FM sürümü desteği'
```

Kaynak değişikliklerini `main` dalına commit edip gönder. Ardından:

```powershell
python scripts/publish-live.py --version 0.1.1 --release-dir release-live --notes RELEASE-NOTES-LIVE.md
```

Yayın aracı kayıtlı GitHub oturumunu kullanır; parola/token uygulamaya konmaz. EXE ve imzalı bildirim aynı draft yayına yüklenir;
tüm dosyalar tamamlandığında yayın açılır. Versiyonu her yayında artır. Başarısız sürümü düzeltirken de yeni bir versiyon kullan.
Özel anahtarın güvenli yedeğini ayrıca sakla. İlk üretimde kullanılan anahtarın yeri yerel teslim notunda belirtilir.

## Kaynak ve lisans

Football Manager 27 Editor üzerine geliştirilmiştir. Upstream: https://github.com/Jackhamerp24/Football-Manager-27-Editor
Bu dağıtım: https://github.com/2dfindomjinx-lang/FM27Live
GPL-3.0-or-later; `LICENSE` ve `NOTICE-LIVE.txt` dosyalarına bak.
