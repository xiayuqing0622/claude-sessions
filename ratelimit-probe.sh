#!/bin/bash
# ratelimit-probe.sh — PostToolUse hook
# Probes Anthropic API for rate limit headers and caches results.
# Runs the actual probe in background so it doesn't block Claude Code.

# Consume hook stdin immediately
cat > /dev/null

CACHE="$HOME/.claude/ratelimit-cache.json"
CREDS="$HOME/.claude/.credentials.json"
LOCK="/tmp/.claude-ratelimit-probe.lock"
TTL=120  # don't probe more often than every 2 minutes

# Write an error status to cache so statusline can display diagnostics
write_error_cache() {
  local err_code="$1" err_msg="$2"
  local now=$(date +%s)
  if command -v jq >/dev/null 2>&1; then
    jq -n --argjson now "$now" --arg code "$err_code" --arg msg "$err_msg" \
      '{probeTime: $now, status: "error", error: $code, errorMsg: $msg}' > "$CACHE"
  else
    cat > "$CACHE" <<EJSON
{"probeTime":${now},"status":"error","error":"${err_code}","errorMsg":"${err_msg}"}
EJSON
  fi
}

# Quick cache freshness check (synchronous, fast)
if [ -f "$CACHE" ]; then
  cache_mtime=$(stat -c %Y "$CACHE" 2>/dev/null || stat -f %m "$CACHE" 2>/dev/null || echo 0)
  now=$(date +%s)
  [ $(( now - cache_mtime )) -lt $TTL ] && exit 0
fi

