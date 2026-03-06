# cs - Claude Sessions Manager

A CLI tool to track and label your parallel Claude Code sessions.

## The Problem

When running multiple Claude Code sessions simultaneously, it's hard to remember which terminal is doing what.

## The Solution

`cs` shows all your active Claude sessions with:
- PID, terminal (pts), and runtime
- Working directory / project name
- **Auto-detected task** from `~/.claude/history.jsonl` (first user message)
- **Manual labels** that persist across restarts

## Install

```bash
git clone https://github.com/xiayuqing0622/claude-sessions.git
cd claude-sessions
./cs install
```

That's it — one command, zero interaction. The installer will:
- Copy `cs` and `cs-hook` to `~/bin`
- Copy `statusline.sh` and `ratelimit-probe.sh` to `~/.claude/`
- Configure `~/.claude/settings.json` (statusLine + auto-label hook + usage limit probe)
- Add `~/bin` to `PATH` in your shell rc if needed

If a project-level `.claude` directory is detected, you'll be asked which one to inject settings into (default: `~/.claude` for all projects).

To install to a custom bin directory: `./cs install /usr/local/bin`

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
───────  ───────  ────── ───────────────────────────────  ────────────────────
123456   pts/1    2h40m   workspace/my-project             refactor auth module
234567   pts/2    23h5m   workspace/backend                fix database migration
345678   pts/3    1h12m   workspace/frontend                add dark mode support
456789   pts/4    31m     workspace/backend                review PR #42
```

- Green text = manual label
- Dim text = auto-detected from history
- `[wt]` = using `--worktree` flag
- `[yolo]` = using `--dangerously-skip-permissions`

## Statusline Integration

When you label a session with `cs label`, the label automatically shows in that session's Claude Code statusline:

```
🏷️ refactor auth module  📁 workspace/my-project  🌿 main  🤖 Opus 4.6  📟 v2.1.70
🧠 Ctx: 56% [=====-----]  ⚡ Session: 40% used, resets in 2h 31m [====------]  📊 Weekly: 6% used, resets in 6d 15h [----------]
```

## Usage Limit Monitoring

The statusline shows real-time usage limits from the Anthropic API:

- **Session (5h)** — your current 5-hour usage window utilization
- **Weekly (7d)** — your 7-day rolling usage utilization
- Color-coded: mint (normal) → peach (≥70%) → red (≥90% or limit hit)

This works by a PostToolUse hook (`ratelimit-probe.sh`) that makes a minimal background API call (1 Haiku token) every 2 minutes to fetch `anthropic-ratelimit-unified-*` response headers. Requires OAuth credentials (`~/.claude/.credentials.json`), which are set up automatically when you log in to Claude Code.

## Requirements

- Python 3.6+
- Linux (uses `/proc` filesystem)
- `jq` (optional but recommended, for statusline JSON parsing)
- `curl` (for usage limit probing)
