#!/bin/bash
# usage-probe.sh — cache the per-model weekly usage window for the statusline.
#
# Claude Code hands the statusline only rate_limits.{five_hour,seven_day}: the
# weekly number there is the aggregate across all models, with no per-model split.
# /usage gets the split from a plain GET /api/oauth/usage, whose limits[] array
# carries a "weekly_scoped" row per model bucket (scope.model.display_name, e.g.
# "Fable"). This script makes that same read-only request with the OAuth token
# Claude Code already stores and caches the answer, so the statusline can show
# "Weekly (all)" and "Weekly (Fable)" side by side.
#
# It is fired in the background by statusline.sh, which only asks for a refresh when
# the native windows on its stdin actually moved — an idle session probes zero times.
# A minimum interval and a lock bound the rate from this side too, so no combination
# of open sessions can hammer the endpoint. It is a usage read, not a model call —
# it costs no tokens.
#
# Env:
#   CS_MODEL_USAGE=0          disable entirely (statusline then shows one Weekly segment)
#   CS_USAGE_MIN_INTERVAL=180 floor between probes; CS_USAGE_FORCE=1 bypasses it
#   CS_USAGE_BASE_URL         override the API host. ANTHROPIC_BASE_URL is deliberately not
#                             followed: it usually points at a third-party endpoint, and this
#                             request carries the claude.ai login's OAuth token.

CLAUDE_DIR="${CLAUDE_CONFIG_DIR:-$HOME/.claude}"
CACHE="$CLAUDE_DIR/model-usage-cache.json"
CREDS="$CLAUDE_DIR/.credentials.json"
LOCK="${TMPDIR:-/tmp}/.claude-usage-probe.lock"
MIN_INTERVAL="${CS_USAGE_MIN_INTERVAL:-180}"
[[ "$MIN_INTERVAL" =~ ^[0-9]+$ ]] || MIN_INTERVAL=180

[ "${CS_MODEL_USAGE:-1}" = "0" ] && exit 0

# Rate floor, independent of whoever called us. `CS_USAGE_FORCE=1 ./usage-probe.sh`
# skips it — that is the troubleshooting path.
if [ "${CS_USAGE_FORCE:-0}" != "1" ] && [ -f "$CACHE" ]; then
  cache_mtime=$(stat -c %Y "$CACHE" 2>/dev/null || stat -f %m "$CACHE" 2>/dev/null || echo 0)
  now=$(date +%s)
  [ $(( now - cache_mtime )) -lt "$MIN_INTERVAL" ] && exit 0
fi

command -v python3 >/dev/null 2>&1 || exit 0

write_error() {
  python3 - "$CACHE" "$1" "$2" <<'PY' 2>/dev/null
import json, sys, time
path, code, msg = sys.argv[1], sys.argv[2], sys.argv[3]
json.dump({"probeTime": int(time.time()), "status": "error", "error": code,
           "errorMsg": msg}, open(path, "w"), indent=2)
PY
}

# One probe at a time; a stale lock must not wedge the probe forever.
if command -v flock >/dev/null 2>&1; then
  exec 9>"$LOCK"
  flock -n 9 || exit 0
else
  if [ -f "$LOCK" ]; then
    lock_mtime=$(stat -c %Y "$LOCK" 2>/dev/null || stat -f %m "$LOCK" 2>/dev/null || echo 0)
    [ $(( $(date +%s) - lock_mtime )) -lt 30 ] && exit 0
  fi
  echo $$ > "$LOCK"
  trap 'rm -f "$LOCK"' EXIT
fi

# ---- OAuth token: credentials file (Linux) or macOS Keychain ----
TOKEN=""
CREDS_FOUND=0
if [ -f "$CREDS" ]; then
  CREDS_FOUND=1
  TOKEN=$(python3 -c "import json,sys; print(json.load(open(sys.argv[1])).get('claudeAiOauth',{}).get('accessToken',''))" "$CREDS" 2>/dev/null)
