#!/usr/bin/env python3
"""cs - Claude Sessions Manager
Track and label your parallel Claude Code sessions.
"""
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

LABELS_FILE = Path.home() / ".claude" / "session-labels.json"
HISTORY_FILE = Path.home() / ".claude" / "history.jsonl"
MY_UID = os.getuid()
MY_USER = os.environ.get("USER", "")

# ANSI colors
GREEN = "\033[0;32m"
YELLOW = "\033[1;33m"
CYAN = "\033[0;36m"
DIM = "\033[2m"
BOLD = "\033[1m"
NC = "\033[0m"


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
        flags = " ".join(args[1:]) if len(args) > 1 else ""
        procs.append({
            "uid": int(uid),
            "pid": int(pid),
            "tty": tty,
            "elapsed_s": int(etimes),
            "user": user,
            "cwd": cwd,
            "flags": flags,
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
                    sessions[sid] = {"ts": ts, "display": msg.get("display", "")}
    except Exception:
        return []

    # Find session closest to process start time
    candidates = []
    for sid, info in sessions.items():
        diff = abs(info["ts"] - pid_start_epoch)
        if diff < 300:
            candidates.append((diff, sid, info["display"]))
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

    # Header
    print(f"{BOLD}{'PID':<8} {'TTY':<8} {'TIME':<7} {'PROJECT':<32} TASK{NC}")
    print(f"{'─'*7}  {'─'*7}  {'─'*6} {'─'*31}  {'─'*20}")

    for p in procs:
        pid = p["pid"]
        tty = p["tty"]
        duration = format_duration(p["elapsed_s"])
        project = short_project(p["cwd"])

        flags = ""
        if "--worktree" in p["flags"]:
            flags = f" {DIM}[wt]{NC}"
        elif "--dangerously-skip-permissions" in p["flags"]:
            flags = f" {DIM}[yolo]{NC}"

        label, is_manual = get_label_for_process(labels, p["cwd"], p["elapsed_s"])

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


def cmd_install(target_dir=None):
    """One-click install: copy files, hooks, statusline, PATH — all set."""
    import shutil

    script_dir = Path(__file__).resolve().parent
    cs_src = script_dir / "cs"
    cs_hook_src = script_dir / "cs-hook"
    statusline_src = script_dir / "statusline.sh"
    # When running from installed copy (~/bin), statusline.sh is in ~/.claude
    if not statusline_src.exists():
        statusline_src = Path.home() / ".claude" / "statusline.sh"
    if not statusline_src.exists():
        print(f"{YELLOW}statusline.sh not found. Run install from the git repo directory.{NC}")
        sys.exit(1)

    # --- Bin dir: default ~/bin, or user-specified ---
    if target_dir:
        bin_dir = Path(target_dir).expanduser().resolve()
    else:
        bin_dir = Path.home() / "bin"

    bin_dir.mkdir(parents=True, exist_ok=True)
    for name, src in [("cs", cs_src), ("cs-hook", cs_hook_src)]:
        dst = bin_dir / name
        if src.resolve() == dst.resolve():
            print(f"{DIM}{dst} already up to date{NC}")
            continue
        if dst.is_symlink():
            dst.unlink()
        shutil.copy2(src, dst)
        dst.chmod(dst.stat().st_mode | 0o755)
        print(f"{GREEN}Installed {dst}{NC}")

    # --- Choose .claude dir: ask only when project-level .claude exists ---
    user_claude = Path.home() / ".claude"
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

    # Copy statusline.sh into chosen .claude dir
    sl_dst = claude_dir / "statusline.sh"
    if statusline_src.resolve() == sl_dst.resolve():
        print(f"{DIM}{sl_dst} already up to date{NC}")
    else:
        if sl_dst.is_symlink():
            sl_dst.unlink()
        shutil.copy2(statusline_src, sl_dst)
        sl_dst.chmod(sl_dst.stat().st_mode | 0o755)
        print(f"{GREEN}Installed {sl_dst}{NC}")

    # Update settings.json in chosen .claude dir
    settings_file = claude_dir / "settings.json"
    settings = {}
    if settings_file.exists():
        try:
            settings = json.loads(settings_file.read_text())
        except Exception:
            pass

    # StatusLine config
    settings["statusLine"] = {
        "type": "command",
        "command": str(sl_dst),
        "padding": 0,
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


def cmd_help():
    print("""cs - Claude Sessions Manager

Usage:
  cs                    List your active Claude sessions
  cs -a                 List all users' Claude sessions
  cs label <id> <text>  Label a session (id = PID or pts number)
  cs unlabel <id>       Remove a session label
  cs clean              Remove labels for dead sessions
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
    elif args[0] == "install":
        cmd_install(args[1] if len(args) > 1 else None)
    elif args[0] in ("help", "-h", "--help", "h"):
        cmd_help()
    else:
        cmd_list()


if __name__ == "__main__":
    main()
