# Changelog

## 1.1.0

**Runs on Raft Mod Loader.** The mod now ships for both loaders from one source tree: the
BepInEx plugin as before, and a `.rmod` for RML. Features, settings and behaviour are the same on
either one, so use whichever loader you already have. Multiplayer across the two is fine. What
matters is that the person doing the moving has the mod, not which loader they run it under.

**Co-op: one player moving no longer freezes another.** The host applies moves one at a time, so
while somebody is carrying a block, everyone else's move waits its turn. That wait used to be
both invisible and unbounded. Your block left the world the moment you asked for it, and then
nothing happened until the host put their own block down. In one recorded session five moves out
of 27 waited between 2 and 34 seconds like that.

Your block now stays exactly where it is until the host really moves it, so nothing vanishes
while you wait. If the answer doesn't come back within half a second you're told the mod is
waiting on the host, and the host gets a line on screen saying somebody's move is waiting and for
how long. A request that has waited thirty seconds is answered rather than left hanging: you're
told the host is busy, and your block stays put.

**The storage hint no longer patches the game.** The "M Move" line over a chest used to come from
a patch on the game's own look-at code. It now comes from the same check that draws the hint for
every other object, so the mod patches no game method at all. One less place where another mod
touching the same code can take this one down with it.

## 1.0.0

First release. Move a placed object without emptying it first. Chests keep their contents,
smelters keep the bar they were halfway through, devices keep their battery, signs keep their
text and paint.
