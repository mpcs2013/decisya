"""The bash that tests run CI shell blocks with (#217).

On Windows, System32's bash.exe is the WSL launcher. When Git's bin folder is not on PATH, a bare
"bash" (or shutil.which("bash")) resolves to it, and the tests run inside the owner's WSL
distribution: on 2026-10-30 that started hundreds of WSL commands, opened editor windows and
dropped the session. So on Windows bash comes only from git's own install folder, never from
PATH and never from the Windows folder. Elsewhere (CI on Linux) it is the bash on PATH.

Every test that starts bash takes it from here; test_gitbash.py fails once, with MISSING, when
there is none, and the bash-based tests skip instead of falling back to another bash.
"""
import os
import shutil
from pathlib import Path

MISSING = ("Git Bash not found next to git (expected <git install>\\bin\\bash.exe). Install Git for "
           "Windows or repair it; the tests never use System32's bash.exe, the WSL launcher (#217).")


def find_bash(which=shutil.which, windows: bool | None = None, system_root: str | None = None) -> str | None:
    if windows is None:
        windows = os.name == "nt"
    if not windows:
        return which("bash")
    git = which("git")
    if not git:
        return None
    system = Path(system_root or os.environ.get("SystemRoot") or r"C:\Windows").resolve()
    for parent in Path(git).resolve().parents:
        candidate = parent / "bin" / "bash.exe"
        if candidate.is_file() and not candidate.resolve().is_relative_to(system):
            return str(candidate)
    return None


BASH = find_bash()
