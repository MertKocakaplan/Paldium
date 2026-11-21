---
group: mekanik
summary: How the child species follows from the two parents' breeding power; special recipes and Alpha.
tools:
  - { label: "Calculator · Pair to child", href: "/calculator?mode=cift" }
  - { label: "Calculator · Target to parents", href: "/calculator?mode=ebeveynler" }
art: power-line
---
# How breeding works

Every Pal species has a hidden **breeding power** (`CombiRank`). A low number means a rare, strong Pal: [[pal:JetDragon]] is 70, [[pal:Anubis]] is 480 and common Pals sit above 3000.

When two Pals breed, the game decides the child species in this order:

1. **Special recipe.** If the parent pair matches a unique recipe (sometimes with a required gender), the child is that species and nothing else is checked.
2. **Target power.** Otherwise the target is the average of both parents' power, rounded up.
3. **Candidate pool.** Recipe-only children, boss forms and species flagged as unbreedable are removed.
4. **Nearest wins.** The species whose power is closest to the target is the child. On a tie the higher duplicate priority wins, then the order in the game table.
5. **Alpha roll.** About 5 % of eggs hatch the Alpha form when the species has one. Alpha is not inherited.

## Example: Melpaca + Anubis

[[figure:power-line a=Alpaca b=Anubis]]

[[try:calculator mode=cift a=Alpaca b=Anubis label="Open this pair in the Calculator"]]

## Good to know
- The child is the species **nearest** to the average, so its power can be below or above the average. Example: [[pal:Alpaca]] (2720) and [[pal:Anubis]] (480) have the average 1600; no species sits exactly there, and [[pal:CatVampire]] (1570) is the closest, so it is the child.
- Two Pals of the same species normally give that species. About two dozen species can only be bred this way.
- Most variants (Cryst, Noct, Lux, Terra, Ignis, Botan, Aqua, Primo) are produced by special recipes, not by power.
- Apart from the gender-bound recipes, a pair only needs one male and one female.
- The Planner and the Calculator in this app use exactly these rules.
