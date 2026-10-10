#!/usr/bin/env python3
"""cs - Claude Sessions Manager
Track and label your parallel Claude Code sessions.
"""
import argparse
import json
import os
import re
import subprocess
import sys
import time
from fractions import Fraction
from pathlib import Path

def _claude_dir():
    """Resolve Claude Code's user config dir, honoring CLAUDE_CONFIG_DIR."""
    env = os.environ.get("CLAUDE_CONFIG_DIR", "").strip()
    return Path(env).expanduser() if env else Path.home() / ".claude"


CLAUDE_DIR = _claude_dir()
LABELS_FILE = CLAUDE_DIR / "session-labels.json"
HISTORY_FILE = CLAUDE_DIR / "history.jsonl"
INSTALL_META = CLAUDE_DIR / "cs-install.json"
CTX_DIR = CLAUDE_DIR / "cs-ctx"
MY_UID = os.getuid()
MY_USER = os.environ.get("USER", "")

# ANSI colors
GREEN = "\033[0;32m"
YELLOW = "\033[1;33m"
CYAN = "\033[0;36m"
RED = "\033[0;31m"
DIM = "\033[2m"
BOLD = "\033[1m"
NC = "\033[0m"


def _file_hash(path):
    """Quick content hash for a file."""
    import hashlib
    try:
        return hashlib.md5(Path(path).read_bytes()).hexdigest()
    except OSError:
        return None


def check_upgrade():
    """Warn if installed files are outdated vs source repo."""
    if not INSTALL_META.exists():
        return
    try:
        meta = json.loads(INSTALL_META.read_text())
    except Exception:
        return
    source_dir = meta.get("source_dir", "")
    if not source_dir or not Path(source_dir).is_dir():
        return
    stale = []
    for name, dst in meta.get("files", {}).items():
        src = Path(source_dir) / name
        if src.exists() and Path(dst).exists():
            if _file_hash(src) != _file_hash(dst):
                stale.append(name)
    if stale:
        print(f"{YELLOW}cs: {', '.join(stale)} outdated. Run: cs install{NC}")


def load_labels():
    if LABELS_FILE.exists():
        try:
            return json.loads(LABELS_FILE.read_text())
        except Exception:
            pass
    return {}


def save_labels(labels):
    LABELS_FILE.parent.mkdir(parents=True, exist_ok=True)
    LABELS_FILE.write_text(json.dumps(labels, indent=2, ensure_ascii=False))


def get_claude_processes(show_all=False):
    """Get running claude processes via ps."""
    result = subprocess.run(
        ["ps", "-eo", "uid,pid,tty,etimes,user,args"],
        capture_output=True, text=True
    )
    procs = []
    for line in result.stdout.strip().split("\n")[1:]:  # skip header
        parts = line.split()
        if len(parts) < 6:
            continue
        uid, pid, tty, etimes, user = parts[0], parts[1], parts[2], parts[3], parts[4]
        args = parts[5:]
        cmd_base = os.path.basename(args[0]) if args else ""
        if cmd_base != "claude":
            continue
        if not show_all and int(uid) != MY_UID:
            continue
        try:
            cwd = os.readlink(f"/proc/{pid}/cwd")
        except OSError:
            cwd = "?"
        # Detect orphaned sessions: walk parent chain, if any ancestor has ppid=1
        # it means the terminal/shell that spawned claude has died
        orphan = False
        try:
            ppid = int(open(f"/proc/{pid}/stat").read().split()[3])
            if ppid == 1:
                orphan = True
            else:
                # Check parent's ppid — catches cases like bash(ppid=1) -> claude
                gppid = int(open(f"/proc/{ppid}/stat").read().split()[3])
                if gppid == 1:
                    orphan = True
        except (OSError, ValueError, IndexError):
            pass
        flags = " ".join(args[1:]) if len(args) > 1 else ""
        procs.append({
            "uid": int(uid),
            "pid": int(pid),
            "tty": tty,
            "elapsed_s": int(etimes),
            "user": user,
            "cwd": cwd,
            "flags": flags,
            "orphan": orphan,
        })
    return procs


