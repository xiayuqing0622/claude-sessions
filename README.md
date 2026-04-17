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
🏷️ fix auth module  📁 workspace/my-project  🌿 feat/auth  🤖 Opus 4.6  📟 v2.1.70  🎨 concise
🧠 Ctx: 56% [=====-----]  ⚡ Session: 40% used, resets in 2h 31m [====------]  📊 Weekly: 6% used, resets in 6d 15h [----------]
```

**Line 1** — Session label, working directory, git branch, model, Claude Code version, output style

**Line 2** — Context window remaining, session (5h) usage limit, weekly (7d) usage limit

The statusline auto-adapts to terminal width so Claude Code doesn't clip it. Two modes:

| Width  | Layout                                                                                     |
|--------|--------------------------------------------------------------------------------------------|
| ≥ 140  | **full** — everything inline with progress bars (`Session: 24% used, resets in 1h 12m [==--------]`) |
| < 140  | **compact** — short labels (`S:` / `W:`), no bars, Weekly on its own row; line 1 drops `📟 version`, `🎨 style`, and the ` (1M context)` suffix |

Terminal width is detected in this order: `CS_STATUSLINE_WIDTH` override → `$COLUMNS` → reading the controlling pts device of an ancestor process → `100` fallback.

### 3. Usage Limit Monitoring

Real-time usage limits from the Anthropic API, displayed in the statusline:

- **Session (5h)** — current 5-hour window utilization
- **Weekly (7d)** — 7-day rolling utilization
- Color-coded: mint (normal) → peach (>=70%) → red (>=90% or limit hit)

A PostToolUse hook (`ratelimit-probe.sh`) makes a minimal background API call (1 Haiku token) every 2 minutes to fetch rate limit headers. If something goes wrong, the statusline shows actionable diagnostics:

```
⚠️ Usage: ~/.claude/.credentials.json not found. Log in: claude auth login
⚠️ Usage: OAuth token expired. Try: claude auth logout && claude auth login
⚠️ Usage: API request failed. Check network or proxy settings
```

### 4. Smart Auto-labeling

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

### 5. Install & Upgrade

**Recommended — Claude Code plugin** (zero-config hooks):

```
/plugin marketplace add xiayuqing0622/claude-sessions
/plugin install claude-sessions@claude-sessions
/claude-sessions:setup
```

Then **exit and restart Claude Code** — hooks only load on startup, so `ratelimit-probe` won't fire (and the Session/Weekly usage bars won't appear) until you restart.

The plugin auto-registers `cs-hook` and `ratelimit-probe.sh`. The `/claude-sessions:setup` slash command runs once to configure the statusline and symlink `cs` into `~/bin`.

**Upgrading** — Claude Code caches plugin files by version, so pulling a new commit isn't enough:

```
/plugin                                          # open UI
→ Marketplaces → claude-sessions → Update marketplace
→ Installed    → claude-sessions → Update now
```

Then exit Claude Code (`exit` / Ctrl+D) and re-run `claude`. Verify in `/plugin` that the version bumped and the Errors tab has no entries for claude-sessions.

## Troubleshooting

**Statusline shows `Ctx` but not `Session` / `Weekly` usage bars.** The `ratelimit-probe.sh` PostToolUse hook hasn't populated `~/.claude/ratelimit-cache.json` yet. Common causes:

- You've only tested with `!cmd` shortcuts — those execute directly in a shell and **don't trigger tool hooks**. Send a normal prompt that makes Claude use a tool (e.g. "read README").
- Claude Code wasn't restarted after installing/updating the plugin. Plugin hooks only register at startup.
- The first probe has a 2-minute TTL; if the cache was just seeded, subsequent tool uses won't re-probe until 2 min elapse.

Quick diagnostic:

```bash
jq -r '.probeTime' ~/.claude/ratelimit-cache.json | xargs -I{} date -d @{} "+%F %T"
date "+%F %T"
```

If `probeTime` lags the current time by more than a few minutes while you're actively using Claude Code with real tool calls, check `/plugin → Errors` for load errors.

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

- Python 3.6+
- Linux (uses `/proc` filesystem)
- `jq` (optional but recommended, for statusline JSON parsing)
- `curl` (for usage limit probing)
