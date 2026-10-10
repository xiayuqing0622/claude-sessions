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
so the segment reads the environment it does pass through; the first match wins:

| # | Source | Shows |
|---|--------|-------|
| 1 | `CS_AUTH_LABEL` | that label |
| 2 | `CLAUDE_CODE_USE_BEDROCK` / `_VERTEX` / `_FOUNDRY` | `Bedrock` / `Vertex` / `Foundry` |
| 3 | `ANTHROPIC_BASE_URL` other than `api.anthropic.com` | its host (terse tier: last two labels) |
| 4 | `ANTHROPIC_API_KEY` or `ANTHROPIC_AUTH_TOKEN` | `API key` |
| 5 | Otherwise — the claude.ai login stored in the config dir | its email (terse tier: the part before `@`) |

Give each launch profile a name by setting `CS_AUTH_LABEL` in its settings file:

```json
{
  "env": {
    "CLAUDE_CODE_OAUTH_TOKEN": "…",
    "CS_AUTH_LABEL": "work"
  }
}
```

`claude --settings work.json` then shows `🔑 work`. A settings file's `env` block is loaded
into every process that runs the session — including a background session that agent view
hands to a pre-started process — so the label holds wherever the session runs. For a profile
that swaps credentials through a variable Claude Code hides from the statusline (a
`claude setup-token` token in `CLAUDE_CODE_OAUTH_TOKEN`), the label is the only way to tell;
without it such a session shows the stored login's email. The email is whatever the config
dir currently holds, so it follows an account switcher that rewrites the login.
`CS_AUTH_SEGMENT=0` hides the segment.

Sessions matched by rules 1–4 do not bill to the stored login, so the usage cache — read
with that login — says nothing about them: they show only the Session/Weekly numbers Claude
Code reports natively (none for third-party endpoints), no `~` cached values and no per-model
bucket, and they never start the probe. Leave `CS_AUTH_LABEL` unset for the stored login
itself.

#### Agent panel: model per subagent

The statusline always follows the **lead** session: `/model` and the `🤖` segment show the
lead's model, and stay put when you look at a subagent in the agent panel (the one below
the prompt). A subagent's model is fixed when it spawns — and can differ from the lead's, e.g.
via `CLAUDE_CODE_SUBAGENT_MODEL` — so the main statusline has nothing to show for it.

`subagent-statusline.sh` fills that gap through Claude Code's `subagentStatusLine`
setting, which styles each row of the agent panel. Every subagent row gets the model the
agent **actually runs on** (the resolved ID, e.g. `sonnet-5-5`, `glm-5.3-flash`), plus effort,
token count and context %:

```
builder · sonnet-5-5 · high · 29k (14%) · Implement the retry logic
```

Models are colored by family (opus / sonnet / haiku / fable); anything else — GLM, Kimi, a
custom gateway model — is yellow, so a subagent that ended up on an unexpected model stands
out. The setup step registers it next to `statusLine`:

```json
"subagentStatusLine": { "type": "command", "command": "…/subagent-statusline.sh" }
```

**Agent teams are not covered.** With `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1`, a subagent
Claude gives a `name` launches as a *teammate*, and teammate rows are never sent to this
script — they keep the default `name · description · tokens` row. With teams off
(`=0`) a named subagent is an ordinary one: it shows up here with its model and can still be
messaged by name.

Unlike `statusLine`, the installer leaves `subagentStatusLine` alone if you already point it
at your own script. It needs `jq` and **needs Claude Code v2.1.205+** (earlier versions do
not send the per-row `model`); without either, rows keep their default rendering.
`CS_SUBAGENT_DEBUG=1` appends the raw stdin to `subagent-statusline-input.jsonl` in your
Claude config dir — useful to see exactly which rows and fields Claude Code sends.

### 3. Context State and `cs ctx wait`