def format_duration(seconds):
    if seconds < 60:
        return f"{seconds}s"
    elif seconds < 3600:
        return f"{seconds // 60}m"
    elif seconds < 86400:
        return f"{seconds // 3600}h{seconds % 3600 // 60}m"
    else:
        return f"{seconds // 86400}d{seconds % 86400 // 3600}h"


def short_project(cwd):
    """Extract a short project name from the cwd."""
    wt_match = re.search(r"worktrees/([^/]+)", cwd)
    if wt_match:
        return f"{YELLOW}wt:{wt_match.group(1)}{NC}"
    parts = cwd.rstrip("/").split("/")
    if len(parts) >= 2:
        return "/".join(parts[-2:])
    return cwd


def get_session_ids_for_project(cwd, pid_start_epoch):
    """Find session IDs matching a project and start time."""
    if not HISTORY_FILE.exists():
        return []
    sessions = {}
    try:
        with open(HISTORY_FILE, "r") as f:
            for line in f:
                try:
                    msg = json.loads(line.strip())
                except Exception:
                    continue
                if msg.get("project", "") != cwd:
                    continue
                sid = msg.get("sessionId", "")
                ts = msg.get("timestamp", 0) / 1000
                if sid not in sessions:
                    sessions[sid] = {"first_ts": ts, "last_ts": ts, "display": msg.get("display", "")}
                else:
                    sessions[sid]["last_ts"] = ts
    except Exception:
        return []

    # Find sessions whose first message is within 300s of process start
    candidates = []
    for sid, info in sessions.items():
        if info["first_ts"] >= pid_start_epoch - 60:
            # Sort by most recently created (descending)
            candidates.append((-info["first_ts"], sid, info["display"]))
    candidates.sort()
    return candidates


def get_label_for_process(labels, cwd, elapsed_s):
    """Get label for a process: check session_id labels, then try history auto-detect."""
    now = time.time()
    pid_start = now - elapsed_s

    # Try to match by session_id from history
    candidates = get_session_ids_for_project(cwd, pid_start)
    for _, sid, display in candidates:
        if sid in labels:
            return labels[sid], True  # (label, is_manual)
        # Auto-detected from first message
        text = display.split("\n")[0]
        if len(text) > 72:
            text = text[:69] + "..."
        if text:
            return text, False

    return "", False


def cmd_list(show_all=False):
    procs = get_claude_processes(show_all)
    if not procs:
        print("No active Claude sessions found.")
        return

    labels = load_labels()
    now = time.time()

    # Assign sessions to processes: newest process claims first to avoid duplicates
    proc_indices_by_age = sorted(range(len(procs)), key=lambda i: procs[i]["elapsed_s"])
    claimed_sids = set()
    proc_labels = {}  # index -> (label_text, is_manual)

    for i in proc_indices_by_age:
        p = procs[i]
        pid_start = now - p["elapsed_s"]
        candidates = get_session_ids_for_project(p["cwd"], pid_start)
        label_text, is_manual = "", False
        for _, sid, display in candidates:
            if sid in claimed_sids:
                continue
            claimed_sids.add(sid)
            if sid in labels:
                label_text, is_manual = labels[sid], True
            else:
                text = display.split("\n")[0]
                if len(text) > 72:
                    text = text[:69] + "..."
                if text:
                    label_text = text
            break
        proc_labels[i] = (label_text, is_manual)

    # Header
    print(f"{BOLD}{'PID':<8} {'TTY':<8} {'TIME':<7} {'PROJECT':<32} TASK{NC}")
    print(f"{'─'*7}  {'─'*7}  {'─'*6} {'─'*31}  {'─'*20}")

    for i, p in enumerate(procs):
        pid = p["pid"]
        tty = p["tty"]
        duration = format_duration(p["elapsed_s"])
        project = short_project(p["cwd"])

        flags = ""
        if p.get("orphan"):
            flags += f" {RED}[orphan]{NC}"
        if "--worktree" in p["flags"]:
            flags += f" {DIM}[wt]{NC}"
        elif "--dangerously-skip-permissions" in p["flags"]:
            flags += f" {DIM}[yolo]{NC}"

        label, is_manual = proc_labels.get(i, ("", False))

        if is_manual:
            task_display = f"{GREEN}{label}{NC}"
        elif label:
            task_display = f"{DIM}{label}{NC}"
        else:
            task_display = f"{DIM}(no label){NC}"

        user_prefix = ""
        if show_all and p["uid"] != MY_UID:
            user_prefix = f"{CYAN}[{p['user']}] {NC}"

        print(f"{pid:<8} {tty:<8} {duration:<7} {user_prefix}{project:<32}{flags} {task_display}")


