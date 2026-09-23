#!/usr/bin/env python3
"""PreToolUse hook (Bash): block commands that would read .env files or user-secrets.

Permission deny rules on Read do not cover shell commands such as `cat .env`
(Claude Code permissions docs), so this hook closes the obvious gaps for every
session and agent. It is a guardrail, not a security boundary: a determined
command can still build the name at run time (threat model T-04); the real
boundary is keeping secrets out of the agent's environment (sandbox follow-up).
`.env.example` stays allowed. Fails open on internal errors.
"""
from __future__ import annotations

import json
import re
import sys

SECRET = re.compile(
    r"""(?:^|[\s/\\'"=<>])\.env(?:\.(?!example\b)[\w.-]+)?(?=$|[\s'";|&)>])"""  # .env, .env.local
    r"""|\.e['"]+n|\.en['"]+v"""                                                # .e''nv, .en""v
    r"""|\.en[?*\[]|\.e\*|\*\.env\b"""                                          # .en?, .e*, *.env
    r"""|UserSecrets|secrets\.json|user-secrets\s+list"""
    r"""|docker\s+compose\s+config""",                                          # prints resolved env values
    re.IGNORECASE,
)

# A heredoc body fed to `git commit` or `gh` (commit message, PR body) is data, not a file read.
# The exemption applies only when that command owns the heredoc: nothing but the command between
# the last shell separator and `<<`, and a single `<<` on the line. Any other heredoc (bash,
# python, ...) is scanned like the rest of the command (review 35, N-01).
EXEMPT_LINE = re.compile(r"(?:^|[;&|]\s*)(?:git\s+(?:-c\s+\S+\s+)*commit|gh)\b[^;&|<\n]*<<-?\s*(['\"]?)(\w+)\1\s*$")


def strip_exempt_heredocs(command: str) -> str:
    lines, out, i = command.split("\n"), [], 0
    while i < len(lines):
        line = lines[i]
        m = EXEMPT_LINE.search(line) if line.count("<<") == 1 else None
        out.append(line)
        i += 1
        if m:
            while i < len(lines) and lines[i].strip() != m.group(2):
                i += 1  # skip the message body
    return "\n".join(out)


def main() -> int:
    try:
        payload = json.load(sys.stdin)
        command = (payload.get("tool_input") or {}).get("command", "")
    except Exception as exc:
        print(f"secret_guard hook error (allowing): {exc}", file=sys.stderr)
        return 0
    if SECRET.search(strip_exempt_heredocs(command)):
        reason = "Blocked: this command touches a .env file or user-secrets (CLAUDE.md security principles). Ask Marco for the value's name, never its content."
        print(json.dumps({"hookSpecificOutput": {"hookEventName": "PreToolUse", "permissionDecision": "deny", "permissionDecisionReason": reason}}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
