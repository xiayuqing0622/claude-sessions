# cs - Claude Sessions Manager

Track, label, and monitor your parallel Claude Code sessions.

## Features

### 1. Session Dashboard (`cs`)

List all active Claude Code sessions on the machine:

```bash
cs        # your sessions
cs -a     # all users (shared server)
```

```
PID      TTY      TIME    PROJECT                          TASK
-------  -------  ------ -------------------------------  --------------------
123456   pts/1    2h40m   workspace/my-project             fix auth module
234567   pts/2    23h5m   workspace/backend          [yolo] database migration
345678   pts/70   1d6h    workspace/frontend                add dark mode
456789   pts/26   1d7h    workspace/backend        [orphan] refactor infra
```

- **Green** = label in `session-labels.json` (manual or auto)
- **Dim** = auto-detected from history at query time
- **Red `[orphan]`** = parent terminal/IDE has died, process still running
- `[wt]` = `--worktree` mode, `[yolo]` = `--dangerously-skip-permissions`

Manual labeling:

```bash
cs label 52 "refactor MoE layer"   # by pts number
cs label 528378 "fix attention bug" # by PID
cs unlabel 52                       # remove
cs clean                            # remove labels for dead sessions
```

### 2. Rich Statusline

A custom Claude Code statusline showing everything at a glance:

```
🏷️ fix auth module  📁 workspace/my-project  🌿 feat/auth  🤖 Opus 5  🔑 you@example.com  📟 v2.1.274  🎨 concise
🧠 Ctx: 56% [=====-----]  ⚡ Session: 40% used, resets in 2h 31m [====------]  📊 Weekly(all): 57% used, resets in 1d 13h [=====-----]  🎭 Fable: 5% used [----------]
```