def cmd_label(target, label_text):
    """Label by session_id, PID, or pts number."""
    labels = load_labels()

    # If target looks like a UUID, use directly as session_id
    if len(target) > 20 and "-" in target:
        labels[target] = label_text
        save_labels(labels)
        print(f"{GREEN}Labeled session {target[:8]}...: {label_text}{NC}")
        return

    # Otherwise, find the session_id for the given PID or tty
    procs = get_claude_processes(show_all=False)
    try:
        n = int(target)
        if n < 200:
            match_tty = f"pts/{n}"
            proc = next((p for p in procs if p["tty"] == match_tty), None)
        else:
            proc = next((p for p in procs if p["pid"] == n), None)
    except ValueError:
        target_clean = target.replace("pts/", "")
        proc = next((p for p in procs if p["tty"] == f"pts/{target_clean}"), None)

    if not proc:
        print(f"{YELLOW}No active Claude session found for {target}{NC}")
        return

    # Find session_id from history
    now = time.time()
    pid_start = now - proc["elapsed_s"]
    candidates = get_session_ids_for_project(proc["cwd"], pid_start)

    if candidates:
        _, sid, _ = candidates[0]
        labels[sid] = label_text
        save_labels(labels)
        print(f"{GREEN}Labeled {proc['tty']} (session {sid[:8]}...): {label_text}{NC}")
    else:
        # Fallback: use tty as key
        labels[proc["tty"]] = label_text
        save_labels(labels)
        print(f"{GREEN}Labeled {proc['tty']}: {label_text}{NC}")


def cmd_unlabel(target):
    labels = load_labels()

    # Direct session_id
    if len(target) > 20 and "-" in target:
        if target in labels:
            del labels[target]
            save_labels(labels)
            print(f"{YELLOW}Removed label for session {target[:8]}...{NC}")
            return

    # Find session_id for PID/tty
    procs = get_claude_processes(show_all=False)
    try:
        n = int(target)
        if n < 200:
            proc = next((p for p in procs if p["tty"] == f"pts/{n}"), None)
        else:
            proc = next((p for p in procs if p["pid"] == n), None)
    except ValueError:
        target_clean = target.replace("pts/", "")
        proc = next((p for p in procs if p["tty"] == f"pts/{target_clean}"), None)

    if proc:
        now = time.time()
        pid_start = now - proc["elapsed_s"]
        candidates = get_session_ids_for_project(proc["cwd"], pid_start)
        for _, sid, _ in candidates:
            if sid in labels:
                del labels[sid]
                save_labels(labels)
                print(f"{YELLOW}Removed label for {proc['tty']} (session {sid[:8]}...){NC}")
                return

    # Fallback: try tty key
    try:
        n = int(target)
        key = f"pts/{n}" if n < 200 else str(n)
    except ValueError:
        key = f"pts/{target.replace('pts/', '')}"

    if key in labels:
        del labels[key]
        save_labels(labels)
        print(f"{YELLOW}Removed label for {key}{NC}")
    else:
        print(f"No label found for {target}")


