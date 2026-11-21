---
group: mekanik
summary: İki zar: kaç pasif geçer, kaç rastgele eklenir. İstenmeyen pasif neden şansı düşürür.
tools:
  - { label: "Hesaplayıcı · Pasif olasılığı", href: "/calculator?mode=olasilik" }
art: dice
---
# Pasif kalıtımı ve en iyi strateji

Bir yavru en fazla **4 pasif** taşır. Oyun bunları iki zarla kurar.

## 1. Havuz

İki ebeveynin pasifleri birleştirilir, tekrarlar silinir. Aynı pasif iki ebeveynde de varsa havuza bir kez girer.

[[figure:pool a=CraftSpeed_up3,CraftSpeed_up2 b=CraftSpeed_up2,PAL_FullStomach_Down_3,Deffence_up2_2 bad=Deffence_up2_2]]

## 2. İki zar

Yavru havuzdan rastgele 1, 2, 3 ya da 4 pasif alır; ardından 4 yuva dolana kadar rastgele pasif eklenir. Eklenenler yalnızca rastgele çıkabilen pasiflerden, çekiliş ağırlığına göre seçilir.

[[figure:inherit-bars]]

## 3. Tam istenenler gelir mi

Havuz yalnızca istenen k pasiften oluşuyorsa şans:

[[figure:chance-bars]]

[[try:calculator mode=olasilik label="Pasif olasılığını Hesaplayıcı'da hesapla"]]

## Strateji
- Pasiflerin ebeveynler arasında nasıl bölündüğü (2+2, 3+1, 4+0) fark etmez.
- Aynı pasifi iki ebeveyne de koymak şansını **artırmaz**.
- İstenmeyen pasifleri havuzdan uzak tut; havuzdaki istenmeyen pasifler slotlar için yarıştığından şansı düşürür.
- Küçük adımlarla ilerle: dördünü birden ummak yerine istenen pasifleri sırayla birleştir. Planlayıcı yalnızca ihtiyacın olanı taşıyan ebeveynleri tercih eder.
- Plan daha ucuzsa eksik pasif için ameliyat kullanılabilir.
- Özel Kek, kalıtılan pasif sayısını 4'e sabitler; Kekler ve pastalar rehberine bak.
- Ebeveynlerden birinde pasif varsa yavruda en az bir pasif olur.
- Örnek pasifler: [[passive:Legend]], [[passive:Rare]], [[passive:CraftSpeed_up2]].
