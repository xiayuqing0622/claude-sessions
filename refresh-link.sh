#!/usr/bin/env bash
# refresh-link.sh — SessionStart hook. Keeps $CLAUDE_DIR/claude-sessions (a symlink with no
# version in its path) pointing at the plugin version that is running now.
#
# Claude Code caches a plugin under .../claude-sessions/<version>/, so any path into the
# cache goes stale after an update. plugin-setup.sh writes statusLine, subagentStatusLine and
# the `cs` link through $CLAUDE_DIR/claude-sessions instead, and this hook re-points that one
# link after each update; nothing else has to be re-run.
#
# A SessionStart hook's stdout becomes session context, so this prints nothing and never fails.
CLAUDE_DIR="${CLAUDE_CONFIG_DIR:-$HOME/.claude}"
ROOT="${CLAUDE_PLUGIN_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")" 2>/dev/null && pwd)}"
LINK="$CLAUDE_DIR/claude-sessions"

[ -n "$ROOT" ] && [ -d "$ROOT" ] || exit 0
# Only touch a path that is absent or already a symlink; never replace a real file or directory.
if [ -L "$LINK" ] || [ ! -e "$LINK" ]; then
  [ "$(readlink "$LINK" 2>/dev/null)" = "$ROOT" ] || ln -sfn "$ROOT" "$LINK" 2>/dev/null
fi
exit 0