def cmd_clean():
    """Remove labels for sessions that are no longer running."""
    labels = load_labels()
    if not labels:
        print("No labels to clean.")
        return

    # Get all active session_ids
    procs = get_claude_processes(show_all=False)
    active_sids = set()
    now = time.time()
    for p in procs:
        pid_start = now - p["elapsed_s"]
        candidates = get_session_ids_for_project(p["cwd"], pid_start)
        for _, sid, _ in candidates:
            active_sids.add(sid)
        active_sids.add(p["tty"])

    removed = []
    for key in list(labels.keys()):
        if key not in active_sids:
            removed.append(f"  {key[:16]}...: {labels[key]}" if len(key) > 16 else f"  {key}: {labels[key]}")
            del labels[key]

    if removed:
        save_labels(labels)
        print(f"{YELLOW}Cleaned {len(removed)} stale labels:{NC}")
        for r in removed:
            print(r)
    else:
        print("No stale labels to clean.")


def _prompt_choice(prompt_text, options, default=0):
    """Interactive arrow-key menu. Falls back to number input if not a tty."""
    if not sys.stdin.isatty():
        # Non-interactive: use default
        return options[default][1]

    import tty
    import termios

    fd = sys.stdin.fileno()
    old = termios.tcgetattr(fd)
    cur = default

    def render():
        for i, (label, _) in enumerate(options):
            marker = f"{GREEN}>{NC}" if i == cur else " "
            print(f"\r  {marker} {label}    ")
        # Move cursor back up
        sys.stdout.write(f"\033[{len(options)}A")
        sys.stdout.flush()

    print(prompt_text)
    print(f"{DIM}  ↑↓ to select, Enter to confirm{NC}")
    render()

    try:
        tty.setraw(fd)
        while True:
            ch = sys.stdin.read(1)
            if ch == "\r" or ch == "\n":
                break
            if ch == "\x03":  # Ctrl-C
                raise KeyboardInterrupt
            if ch == "\x1b":  # escape sequence
                seq = sys.stdin.read(2)
                if seq in ("[A", "OA"):  # up
                    cur = (cur - 1) % len(options)
                elif seq in ("[B", "OB"):  # down
                    cur = (cur + 1) % len(options)
            termios.tcsetattr(fd, termios.TCSADRAIN, old)
            render()
            tty.setraw(fd)
    except (KeyboardInterrupt, EOFError):
        termios.tcsetattr(fd, termios.TCSADRAIN, old)
        sys.stdout.write(f"\033[{len(options)}B\n")
        sys.exit(0)
    finally:
        termios.tcsetattr(fd, termios.TCSADRAIN, old)

    # Move past the menu and show selection
    sys.stdout.write(f"\033[{len(options)}B\n")
    print(f"  {GREEN}Selected: {options[cur][0]}{NC}")
    return options[cur][1]


def _symlink_or_copy(src, dst):
    """Create symlink src->dst. Falls back to copy if cross-device."""
    import shutil
    if dst.is_symlink() or dst.exists():
        if dst.is_symlink() and dst.resolve() == src.resolve():
            print(f"{DIM}  {dst.name}: ✓ linked{NC}")
            return
        dst.unlink()
    try:
        dst.symlink_to(src)
        print(f"{GREEN}  {dst.name}: → {src}{NC}")
    except OSError:
        shutil.copy2(src, dst)
        dst.chmod(dst.stat().st_mode | 0o755)
        print(f"{GREEN}  {dst.name}: copied{NC}")


