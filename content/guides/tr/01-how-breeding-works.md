---
group: mekanik
summary: Yavrunun türü iki ebeveynin breeding gücünden nasıl çıkar; özel tarifler ve Alfa.
tools:
  - { label: "Hesaplayıcı · Çift → yavru", href: "/calculator?mode=cift" }
  - { label: "Hesaplayıcı · Hedef → ebeveynler", href: "/calculator?mode=ebeveynler" }
art: power-line
---
# Breeding nasıl çalışır

Her Pal türünün gizli bir **breeding gücü** (`CombiRank`) vardır. Düşük sayı nadir ve güçlü Pal demektir: [[pal:JetDragon]] 70, [[pal:Anubis]] 480, yaygın Pal'lar ise 3000'in üzerindedir.

İki Pal çiftleştiğinde oyun çocuğun türünü şu sırayla belirler:

1. **Özel tarif.** Ebeveyn çifti özel bir tarife uyuyorsa (bazen cinsiyet şartıyla) çocuk o türdür; başka bir şey kontrol edilmez.
2. **Hedef güç.** Aksi halde hedef, iki ebeveynin gücünün ortalamasıdır (yukarı yuvarlanır).
3. **Aday havuzu.** Yalnızca tarifle elde edilen çocuklar, boss formları ve breed edilemez işaretli türler havuzdan çıkarılır.
4. **En yakın kazanır.** Gücü hedefe en yakın tür çocuk olur. Eşitlikte yüksek öncelik, o da eşitse oyun tablosundaki sıra kazanır.
5. **Alfa zarı.** Türün Alfa formu varsa yumurtaların yaklaşık %5'i Alfa çıkar. Alfa kalıtılmaz.

## Örnek: Melpaca + Anubis

[[figure:power-line a=Alpaca b=Anubis]]

[[try:calculator mode=cift a=Alpaca b=Anubis label="Bu çifti Hesaplayıcı'da aç"]]

## Bilmekte fayda var
- Çocuk, gücü ortalamaya **en yakın** türdür; yani gücü ortalamanın altında da üstünde de olabilir. Örnek: [[pal:Alpaca]] (2720) ve [[pal:Anubis]] (480) çiftinin ortalaması 1600'dür; tam orada bir tür yoktur, en yakını [[pal:CatVampire]] (1570) olduğu için çocuk odur.
- Aynı türden iki Pal normalde aynı türü verir. Yaklaşık yirmi dört tür yalnızca bu şekilde elde edilir.
- Varyantların çoğu (Cryst, Noct, Lux, Terra, Ignis, Botan, Aqua, Primo) güçle değil özel tarifle çıkar.
- Cinsiyete bağlı tarifler dışında çift için tek şart bir erkek ve bir dişi olmasıdır.
- Bu uygulamadaki Planlayıcı ve Hesaplayıcı tam olarak bu kuralları kullanır.
