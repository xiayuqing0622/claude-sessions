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
123456   pts/1    2h40m   workspace/my-project             Issue1311规划处理
234567   pts/2    23h5m   workspace/backend          [yolo] 修复数据库迁移
345678   pts/70   1d6h    workspace/frontend                添加暗色模式
456789   pts/26   1d7h    workspace/backend        [orphan] 检查仓库基础设施配置
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
🏷️ Issue1311规划处理  📁 workspace/my-project  🌿 feat/auth  🤖 Opus 4.6  📟 v2.1.70  🎨 concise
🧠 Ctx: 56% [=====-----]  ⚡ Session: 40% used, resets in 2h 31m [====------]  📊 Weekly: 6% used, resets in 6d 15h [----------]
```

**Line 1** — Session label, working directory, git branch, model, Claude Code version, output style

**Line 2** — Context window remaining, session (5h) usage limit, weekly (7d) usage limit

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
| 处理issue1311,好好plan,有问题一定要问我 | Issue1311规划处理 |
| 这台机器上的别的用户安装这个项目，为什么usage limit没显示 | 其他用户usage limit显示问题 |
| fix bug in auth | fix bug in auth |

### 5. Install & Upgrade

```bash
git clone https://github.com/xiayuqing0622/claude-sessions.git
cd claude-sessions
./cs install
```

The installer:
- **Symlinks** `cs`, `cs-hook` to `~/bin` and `statusline.sh`, `ratelimit-probe.sh` to `~/.claude/`
- Configures `~/.claude/settings.json` (statusLine + hooks)
- Adds `~/bin` to `PATH` if needed
- Falls back to file copy if symlinks aren't possible (cross-device, etc.)

**Upgrading** — with symlinks, just pull:

```bash
cd claude-sessions && git pull
```

If installed via copy, `cs` warns on startup:

```
cs: statusline.sh, cs-hook outdated. Run: cs install
```

Custom bin directory: `./cs install /usr/local/bin`

## Requirements

- Python 3.6+
- Linux (uses `/proc` filesystem)
- `jq` (optional but recommended, for statusline JSON parsing)
- `curl` (for usage limit probing)
