# Pick Up & Move

A Raft mod for repositioning things you've already built. Aim at a placed object, press a key, and
the normal build ghost follows your cursor until you click to set it back down — with its contents
and state intact.

It moves crafted **placeables** (storage, devices, decor), **not** the raft structure itself
(foundations, walls, floors, pillars). No more emptying a chest into your hands just to nudge
it one tile over.

## What it moves (keeping state)

- **Storage** — small, medium, large, wall cabinets. Items stay inside.
- **Cooking** — cooking pot, grill, juicer, smelter, purifier. Recipe, progress and slots survive.
- **Power/water devices** that the game knows how to restore — fuel tank, wind turbine, and similar.
- **Signs / plaques** — keeps the written text.
- **Decor, furniture, plain placeables.**

If an object has device state the mod can't safely carry yet (some planters/sprinklers/recyclers),
it simply **refuses to move** rather than risk losing that state. Nothing is ever silently dropped.

## Install

1. Install [BepInEx 5](https://docs.bepinex.dev/) (Mono build) for Raft.
2. Put `PickUpMove.dll` in `Raft/BepInEx/plugins/`.
3. Start the game once so the config file gets written.

## How to use

Look at a movable object and press the move key (default `M`). It lifts into placement mode and a
ghost tracks your aim. Left-click to place it. Right-click, or press the key again, to cancel and
leave it where it was.

Change the key in `BepInEx/config/com.cyace84.pickupmove.cfg`.

## Multiplayer

Works in co-op, as host or as a client. Only the player moving an object needs the mod.

## Build from source

```bash
DOTNET_ROOT=$HOME/.dotnet dotnet build -c Release
# if Raft isn't at the default path:
#   dotnet build -c Release -p:GameDir="/path/to/Raft"
```

The DLL ends up in `bin/Release/`. Copy it to `Raft/BepInEx/plugins/`.

## License

[MIT](LICENSE).
