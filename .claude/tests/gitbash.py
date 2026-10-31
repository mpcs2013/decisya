"""The bash that tests run CI shell blocks with (#217).

On Windows, System32's bash.exe is the WSL launcher. When Git's bin folder is not on PATH, a bare
lookup of bash by name resolves to it, and the tests run inside the owner's WSL
distribution: on 2026-10-30 that started hundreds of WSL commands, opened editor windows and
dropped the session. So on Windows bash comes only from git's own install folder, never from
PATH and never from the Windows folder. Elsewhere (CI on Linux) it is the bash on PATH.

Every test that starts bash takes it from here; test_gitbash.py fails once, with MISSING, when
there is none, and the bash-based tests skip instead of falling back to another bash.

The install folder is the first parent of git, at most four levels up, that holds cmd\\git.exe, and
git must sit at one of the install's own paths (inside a git hook PATH starts with
<install>\\mingw64\\libexec\\git-core). The lookup never goes further up, so a bin\\bash.exe planted
in a folder any user may write (a drive root) is never taken (#246, Norma's N-017 note).
"""
import os
import shutil
from pathlib import Path

MISSING = ("Git Bash not found next to git (expected <git install>\\bin\\bash.exe). Install Git for "
           "Windows or repair it; the tests never use System32's bash.exe, the WSL launcher (#217).")
MAX_UP = 4
GIT_LAYOUTS = {"cmd/git.exe", "bin/git.exe"} | {f"{env}/{sub}/git.exe" for env in ("mingw64", "ucrt64", "clangarm64")
                                                 for sub in ("bin", "libexec/git-core")}


def git_install(git: Path) -> Path | None:
    """The Git for Windows install folder that holds this git, or None."""
    for parent in list(git.parents)[:MAX_UP]:
        if (parent / "cmd" / "git.exe").is_file():
            return parent if git.relative_to(parent).as_posix().lower() in GIT_LAYOUTS else None
    return None


def find_bash(which=shutil.which, windows: bool | None = None, system_root: str | None = None) -> str | None:
    if windows is None:
        windows = os.name == "nt"
    if not windows:
        return which("bash")  # not a bash lookup: off Windows (CI on Linux) there is no WSL launcher
    git = which("git")
    if not git:
        return None
    install = git_install(Path(git).resolve())
    if install is None:
        return None
    system = Path(system_root or os.environ.get("SystemRoot") or r"C:\Windows").resolve()
    candidate = install / "bin" / "bash.exe"
    if candidate.is_file() and not candidate.resolve().is_relative_to(system):
        return str(candidate)
    return None


BASH = find_bash()
