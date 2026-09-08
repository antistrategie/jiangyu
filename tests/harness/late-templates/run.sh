#!/usr/bin/env bash
# Late-template regression harness: compiles and deploys the harness mod, waits for a game
# session, checks the loader's log and the live templates, and removes the mod again.
#
#   mise run harness:late-templates            deploy, then launch MENACE yourself
#   mise run harness:late-templates -- --launch   also launches it through Steam
#   KEEP=1 ...                                  leave the harness mod deployed
#
# Needs the dev loader in the game (the checks read the Studio bridge).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../../.." && pwd)
cli="$repo/src/Jiangyu.Cli/bin/Release/net10.0/jiangyu"
game="${MENACE_DIR:-$HOME/.local/share/Steam/steamapps/common/Menace}"

[ -x "$cli" ] || dotnet build "$repo/src/Jiangyu.Cli/Jiangyu.Cli.csproj" -c Release
cd "$here"
"$cli" compile
"$cli" deploy

case "${1:-}" in
  --launch) steam -applaunch 2432860 >/dev/null 2>&1 & echo "launching MENACE through Steam" ;;
  *) echo "Launch MENACE now (title screen is enough); the check waits for it." ;;
esac

rc=0
python3 "$here/check.py" "$game" || rc=$?
if [ "${KEEP:-}" != 1 ]; then
  rm -rf "$game/Mods/jiangyu-late-harness"
  echo "removed $game/Mods/jiangyu-late-harness (KEEP=1 keeps it)"
fi
exit $rc
