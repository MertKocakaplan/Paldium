---
group: mekanik
summary: Two dice: how many passives pass on and how many random ones are added. Why an unwanted passive lowers the odds.
tools:
  - { label: "Calculator · Passive odds", href: "/calculator?mode=olasilik" }
art: dice
---
# Passive inheritance and the best strategy

A child carries at most **4 passives**. The game builds them with two dice.

## 1. The pool

The passives of both parents are merged and duplicates are removed. A passive both parents have enters the pool once.

[[figure:pool a=CraftSpeed_up3,CraftSpeed_up2 b=CraftSpeed_up2,PAL_FullStomach_Down_3,Deffence_up2_2 bad=Deffence_up2_2]]

## 2. Two dice

The child takes 1, 2, 3 or 4 passives from the pool at random; then random passives are added until the 4 slots are full. The added ones come only from passives that can appear at random, weighted by their draw weight.

[[figure:inherit-bars]]

## 3. Do exactly the wanted ones arrive

If the pool consists only of the k wanted passives, the chance is:

[[figure:chance-bars]]

[[try:calculator mode=olasilik label="Work out the passive odds in the Calculator"]]

## Strategy
- How the passives are split between the parents (2+2, 3+1, 4+0) makes no difference.
- Putting the same passive on both parents does **not** raise the chance.
- Keep unwanted passives out of the pool; they compete for the slots and lower the chance.
- Go in small steps: instead of hoping for all four, combine the wanted passives one after another. The Planner prefers parents that carry only what you need.
- If the plan is cheaper, surgery can add a missing passive.
- The Special Cake fixes the inherited count at 4; see the cakes guide.
- If either parent has a passive, the child has at least one.
- Example passives: [[passive:Legend]], [[passive:Rare]], [[passive:CraftSpeed_up2]].
