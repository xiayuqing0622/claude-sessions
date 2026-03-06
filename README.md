# cs - Claude Sessions Manager

A CLI tool to track and label your parallel Claude Code sessions.

## The Problem

When running multiple Claude Code sessions simultaneously, it's hard to remember which terminal is doing what.

## The Solution

`cs` shows all your active Claude sessions with:
- PID, terminal (pts), and runtime
- Working directory / project name
- **Auto-detected task** from session history (summarized by Haiku if long)
- **Manual labels** that persist across restarts
- **Orphan detection** — marks sessions whose parent terminal/IDE has died
- **Usage limit monitoring** — real-time session and weekly utilization in the statusline

## Install

```bash
git clone https://github.com/xiayuqing0622/claude-sessions.git
cd claude-sessions
./cs install
```

The installer will:
- **Symlink** `cs` and `cs-hook` to `~/bin` (updates to the repo take effect immediately)
- **Symlink** `statusline.sh` and `ratelimit-probe.sh` to `~/.claude/`
- Configure `~/.claude/settings.json` (statusLine + auto-label hook + usage limit probe)
- Add `~/bin` to `PATH` in your shell rc if needed
- Save install metadata to `~/.claude/cs-install.json` for upgrade checks

Falls back to file copy if symlinks aren't possible (e.g. cross-device).

If a project-level `.claude` directory is detected, you'll be asked which one to inject settings into (default: `~/.claude` for all projects).

To install to a custom bin directory: `./cs install /usr/local/bin`

### Upgrading

With symlink-based install, pulling the repo is enough — no re-install needed:

```bash
cd claude-sessions && git pull
```

If installed via copy (fallback), `cs` will warn you on startup:

```
cs: statusline.sh, cs-hook outdated. Run: cs install
```

Re-run `cs install` to update.

## Usage

```bash
# List your active Claude sessions
cs

# List all users' sessions (shared server)
cs -a

# Label a session by pts number
cs label 52 "refactor MoE layer"

# Label a session by PID
cs label 528378 "fix attention bug"

# Remove a label
cs unlabel 52

# Clean up labels for dead sessions
cs clean
```

## Example Output

```
PID      TTY      TIME    PROJECT                          TASK
-------  -------  ------ -------------------------------  --------------------
123456   pts/1    2h40m   workspace/my-project             Issue1311规划处理
234567   pts/2    23h5m   workspace/backend          [yolo] 修复数据库迁移
345678   pts/3    1h12m   workspace/frontend                添加暗色模式
456789   pts/26   1d7h    workspace/backend        [orphan] 检查仓库基础设施配置
```

- **Green** text = label persisted in `session-labels.json` (manual or auto-set by cs-hook)
- **Dim** text = auto-detected from history at query time
- **Red `[orphan]`** = parent terminal/IDE has died, process is orphaned
- `[wt]` = using `--worktree` flag
- `[yolo]` = using `--dangerously-skip-permissions`

## Auto-labeling

When a session triggers its first tool use, `cs-hook` (PreToolUse hook) automatically:
1. Matches the session by project directory and start time
2. If the first message is short (<=30 chars), uses it directly
3. If long, calls **Haiku** to summarize into a concise label (same language as input)
4. Writes the label to `~/.claude/session-labels.json`

This label then appears in both `cs` output and the Claude Code statusline.

## Statusline Integration

The statusline shows in every Claude Code session:

```
🏷️ Issue1311规划处理  📁 workspace/my-project  🌿 main  🤖 Opus 4.6  📟 v2.1.70
🧠 Ctx: 56% [=====-----]  ⚡ Session: 40% used, resets in 2h 31m [====------]  📊 Weekly: 6% used, resets in 6d 15h [----------]
```

## Usage Limit Monitoring

The statusline shows real-time usage limits from the Anthropic API:

- **Session (5h)** — your current 5-hour usage window utilization
- **Weekly (7d)** — your 7-day rolling usage utilization
- Color-coded: mint (normal) → peach (>=70%) → red (>=90% or limit hit)

This works by a PostToolUse hook (`ratelimit-probe.sh`) that makes a minimal background API call (1 Haiku token) every 2 minutes to fetch `anthropic-ratelimit-unified-*` response headers.

### Error diagnostics

If the probe fails (missing credentials, expired token, network issues), the statusline shows actionable hints instead of a blank space:

```
⚠️ Usage: ~/.claude/.credentials.json not found. Log in: claude auth login
⚠️ Usage: OAuth token expired. Try: claude auth logout && claude auth login
⚠️ Usage: API request failed. Check network or proxy settings
```

## Requirements

- Python 3.6+
- Linux (uses `/proc` filesystem)
- `jq` (optional but recommended, for statusline JSON parsing)
- `curl` (for usage limit probing)