Claude Code hands the statusline the current context size of the session and of every subagent on each refresh. `statusline.sh` and `subagent-statusline.sh` also leave it in a small file, so other programs — a lead session waiting on a builder, for instance — can read it without polling Claude Code. `ctx-state.sh` is the one script that writes these files; both statuslines call it.

**State files.** One JSON file per context holder, where a holder is the session itself or one of its subagents:

```
$CLAUDE_CONFIG_DIR/cs-ctx/<session_id>/<holder>.json      # default ~/.claude
{"name": "builder", "status": "running", "tokenCount": 28607, "contextWindowSize": 200000}
```

- `<holder>` is `main` for the session itself and the subagent id for a subagent.
- `name`: the subagent's name, or its label when it has no name; `null` for `main`.
- `status`: the subagent's status as Claude Code reports it (`running`, `completed`); `null` for `main`.
- `tokenCount`: the current context size in tokens, `input_tokens + cache_read_input_tokens + cache_creation_input_tokens`.
- `contextWindowSize`: the window of the model that holder runs on, in tokens.

**When files are written.**

- On every statusline call. A file's modification time says how fresh its numbers are; there is no time field inside the file. Claude Code stops calling for a subagent some time after it has `completed`, so that file stops being refreshed and keeps `status` `completed`: trust its status, not its age. When the statusline has a `refreshInterval` configured, `main.json` is also refreshed while the session is idle.
- Not for `main` before the first reply (`current_usage` is `null`), and not when the window size is missing or not above 0. An existing file is left untouched in both cases.
- Each file is written under a temporary name and renamed, so a reader never sees half a file. A write failure never changes what the statusline prints.
- Without `jq` nothing is written; the statusline itself renders as before.

**Cleanup.** When writing, `ctx-state.sh` removes every session directory whose newest file was modified more than a day ago. It does this at most once an hour, throttled by the modification time of `cs-ctx/.last-cleanup`, and never reads file contents.

**`cs ctx wait`** blocks until one holder's context reaches a threshold, then prints one line and exits. Nothing is printed while waiting, so a background job running it wakes its caller exactly once.

```
cs ctx wait --at 60                                  # the current session, at 60% of its window
cs ctx wait --at 60 --target builder-1               # a subagent, by name or by id
cs ctx wait --at 80 --session <session id> --target main
```

- `--at <percent>`: reached when `tokenCount * 100 >= percent * contextWindowSize`, compared without rounding.
- `--target`: `main` (default), a subagent id, or a subagent name; an id is tried first, then the name.
- `--session`: defaults to `$CLAUDE_CODE_SESSION_ID`; with neither, the command fails.
- `--interval` (default 5) is the seconds between reads of the local file; `--no-data-after` (default 300) is how long a missing or unchanged file is tolerated before giving up.

| Output line | Meaning | Exit code |
|---|---|---|
| `REACHED <pct>% tokenCount=<n> contextWindowSize=<w> target=<name>` | threshold reached (wins over `COMPLETED` when both hold) | 0 |
| `COMPLETED target=<name> <pct>%` | the subagent finished below the threshold | 0 |
| `NO-DATA target=<name> reason=<why>` | no file yet after `--no-data-after`, or the file stopped updating | 2 |
| `AMBIGUOUS target=<name> ids=<id>,...` | several subagents share that name; use an id | 2 |

Usage errors (missing `--at`, no session id) exit with 1. `cs ctx wait --help` has the full reference.

### 4. Usage Limit Monitoring

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