fi
if [ -z "$TOKEN" ] && command -v security >/dev/null 2>&1; then
  TOKEN=$(security find-generic-password -s "Claude Code-credentials" -w 2>/dev/null \
    | python3 -c "import json,sys; print(json.load(sys.stdin).get('claudeAiOauth',{}).get('accessToken',''))" 2>/dev/null)
  [ -n "$TOKEN" ] && CREDS_FOUND=1
fi
if [ -z "$TOKEN" ]; then
  if [ "$CREDS_FOUND" -eq 0 ]; then
    write_error "no_credentials" "$CREDS not found — API-key auth has no per-model weekly window"
  else
    write_error "no_token" "no OAuth token in $CREDS. Try: claude auth logout && claude auth login"
  fi
  exit 1
fi

# The token goes through the environment, never argv (argv is world-readable in ps).
CS_OAUTH_TOKEN="$TOKEN" \
CS_USAGE_BASE="${CS_USAGE_BASE_URL:-https://api.anthropic.com}" \
python3 - "$CACHE" <<'PY' 2>/dev/null
import calendar, json, os, re, sys, time, urllib.error, urllib.request

cache = sys.argv[1]
url = os.environ["CS_USAGE_BASE"].rstrip("/") + "/api/oauth/usage"


def write(obj):
    obj["probeTime"] = int(time.time())
    tmp = cache + ".tmp"
    with open(tmp, "w") as f:
        json.dump(obj, f, indent=2)
        f.write("\n")
    os.replace(tmp, cache)


def epoch(s):
    """ISO 8601 (with optional fraction and offset) -> unix seconds."""
    if not s:
        return None
    m = re.match(r"(\d{4})-(\d\d)-(\d\d)[T ](\d\d):(\d\d):(\d\d)(?:\.\d+)?(Z|[+-]\d\d:?\d\d)?", s)
    if not m:
        return None
    y, mo, d, h, mi, sec = (int(x) for x in m.groups()[:6])
    ts = calendar.timegm((y, mo, d, h, mi, sec, 0, 0, 0))
    off = m.group(7)
    if off and off != "Z":
        sign = 1 if off[0] == "+" else -1
        off = off[1:].replace(":", "")
        ts -= sign * (int(off[:2]) * 3600 + int(off[2:4]) * 60)
    return ts


req = urllib.request.Request(url, headers={
    "Authorization": "Bearer " + os.environ["CS_OAUTH_TOKEN"],
    "Content-Type": "application/json",
    "anthropic-beta": "oauth-2025-04-20",
})
try:
    with urllib.request.urlopen(req, timeout=10) as resp:
        data = json.loads(resp.read().decode("utf-8"))
except urllib.error.HTTPError as e:
    msg = {401: "OAuth token expired. Try: claude auth logout && claude auth login",
           403: "access denied (HTTP 403) — check your subscription status"}.get(
        e.code, "usage endpoint returned HTTP %d" % e.code)
    write({"status": "error", "error": "http_%d" % e.code, "errorMsg": msg})
    sys.exit(1)
except Exception as e:
    write({"status": "error", "error": "request_failed",
           "errorMsg": "usage request failed: %s" % e})
    sys.exit(1)

# limits[]: one row per window. "weekly_scoped" rows are the per-model buckets we came
# for; "session" and "weekly_all" duplicate what the statusline normally gets natively,
# and are kept so it can still show them in a window Claude Code has not yet reported
# any rate_limits for (they arrive only after that window's first API response).
session, weekly_all, models = None, None, []
for row in data.get("limits") or []:
    if not isinstance(row, dict):
        continue
    pct, resets = row.get("percent"), epoch(row.get("resets_at"))
    if pct is None:
        continue
    kind = row.get("kind")
    if kind == "session":
        session = {"percent": pct, "resetsAt": resets}
    elif kind == "weekly_all":
        weekly_all = {"percent": pct, "resetsAt": resets}
    elif kind == "weekly_scoped":
        name = ((row.get("scope") or {}).get("model") or {}).get("display_name")
        if name:
            models.append({"name": name, "percent": pct, "resetsAt": resets})

write({"status": "ok", "session": session, "weeklyAll": weekly_all, "models": models})
PY
