# Worked example: The Watchpost

A six-night level on a cleared Nordfels, built only with `palace.py`. Use it as a template: copy the two JSON files,
change the ids, positions and nights.

Design: the castle sits on the Nordfels plateau. Six houses fan out north of it (behind the castle, away from the
lanes). A mill with three fields sits west. Enemies come from two lanes: the long southern road from the Main Bridge
(rock props narrow it near z = -60; a tower and a barracks hold it) and the shorter eastern Forest path (opened at
castle level 2, with its own tower and an archery range). Wasps come over the pond on night 4, more flyers over the
river on the last night.

## Commands

```sh
python3 palace.py where                                   # game found, bridge on
python3 palace.py ref build                               # once
python3 palace.py ref render Nordfels -o base.png         # study the map: plateau, lanes, spawn lines
python3 palace.py new example.watchpost --name "Example: The Watchpost" --author PalaceMod
python3 palace.py level example.watchpost watchpost --host Nordfels --base cleared
python3 palace.py apply example.watchpost examples/watchpost.ops.json
python3 palace.py render example.watchpost -o map.png --labels reqs
python3 palace.py show example.watchpost
python3 palace.py economy example.watchpost --compare
python3 palace.py waves example.watchpost --compare
python3 palace.py check example.watchpost --live
python3 palace.py play example.watchpost watchpost
python3 palace.py paths
python3 palace.py render example.watchpost --live -o live.png
python3 palace.py test example.watchpost watchpost --plan examples/watchpost.plan.json --shots shots/
```

## What they printed (abridged)

- First `apply` (houses originally at z = 18-20): three warnings, `"h3 at (10, 18) isn't on walkable ground (cliff,
  water or blocked): the king may not reach it"`. The plateau ends near z = 15 there (the render shows it); moving the
  houses to z = 5-14 cleared them. Second `apply`: `"errors": [], "warnings": []`.
- `check --live`: `{"offline": [], "game": {"ok": true, "issues": []}, "ok": true}`.
- `play`: `"loadWarnings": []`. `paths`: Forest 96 m, Main Bridge 56 m (the game counts from the line's nearest
  walkable point), Right Bridge 92 m, all `"reachable": true`.
- `economy --compare`: a greedy all-economy player reaches 3 gold per dawn after day 1, 8 after day 2, 13 after day 3
  (castle level 2 lets the houses upgrade); vanilla Nordfels is 2 / 6 / 13. Close to vanilla, as intended.
- `waves --compare`: effective HP 100, 210, 312, 385, 552, 1755 for nights 1-6 vs vanilla Nordfels 75, 200, 260, 480,
  525, 900: a compressed vanilla curve with a doubled finale.
- `test` with the plan (speed 3, the king idle):

| Night | Outcome | Castle min HP | Knocked out | Bought that day |
|---|---|---|---|---|
| 1 | dawn | 100/100 | - | castle, h2, t-pass |
| 2 | dawn | 96/100 | - | h4, mill, f1 |
| 3 | dawn | 300/300 | - | castle L2 (Royal Training), barracks (Knights), h1 |
| 4 | dawn | 300/300 | - | t-gate, f2, h5, mill L2 |
| 5 | dawn | 300/300 | t-forest | t-forest, range, h3, t-pass L2 (Castle Tower) |
| 6 | victory | 300/300 | h1, h3, h6, range, t-forest | barracks L2, t-gate L2 (Armored), h6, f3 |

Reading it: nights 1-4 hold easily (the player has gold to spare for houses: good for a teaching level); night 5
starts to bite on the Forest side; the finale breaks through in the east without reaching the castle. A harder
version would add an Ogre to night 5 or move the range closer to the forest tower.

## Iterating

- A plan item that can't be bought reports why (`"isn't available yet (its requirement isn't met)"` = buy its
  prerequisite first; `show` lists every slot's `needs`; `"not enough gold"` = the economy is too slow for that plan).
- After changing ops, run `apply` again on a fresh level (`level ... ` with a new id, or `new --force` and redo), or
  edit with `move_slot`/`set_slot`/`remove_slot` ops on the existing one.