**Switching accounts.** Every number here belongs to one account, and a switcher such as
[claude-swap](https://github.com/realiti4/claude-swap) can replace it under running
sessions. Both sides of the display know that:

- The cache carries an `account` field — the `oauthAccount.accountUuid` Claude Code
  records in its global config, which switchers rewrite along with the credential. A
  cache stamped with the account you just left is not *stale*, it is about somebody
  else's quota, so it is discarded outright rather than served until `CS_USAGE_MAX_AGE`,
  and it refreshes immediately instead of waiting out `CS_USAGE_MIN_INTERVAL`.
- The native `rate_limits` are dropped the same way. Claude Code refreshes them from the
  response headers of *that session's* API calls, so right after a switch every open
  window is still quoting the old account — and a window you never type in again would
  quote it forever. The session's own transcript dates it: if its last reply (the last
  assistant entry — not merely the last write, since prompts, slash commands and away
  summaries land there too) predates the switch, there has been no response since, so
  the numbers go and the cache takes over.
  This applies only to sessions on the stored login; one matched by rules 1–4 of the
  [auth source](#auth-source) never billed to it and keeps its numbers.

The practical effect: one render (≤ `refreshInterval`, 60s) after the switch, every
window shows the new account. In between there is a brief gap where a segment is blank
rather than wrong. To collapse even that, have your switcher run the probe on the way
out:

```bash
CS_USAGE_FORCE=1 ~/.claude/usage-probe.sh
```

Tunables (env vars):

| Var | Default | Effect |
|-----|---------|--------|
| `CS_MODEL_USAGE=0` | on | Turn the per-model segment and its probe off entirely |
| `CS_USAGE_MIN_INTERVAL` | `180` | Floor between refreshes, even when usage keeps moving |
| `CS_USAGE_ERROR_BACKOFF` | `900` | Slower retry after a failed probe |
| `CS_USAGE_MAX_AGE` | `1800` | Ignore (and refresh) the cache once it is older than this |
| `CS_USAGE_FORCE=1` | off | Bypass the floor (for a manual probe run) |
| `CS_ACCOUNT_GRACE` | `900` | After an account switch, how long to distrust a session's native numbers when it has no readable transcript to date them by |
| `CS_USAGE_BASE_URL` | `https://api.anthropic.com` | API host for the usage read. `ANTHROPIC_BASE_URL` is not followed: it usually points at a third-party endpoint, and the read carries your claude.ai OAuth token |
| `CS_STATUSLINE_MAX_ROWS` | `1` | Row budget per segment group (see above) |
| `CS_AUTH_LABEL` | unset | Name of a launch profile, shown in the [auth source](#auth-source) segment |
| `CS_AUTH_SEGMENT=0` | on | Hide the auth source segment |
| `CS_STATUSLINE_LOG=1` | off | Write `statusline.log` (the whole stdin payload per render) |
| `CS_STATUSLINE_LOG_MAX` | `1048576` | Rotate that log to `.log.1` past this size |

> **Earlier versions** ran a `ratelimit-probe.sh` PostToolUse hook that made a
> background **Haiku API call** to fetch rate-limit headers. That's gone — Claude Code
> surfaces the session/weekly numbers natively now, so the probe, its hook, and
> `ratelimit-cache.json` were removed. Re-running the installer cleans them up.
> `usage-probe.sh` is not a revival of it: it makes no model call, it only reads the
> usage endpoint, and only for the one number Claude Code doesn't hand us.

### 5. Multiple Sessions

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

### 6. Smart Auto-labeling

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

### 7. Known Limitations

- **Cached numbers can be up to `CS_USAGE_MAX_AGE` old.** A `~` value is normally seconds
  to minutes behind; if every session is idle it can be up to 30 minutes behind before the
  staleness rule refreshes it. Don't size a big job off a `~` number.
- **"Higher percentage is newer" assumes usage only grows inside a window.** If the server
  ever revises a percentage *down* within the same `resets_at` — a correction, a refund,
  rolling-window semantics — the higher cached value keeps winning until that window
  resets. Deliberate trade: it is what lets sessions order two readings with no clock.
- **Account switching is detected on render, not on the switch.** There is no event to
  hook, so the first render after the switch by a session on the stored login is what
  timestamps it in `$CLAUDE_CONFIG_DIR/account-state`. A switch made while every such
  session is closed is noticed by the first one to render afterwards, which is early
  enough.
- **A session's numbers are dated by its last reply, not its last API call.** A call that
  leaves no assistant entry in the session's transcript — a subagent's, an away
  summary's — does not count, so a session doing only that kind of work right after a
  switch shows the cache instead of its own numbers until its next reply. The error is
  always toward the cache, never toward the account you left.
- **A window-rollover in the first `CS_ACCOUNT_GRACE` after a switch can show a cached
  number for up to `CS_USAGE_MIN_INTERVAL`.** It only affects sessions whose transcript
  is unreadable, where the switch has to be bounded by a timer instead.

### 8. Install & Upgrade

**Recommended — Claude Code plugin** (zero-config hooks):

```
/plugin marketplace add xiayuqing0622/claude-sessions
/plugin install claude-sessions@claude-sessions
/claude-sessions:setup
```

Then **exit and restart Claude Code** — the auto-labeling hook only loads on startup.

The plugin auto-registers `cs-hook`. The `/claude-sessions:setup` slash command runs once to configure the statusline and symlink `cs` into `~/bin`. Both point at `~/.claude/claude-sessions`, a symlink to the newest installed plugin version that a SessionStart hook keeps current (it only moves forward, so a session that started before an update cannot point it back), so plugin updates need no re-run of setup.

**Upgrading** — Claude Code caches plugin files by version, so pulling a new commit isn't enough:

```
/plugin                                          # open UI
→ Marketplaces → claude-sessions → Update marketplace
→ Installed    → claude-sessions → Update now
```

Then exit Claude Code (`exit` / Ctrl+D) and re-run `claude`. Verify in `/plugin` that the version bumped and the Errors tab has no entries for claude-sessions.

If you installed before 1.6.1, run `/claude-sessions:setup` once more: it moves `statusLine`, `subagentStatusLine` and the `cs` link from the versioned cache path (which goes stale on every update) to `~/.claude/claude-sessions`.

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

**Usage still shows the account I switched away from.** Check what the statusline thinks is logged in:

```bash
jq -r '.oauthAccount.accountUuid' "${CLAUDE_CONFIG_DIR:-$HOME}/.claude.json"   # the live account
jq -r '.account' "${CLAUDE_CONFIG_DIR:-$HOME/.claude}/model-usage-cache.json"   # the cached one
cat "${CLAUDE_CONFIG_DIR:-$HOME/.claude}/account-state"                # account + when it changed
```

If the first two disagree and stay disagreeing, the probe cannot refresh — run it by hand (`CS_USAGE_FORCE=1 ~/.claude/usage-probe.sh`) and read its `errorMsg`. If they agree but a window still shows the old numbers, that window's transcript has a reply dated after the switch, so its numbers are taken as current. `CS_STATUSLINE_LOG=1` logs an `Account:` line per render with all of it: the live account, when it changed, the session's last reply, the cache's account, and whether the session's own numbers were dropped.

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
- `jq` — **optional**; the statusline parses its JSON with a pure-bash fallback when `jq` isn't on `PATH`. The agent-panel script (`subagent-statusline.sh`) has no fallback: without `jq` it prints nothing and the panel keeps its default rows. Context state files (see Context State) are only written when `jq` is present

Session and weekly (all models) limits come straight from Claude Code's statusline input — no API call, no OAuth token. Only the per-model weekly bucket needs the `usage-probe.sh` read, which uses Python's `urllib` (no `curl` dependency) and can be switched off with `CS_MODEL_USAGE=0`.

## Custom Claude config dir

All scripts honor Claude Code's `CLAUDE_CONFIG_DIR` env var. If you've moved your Claude config out of `~/.claude` (e.g. `export CLAUDE_CONFIG_DIR=/data/.claude`), `cs`, the statusline, and both hooks read/write inside that dir instead. Project-level `.claude` dirs keep their conventional name.
