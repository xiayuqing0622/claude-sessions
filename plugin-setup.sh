#!/usr/bin/env bash
# plugin-setup.sh — finish claude-sessions setup after `/plugin install`.
# Plugin auto-registers hooks; this script handles the two things plugins can't:
#   1. statusLine config in ~/.claude/settings.json
#   2. symlinking `cs` into ~/bin so it's runnable from the terminal
set -euo pipefail

PLUGIN_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SETTINGS="$HOME/.claude/settings.json"
BIN_DIR="${1:-$HOME/bin}"

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
DIM='\033[2m'
NC='\033[0m'

mkdir -p "$BIN_DIR" "$HOME/.claude"

# 1. Symlink cs to ~/bin
CS_DST="$BIN_DIR/cs"
if [ -L "$CS_DST" ] && [ "$(readlink "$CS_DST")" = "$PLUGIN_ROOT/cs" ]; then
  echo -e "${DIM}cs: already linked${NC}"
else
  ln -sfn "$PLUGIN_ROOT/cs" "$CS_DST"
  echo -e "${GREEN}cs → $CS_DST${NC}"
fi

# 2. Configure statusLine in user settings.json (preserve existing keys)
python3 - "$SETTINGS" "$PLUGIN_ROOT/statusline.sh" <<'PY'
import json, os, sys
path, sl = sys.argv[1], sys.argv[2]
data = {}
if os.path.exists(path):
    try:
        data = json.loads(open(path).read())
    except Exception:
        data = {}
data["statusLine"] = {"type": "command", "command": sl, "padding": 0}
os.makedirs(os.path.dirname(path), exist_ok=True)
with open(path, "w") as f:
    json.dump(data, f, indent=2, ensure_ascii=False)
    f.write("\n")
print(f"\033[0;32mstatusLine → {sl}\033[0m")
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
