#!/usr/bin/env bash
# ctx-state.sh — keep each context holder's latest context size where other programs can
# read it. Called by statusline.sh (mode `main`) and subagent-statusline.sh (mode
# `subagents`) with the status line JSON on stdin; `cs ctx wait` is the reader.
#
# A context holder is the session itself (`main`) or one of its subagents (by subagent id).
# Each holder gets one small file:
#
#   $CLAUDE_CONFIG_DIR/cs-ctx/<session_id>/<main | subagent id>.json   (default ~/.claude)
#   {"name": ..., "status": ..., "tokenCount": N, "contextWindowSize": W}
#
#   name               subagent name, or its label when it has no name; null for `main`
#   status             subagent status as Claude Code reports it; null for `main`
#   tokenCount         current context size: input + cache read + cache creation tokens
#   contextWindowSize  window of the model the holder runs on
#
# The file is rewritten on every call, so its modification time says how fresh the numbers
# are. Claude Code stops calling for a subagent some time after it has completed; that file
# then keeps `status: completed` and only ages, so a reader trusts its status, not its age.
# Nothing is written when the context size is not known yet (no usage before the first
# reply) or the window size is missing.
#
# This script must never disturb the status line: it prints nothing, always exits 0, and
# skips silently without jq. Each file is written to a temporary name and renamed, so a
# reader never sees half a file.
#
# Cleanup: a session directory whose newest file is older than the retention period is
# removed. It runs as part of a write, at most once per interval, throttled by the
# modification time of a marker file; it never reads file contents. It is skipped whenever
# find does not behave as expected, so a failing find can never cause a deletion.
exec 2>/dev/null
set -u

RETENTION_MIN=1440       # one day
CLEANUP_INTERVAL_MIN=60

command -v jq >/dev/null 2>&1 || exit 0

mode="${1:-}"
case "$mode" in main|subagents) ;; *) exit 0 ;; esac

root="${CLAUDE_CONFIG_DIR:-$HOME/.claude}/cs-ctx"
input=$(cat)

# One line per file to write: session_id, holder id, JSON document, separated by tabs.
# Ids become path components, so only plain names pass.
records=$(printf '%s' "$input" | jq -r --arg mode "$mode" '
  def safe_id: type == "string" and test("^[A-Za-z0-9_][A-Za-z0-9._-]*$");
  def row($sid; $id; $doc): [$sid, $id, ($doc | tojson)] | join("\t");

  .session_id as $sid
  | select($sid | safe_id)
  | if $mode == "main" then
      (.context_window // {}) as $cw
      | $cw.current_usage as $u
      | select($u != null and ($cw.context_window_size | type == "number" and . > 0))
      | row($sid; "main"; {
          name: null, status: null,
          tokenCount: (($u.input_tokens // 0) + ($u.cache_read_input_tokens // 0)
                       + ($u.cache_creation_input_tokens // 0)),
          contextWindowSize: $cw.context_window_size})
    else
      (.tasks // [])[]
      | select((.id | safe_id)
               and (.contextWindowSize | type == "number" and . > 0)
               and (.tokenCount | type == "number"))
      | row($sid; .id; {
          name: (.name // .label), status: .status,
          tokenCount: .tokenCount, contextWindowSize: .contextWindowSize})
    end') || exit 0
[ -n "$records" ] || exit 0

while IFS=$'\t' read -r sid id doc; do
  [ -n "$doc" ] || continue
  dir="$root/$sid"
  [ -d "$dir" ] || mkdir -p "$dir" || continue
  tmp="$dir/.$id.json.$$"
  if printf '%s\n' "$doc" > "$tmp" && mv -f "$tmp" "$dir/$id.json"; then :; else rm -f "$tmp"; fi
done <<< "$records"

# Cleanup deletes directories, so it only runs when every find call it relies on is known to
# work: a failed find prints nothing, which must never be read as "no recent files".
marker="$root/.last-cleanup"
cleanup_due() {
  local recent
  [ -d "$root" ] || return 1
  find "$root" -type f -mmin -1 >/dev/null 2>&1 || return 1
  [ -e "$marker" ] || return 0
  recent=$(find "$marker" -mmin "-$CLEANUP_INTERVAL_MIN" 2>/dev/null) || return 1
  [ -z "$recent" ]
}

if cleanup_due; then
  touch "$marker"
  for d in "$root"/*/; do
    [ -d "$d" ] || continue
    recent=$(find "$d" -type f -mmin "-$RETENTION_MIN" 2>/dev/null) || continue
    [ -z "$recent" ] && rm -rf "$d"
  done
fi
exit 0