# Background the actual probe so we don't block Claude Code
(
  # File lock to prevent concurrent probes
  if command -v flock >/dev/null 2>&1; then
    exec 9>"$LOCK"
    flock -n 9 || exit 0
  else
    # macOS: simple lock file with staleness check
    if [ -f "$LOCK" ]; then
      lock_age=$(( $(date +%s) - $(stat -f %m "$LOCK" 2>/dev/null || echo 0) ))
      [ "$lock_age" -lt 30 ] && exit 0
    fi
    echo $$ > "$LOCK"
    trap 'rm -f "$LOCK"' EXIT
  fi

  # Read OAuth access token
  TOKEN=""
  CREDS_FOUND=0
  # 1. Try credentials file (Linux)
  if [ -f "$CREDS" ]; then
    CREDS_FOUND=1
    if command -v jq >/dev/null 2>&1; then
      TOKEN=$(jq -r '.claudeAiOauth.accessToken // empty' "$CREDS" 2>/dev/null)
    else
      TOKEN=$(python3 -c "import json; print(json.load(open('$CREDS')).get('claudeAiOauth',{}).get('accessToken',''))" 2>/dev/null)
    fi
  fi
  # 2. Try macOS Keychain
  if [ -z "$TOKEN" ] && command -v security >/dev/null 2>&1; then
    TOKEN=$(security find-generic-password -s "Claude Code-credentials" -w 2>/dev/null \
      | python3 -c "import sys,json; print(json.load(sys.stdin).get('claudeAiOauth',{}).get('accessToken',''))" 2>/dev/null)
    [ -n "$TOKEN" ] && CREDS_FOUND=1
  fi
  if [ -z "$TOKEN" ]; then
    if [ "$CREDS_FOUND" -eq 0 ]; then
      write_error_cache "no_credentials" "$CREDS not found. Log in: claude auth login"
    else
      write_error_cache "no_token" "OAuth token not found in $CREDS. Try: claude auth logout && claude auth login"
    fi
    exit 1
  fi

  # Minimal API call (Haiku, 1 token) — just to get rate limit headers
  RESP=$(curl -sS -i --max-time 10 \
    -X POST "https://api.anthropic.com/v1/messages" \
    -H "Content-Type: application/json" \
    -H "Authorization: Bearer $TOKEN" \
    -H "anthropic-version: 2023-06-01" \
    -H "anthropic-beta: oauth-2025-04-20" \
    -d '{"model":"claude-haiku-4-5-20251001","max_tokens":1,"messages":[{"role":"user","content":"."}]}' \
    2>/dev/null)

  if [ -z "$RESP" ]; then
    write_error_cache "api_failed" "API request failed. Check network or proxy settings"
    exit 1
  fi

  # Check for HTTP error status
  http_status=$(echo "$RESP" | head -1 | grep -o '[0-9]\{3\}')
  if [ -n "$http_status" ] && [ "$http_status" -ge 400 ] 2>/dev/null; then
    case "$http_status" in
      401) write_error_cache "auth_expired" "OAuth token expired. Try: claude auth logout && claude auth login" ;;
      403) write_error_cache "auth_forbidden" "Access denied (HTTP 403). Check your subscription status" ;;
      *)   write_error_cache "api_http_${http_status}" "API returned HTTP ${http_status}" ;;
    esac
    exit 1
  fi

  # Helper: extract a response header value (case-insensitive)
  hdr() { echo "$RESP" | grep -i "^$1:" | head -1 | sed 's/^[^:]*: *//' | tr -d '\r\n'; }

  # Extract all rate limit headers
  status=$(hdr "anthropic-ratelimit-unified-status")
  reset_at=$(hdr "anthropic-ratelimit-unified-reset")
  claim=$(hdr "anthropic-ratelimit-unified-representative-claim")
  fallback=$(hdr "anthropic-ratelimit-unified-fallback")
  overage_status=$(hdr "anthropic-ratelimit-unified-overage-status")
  overage_reset=$(hdr "anthropic-ratelimit-unified-overage-reset")

  # Per-claim: session (5h) and weekly (7d)
  u5h=$(hdr "anthropic-ratelimit-unified-5h-utilization")
  r5h=$(hdr "anthropic-ratelimit-unified-5h-reset")
  u7d=$(hdr "anthropic-ratelimit-unified-7d-utilization")
  r7d=$(hdr "anthropic-ratelimit-unified-7d-reset")

  # Build JSON cache
  now=$(date +%s)
  if command -v jq >/dev/null 2>&1; then
    jq -n \
      --argjson now "$now" \
      --arg status "${status:-unknown}" \
      --arg reset_at "${reset_at}" \
      --arg claim "${claim}" \
      --arg fallback "${fallback}" \
      --arg ovs "${overage_status}" \
      --arg ovr "${overage_reset}" \
      --arg u5h "${u5h}" \
      --arg r5h "${r5h}" \
      --arg u7d "${u7d}" \
      --arg r7d "${r7d}" \
      '{
        probeTime: $now,
        status: $status,
        resetsAt: (if $reset_at != "" then ($reset_at | tonumber) else null end),
        rateLimitType: (if $claim != "" then $claim else null end),
        fallbackAvailable: ($fallback == "available"),
        overageStatus: (if $ovs != "" then $ovs else null end),
        overageResetsAt: (if $ovr != "" then ($ovr | tonumber) else null end),
        session: {
          utilization: (if $u5h != "" then ($u5h | tonumber) else null end),
          resetsAt: (if $r5h != "" then ($r5h | tonumber) else null end)
        },
        weekly: {
          utilization: (if $u7d != "" then ($u7d | tonumber) else null end),
          resetsAt: (if $r7d != "" then ($r7d | tonumber) else null end)
        }
      }' > "$CACHE" 2>/dev/null
  else
    # Fallback: python3
    python3 - "$now" "$status" "$reset_at" "$claim" "$fallback" \
      "$overage_status" "$overage_reset" "$u5h" "$r5h" "$u7d" "$r7d" "$CACHE" <<'PY'
import json, sys
a = sys.argv[1:]
def num(s):
    if not s: return None
    try: return float(s) if '.' in s else int(s)
    except: return None
json.dump({
    "probeTime": int(a[0]),
    "status": a[1] or "unknown",
    "resetsAt": num(a[2]),
    "rateLimitType": a[3] or None,
    "fallbackAvailable": a[4] == "available",
    "overageStatus": a[5] or None,
    "overageResetsAt": num(a[6]),
    "session": {"utilization": num(a[7]), "resetsAt": num(a[8])},
    "weekly": {"utilization": num(a[9]), "resetsAt": num(a[10])},
}, open(a[11], "w"), indent=2)
PY
  fi
) &

exit 0
