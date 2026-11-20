#!/usr/bin/env bash
# Copies the Qwen model files to the headset over USB.
# Usage: tools/copy-models.sh [folder_with_the_models]
# Default folder: QwenModels/ at the project root.
# adb: uses $ADB, then adb on PATH, then the one that ships with Unity 6000.0.66f2.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="${1:-$ROOT/QwenModels}"
LIST="$ROOT/Assets/PrivacyProxy/Resources/qwen_models.json"
APP=br.ufpe.cin.privacyproxy
DEST=/sdcard/Android/data/$APP/files/models

ADB="${ADB:-$(command -v adb || echo /Applications/Unity/Hub/Editor/6000.0.66f2/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb)}"
[ -x "$ADB" ] || { echo "adb not found. Install it or pass its path: ADB=/path/to/adb $0" >&2; exit 1; }

# File names come from the same list the app checks.
FILES=$(grep -o '"name": *"[^"]*"' "$LIST" | sed 's/.*"\([^"]*\)"$/\1/')
for f in $FILES; do
    [ -f "$SRC/$f" ] || { echo "Missing $f in $SRC. Pass the right folder: $0 /folder/with/models" >&2; exit 1; }
done

[ "$("$ADB" get-state 2>/dev/null)" = device ] || { echo "Headset not found. Connect the USB cable and accept USB debugging." >&2; exit 1; }
"$ADB" shell pm path "$APP" >/dev/null 2>&1 || { echo "The app $APP is not installed on the headset. Install it first." >&2; exit 1; }

# The app must create its own folder first; one made by adb may not be readable by the app.
"$ADB" shell test -d "$(dirname "$DEST")" || { echo "Open the app once on the headset (it creates its folder), then run this again." >&2; exit 1; }

# adb push keeps the computer's file date, so the app would not notice a re-copy: forget past checks.
"$ADB" shell rm -f "$(dirname "$DEST")/models_verified.txt"
"$ADB" shell mkdir -p "$DEST"
for f in $FILES; do
    echo "Copying $f..."
    "$ADB" push "$SRC/$f" "$DEST/"
done
echo "Done. Open the app: on the first launch it checks the files."
