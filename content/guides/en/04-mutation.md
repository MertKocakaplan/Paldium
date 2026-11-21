---
group: mekanik
summary: 1 % of eggs are mutated; which species and which passives can appear.
tools:
  - { label: "Calculator · Mutation", href: "/calculator?mode=mutasyon" }
art: eggs
---
# Mutation

Since version 1.0 an egg can mutate. The base chance is **1 %** per egg, **3 %** with an Extravagant Vegetable Cake.

## The mutant
- It is always an **Alpha**.
- It has 4 passives, at least one of them a mutation passive such as [[passive:MutationPal_Babysitter]], [[passive:MutationPal_Immortal]] or [[passive:MutationPal_Mutant]].
- Its IVs are between 90 and 100.

## Which species
A mutation ignores special recipes and aims at a *rarer* species than the normal child. With `m` the lower and `d` the difference of the parents' breeding power, the target is a random value from `offset` to `offset + n - 1`, where `n` grows with `m` and `offset` grows with `m` and shrinks with `d`. The species nearest to that value is the mutant. Every value in the range is equally likely.

Example: [[pal:JetDragon]] (70) with [[pal:Anubis]] (480): `n = 7`, targets 200 to 206. Five of the seven land on one species and two on the next one.

## In this app
Open Calculator, tab Mutation: pick two parents to see the mutation children and their shares, or pick a target to see the 50 best pairs. Switch on "My Pals" to see only the pairs you can form.

The chance per egg is the share multiplied by the mutation rate, so mutations are a farming game: many eggs, an Extravagant Vegetable Cake and patience.
