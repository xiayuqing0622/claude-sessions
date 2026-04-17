---
description: Finish claude-sessions install — configure statusline + add cs to PATH (run once)
allowed-tools: Bash
---

You are completing the post-install setup for the `claude-sessions` plugin. The plugin's hooks are already active; you need to do the two things plugins can't do automatically: configure the user's `statusLine` and symlink `cs` into `~/bin`.

Steps:

1. Locate the plugin root by searching for `plugin-setup.sh` under the Claude plugins cache:

   ```bash
   PLUGIN_ROOT=$(find ~/.claude/plugins -maxdepth 8 -type f -name plugin-setup.sh -path '*claude-sessions*' 2>/dev/null | head -1 | xargs -r dirname)
   ```

2. If `PLUGIN_ROOT` is empty, tell the user the plugin isn't installed and to run `/plugin install claude-sessions@claude-sessions` first, then exit.

3. Otherwise run `bash "$PLUGIN_ROOT/plugin-setup.sh"` and show the output.

4. Confirm to the user: hooks were registered automatically by the plugin; the statusline + `cs` PATH are now set.