def cmd_install(target_dir=None):
    """One-click install: symlink files, hooks, statusline, PATH — all set."""
    script_dir = Path(__file__).resolve().parent
    cs_src = script_dir / "cs"
    cs_hook_src = script_dir / "cs-hook"
    statusline_src = script_dir / "statusline.sh"

    # When running from installed copy (~/bin), try to find source repo from metadata
    if not statusline_src.exists() and INSTALL_META.exists():
        try:
            meta = json.loads(INSTALL_META.read_text())
            source_dir = Path(meta.get("source_dir", ""))
            if source_dir.is_dir():
                script_dir = source_dir
                cs_src = script_dir / "cs"
                cs_hook_src = script_dir / "cs-hook"
                statusline_src = script_dir / "statusline.sh"
        except Exception:
            pass
    if not statusline_src.exists():
        print(f"{YELLOW}statusline.sh not found. Run install from the git repo directory.{NC}")
        sys.exit(1)

    # --- Bin dir: default ~/bin, or user-specified ---
    if target_dir:
        bin_dir = Path(target_dir).expanduser().resolve()
    else:
        bin_dir = Path.home() / "bin"

    bin_dir.mkdir(parents=True, exist_ok=True)
    print(f"{BOLD}Installing to {bin_dir}:{NC}")
    installed_files = {}
    for name, src in [("cs", cs_src), ("cs-hook", cs_hook_src)]:
        dst = bin_dir / name
        _symlink_or_copy(src, dst)
        installed_files[name] = str(dst)

    # --- Choose .claude dir: ask only when project-level .claude exists ---
    # User-level dir honors CLAUDE_CONFIG_DIR; project-level dirs are always named `.claude`.
    user_claude = CLAUDE_DIR
    claude_options = [
        (str(user_claude) + "  (all projects)", user_claude),
    ]
    cwd = Path.cwd().resolve()
    real_home = str(Path.home().resolve())
    sym_home = str(Path.home())
    for d in [cwd] + list(cwd.parents):
        proj_claude = d / ".claude"
        if proj_claude.is_dir() and proj_claude != user_claude:
            label = str(proj_claude)
            if label.startswith(real_home):
                label = "~" + label[len(real_home):]
            elif label.startswith(sym_home):
                label = "~" + label[len(sym_home):]
            claude_options.append((label + "  (this project)", proj_claude))
            break

    if len(claude_options) > 1:
        claude_dir = _prompt_choice(
            f"\n{BOLD}Inject settings into which .claude?{NC}", claude_options, default=0
        )
    else:
        claude_dir = user_claude

    claude_dir.mkdir(parents=True, exist_ok=True)
    print(f"{BOLD}Installing to {claude_dir}:{NC}")

    # Symlink statusline.sh into chosen .claude dir
    sl_dst = claude_dir / "statusline.sh"
    _symlink_or_copy(statusline_src, sl_dst)
    installed_files["statusline.sh"] = str(sl_dst)

    # The statusline fires usage-probe.sh from its own directory, so the probe has to
    # land next to it — including when the statusline gets copied instead of linked.
    probe_src = script_dir / "usage-probe.sh"
    if probe_src.exists():
        probe_dst = claude_dir / "usage-probe.sh"
        _symlink_or_copy(probe_src, probe_dst)
        installed_files["usage-probe.sh"] = str(probe_dst)

    # Likewise the statusline hands each render's context size to ctx-state.sh.
    ctx_src = script_dir / "ctx-state.sh"
    if ctx_src.exists():
        ctx_dst = claude_dir / "ctx-state.sh"
        _symlink_or_copy(ctx_src, ctx_dst)
        installed_files["ctx-state.sh"] = str(ctx_dst)

    # Clean up the retired ratelimit-probe.sh (superseded by native rate_limits in
    # the statusline stdin). Remove any stale symlink/copy and its cache from prior installs.
    for stale in (claude_dir / "ratelimit-probe.sh", claude_dir / "ratelimit-cache.json"):
        try:
            if stale.is_symlink() or stale.exists():
                stale.unlink()
                print(f"{DIM}  removed retired {stale.name}{NC}")
        except OSError:
            pass

    # Save install metadata for upgrade checks
    INSTALL_META.write_text(json.dumps({
        "source_dir": str(script_dir),
        "files": installed_files,
    }, indent=2) + "\n")

    # Update settings.json in chosen .claude dir
    settings_file = claude_dir / "settings.json"
    settings = {}
    if settings_file.exists():
        try:
            settings = json.loads(settings_file.read_text())
        except Exception:
            pass

    # StatusLine config. refreshInterval re-runs the status line on a timer as well as on
    # events; without it a window Claude Code sees no activity in never re-runs the script,
    # so its usage numbers freeze while you work in another one. Keep an existing choice.
    prev_sl = settings.get("statusLine") or {}
    settings["statusLine"] = {
        "type": "command",
        "command": str(sl_dst),
        "padding": 0,
        "refreshInterval": prev_sl.get("refreshInterval", 60),
    }

    # Add cs-hook to PreToolUse hooks (if not already present)
    cs_hook_cmd = str(bin_dir / "cs-hook")
    hooks = settings.setdefault("hooks", {})
    pre_tool = hooks.setdefault("PreToolUse", [])

    # Find or create the catch-all matcher entry
    catch_all = None
    for entry in pre_tool:
        if entry.get("matcher", "") == "":
            catch_all = entry
            break
    if catch_all is None:
        catch_all = {"matcher": "", "hooks": []}
        pre_tool.append(catch_all)

    hook_list = catch_all.setdefault("hooks", [])
    # Check if cs-hook is already registered
    already = any(h.get("command", "").endswith("cs-hook") for h in hook_list)
    if not already:
        hook_list.append({"type": "command", "command": cs_hook_cmd})
        print(f"{GREEN}Added cs-hook to PreToolUse hooks{NC}")
    else:
        print(f"{DIM}cs-hook already in PreToolUse hooks{NC}")

    # Remove any retired ratelimit-probe.sh PostToolUse hook left by prior installs,
    # then drop emptied entries so we don't leave a dangling hook command behind.
    post_tool = hooks.get("PostToolUse", [])
    for entry in post_tool:
        entry["hooks"] = [
            h for h in entry.get("hooks", [])
            if not h.get("command", "").endswith("ratelimit-probe.sh")
        ]
    post_tool = [e for e in post_tool if e.get("hooks")]
    if post_tool:
        hooks["PostToolUse"] = post_tool
    else:
        hooks.pop("PostToolUse", None)

    settings_file.write_text(json.dumps(settings, indent=2, ensure_ascii=False) + "\n")
    print(f"{GREEN}Updated {settings_file}{NC}")

    # 4. Ensure ~/bin is in PATH
    path_dirs = os.environ.get("PATH", "").split(":")
    need_source = False
    if str(bin_dir) not in path_dirs:
        # Detect shell rc file
        shell = os.environ.get("SHELL", "/bin/bash")
        if "zsh" in shell:
            rc_file = Path.home() / ".zshrc"
        else:
            rc_file = Path.home() / ".bashrc"

        path_line = 'export PATH="$HOME/bin:$PATH"'
        rc_has_it = False
        if rc_file.exists():
            rc_has_it = path_line in rc_file.read_text()

        if not rc_has_it:
            with open(rc_file, "a") as f:
                f.write(f"\n# Added by cs (claude-sessions)\n{path_line}\n")
            print(f"{GREEN}Added ~/bin to PATH in {rc_file}{NC}")

        need_source = True

    if need_source:
        print(f"\n{GREEN}Install complete!{NC}")
        print(f"{YELLOW}>>> Run this to activate:  source {rc_file}{NC}")
    else:
        print(f"\n{GREEN}Install complete! All set.{NC}")


