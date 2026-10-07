#!/usr/bin/env bash
# subagent-statusline.sh — row body for each subagent in the agent panel
# (Claude Code's `subagentStatusLine` setting). Agent-team teammates are never sent to this
# script (verified on v2.1.287); only ordinary subagents, named or not, are.
#
# Claude Code runs this once per refresh tick and pipes ALL visible rows in as one JSON
# object: {"columns": N, "tasks": [{id, name, model, effort, tokenCount, contextWindowSize,
# description, ...}]}. For every row we want to override we print one JSON line
# {"id": "...", "content": "..."}; rows we do not print keep the default rendering.
#
# Besides drawing rows it records each subagent's context size through ctx-state.sh.
#
# What it adds over the default `name · description · tokens` row: the model each agent
# actually runs on, which the main statusline cannot show (it always follows the lead).
#
#   CS_SUBAGENT_DEBUG=1       append the raw stdin to subagent-statusline-input.jsonl
#   CS_SUBAGENT_DEBUG_FILE    ...or to this path instead
#   NO_COLOR                  disable ANSI colors
set -u

input=$(cat)

if [ "${CS_SUBAGENT_DEBUG:-0}" = "1" ]; then
  dbg="${CS_SUBAGENT_DEBUG_FILE:-${CLAUDE_CONFIG_DIR:-$HOME/.claude}/subagent-statusline-input.jsonl}"
  printf '%s\n' "$input" >> "$dbg" 2>/dev/null || true
fi

# Without jq we print nothing, which leaves every row on the default rendering.
command -v jq >/dev/null 2>&1 || exit 0

# Leave each subagent's context size where `cs ctx wait` can read it. ctx-state.sh prints
# nothing and cannot affect the rows below.
ctx_dir="${BASH_SOURCE[0]%/*}"
[ "$ctx_dir" = "${BASH_SOURCE[0]}" ] && ctx_dir=.
if [ -x "$ctx_dir/ctx-state.sh" ]; then
  "$ctx_dir/ctx-state.sh" subagents <<< "$input" >/dev/null 2>&1 || true
fi

use_color=1
[ -n "${NO_COLOR:-}" ] && use_color=0

printf '%s' "$input" | jq -rc --argjson color "$use_color" '
  def esc(c): if $color == 1 then "\u001b[" + c + "m" else "" end;
  def paint(c; s): esc(c) + s + esc("0");

  # claude-haiku-4-5-20251001 -> haiku-4-5 ; glm-5.3[1m] -> glm-5.3
  def short: sub("^claude-"; "") | sub("-[0-9]{8}$"; "") | sub("\\[1m\\]$"; "");

  def model_color:
    if   test("opus")   then "35"
    elif test("sonnet") then "36"
    elif test("haiku")  then "32"
    elif test("fable")  then "34"
    else "33" end;   # anything else (GLM, Kimi, ...) stands out in yellow

  def tok: if . >= 1000 then ((. / 1000 | floor | tostring) + "k") else tostring end;

  (.columns // 80) as $cols
  | (.tasks // [])[]
  | select(.id != null)
  | (.name // .label // .id) as $name
  | (.model // "" | short) as $model
  | (.effort // "" | tostring) as $effort
  | (.tokenCount // 0) as $n
  | (if (.contextWindowSize // 0) > 0
       then " (" + (($n * 100 / .contextWindowSize) | floor | tostring) + "%)" else "" end) as $pct
  | ($name + " · "
       + (if $model != "" then $model else "…" end)
       + (if $effort != "" then " · " + $effort else "" end)
       + " · " + ($n | tok) + $pct) as $plain
  # Claude Code indents the row itself; leave some slack so the description never wraps it.
  | ($cols - ($plain | length) - 8) as $room
  # Plain subagents carry their title in `label` (name is null) and repeat it as the
  # description; print it only once.
  | ((.description // "") | gsub("[\\r\\n]+"; " ")) as $desc0
  | (if $desc0 == $name then "" else $desc0 end) as $desc
  | (if $room > 8 and ($desc | length) > 0
       then " · " + (if ($desc | length) > $room then $desc[0:($room - 1)] + "…" else $desc end)
       else "" end) as $tail
  | {id: .id,
     content: (paint("1"; $name)
       + paint("2"; " · ")
       + (if $model != "" then paint($model | model_color; $model) else paint("2"; "…") end)
       + (if $effort != "" then paint("2"; " · " + $effort) else "" end)
       + paint("2"; " · " + ($n | tok) + $pct)
       + paint("2"; $tail))}
' 2>/dev/null || true
