#!/usr/bin/env bash
# Copies the OpenAI API key file to the headset over USB. Never prints the key.
# Usage: tools/copy-openai-key.sh [key_file]
# Default file: ~/.config/privacy-proxy/openai_key.txt (one line, the key only).
# adb: uses $ADB, then adb on PATH, then the one that ships with Unity 6000.0.66f2.
set -euo pipefail

SRC="${1:-$HOME/.config/privacy-proxy/openai_key.txt}"
APP=br.ufpe.cin.privacyproxy
DEST=/sdcard/Android/data/$APP/files/openai_key.txt

ADB="${ADB:-$(command -v adb || echo /Applications/Unity/Hub/Editor/6000.0.66f2/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb)}"
[ -x "$ADB" ] || { echo "adb not found. Install it or pass its path: ADB=/path/to/adb $0" >&2; exit 1; }

[ -s "$SRC" ] || { echo "Key file is empty or missing: $SRC. Pass its path: $0 /path/openai_key.txt" >&2; exit 1; }

[ "$("$ADB" get-state 2>/dev/null)" = device ] || { echo "Headset not found. Connect the USB cable and accept USB debugging." >&2; exit 1; }
"$ADB" shell pm path "$APP" >/dev/null 2>&1 || { echo "The app $APP is not installed on the headset. Install it first." >&2; exit 1; }

# The app must create its own folder first; one made by adb may not be readable by the app.
"$ADB" shell test -d "$(dirname "$DEST")" || { echo "Open the app once on the headset (it creates its folder), then run this again." >&2; exit 1; }

"$ADB" push "$SRC" "$DEST" >/dev/null
echo "Done. Key copied to $DEST (contents not shown)."