**Identity group** — Session label, working directory, git branch, model, [auth source](#auth-source), Claude Code version, output style

**Usage group** — Context window remaining, session (5h) usage limit, weekly (7d) usage limit for **all models**, and the weekly limit for the **model-scoped bucket** (e.g. Fable) when your plan has one

Each group is one row when it fits, and wraps onto more when it doesn't.

**Width never costs you a segment.** Every segment is built at three verbosity tiers, and
the statusline picks the richest tier that fits, then *wraps* instead of dropping anything.
Split your terminal in half and both panes still show the complete status line:

| Tier | Looks like | Used when |
|------|------------|-----------|
| 1 | `⚡ Session: 24% used, resets in 1h 12m [==--------]` | it fits the row budget |
| 2 | `⚡ Session: 24% used, 1h12m` | tier 1 would need extra rows |
| 3 | `⚡ S: 24% 1h12m` | tier 2 would too |

`CS_STATUSLINE_MAX_ROWS` (default `1`) is the row budget **per group** — identity segments
are one group, usage segments the other. Raise it to `2` to prefer progress bars over
compactness on a narrow pane. If even tier 3 overflows the budget, segments wrap onto
extra rows; nothing is ever dropped or clipped.

```
# 70 columns — same segments, terser, wrapped
🏷️ fix auth module  📁 workspace/my-project  🌿 feat/auth
🤖 Opus 5 (1M)  🔑 you  📟 v2.1.274  🎨 concise
🧠 Ctx: 56%  ⚡ S: 40% 2h31m  📊 W(all): 57% 1d13h  🎭 Fable: 5%
```

Widths are measured with `python3` when available, so CJK session labels and emoji are
counted as double-width; without it byte counts are used, which over-estimates and so
wraps early rather than clipping.

Terminal width is detected in this order: `CS_STATUSLINE_WIDTH` override → `$COLUMNS` →
reading the controlling pts device of an ancestor process → `100` fallback.

The weekly segment is labelled `Weekly` when it is the only weekly number, and `Weekly(all)` once a per-model bucket sits next to it.

#### Auth source

When you run sessions against more than one account or provider side by side — a claude.ai
login, a `claude --settings <file>` whose `env` block carries a token or a third-party
endpoint, a `CLAUDE_CONFIG_DIR` profile — they look identical from inside a session. The
`🔑` segment says which one this session uses. Claude Code keeps its own OAuth token and
subscription variables out of the statusline's environment and puts no auth field on stdin,
so the segment works it out from what is left; the first match wins:

| # | Source | Shows |
|---|--------|-------|
| 1 | Launched with `--settings <file>` whose `env` sets an auth or endpoint variable, or which sets `apiKeyHelper` | the file name without `.json` |
| 2 | `CLAUDE_CODE_USE_BEDROCK` / `_VERTEX` / `_FOUNDRY` | `Bedrock` / `Vertex` / `Foundry` |
| 3 | `ANTHROPIC_BASE_URL` other than `api.anthropic.com` | its host (terse tier: last two labels) |
| 4 | `ANTHROPIC_API_KEY` or `ANTHROPIC_AUTH_TOKEN` | `API key` |
| 5 | Otherwise — the claude.ai login stored in the config dir | its email (terse tier: the part before `@`) |

So `claude --settings ~/profiles/work.json` shows `🔑 work` with no extra setup. Only the
*names* of variables in that file are read, never their values. Rule 1 reads the command line
from `/proc/$CLAUDE_PID/cmdline`, falling back to `ps` (where a path containing a space is
missed). A background session may run in a pre-started spare process whose command line
carries no flags; for those the flags are looked up in the agent-view daemon's
`daemon/roster.json` by process or session id — an internal Claude Code file, so if its
format changes such sessions fall back to rules 2–5. Inline JSON passed to `--settings` has
no name, so rules 2–5 apply to it. The email
in rule 5 is whatever the config dir currently holds, so it follows an account switcher that
rewrites the login. `CS_AUTH_LABEL` replaces the text shown (set it in the settings file's
`env` block); `CS_AUTH_SEGMENT=0` hides the segment.

Sessions matched by rules 1–4 do not bill to the stored login, so the usage cache — read
with that login — says nothing about them: they show only the Session/Weekly numbers Claude
Code reports natively (none for third-party endpoints), no `~` cached values and no per-model
bucket, and they never start the probe.

### 3. Usage Limit Monitoring

Two sources, by necessity:

**Session (5h) and Weekly (7d), all models** — read straight from the data Claude Code
already pipes to the statusline on stdin (`rate_limits`). No API call, no OAuth token.

- **Session (5h)** — current 5-hour window utilization
- **Weekly (7d), all models** — 7-day rolling utilization across every model
- Color-coded: mint (normal) → peach (>=70%) → red (>=90%) → bold red (limit hit)

`rate_limits` is provided by Claude Code **only for Claude.ai subscribers (Pro/Max)**,
and only after the first API response in a session; each window can be independently
absent. A window you just opened therefore reports nothing at all until you send your
first prompt.

The probe cache below carries copies of both numbers, so a freshly opened window shows
them straight away, **marked with `~`** (`⚡ Session: ~11% used, 4h48m`) to say they came
from cache rather than from this session. They switch to the native values — and the `~`
disappears — on the first API response. With no usable cache (API-key auth, probe off,
cache older than `CS_USAGE_MAX_AGE`), the segments are simply hidden, as before.

**Weekly (7d), per model** — e.g. the separate Fable weekly bucket that `/usage` shows
as "Current week (Fable)". This one is *not* on the statusline stdin at all: Claude Code
only passes the aggregate `seven_day`. So `usage-probe.sh` reads it from the same place
`/usage` does — a plain `GET /api/oauth/usage` with the OAuth token Claude Code already
stores — and caches it in `$CLAUDE_CONFIG_DIR/model-usage-cache.json`. The same response
carries the session and all-model weekly windows, which are cached too and used to fill
the gap before Claude Code reports them.

- **Event-driven, not polled.** A request is only worth making when a session knows
  something the cache does not — its native windows have moved past the cached ones, so
  the per-model bucket has moved too. **A session that is only reading the cache makes
  zero requests**, however often it renders; refreshing falls on whichever session is
  actually consuming usage, at most once per `CS_USAGE_MIN_INTERVAL` (180s), with a lock
  so concurrent sessions never stack up. When every session is idle, staleness
  (`CS_USAGE_MAX_AGE`) is the only thing that triggers a refresh.
- Fired **in the background by the statusline** with every fd detached, so rendering never
  waits on the network (measured: ~0.19s per statusline run).
- It is a **usage read, not a model call** — it costs no tokens.
- Whatever the cache holds is what gets drawn. If the probe fails (API-key auth, expired
  token, no network) or the cache goes stale (>30 min), the segment disappears and the
  weekly label falls back to plain `Weekly`. The reason is logged when `CS_STATUSLINE_LOG=1`.
- The per-model reset time is only printed when it differs from the all-models reset —
  normally both buckets roll over together.
- Whatever buckets the endpoint returns are shown by name, so this works unchanged for
  an `Opus` bucket or any future model-scoped window.

Tunables (env vars):

| Var | Default | Effect |
|-----|---------|--------|
| `CS_MODEL_USAGE=0` | on | Turn the per-model segment and its probe off entirely |
| `CS_USAGE_MIN_INTERVAL` | `180` | Floor between refreshes, even when usage keeps moving |
| `CS_USAGE_ERROR_BACKOFF` | `900` | Slower retry after a failed probe |
| `CS_USAGE_MAX_AGE` | `1800` | Ignore (and refresh) the cache once it is older than this |
| `CS_USAGE_FORCE=1` | off | Bypass the floor (for a manual probe run) |
| `CS_USAGE_BASE_URL` | `https://api.anthropic.com` | API host for the usage read. `ANTHROPIC_BASE_URL` is not followed: it usually points at a third-party endpoint, and the read carries your claude.ai OAuth token |
| `CS_STATUSLINE_MAX_ROWS` | `1` | Row budget per segment group (see above) |
| `CS_AUTH_LABEL` | unset | Text for the [auth source](#auth-source) segment |
| `CS_AUTH_SEGMENT=0` | on | Hide the auth source segment |
| `CS_STATUSLINE_LOG=1` | off | Write `statusline.log` (the whole stdin payload per render) |
| `CS_STATUSLINE_LOG_MAX` | `1048576` | Rotate that log to `.log.1` past this size |

> **Earlier versions** ran a `ratelimit-probe.sh` PostToolUse hook that made a
> background **Haiku API call** to fetch rate-limit headers. That's gone — Claude Code
> surfaces the session/weekly numbers natively now, so the probe, its hook, and
> `ratelimit-cache.json` were removed. Re-running the installer cleans them up.
> `usage-probe.sh` is not a revival of it: it makes no model call, it only reads the
> usage endpoint, and only for the one number Claude Code doesn't hand us.

### 4. Multiple Sessions

Usage limits are account-wide, but each Claude Code session only learns about them from
**its own** API responses — `rate_limits` on the statusline stdin is a per-session
snapshot. A window you left idle keeps reporting whatever it was last told, however much
you consume in another one. Worse, Claude Code re-runs the status line command only on
events (a new assistant message, a model/mode/permission change — **not** keystrokes), so
an idle window does not even re-render.

Both halves are handled:

**Re-render on a timer.** The installer sets `statusLine.refreshInterval` to 60 seconds:

```json
"statusLine": { "type": "command", "command": "…/statusline.sh", "padding": 0, "refreshInterval": 60 }
```

Claude Code then re-runs the command every N seconds *in addition to* its event-driven
updates. An existing value is kept; remove the key for event-driven only.

This is not free: every open session re-runs the script on that timer whether or not you
are looking at it. One render costs ~190ms and forks ~115 short-lived processes, so six
sessions at 60s work out to ~360 renders an hour, roughly 2% of one core around the clock.
Halving the interval doubles that. It is also why the debug log is off by default — see
`CS_STATUSLINE_LOG` below.

**Show whoever has the newer reading.** The probe cache is one file per Claude config dir,
shared by every session on the machine. Usage only grows inside a window, so a higher
percentage for the same `resets_at` is simply the newer reading — each session compares
its own snapshot against the cache and shows whichever is ahead, marking cached values
with `~`:

| This session vs. cache | Shows | Refreshes the cache |
|---|---|---|
| Ahead (you are working here) | its own native numbers | yes, so other windows catch up |
| Behind (you are working elsewhere) | `~` cached numbers | no |
| No `rate_limits` yet (just opened) | `~` cached numbers | no |
| Equal | native numbers | no |

So the session doing the work pays for the refresh, and every other window picks the
result up on its next render — within `refreshInterval` seconds, without asking the API
anything itself.

### 5. Smart Auto-labeling

A PreToolUse hook (`cs-hook`) automatically labels each session on first tool use:

- Short messages (<=30 chars) → used directly as the label
- Long messages → **summarized by Haiku** into a concise label (~30 chars, same language)
- Labels appear in both `cs` output and the statusline
- Only runs once per session, cost is negligible

Examples:

| First message | Auto-label |
|---------------|------------|
| handle issue 1311, plan carefully, ask me if unclear | plan issue 1311 |
| why is usage limit not showing for other users on this machine | debug usage limit display |
| fix bug in auth | fix bug in auth |

### 6. Known Limitations

- **Cached numbers can be up to `CS_USAGE_MAX_AGE` old.** A `~` value is normally seconds
  to minutes behind; if every session is idle it can be up to 30 minutes behind before the
  staleness rule refreshes it. Don't size a big job off a `~` number.
- **"Higher percentage is newer" assumes usage only grows inside a window.** If the server
  ever revises a percentage *down* within the same `resets_at` — a correction, a refund,
  rolling-window semantics — the higher cached value keeps winning until that window
  resets. Deliberate trade: it is what lets sessions order two readings with no clock.

### 7. Install & Upgrade

**Recommended — Claude Code plugin** (zero-config hooks):

```
/plugin marketplace add xiayuqing0622/claude-sessions
/plugin install claude-sessions@claude-sessions
/claude-sessions:setup
```

Then **exit and restart Claude Code** — the auto-labeling hook only loads on startup.

The plugin auto-registers `cs-hook`. The `/claude-sessions:setup` slash command runs once to configure the statusline and symlink `cs` into `~/bin`.

**Upgrading** — Claude Code caches plugin files by version, so pulling a new commit isn't enough:

```
/plugin                                          # open UI
→ Marketplaces → claude-sessions → Update marketplace
→ Installed    → claude-sessions → Update now
```

Then exit Claude Code (`exit` / Ctrl+D) and re-run `claude`. Verify in `/plugin` that the version bumped and the Errors tab has no entries for claude-sessions.

## Troubleshooting

**Statusline shows `Ctx` but not `Session` / `Weekly` usage.** `rate_limits` is only in the statusline input for **Claude.ai subscribers (Pro/Max)**, and only **after the first API response** in a session. Common cases:

- You authenticate with an **API key** (console billing) rather than a Pro/Max subscription — API-key usage has no 5h/7d windows, so these segments never appear. This is expected.
- You just started the session and Claude hasn't made an API call yet. With a usable probe cache the numbers appear immediately with a `~` prefix; without one they appear on the first API call.

**Session / Weekly show a `~` prefix.** Those numbers came from the probe cache because this session has no `rate_limits` of its own yet. They are replaced by live values as soon as Claude makes an API call.

Quick check — see what Claude Code is actually handing the statusline:

```bash
echo '' | your-statusline-cmd    # or inspect: the input JSON has a top-level "rate_limits" object only for subscribers
```

**`Ctx` shows `…`.** `context_window` is `null` before the first API response of a session; it fills in as soon as Claude makes a call.

**Weekly shows only one number (no `🎭 Fable` segment).** The per-model bucket comes from `usage-probe.sh`, not from Claude Code. Check, in order:

```bash
CS_USAGE_FORCE=1 ~/.claude/usage-probe.sh   # force a probe, ignoring the interval floor
cat "${CLAUDE_CONFIG_DIR:-$HOME/.claude}/model-usage-cache.json"
```

- `"status": "error"` — `errorMsg` says why (no credentials file → API-key auth, which has no per-model window; HTTP 401 → re-login with `claude auth logout && claude auth login`).
- `"models": []` — your plan has no model-scoped weekly window. Nothing to show.
- Cache fine but nothing renders — it may be older than `CS_USAGE_MAX_AGE`. Turn the log on to see what the statusline actually saw, including whether it asked for a refresh:

```bash
CS_STATUSLINE_LOG=1 ~/.claude/plugins/.../statusline.sh < /dev/null   # or export it for a session
grep "Usage cache" statusline.log | tail
```

---

**Alternative — clone + script** (no plugin):

```bash
git clone https://github.com/xiayuqing0622/claude-sessions.git
cd claude-sessions
./cs install
```

Does the same thing as the plugin path, but registers hooks directly in `~/.claude/settings.json`. Upgrading: `git pull` (symlinks stay live).

Custom bin directory: `./cs install /usr/local/bin`

## Requirements

- Python 3.6+ — for the `cs` dashboard, auto-labeling, and the per-model usage probe
- Linux (the `cs` dashboard uses the `/proc` filesystem)
- `jq` — **optional**; the statusline parses its JSON with a pure-bash fallback when `jq` isn't on `PATH`

Session and weekly (all models) limits come straight from Claude Code's statusline input — no API call, no OAuth token. Only the per-model weekly bucket needs the `usage-probe.sh` read, which uses Python's `urllib` (no `curl` dependency) and can be switched off with `CS_MODEL_USAGE=0`.

## Custom Claude config dir

All scripts honor Claude Code's `CLAUDE_CONFIG_DIR` env var. If you've moved your Claude config out of `~/.claude` (e.g. `export CLAUDE_CONFIG_DIR=/data/.claude`), `cs`, the statusline, and both hooks read/write inside that dir instead. Project-level `.claude` dirs keep their conventional name.