CTX_WAIT_HELP = """\
Wait until a context holder's context size reaches a threshold, then print one line and exit.

A holder is the session itself (main) or one of its subagents. statusline.sh and
subagent-statusline.sh record each holder's latest context size in
$CLAUDE_CONFIG_DIR/cs-ctx/<session id>/<holder>.json (default ~/.claude); this command only
reads those files. Nothing is printed while waiting.

Usage:
  cs ctx wait --at <percent> [--target <main|subagent id|subagent name>]
              [--session <session id>] [--interval <seconds>] [--no-data-after <seconds>]

Options:
  --at <percent>           Threshold, a number above 0. The holder has reached it when
                           tokenCount * 100 >= percent * contextWindowSize (no rounding).
  --target <t>             main (default) is the session itself. Anything else is matched
                           first against subagent ids, then against subagent names.
  --session <session id>   Session to watch. Default: $CLAUDE_CODE_SESSION_ID.
  --interval <seconds>     How often the local file is read. Default: {interval}.
  --no-data-after <seconds>
                           A target whose file is missing, or was last updated longer ago
                           than this, counts as having no data. Default: {no_data}. A subagent
                           that has just started has no file yet; it is waited for this long.

Output (exactly one line, after which the command exits):
  REACHED <pct>% tokenCount=<n> contextWindowSize=<w> target=<name>
      The threshold is reached. When it is reached and the holder has completed at the
      same read, REACHED wins. Exit code 0.
  COMPLETED target=<name> <pct>%
      The subagent has completed below the threshold. Exit code 0.
  NO-DATA target=<name> reason=<why>
      The file is missing or stale. Exit code 2.
  AMBIGUOUS target=<name> ids=<id>,<id>,...
      Several subagents carry that name; pass one of the ids instead. Exit code 2.

Exit codes:
  0  REACHED or COMPLETED
  2  NO-DATA or AMBIGUOUS
  1  Usage error (missing --at, no session id from --session or the environment, ...)
"""

