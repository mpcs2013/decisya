"""secret_guard must-deny / must-allow fixtures (#39 item 9, G4-39-35 to 47).

Forbidden names are built at run time so this file never contains them literally (G4-39-57).
"""
import random
import string
import sys
import time
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "hooks"))
import secret_guard as sg  # noqa: E402

E = "." + "env"                     # the dotenv file name
US = "User" + "Secrets"             # the user-secrets folder
USC = "user" + "-secrets"           # the dotnet tool verb
SJ = "secrets" + ".json"
NL = "\n"

MUST_DENY = {
    # plain and near-literal names (N-02 and the G3 evidence)
    "plain": f"cat {E}",
    "local": f"cat {E}.local",
    "backtick lead": f"cat `echo x`{E}",
    "brace lead": f"cat {{a,{E}}}",
    "trailing dot": f"cat {E}.",
    "ads stream": f"cat {E}::$DATA",
    "tilde backup": f"cat {E}~",
    "example.bak": f"cat {E}.example.bak",
    "examples": f"cat {E}.examples",
    "quote split": "cat .e''nv",
    "double quote split": 'cat .e""nv',
    "glob ?": "cat .en?",
    "glob class": "cat .[e]nv",
    "glob e*": "cat .e*",
    "star env": "cat *" + E,
    "python concat": "python -c \"open('.'+'env')\"",
    "powershell concat": "Get-Content ('.e'+'nv')",
    "compose config": "docker compose config",
    "compose convert": "docker compose convert",
    "docker-compose": "docker-compose -f x.yml config",
    "user secrets dir": f"ls $APPDATA/Microsoft/{US}/x",
    "secrets json": f"cat {SJ}",
    "secrets glob": "cat secrets.j*",
    "dotnet verb": f"dotnet {USC} list",
    # N-01 residual: continuation, comment and quote forms, and heredocs not owned by git/gh
    "gh then bash heredoc": f"gh pr view 1; bash <<'X'{NL}cat {E}{NL}X",
    "commit && python heredoc": f"git commit -m x && python - <<'X'{NL}open('{E}'){NL}X",
    "gh piped to sh": f"gh pr view 1 | sh <<'X'{NL}cat {E}{NL}X",
    "two heredocs": f"bash <<'A'; gh pr view 1 <<'B'{NL}cat {E}{NL}A{NL}b{NL}B",
    "continuation python": f"python3 - \\{NL}gh <<'X'{NL}cat {E}{NL}X",
    "continuation bash -s": f"bash -s \\{NL}gh <<'X'{NL}cat {E}{NL}X",
    "heredoc in comment": f"echo hi # ; gh pr view 1 <<X{NL}cat {E}{NL}X",
    "heredoc in double quotes": f'echo "; gh pr view 1 <<"X"{NL}cat {E}{NL}X',
    "heredoc in single quotes": f"echo '; gh pr view 1 <<'X{NL}cat {E}{NL}X",
    "gh heredoc piped": f"gh pr create --body-file - <<X | bash{NL}cat {E}{NL}X",
    "read after terminator": f"git commit -F - <<'EOF'{NL}msg{NL}EOF{NL}cat {E}",
    # H-06: exemptions must not open bypasses
    "grep -f": f"grep -f {E} x",
    "rg -e then file": f"rg -e x {E}",
    "grep pattern then file": f"grep -rn '\\.env' {E}",
    "commit -F": f"git commit -F {E}",
    "commit -m subst": f'git commit -m "$(cat {E})"',
    "commit then cat": f"git commit -m x; cat {E}",
    "log after dashdash": f"git log --grep=x -- {E}",
    "log grep subst": f'git log --grep="$(cat {E})"',
    "gh body-file": f"gh issue create --body-file {E}",
    "gh body subst": f'gh pr comment 1 --body "$(cat {E})"',
}

MUST_ALLOW = {
    "commit heredoc mentioning names": f"git commit -q -F - <<'EOF'{NL}blocks cat {E} and {USC} list{NL}EOF",
    "git -c commit heredoc": f"git -c user.name=x commit -F - <<'EOF'{NL}mentions {E}{NL}EOF",
    "cd && commit heredoc": f"cd /repo && git commit -F - <<'EOF'{NL}{E} text{NL}EOF",
    "cd && add && commit heredoc": f"cd /repo && git add a b && git commit -F - <<'EOF'{NL}{SJ}{NL}EOF",
    "gh pr create heredoc": f"gh pr create --title \"feat: x\" --body-file - <<'EOF'{NL}mentions {E}{NL}EOF",
    "log grep": f"git log --grep={US}",
    "log -S": f"git log -S'{E}' --oneline",
    "commit -m": f"git commit -m 'docs: explain {SJ}'",
    "grep pattern": f'grep -rn "\\{E}" docs',
    "rg pattern": f"rg -n '{USC}' docs .claude",
    "gh comment body": f"gh issue comment 39 --body 'rotate the {E} values'",
    "example": f"cat {E}.example",
    "example upper": f"cat {E}.EXAMPLE",
    "diff example": f"git diff -- {E}.example",
    "process.env": "grep -rn process.env src/Decisya.Web",
    "import.meta.env": "grep -rn import.meta.env src",
    "editorconfig": "cat .editorconfig",
    "eslintrc": "cat .eslintrc.json",
    "dotnet build": "dotnet build -warnaserror",
    "git log": "git log --oneline -5",
}


class SecretGuardTests(unittest.TestCase):
    def test_must_deny(self):
        for name, cmd in MUST_DENY.items():
            with self.subTest(name):
                self.assertIsNotNone(sg.decide(cmd), "allowed")

    def test_must_allow(self):
        for name, cmd in MUST_ALLOW.items():
            with self.subTest(name):
                self.assertIsNone(sg.decide(cmd), "denied")

    def test_linear_time(self):
        rnd = random.Random(39)
        blob = "".join(rnd.choice(string.printable) for _ in range(256 * 1024))
        start = time.perf_counter()
        for cmd in list(MUST_DENY.values()) + list(MUST_ALLOW.values()) + [blob]:
            sg.decide(cmd)
        self.assertLess(time.perf_counter() - start, 1.0)


if __name__ == "__main__":
    unittest.main()
