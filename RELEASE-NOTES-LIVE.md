FM27 Live 0.1.10 Windows sürümü.

- FM’de açık oyuncu/kulüp ekranının bulunamamasına neden olan aday doğrulama hatası düzeltildi. Bellekte aynı sınıf işaretçisini taşıyan alakasız veriler artık gerçek ekran nesneleriyle karıştırılmıyor.
- Arka plan hazırlığı tüm adayları saklar; her tıklamada seçili nesnenin türü ve kimliği yeniden doğrulanır. Birden fazla geçerli seçim varsa rastgele birini açmaz. Önceki oyuncu seçimi önbellekten döndürülmez.
- Sayı kutularının büyüklüğü, ilk tıklamada tüm sayıyı seçme ve kaydedilmemiş girişlerin korunması devam eder.

Doğrulama: 2.103 otomatik test başarılı. Açık Steam 27.1.0+2505117 oturumunda yalnızca okuma erişimiyle hata yeniden üretildi; düzeltilmiş okuyucu oyuncuyu kataloğa çözümledi. Ölçülen ilk okuma 693 ms, sonraki okuma 11 ms (bu oturuma ait ölçüm; hız garantisi değildir). Alakasız sınıf işaretçileri, birden fazla geçerli aday ve okuma sırasında seçim değişimi için regresyon testleri eklendi. Yeni FM sürümü desteği eklenmedi.

Güncellemeler → Şimdi kontrol et ile alınabilir. Oyun bağlıyken veya kaydedilmemiş değişiklik varken kurulum ertelenir.

GPL-3.0-or-later. Cabinet Grotesk, Fontshare Free Font License koşulları altında EXE içine gömülüdür.