CTX_WAIT_INTERVAL = 5
CTX_WAIT_NO_DATA_AFTER = 300


def _ctx_usage_error(msg):
    print(f"cs ctx wait: {msg}", file=sys.stderr)
    sys.exit(1)


class _CtxArgParser(argparse.ArgumentParser):
    def error(self, message):
        # argparse exits with 2 by default, which this command uses for NO-DATA.
        _ctx_usage_error(message)


def _ctx_read_state(path):
    """Return the state dict of one holder file, or None when it is absent or unreadable."""
    try:
        d = json.loads(path.read_text())
    except (OSError, ValueError):
        return None
    if not isinstance(d, dict):
        return None
    n, w = d.get("tokenCount"), d.get("contextWindowSize")
    if not all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in (n, w)) or w <= 0:
        return None
    return d


def _ctx_resolve_target(session_dir, target):
    """Return (path, ambiguous_ids). path is None when no file carries the target yet."""
    if target == "main":
        return session_dir / "main.json", []
    by_id = session_dir / f"{target}.json"
    if by_id.is_file():
        return by_id, []
    matches = []
    if session_dir.is_dir():
        for f in sorted(session_dir.glob("*.json")):
            d = _ctx_read_state(f)
            if d is not None and d.get("name") == target:
                matches.append(f)
    if len(matches) > 1:
        return None, [f.stem for f in matches]
    return (matches[0] if matches else None), []


