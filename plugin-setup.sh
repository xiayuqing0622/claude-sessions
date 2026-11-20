#!/usr/bin/env bash
# plugin-setup.sh — finish claude-sessions setup after `/plugin install`.
# Plugin auto-registers hooks; this script handles the two things plugins can't:
#   1. statusLine config in $CLAUDE_CONFIG_DIR/settings.json
#      (defaults to ~/.claude)
#   2. symlinking `cs` into ~/bin so it's runnable from the terminal
# Both go through $CLAUDE_DIR/claude-sessions, a symlink to the installed plugin version that
# the SessionStart hook (refresh-link.sh) keeps current, so a plugin update needs no re-run.
set -euo pipefail

PLUGIN_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
CLAUDE_DIR="${CLAUDE_CONFIG_DIR:-$HOME/.claude}"
SETTINGS="$CLAUDE_DIR/settings.json"
BIN_DIR="${1:-$HOME/bin}"

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
DIM='\033[2m'
NC='\033[0m'

mkdir -p "$BIN_DIR" "$CLAUDE_DIR"

# 0. Version-free path to this plugin. Only an absent path or an existing symlink is replaced;
#    if something else sits there, fall back to the versioned path (it then goes stale on update).
STABLE="$CLAUDE_DIR/claude-sessions"
if [ -L "$STABLE" ] || [ ! -e "$STABLE" ]; then
  [ "$(readlink "$STABLE" 2>/dev/null)" = "$PLUGIN_ROOT" ] || ln -sfn "$PLUGIN_ROOT" "$STABLE"
  ROOT="$STABLE"
else
  echo -e "${YELLOW}$STABLE exists and is not a symlink; using the versioned path, re-run setup after each update${NC}"
  ROOT="$PLUGIN_ROOT"
fi

# 1. Symlink cs to ~/bin
CS_DST="$BIN_DIR/cs"
if [ -L "$CS_DST" ] && [ "$(readlink "$CS_DST")" = "$ROOT/cs" ]; then
  echo -e "${DIM}cs: already linked${NC}"
else
  ln -sfn "$ROOT/cs" "$CS_DST"
  echo -e "${GREEN}cs → $CS_DST${NC}"
fi

# 2. Configure statusLine in user settings.json (preserve existing keys)
python3 - "$SETTINGS" "$ROOT/statusline.sh" <<'PY'
import json, os, sys
path, sl = sys.argv[1], sys.argv[2]
data = {}
if os.path.exists(path):
    try:
        data = json.loads(open(path).read())
    except Exception:
        data = {}
# refreshInterval re-runs the status line on a timer as well as on events. Without it a
# window Claude Code sees no activity in never re-runs the script at all, so its usage
# numbers freeze while you work in another one. Keep whatever the user chose.
existing = data.get("statusLine") or {}
data["statusLine"] = {
    "type": "command",
    "command": sl,
    "padding": 0,
    "refreshInterval": existing.get("refreshInterval", 60),
}
# The agent list rows are drawn by the plugin's own hooks module now. A subagentStatusLine
# that still points at this plugin's old script would draw nothing, so it is removed; one that
# points at the user's own script is left alone.
removed = False
if "claude-sessions" in (data.get("subagentStatusLine") or {}).get("command", ""):
    del data["subagentStatusLine"]
    removed = True
os.makedirs(os.path.dirname(path), exist_ok=True)
with open(path, "w") as f:
    json.dump(data, f, indent=2, ensure_ascii=False)
    f.write("\n")
print(f"\033[0;32mstatusLine → {sl}\033[0m")
if removed:
    print("\033[2mremoved the obsolete subagentStatusLine\033[0m")
PY

# 3. Ensure BIN_DIR is on PATH (only adds to rc once)
case ":$PATH:" in
  *":$BIN_DIR:"*) ;;
  *)
    SHELL_NAME="$(basename "${SHELL:-bash}")"
    case "$SHELL_NAME" in
      zsh) RC="$HOME/.zshrc" ;;
      *)   RC="$HOME/.bashrc" ;;
    esac
    LINE="export PATH=\"$BIN_DIR:\$PATH\""
    if ! grep -qsF "$LINE" "$RC" 2>/dev/null; then
      printf '\n# Added by claude-sessions plugin\n%s\n' "$LINE" >> "$RC"
      echo -e "${GREEN}Added $BIN_DIR to PATH in $RC${NC}"
      echo -e "${YELLOW}Run: source $RC${NC}"
    fi
    ;;
esac

echo -e "${GREEN}Done.${NC} Restart Claude Code to pick up the new statusline."
