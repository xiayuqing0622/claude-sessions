#!/usr/bin/env bash
# subagent-tokens.sh — replace each subagent's `tokenCount` where Claude Code gets it wrong.
# Reads the subagentStatusLine JSON on stdin and prints the same JSON.
#
# Claude Code computes a subagent's `tokenCount` from the usage that arrives with the
# streaming `message_start` event. Anthropic's API puts the input and cache token counts
# there. Other providers behind an Anthropic-compatible endpoint (GLM, for one) send zeros
# in `message_start` and the real counts only in the final `message_delta`, so `tokenCount`
# stays at the size of the text streamed so far, orders of magnitude below the real context.
# The transcript of the subagent records the final usage of every response.
#
# For a subagent whose model id does not start with `claude-`, `tokenCount` becomes
# input_tokens + cache_read_input_tokens + cache_creation_input_tokens of the newest
# transcript record that carries a non-zero usage. Every other subagent is left alone, as is
# any subagent whose transcript is missing or whose last WINDOW_BYTES hold no such record.
#
# The transcript of subagent <id> is <session transcript without .jsonl>/subagents/agent-<id>.jsonl.
#
# This script must never disturb the status line: on any failure it prints its input
# unchanged, and it always exits 0.
exec 2>/dev/null
set -u

WINDOW_BYTES=1048576   # only the end of a transcript is read; an assistant record is far smaller

input=$(cat)
passthrough() { printf '%s\n' "$input"; exit 0; }

command -v jq >/dev/null 2>&1 || passthrough

transcript=$(printf '%s' "$input" | jq -r '.transcript_path | select(type == "string" and endswith(".jsonl"))') || passthrough
[ -n "$transcript" ] || passthrough

# Ids become path components, so only plain names pass.
ids=$(printf '%s' "$input" | jq -r '
  (.tasks // [])[]
  | select((.model | type == "string") and (.model | startswith("claude-") | not)
           and (.id | type == "string" and test("^[A-Za-z0-9_][A-Za-z0-9._-]*$")))
  | .id') || passthrough
[ -n "$ids" ] || passthrough

dir="${transcript%.jsonl}/subagents"
sizes='{}'
for id in $ids; do
  file="$dir/agent-$id.jsonl"
  [ -r "$file" ] || continue
  # The first line of the window may be cut in half; `fromjson?` drops it.
  n=$(tail -c "$WINDOW_BYTES" "$file" | jq -R '
        fromjson? | select(.type == "assistant") | .message.usage | select(type == "object")
        | ((.input_tokens // 0) + (.cache_read_input_tokens // 0) + (.cache_creation_input_tokens // 0))
        | select(. > 0)' | tail -n 1)
  [ -n "$n" ] || continue
  sizes=$(printf '%s' "$sizes" | jq -c --arg id "$id" --argjson n "$n" '.[$id] = $n') || continue
done

out=$(printf '%s' "$input" | jq -c --argjson sizes "$sizes" '
  .tasks |= map(if $sizes[.id] != null then .tokenCount = $sizes[.id] else . end)') || passthrough
printf '%s\n' "$out"
exit 0
