#!/usr/bin/env bash
# refresh-link.sh — SessionStart hook. Keeps $CLAUDE_DIR/claude-sessions (a symlink with no
# version in its path) pointing at the newest plugin version in use.
#
# Claude Code caches a plugin under .../claude-sessions/<version>/, so any path into the
# cache goes stale after an update. plugin-setup.sh writes statusLine, subagentStatusLine and
# the `cs` link through $CLAUDE_DIR/claude-sessions instead, and this hook re-points that one
# link after each update; nothing else has to be re-run.
#
# A session keeps running the plugin version it loaded at startup, and SessionStart also fires
# on resume, clear and compact. So a session that started before an update runs this hook from
# the old version too. The link therefore only moves forward: when both the link's target and
# this version have plain version numbers, it is re-pointed only if this version is not older.
# Anything else (no link yet, a target that no longer exists, a non-numeric directory name such
# as a development checkout, or a sort that cannot order versions) is re-pointed as before.
#
# A SessionStart hook's stdout becomes session context, so this prints nothing and never fails.
CLAUDE_DIR="${CLAUDE_CONFIG_DIR:-$HOME/.claude}"
ROOT="${CLAUDE_PLUGIN_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")" 2>/dev/null && pwd)}"
LINK="$CLAUDE_DIR/claude-sessions"

[ -n "$ROOT" ] && [ -d "$ROOT" ] || exit 0
# Only touch a path that is absent or already a symlink; never replace a real file or directory.
[ -L "$LINK" ] || [ ! -e "$LINK" ] || exit 0

current="$(readlink "$LINK" 2>/dev/null)"
[ "$current" = "$ROOT" ] && exit 0

# Whether $ROOT should replace $current: false only when $current is an existing, strictly
# newer version.
should_replace() {
  local pattern='^[0-9]+(\.[0-9]+)*$' mine theirs newest
  [ -d "$current" ] || return 0
  mine="$(basename "$ROOT")"
  theirs="$(basename "$current")"
  [[ "$mine" =~ $pattern && "$theirs" =~ $pattern ]] || return 0
  newest="$(printf '%s\n%s\n' "$mine" "$theirs" | sort -V 2>/dev/null | tail -n 1)"
  [ -n "$newest" ] || return 0
  [ "$newest" = "$mine" ]
}

should_replace && ln -sfn "$ROOT" "$LINK" 2>/dev/null
exit 0