def cmd_ctx_wait(argv):
    parser = _CtxArgParser(prog="cs ctx wait", add_help=False)
    parser.add_argument("-h", "--help", action="store_true")
    parser.add_argument("--at")
    parser.add_argument("--target", default="main")
    parser.add_argument("--session")
    parser.add_argument("--interval", type=float, default=CTX_WAIT_INTERVAL)
    parser.add_argument("--no-data-after", type=float, default=CTX_WAIT_NO_DATA_AFTER)
    args = parser.parse_args(argv)
    if args.help:
        print(CTX_WAIT_HELP.format(interval=CTX_WAIT_INTERVAL, no_data=CTX_WAIT_NO_DATA_AFTER), end="")
        return
    if args.at is None:
        _ctx_usage_error("--at <percent> is required (see: cs ctx wait --help)")
    try:
        threshold = Fraction(args.at)
    except (ValueError, ZeroDivisionError):
        _ctx_usage_error(f"--at must be a number, got {args.at!r}")
    if threshold <= 0:
        _ctx_usage_error("--at must be above 0")
    if args.interval <= 0 or args.no_data_after <= 0:
        _ctx_usage_error("--interval and --no-data-after must be above 0")
    session = args.session or os.environ.get("CLAUDE_CODE_SESSION_ID", "").strip()
    if not session:
        _ctx_usage_error("no session id: pass --session or set CLAUDE_CODE_SESSION_ID")
    if "/" in session or session in (".", ".."):
        _ctx_usage_error(f"invalid session id {session!r}")

    session_dir = CTX_DIR / session
    target = args.target
    started = time.time()
    while True:
        path, ambiguous = _ctx_resolve_target(session_dir, target)
        if ambiguous:
            print(f"AMBIGUOUS target={target} ids={','.join(ambiguous)}")
            sys.exit(2)
        state = None
        if path is not None:
            try:
                age = time.time() - path.stat().st_mtime
            except OSError:
                age = None
            state = _ctx_read_state(path)
        else:
            age = None
        if state is not None:
            n, w = state["tokenCount"], state["contextWindowSize"]
            pct = float(Fraction(n) * 100 / Fraction(w))
            name = state.get("name") or (path.stem if target != "main" else "main")
            if Fraction(n) * 100 >= threshold * Fraction(w):
                print(f"REACHED {pct:.1f}% tokenCount={n} contextWindowSize={w} target={name}")
                return
            if state.get("status") == "completed":
                print(f"COMPLETED target={name} {pct:.1f}%")
                return
        if age is not None and age > args.no_data_after:
            print(f"NO-DATA target={target} reason=file not updated for {int(age)}s "
                  f"(limit {args.no_data_after:g}s)")
            sys.exit(2)
        if age is None and time.time() - started > args.no_data_after:
            print(f"NO-DATA target={target} reason=no file for this target after "
                  f"{args.no_data_after:g}s in {session_dir}")
            sys.exit(2)
        time.sleep(args.interval)


def cmd_ctx(argv):
    if argv and argv[0] == "wait":
        cmd_ctx_wait(argv[1:])
    elif not argv or argv[0] in ("-h", "--help"):
        print("Usage: cs ctx wait --at <percent> [options]    (see: cs ctx wait --help)")
    else:
        _ctx_usage_error(f"unknown subcommand {argv[0]!r}; try: cs ctx wait --help")


def cmd_help():
    print("""cs - Claude Sessions Manager

Usage:
  cs                    List your active Claude sessions
  cs -a                 List all users' Claude sessions
  cs label <id> <text>  Label a session (id = PID or pts number)
  cs unlabel <id>       Remove a session label
  cs clean              Remove labels for dead sessions
  cs ctx wait --at <percent>
                        Block until a context size reaches the threshold (see --help)
  cs install [dir]      One-click install (default: ~/bin)
  cs help               Show this help

Examples:
  cs                          # show my sessions
  cs label 52 "refactor MoE"  # label session on pts/52
  cs label 528378 "fix bug"   # label session by PID
  cs unlabel 52               # remove label
  cs -a                       # show everyone's sessions
  cs install                  # install to ~/bin
  cs install /usr/local/bin   # install to custom dir

Labels are stored by session_id and shown in the Claude Code statusline.
When no manual label is set, cs auto-detects the task from history.""")


def main():
    args = sys.argv[1:]
    if not args or args[0] not in ("install", "help", "-h", "--help", "h", "ctx"):
        check_upgrade()
    if not args:
        cmd_list()
    elif args[0] in ("-a", "--all"):
        cmd_list(show_all=True)
    elif args[0] in ("label", "l"):
        if len(args) < 3:
            print("Usage: cs label <pid|pts> <description>")
            sys.exit(1)
        cmd_label(args[1], " ".join(args[2:]))
    elif args[0] in ("unlabel", "ul"):
        if len(args) < 2:
            print("Usage: cs unlabel <pid|pts>")
            sys.exit(1)
        cmd_unlabel(args[1])
    elif args[0] == "clean":
        cmd_clean()
    elif args[0] == "ctx":
        cmd_ctx(args[1:])
    elif args[0] == "install":
        cmd_install(args[1] if len(args) > 1 else None)
    elif args[0] in ("help", "-h", "--help", "h"):
        cmd_help()
    else:
        cmd_list()


if __name__ == "__main__":
    main()
