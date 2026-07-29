**In vanilla Raft you can pick a placed object back up (hold X), but only the object itself goes to
your inventory.** A chest dumps everything inside onto the floor, powered devices spit out the
battery, and a cooking pot or a fuel tank loses its contents outright. Put a smelter one tile too
far over and you're emptying it by hand and filling it back up.

This mod skips all of that. Aim at the object, press a key, and the normal build ghost follows your
cursor until you click it down where you actually wanted it. Contents come along. If a smelter was
halfway through a bar it picks up where it left off.

It works on the things you craft and place. The raft itself is off-limits (foundations, walls,
floors, pillars). That would be a different, scarier mod.

## What can be moved

* Storages of any size. Items stay inside.
* Cooking pot, grill, juicer, smelter, purifier. Whatever is cooking or smelting rides along,
  timer and all.
* Crop plots take the plants with them, at whatever growth stage they were in.
* Beehives arrive with the combs still in them.
* Battery chargers, biofuel refiners, fuel tanks. Nothing drains out on the way.
* Pipes rewire themselves around the new spot.
* The receiver remembers its frequency. Antennas move on their own, wire and all.
* Sprinklers keep their water and battery.
* Sails, steering wheels, engines and anchors, ready to use where you drop them.
* Whatever you wrote on a sign is still on it.
* Decor, furniture, lights. Paint stays on.

Whatever is standing on top of a moved block comes along with it. The placement ghost previews the
whole group, live contents like batteries and purifier water included, tinted green or red exactly
like the vanilla ghost.

> One exception: the detail plank, whose stretch mechanic doesn't survive a move.

## How to use

Look at a movable object and press the move key (default **M**). It lifts into placement mode and a
ghost tracks your aim. Left-click to place it. Right-click, or press the key again, to cancel and
leave it where it was.

Sometimes a move won't go through. When that happens, a short note at the top of the screen tells
you why, in your game's language.

The move key and two logging switches live in a plain `settings.txt` inside
`mods/ModData/pick-up-and-move/`, written with defaults the first time you run the mod.

## Multiplayer

Works in co-op, as host or as a client. Anyone with the mod can move things, and moves show up live
for every other player running it.

Only one player at a time can be carrying a given object. If two players press the key on the same
chest at once, the first one gets it and the second sees a short note. There is no way to steal a
carried object or to duplicate it in the process.

The host applies moves one at a time. If the host happens to be carrying a block when you start
your move, yours waits its turn: your object stays visible where it is, a note tells you the mod is
waiting on the host, and it goes through the moment the host puts theirs down. The host meanwhile
sees how long you have been waiting.

A player without the mod sees the new position after rejoining, since it's saved correctly.

## Bug reports

This build is a port. The mod grew up as a BepInEx plugin, and the Raft Mod Loader version is
the same code behind a different entry point, so most of my hours testing it are on the other
side. Loading, the everyday moves and a co-op session are checked here, long play is not. If
something behaves differently under RML, I want to hear about it.

If something breaks I need the log.

1. Open `mods/ModData/pick-up-and-move/settings.txt` and set `relay=true`.
2. Reproduce the problem.
3. Zip that folder and attach it to an issue on
   [the GitHub tracker](https://github.com/Cyace84/raft-pick-up-and-move/issues).

Despite the name, relaying always writes this mod's lines to a file, solo or in co-op: one file per
session in that same folder, and nothing else goes in there, so they stay small. For a co-op bug the
host can gather everything from one place, because a client with relaying on sends its lines to the
host on its own. Warnings and errors are recorded even with the verbose switch off, so the report
keeps them.

## Source

[github.com/Cyace84/raft-pick-up-and-move](https://github.com/Cyace84/raft-pick-up-and-move) (MIT).

Thanks to the HarmonyX team for the tooling this is built on.
