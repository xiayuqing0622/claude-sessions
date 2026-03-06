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

# Install cs command
ln -s $(pwd)/cs ~/bin/cs

# Install statusline (shows session label inside Claude Code)
ln -sf $(pwd)/statusline.sh ~/.claude/statusline.sh
```

Make sure `~/bin` is in your `PATH`.

Then add to `~/.claude/settings.json`:
```json
{
  "statusLine": {
    "type": "command",
    "command": "~/.claude/statusline.sh",
    "padding": 0
  }
}
```

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
🏷️ refactor auth module  📁 workspace/my-project  🌿 main  🤖 Opus 4.6  📟 v2.1.69
🧠 Context Remaining: 56% [=====-----]
```

## Requirements

- Python 3.6+
- Linux (uses `/proc` filesystem)
- `jq` (optional, for faster statusline JSON parsing)
