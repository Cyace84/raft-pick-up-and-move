#!/usr/bin/env bash
# Packs the Raft Mod Loader build.
#
# An .rmod is a zip of SOURCE, not a compiled assembly: RML compiles every .cs in the archive at
# load time. So the package is the shared mod logic (src/, minus the BepInEx entry point) plus the
# RML entry point (rml/Host.Rml.cs), the manifest and the two images RML shows in its mod list.
#
# The BepInEx build is unaffected: it compiles src/ including src/hosts/Host.BepInEx.cs and drops
# rml/ (see the Compile Remove in PickUpMove.csproj).
set -euo pipefail
cd "$(dirname "$0")"

VER=$(python3 -c "import json;print(json.load(open('rml/modinfo.json'))['version'])")
NAME=$(python3 -c "import json;print(json.load(open('rml/modinfo.json'))['name'])")
OUT="dist/$NAME $VER.rmod"

bash gen-stamp.sh   # same build stamp the BepInEx build reads back from the load line

STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT

cp src/*.cs "$STAGE/"                 # shared logic; src/hosts/ is a subfolder, so the BepInEx
                                      # entry point is NOT picked up by this glob
cp rml/Host.Rml.cs "$STAGE/"          # the RML entry point
cp rml/modinfo.json "$STAGE/"
cp rml/icon.jpg   "$STAGE/"
cp rml/banner.jpg "$STAGE/"

# Guard: RML rejects an archive that does not contain exactly one Mod subclass, and a stray
# BepInEx reference would fail the load-time compile with no assembly to bind against.
# Matches real surface only (a using directive or a base-class clause), not prose: the shared
# architecture note legitimately explains what BepInEx does differently.
if grep -rlE "^ *using BepInEx|: *BaseUnityPlugin" "$STAGE" >/dev/null 2>&1; then
    echo "FAIL: BepInEx surface leaked into the rmod:" >&2
    grep -rlnE "^ *using BepInEx|: *BaseUnityPlugin" "$STAGE" >&2
    exit 1
fi
MODCLASSES=$(grep -rhoE "class Plugin *: *Mod\b" "$STAGE" | wc -l | tr -d ' ')
if [ "$MODCLASSES" != "1" ]; then
    echo "FAIL: expected exactly 1 'class Plugin : Mod' declaration, found $MODCLASSES" >&2
    exit 1
fi

mkdir -p dist
rm -f "$OUT"
( cd "$STAGE" && zip -q -r -X "$OLDPWD/$OUT" . )

echo "packed: $OUT"
unzip -Z1 "$OUT" | sort | sed 's/^/  /'
