#!/usr/bin/env python3
"""Writes the commitlint fixture messages (*.msg) next to this file.

The expected outputs (*.expected, expected.json) are golden files produced once by the real
commitlint 19.2.1 + @commitlint/config-conventional 19.1.0 (the versions wagoid/commitlint-github-action
v6.2.1 locks) with `.claude/tests/fixtures/commitlint/make_golden.mjs`; see README.md. Re-run both
only when CI's commitlint version changes.
"""
from pathlib import Path

HERE = Path(__file__).resolve().parent
TRAILER = "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"


def pad(prefix: str, total: int) -> str:
    return prefix + "x" * (total - len(prefix))


FIXTURES = {
    "valid-simple": "feat: add money type",
    "valid-scope-body-trailer": f"fix(claude): anchor the guard\n\nWrap at 72 characters.\n\nRefs #39\n\n{TRAILER}",
    "header-100": pad("docs: ", 100),
    "header-101": pad("docs: ", 101),
    "subject-sentence-g6": "feat: G6 review",
    "subject-sentence-add": "feat: Add x",
    "subject-upper": "feat: ADD X",
    "subject-lower-with-acronym": "feat: add G6 review",
    "subject-digit": "feat: 2fa support",
    "subject-backtick": "feat: `Money` value type",
    "type-case": "Feat: x",
    "no-colon": "feat x",
    "subject-full-stop": "feat: x.",
    "type-enum": "wip: x",
    "empty-subject": "feat: ",
    "body-101": "docs: long body\n\n" + "b" * 101,
    "body-url-120": "docs: url body\n\nhttps://example.com/" + "u" * 100,
    "footer-blank": "fix: close issue\n\nSome body.\n\nCloses #39",
    "footer-no-blank": "fix: close issue\n\nSome body.\nCloses #39",
    "body-no-blank": "fix: close issue\nbody right after header",
    "breaking-change": "feat!: drop api v1\n\nBREAKING CHANGE: the v1 endpoints are removed.",
    "footer-101": "fix: long footer\n\nbody\n\nRefs: " + "f" * 95,
    "header-non-bmp": pad("docs: \U0001F600 ", 100),
    "header-trim": " feat: leading space",
    "ignored-merge": "Merge branch 'main' into issue/39-harden-hooks",
    "ignored-fixup": "fixup! feat: add money type",
    "ignored-amend": "amend! feat: add money type\n\nfeat: add the money type",
    "revert": "revert: feat: add money type\n\nThis reverts commit abc123.",
    "comment-and-scissors": "feat: add money type\n# a comment line\n\nbody text\n# ------------------------ >8 ------------------------\ndiff --git a/x b/x\nThis Is Not Linted At All And Is Longer Than One Hundred Characters Yes It Really Is Much Much Longer",
}

if __name__ == "__main__":
    for name, text in FIXTURES.items():
        (HERE / f"{name}.msg").write_text(text + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {len(FIXTURES)} fixtures")
