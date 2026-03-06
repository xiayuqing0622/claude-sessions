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


def cmd_help():
    print("""cs - Claude Sessions Manager

Usage:
  cs                    List your active Claude sessions
  cs -a                 List all users' Claude sessions
  cs label <id> <text>  Label a session (id = PID or pts number)
  cs unlabel <id>       Remove a session label
  cs clean              Remove labels for dead sessions
  cs help               Show this help

Examples:
  cs                          # show my sessions
  cs label 52 "refactor MoE"  # label session on pts/52
  cs label 528378 "fix bug"   # label session by PID
  cs unlabel 52               # remove label
  cs -a                       # show everyone's sessions

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
    elif args[0] in ("help", "-h", "--help", "h"):
        cmd_help()
    else:
        cmd_list()


if __name__ == "__main__":
    main()
