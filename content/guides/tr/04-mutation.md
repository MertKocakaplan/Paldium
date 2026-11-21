---
group: mekanik
summary: Yumurtaların %1'i mutasyonlu; hangi türler, hangi pasifler çıkar.
tools:
  - { label: "Hesaplayıcı · Mutasyon", href: "/calculator?mode=mutasyon" }
art: eggs
---
# Mutasyon

1.0 sürümünden beri bir yumurta mutasyona uğrayabilir. Temel şans yumurta başına **%1**, Lüks Sebze Pastası ile **%3**.

## Mutant
- Her zaman bir **Alfa**'dır.
- 4 pasifi vardır; en az biri [[passive:MutationPal_Babysitter]], [[passive:MutationPal_Immortal]] veya [[passive:MutationPal_Mutant]] gibi mutasyona özel bir pasiftir.
- IV değerleri 90 ile 100 arasındadır.

## Hangi tür
Mutasyon özel tarifleri yok sayar ve normal çocuktan *daha nadir* bir türü hedefler. Ebeveynlerin breeding gücünün küçüğü `m`, farkı `d` olsun: hedef `offset` ile `offset + n - 1` arasında rastgele bir değerdir; `n` ile `offset` `m` ile büyür, `offset` `d` büyüdükçe küçülür. Hedefe en yakın tür mutant olur. Aralıktaki her değer eşit olasılıklıdır.

Örnek: [[pal:JetDragon]] (70) ile [[pal:Anubis]] (480): `n = 7`, hedefler 200 ile 206 arası. Yedide beşi bir türe, ikisi sonraki türe düşer.

## Bu uygulamada
Hesaplayıcı'da Mutasyon sekmesini aç: iki ebeveyn seçerek mutasyon çocuklarını ve paylarını, hedef seçerek en iyi 50 çifti gör. "Sahip olduklarım" anahtarını açarsan yalnızca kurabileceğin çiftler listelenir.

Yumurta başına şans, payın mutasyon oranıyla çarpımıdır; yani mutasyon bir avlanma işidir: çok yumurta, Lüks Sebze Pastası ve sabır.
