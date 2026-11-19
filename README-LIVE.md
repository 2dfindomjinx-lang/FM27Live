# FM27 Live

Windows 10/11 x64 için normal uygulama ve oyun üstü overlay içeren FM27 anlık editör.

## Kullanım

1. `FM27Live.exe` dosyasını yazılabilir bir klasöre koy. .NET kurulumu gerekmez.
2. FM27'de kariyerini yükle. Önce yedek kayıt oluştur; ilk denemeyi ayrı bir kayıtta yap.
3. Uygulamadaki **Load Game** düğmesiyle bağlan. Oyuncular, personel, kulüpler ve diğer editör ekranları aynı oturumu paylaşır.
4. **F8** ile overlay aç/gizle. Üstteki başlıktan sürükle, kenarlardan boyutlandır. **Normal uygulama** düğmesi masaüstü görünümüne döner. **F11** editörü tam ekran yapar.
5. Overlay için FM'de **kenarlıksız tam ekran** veya pencere modu kullan. Exclusive fullscreen üzerinde görünmesi garanti edilmez.
6. Gizlenen pencereyi bildirim alanındaki FM27 Live simgesinden açabilirsin. Overlay'in pencere kapatma düğmesi gizler; **Çıkış** uygulamayı kapatır.

Editörün kapsamlı mevcut ekranları İngilizce/Vietnamca; eklediğimiz overlay ve güncelleme kontrolleri Türkçedir.

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
