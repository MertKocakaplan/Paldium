---
group: uygulama
summary: Save dosyası ve mod köprüsü; sahip ve kapsam ne demek.
tools:
  - { label: "Ayarlar · Kaynaklar", href: "/settings#kaynak" }
art: import
---
# Çok oyunculu oyunda Pal'larını içe aktarma

Paldium, Pal'larını Palworld kayıt dosyalarından okur; yalnızca katıldığın bir dünyada ise asistan moddan. Hangisine ihtiyacın olduğu nasıl oynadığına bağlıdır.

## Kayıtlar nerede
`%LOCALAPPDATA%\Pal\Saved\SaveGames\<SteamID64>\<WorldId>\`

- **Host ettiğin dünyalar:** klasörde `Level.sav`, `LevelMeta.sav` ve `Players\*.sav` bulunur. Kaynak olarak eklersen Pal'ların okunur. Host'un `Level.sav` dosyası **tüm oyuncuların** Pal'larını içerir; yani host herkesinkini içe aktarabilir.
- **Katıldığın dünyalar** (arkadaşının co-op'u veya bir sunucu): klasörde yalnızca `LocalData.sav` vardır, okunacak Pal verisi yoktur. Aşağıdaki asistan modu kullan ya da host'tan dünya klasörünü paylaşmasını iste (sonraki bölüm).
- **Boyutsal Pal Deposu:** host'un dünya klasöründeki `Players\<uid>_dps.sav`.
- **Dedicated sunucu:** `Pal\Saved\SaveGames\0\<WorldId>\`. Kiralık sunucuda klasörü SFTP veya panelden indir.

## Kaynak ekleme
**Ayarlar → Kaynaklar**'ı aç, bir kayıt kaynağı ekle ve dünya klasörünü seç. Sonra eşitle. Uygulama önce dosyaları kopyalar ve oyun klasörüne asla yazmaz. Pal'ların konumlarıyla (takım, sayfa ve yuvasıyla Pal Kutusu, üs, kafes) Pal'larım sayfasında görünür.

## Katıldığın dünyalar, seçenek 1: host dünya klasörünü paylaşır
Host, dünya klasörünün bir kopyasını senkronize bir klasöre (örneğin bir bulut sürücüsü) koyar ve sen o klasörü kaynak olarak eklersin. Host'un kaydındaki her şey, bütün oyuncuların Pal'ları dahil, okunur. Kopya, host'un son kaydı kadar günceldir.

## Katıldığın dünyalar, seçenek 2: asistan mod
Mod, oynarken oyunun kendi istemcine gönderdiği bilgiyi okur.
1. **Ayarlar → Kaynaklar**'da **Modu kur**'a bas (ya da `PaldiumBridge` klasörünü oyunun `Mods\NativeMods\UE4SS\Mods` klasörüne kopyala) ve Palworld'u yeniden başlat.
2. Dünyaya gir, Pal Kutusu'nu ya da bir Boyutsal Pal Deposu'nu aç. Baktığın her sayfa kısa sürede okunur.
3. Kutu açıkken **F9**'a basarsan mod sayfaları senin yerine çevirir. **F10** UE4SS günlüğüne tanı bilgisi yazar (bir şey yanlış görünürse bunu gönder). **F11** modun kendi oturum önbelleğini temizler. Tuşlar Ayarlar'daki Mod kısayolları'ndan değiştirilir.
4. Kaynaklar paneli her kutu için hangi sayfaların ne zaman okunduğunu gösterir. Henüz açmadığın sayfa eksik görünür.

Uygulama önceki oyun oturumlarının sayfalarını (dünya başına) saklar; bu yüzden oyunu yeniden başlatınca liste küçülmez, yeniden okunan sayfa eski içeriğin yerine geçer. Ayarlar → Kaynaklar'daki "Sayfa önbelleğini temizle" hepsini unutturur.

### Sınırlar
- Yalnızca oyunun sana gönderdiği okunabilir: kendi Pal Kutun ve takımın, açtığın depolar ve oyunun sana ilettiği üsler. Arkadaşının takımı ve kimsenin açmadığı depolar okunamaz.
- Bir deponun sahibi her zaman bilinmez. Panel bunu söyler ve o depoyu diğerlerinden ayrı tutar.
- Sayfa en son okunduktan sonra taşınan Pal'lar o zamanki yerinde görünür.

## Global Pal Kutusu
`GlobalPalStorage.sav` hesap klasöründedir (`<SteamID64>\`). İçindeki Pal'lar yalnızca **genetik kopyadır**: bu dünyada doğrudan kullanılamazlar (başka bir dünyada yeniden oluşturulabilirler). Bu yüzden Paldium bunları varsayılan olarak saymaz ve bu rehber katıldığın dünyalar için önermez. Kendi dünyan için gelişmiş bir kaynak olarak eklenebilir.

## Notlar
- İki kayıt biçimi de desteklenir (`PlM1`, yeni Oodle biçimi ve eski `PlZ2`). Steam ve Xbox kayıtları çalışır.
- Eşitleme engellenirse oyunu kapat veya klasörü önce kopyala.
