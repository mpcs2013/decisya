#!/usr/bin/env python3
"""PreToolUse hook (Bash): block commands that would read .env files or user-secrets.

Permission deny rules on Read do not cover shell commands such as `cat .env`
(Claude Code permissions docs), so this hook closes that gap for every session
and agent. `.env.example` stays allowed. Fails open on internal errors.
"""
from __future__ import annotations

import json
import re
import sys

SECRET = re.compile(
    r"""(?:^|[\s/\'"=<>])\.env(?:\.(?!example\b)[\w.-]+)?(?=$|[\s'";|&)>])"""
    r"""|UserSecrets|secrets\.json|user-secrets\s+list""",
    re.IGNORECASE,
)

# Heredoc bodies are data fed to a command's stdin (e.g. a commit message), not file reads.
HEREDOC = re.compile(r"<<-?\s*(['\"]?)(\w+)\1[^\n]*\n.*?\n\s*\2\s*(?=\n|$)", re.DOTALL)


def main() -> int:
    try:
        payload = json.load(sys.stdin)
        command = (payload.get("tool_input") or {}).get("command", "")
    except Exception as exc:
        print(f"secret_guard hook error (allowing): {exc}", file=sys.stderr)
        return 0
    if SECRET.search(HEREDOC.sub("<<heredoc", command)):
        reason = "Blocked: this command touches a .env file or user-secrets (CLAUDE.md security principles). Ask Marco for the value's name, never its content."
        print(json.dumps({"hookSpecificOutput": {"hookEventName": "PreToolUse", "permissionDecision": "deny", "permissionDecisionReason": reason}}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
