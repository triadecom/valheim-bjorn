#!/usr/bin/env bash
# Собирает приложение-лаунчер «Valheim с модами» (нужно для доступа к микрофону).
# Запуск: ./build.sh [папка игры с run_bepinex.sh]
#   APP=...        куда положить приложение (по умолчанию /Applications/Valheim с модами.app)
#   BUNDLE_ID=...  идентификатор приложения (доступ к микрофону macOS привязывает к нему)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(dirname "$here")"

GAME_DIR="${1:-$HOME/Library/Application Support/Steam/steamapps/common/Valheim}"
APP="${APP:-/Applications/Valheim с модами.app}"
BUNDLE_ID="${BUNDLE_ID:-local.valheim-bjorn.launcher}"
BRIDGE_DIR="$repo/CompanionBridge"

if [ ! -x "$GAME_DIR/run_bepinex.sh" ]; then
    echo "Не найден $GAME_DIR/run_bepinex.sh — укажи папку игры с установленным BepInEx первым аргументом." >&2
    exit 1
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
sed -e "s|__BRIDGE_DIR__|$BRIDGE_DIR|g" -e "s|__GAME_DIR__|$GAME_DIR|g" "$here/Launcher.applescript" > "$tmp/launcher.applescript"

rm -rf "$APP"
osacompile -s -o "$APP" "$tmp/launcher.applescript"

plist="$APP/Contents/Info.plist"
# Ключ Info.plist: заменить, а если его нет — добавить (osacompile часть ключей уже кладёт)
plist_set() {
    /usr/libexec/PlistBuddy -c "Set :$1 $3" "$plist" 2>/dev/null || /usr/libexec/PlistBuddy -c "Add :$1 $2 $3" "$plist"
}
/usr/libexec/PlistBuddy -c "Delete :CFBundleIconName" "$plist" 2>/dev/null || true
rm -f "$APP/Contents/Resources/Assets.car"
plist_set NSMicrophoneUsageDescription string "'Разговор с компаньоном голосом (клавиша R в игре).'"
plist_set LSUIElement bool true
plist_set CFBundleIdentifier string "$BUNDLE_ID"

icon="$GAME_DIR/valheim.app/Contents/Resources/PlayerIcon.icns"
[ -f "$icon" ] && cp "$icon" "$APP/Contents/Resources/applet.icns"

codesign --force --deep -s - "$APP"
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$APP"
echo "Готово: $APP"
