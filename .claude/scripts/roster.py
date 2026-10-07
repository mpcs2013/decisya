#!/usr/bin/env python3
"""Render the agent roster from the Claude Code config into the docs (#76).

Usage (from the repository root):
  python .claude/scripts/roster.py           rewrite the generated blocks
  python .claude/scripts/roster.py --check   exit 1 if a block is stale; never writes

Sources: .claude/boundaries.json (lanes, shared deny, Docker allow-list, G4 routing), agent
frontmatter (model, tools) and the skills each agent body names, skill frontmatter, and gates.py
(gate owners). Skills git does not track (a local, git-ignored skill) are left out, read from the
git index; without a readable index every skill on disk counts (#135). Targets, each between its own begin/end markers; bytes outside them are never touched:
  docs/ai/README.md                  roster:begin ... roster:end
  .claude/skills/issue/SKILL.md      routing:begin ... routing:end (routing.G4 fields only, G4-76-11)
Exit codes: 0 current or written; 1 stale, or a config or frontmatter problem (run lint.py);
2 marker or encoding problem. Standard library only; no network, no subprocess (NFR-19).
"""
from __future__ import annotations

import importlib.util
import os
import re
import sys
import tempfile
from pathlib import Path

SELF = Path(__file__).resolve()
ROOT = SELF.parents[2]


def _load(name: str, path: Path):
    """Import a sibling module by absolute path, not by sys.path name (G4-76-16)."""
    module = sys.modules.get(name)
    if module is not None and Path(getattr(module, "__file__", "")).resolve() == path:
        return module
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


hooklib = _load("_hooklib", SELF.parents[1] / "hooks" / "_hooklib.py")
claudecfg = _load("_claudecfg", SELF.parent / "_claudecfg.py")
gates = _load("gates", SELF.parent / "gates.py")

TARGETS = {
    "readme": ("docs/ai/README.md", "roster"),
    "skill": (".claude/skills/issue/SKILL.md", "routing"),
}
FIX = "run: python .claude/scripts/roster.py"
GATE_TITLES = {  # fixed template text (never taken from config)
    "G0": "G0 branch, manifest", "G1": "G1 requirements", "G2": "G2 architecture", "G3": "G3 threat model",
    "G4": "G4 code and tests", "G5": "G5 traceability", "G6": "G6 diff review", "G7": "G7 PR body",
}
_UNSAFE = re.compile("[\x00-\x1f\x7f-\x9f​-‏‪-‮⁦-⁩﻿]")
_MERMAID_LABEL = re.compile(r"[A-Za-z0-9 ,.()*+_-]{1,60}")


class RosterError(Exception):
    def __init__(self, message: str, code: int):
        super().__init__(message)
        self.code = code


def md_cell(text: str, source: str) -> str:
    """The one Markdown table-cell encoder (G4-76-12): control, zero-width and bidi characters
    are refused; `|`, `<` and `>` are escaped so the cell cannot end early or open HTML."""
    if _UNSAFE.search(text):
        raise RosterError(f"roster: {source} has a character that cannot be rendered safely", 2)
    return text.replace("|", "\\|").replace("<", "&lt;").replace(">", "&gt;")


def mermaid_label(text: str, source: str) -> str:
    """The one Mermaid label encoder: only the restricted name/label alphabet, always quoted."""
    if not _MERMAID_LABEL.fullmatch(text):
        raise RosterError(f"roster: {source} cannot be used as a diagram label", 2)
    return f'"{text}"'


def code(text: str, source: str) -> str:
    """A code span in a table cell: `|` is escaped (GitHub unescapes it inside the span); a
    backtick, `<` or `>` cannot be shown faithfully there and is refused."""
    if any(ch in text for ch in "`<>"):
        raise RosterError(f"roster: {source} contains a backtick, '<' or '>'", 2)
    return f"`{md_cell(text, source)}`"


def node_ids(names: list[str], prefix: str) -> dict[str, str]:
    ids: dict[str, str] = {}
    for name in names:
        node = prefix + name.replace("-", "_")
        if node in ids.values():
            raise RosterError(f"roster: two names give the diagram id '{node}'", 2)
        ids[name] = node
    return ids


def _git_dir(root: Path) -> Path | None:
    """The repository's git directory: root/.git, or the target of a worktree's `gitdir:` file."""
    dot_git = root / ".git"
    if dot_git.is_dir():
        return dot_git
    if dot_git.is_file():
        text = dot_git.read_text(encoding="utf-8").strip()
        if text.startswith("gitdir:"):
            target = Path(text[len("gitdir:"):].strip())
            target = target if target.is_absolute() else (root / target)
            return target if target.is_dir() else None
    return None


def _index_offset(data: bytes, pos: int) -> tuple[int, int]:
    """Git's v4 varint for the number of bytes to strip from the previous path."""
    byte = data[pos]
    pos += 1
    value = byte & 0x7F
    while byte & 0x80:
        byte = data[pos]
        pos += 1
        value = ((value + 1) << 7) | (byte & 0x7F)
    return value, pos


def tracked_paths(root: Path) -> frozenset[str] | None:
    """Paths in the git index (#135, G3 G4-135-01 to 03), or None to fall back to every file on disk.

    Standard library only, read-only, no subprocess and no environment (NFR-19). Supports index
    versions 2 to 4 of a SHA-1 repository. Any doubt returns None, never a partial set: no git
    directory, an unreadable or truncated file, a checksum mismatch, an unknown version, a SHA-256
    repository, a split index (`link`) or a sparse index (`sdir`, or a directory entry). Every entry
    counts as tracked, whatever its flags (staged new files, skip-worktree)."""
    import hashlib
    try:
        git_dir = _git_dir(root)
        if git_dir is None:
            return None
        config = git_dir / "config"
        if config.is_file() and re.search(r"(?im)^\s*objectformat\s*=\s*sha256\s*$", config.read_text(encoding="utf-8", errors="replace")):
            return None
        index = git_dir / "index"
        if index.stat().st_size > 64 * 1024 * 1024:  # G6-135-01: bounded read inside the commit hook
            return None
        data = index.read_bytes()
        if len(data) < 32 or data[:4] != b"DIRC" or hashlib.sha1(data[:-20]).digest() != data[-20:]:
            return None
        version = int.from_bytes(data[4:8], "big")
        count = int.from_bytes(data[8:12], "big")
        if version not in (2, 3, 4):
            return None
        body_end = len(data) - 20
        pos, previous, paths = 12, b"", set()
        for _ in range(count):
            start = pos
            mode = int.from_bytes(data[pos + 24:pos + 28], "big")
            flags = int.from_bytes(data[pos + 60:pos + 62], "big")
            pos += 62
            if version >= 3 and flags & 0x4000:
                pos += 2
            if version == 4:
                strip, pos = _index_offset(data, pos)
                end = data.index(b"\x00", pos)
                if strip > len(previous):
                    return None
                path = previous[:len(previous) - strip] + data[pos:end]
                pos = end + 1
            else:
                end = data.index(b"\x00", pos)
                path = data[pos:end]
                pos = start + ((end - start) // 8 + 1) * 8  # NUL-padded to a multiple of 8
            if pos > body_end or (mode & 0o170000) == 0o040000:
                return None  # truncated, or a sparse-index directory entry
            previous = path
            paths.add(path.decode("utf-8"))
        while pos + 8 <= body_end:  # extensions: refuse those that make the entry list incomplete
            signature, size = data[pos:pos + 4], int.from_bytes(data[pos + 4:pos + 8], "big")
            if signature in (b"link", b"sdir"):
                return None
            pos += 8 + size
        if pos != body_end:
            return None
        return frozenset(paths)
    except Exception:  # noqa: BLE001 - any doubt: fall back to the files on disk (G4-135-02)
        return None


def read_sources(root: Path) -> dict:
    try:
        config = hooklib.parse_boundaries((root / ".claude" / "boundaries.json").read_text(encoding="utf-8-sig"))
    except Exception as exc:  # noqa: BLE001 - any config problem: lint names it
        raise RosterError(f"roster: .claude/boundaries.json is invalid ({type(exc).__name__}); run python .claude/scripts/lint.py", 1) from None
    agents, skills = {}, {}
    for path in sorted((root / ".claude" / "agents").glob("*.md")):
        text = path.read_text(encoding="utf-8")
        data, body_start, problems = claudecfg.parse_frontmatter(text)
        if problems or not data.get("name"):
            raise RosterError(f"roster: {path.relative_to(root).as_posix()} frontmatter has problems; run python .claude/scripts/lint.py", 1)
        body = "\n".join(text.splitlines()[body_start:])
        agents[data["name"]] = {"model": data.get("model", ""), "tools": data.get("tools", ""),
                                "skills": sorted(claudecfg.skill_refs(body))}
    # #135: a skill folder git does not track (a local, git-ignored skill) is not in the committed
    # roster. The index only filters the on-disk glob; content always comes from disk (G4-135-01).
    tracked = tracked_paths(root)
    for path in sorted((root / ".claude" / "skills").glob("*/SKILL.md")):
        if tracked is not None and path.relative_to(root).as_posix() not in tracked:
            continue
        data, _, problems = claudecfg.parse_frontmatter(path.read_text(encoding="utf-8"))
        if problems or not data.get("name"):
            raise RosterError(f"roster: {path.relative_to(root).as_posix()} frontmatter has problems; run python .claude/scripts/lint.py", 1)
        if not hooklib.NAME.fullmatch(data["name"]):
            raise RosterError(f"roster: skill name '{data['name']}' is not a valid name; run python .claude/scripts/lint.py", 1)
        skills[data["name"]] = {"description": data.get("description", ""),
                                "manual": data.get("disable-model-invocation", "").lower() == "true"}
    for name in agents:
        if not hooklib.NAME.fullmatch(name):
            raise RosterError(f"roster: agent name '{name}' is not a valid name; run python .claude/scripts/lint.py", 1)
    return {"config": config, "agents": agents, "skills": skills}


def agent_order(src: dict) -> list[str]:
    """First gate (G1, G2, G3, G4 in routing order, G5, G6), then the rest by name."""
    ordered: list[str] = []
    routes = [row["agent"] for row in src["config"].get("routing", {}).get("G4", [])]
    for gate in ("G1", "G2", "G3", "G4", "G5", "G6"):
        for name in routes if gate == "G4" else [gates.GATE_OWNER.get(gate)]:
            if name in src["agents"] and name not in ordered:
                ordered.append(name)
    return ordered + sorted(n for n in src["agents"] if n not in ordered)


def gate_cell(name: str, src: dict) -> str:
    parts = [g for g, owner in sorted(gates.GATE_OWNER.items()) if owner == name]
    parts += [f"G4: {row['summary']}" for row in src["config"].get("routing", {}).get("G4", []) if row["agent"] == name]
    return md_cell(", ".join(sorted(parts)), f"gates of {name}") if parts else "—"


def tools_cell(name: str, tools: str) -> str:
    out = []
    for entry in (t.strip() for t in tools.split(",")):
        if not entry or entry in ("Read", "Grep", "Glob"):
            continue
        m = re.fullmatch(r"Bash\((.*)\)", entry)
        out.append(code(m.group(1) if m else entry, f"tools of {name}"))
    return ", ".join(out) or "—"


def docker_cell(name: str, config: dict) -> str:
    entries = config.get("docker", {}).get(name, [])
    if not entries:
        return "—"
    cells = []
    for i, entry in enumerate(entries):
        allowed = ", ".join(code(s, f"docker.{name}[{i}]") for s in hooklib.docker_expansions(entry["pattern"]))
        cells.append(allowed + (" (with exclusions)" if "(?!" in entry["pattern"] else ""))
    return "; ".join(cells)


def render_readme(src: dict) -> str:
    config, agents, skills = src["config"], src["agents"], src["skills"]
    order = agent_order(src)
    routes = config.get("routing", {}).get("G4", [])
    ids = node_ids(order, "")
    out = [
        f"{len(agents)} agents and {len(skills)} skills, generated from `.claude/boundaries.json` (lanes, shared deny, "
        "Docker allow-list, G4 routing), the agent and skill frontmatter and `gates.py`. Edit those sources, then run "
        "`python .claude/scripts/roster.py`; lint and CI fail while this block is stale.",
        "",
        "### Gates and owners",
        "",
        "```mermaid",
        "flowchart LR",
        "  " + " --> ".join(f"{g}[{mermaid_label(GATE_TITLES[g], g)}]" for g in GATE_TITLES),
        f"  main_session[{mermaid_label('main session', 'main session')}]",
        "  G0 --> main_session",
        f"  G4 -->|{mermaid_label('evidence', 'G4')}| main_session",
        "  G7 --> main_session",
    ]
    for gate, owner in sorted(gates.GATE_OWNER.items()):
        if owner in ids:
            out.append(f"  {gate} --> {ids[owner]}[{mermaid_label(owner, owner)}]")
    for row in routes:
        out.append(f"  G4 -->|{mermaid_label(row['summary'], 'routing summary')}| {ids[row['agent']]}[{mermaid_label(row['agent'], row['agent'])}]")
    out += [
        "  classDef sec stroke:#c0392b,stroke-width:3px",
        "  class G3,G6 sec",
        "```",
        "",
        "G3 and G6 (outlined) are the security gates; a change to agent lanes, tools, hooks, settings, the checkers "
        "or the workflows always needs both (`gates.py`).",
        "",
        "### Agents",
        "",
        "| Agent | Model | Gate(s) | May write (hook-enforced) | Docker (hook-enforced) | Skills | Tools beyond Read/Grep/Glob (declared) |",
        "| --- | --- | --- | --- | --- | --- | --- |",
    ]
    for name in order:
        a = agents[name]
        lanes = ", ".join(code(g, f"lanes of {name}") for g in config["agents"].get(name, [])) or "—"
        skill_list = ", ".join(md_cell(s, f"skills of {name}") for s in a["skills"]) or "—"
        out.append(f"| {md_cell(name, name)} | {md_cell(a['model'], f'model of {name}') or '—'} | {gate_cell(name, src)} | "
                   f"{lanes} | {docker_cell(name, config)} | {skill_list} | {tools_cell(name, a['tools'])} |")
    deny = ", ".join(code(g, "deny") for g in config.get("deny", []))
    out += [
        "",
        f"No agent writes {deny}. The hook enforces the write lanes, the Docker allow-list and the command policy; "
        "the declared tools are what the agent is offered, not a boundary.",
        "",
        "Docker allow-list as enforced (patterns from `.claude/boundaries.json`; exclusions are negative lookaheads):",
        "",
        "| Agent | Allows | Pattern |",
        "| --- | --- | --- |",
    ]
    for name in order:
        for i, entry in enumerate(config.get("docker", {}).get(name, [])):
            out.append(f"| {md_cell(name, name)} | {md_cell(entry['label'], f'docker.{name}[{i}].label')} | "
                       f"{code(entry['pattern'], f'docker.{name}[{i}].pattern')} |")
    used = {s: [n for n in order if s in agents[n]["skills"]] for s in sorted(skills)}
    skill_ids = node_ids(sorted(skills), "s_")
    agent_ids = node_ids(order, "a_")
    out += ["", "### Skills", "", "```mermaid", "flowchart LR"]
    for name in order:
        for s in agents[name]["skills"]:
            if s in skills:
                out.append(f"  {agent_ids[name]}[{mermaid_label(name, name)}] --> {skill_ids[s]}[{mermaid_label(s, s)}]")
    out += ["```", "", "| Skill | Used by | Purpose |", "| --- | --- | --- |"]
    for s, users in used.items():
        who = ", ".join(users) if users else (f"you (`/{s}`)" if skills[s]["manual"] else "—")
        purpose = skills[s]["description"].split(". ")[0].rstrip(".")
        out.append(f"| `{md_cell(s, s)}` | {md_cell(who, f'users of {s}')} | {md_cell(purpose, f'description of {s}')} |")
    return "\n".join(out)


def render_skill(src: dict) -> str:
    """Only routing.G4 fields and fixed text reach the issue skill (G4-76-11)."""
    out = ["| Paths | Owner |", "| --- | --- |"]
    for row in src["config"].get("routing", {}).get("G4", []):
        out.append(f"| {md_cell(row['paths'], 'routing paths')} | {md_cell(row['agent'], 'routing agent')} |")
    return "\n".join(out)


def _split(text: str, marker: str, rel: str) -> tuple[str, str, str]:
    begin = re.compile(rf"^<!-- {marker}:begin\b[^\n]*-->[ \t]*$", re.MULTILINE)
    end = re.compile(rf"^<!-- {marker}:end -->[ \t]*$", re.MULTILINE)
    b, e = list(begin.finditer(text)), list(end.finditer(text))
    if len(b) != 1 or len(e) != 1 or b[0].end() > e[0].start():
        raise RosterError(f"roster: {rel} needs exactly one '<!-- {marker}:begin ... -->' line followed by one '<!-- {marker}:end -->' line", 2)
    return text[:b[0].end()], text[b[0].end():e[0].start()], text[e[0].start():]


def rendered(root: Path) -> dict[str, tuple[Path, str, str]]:
    """{target: (path, current text, expected text)} with LF line endings."""
    src = read_sources(root)
    bodies = {"readme": render_readme(src), "skill": render_skill(src)}
    result = {}
    for key, (rel, marker) in TARGETS.items():
        path = root / rel
        if not path.is_file():
            raise RosterError(f"roster: {rel} is missing", 2)
        current = path.read_bytes().decode("utf-8").replace("\r\n", "\n")
        head, _, tail = _split(current, marker, rel)
        result[key] = (path, current, f"{head}\n\n{bodies[key]}\n\n{tail}")
    return result


def check(root: Path = ROOT) -> list[str]:
    """Stale targets as messages ([] when current). Never opens a file for writing (G4-76-15)."""
    return [f"roster: {TARGETS[k][0]} is stale; {FIX}" for k, (_, current, expected) in rendered(root).items()
            if current != expected]


def write(root: Path = ROOT) -> list[str]:
    changed = []
    for key, (path, current, expected) in rendered(root).items():
        if current == expected:
            continue
        if path.is_symlink():
            raise RosterError(f"roster: {TARGETS[key][0]} is a symlink; refusing to write", 2)
        crlf = b"\r\n" in path.read_bytes()
        data = (expected.replace("\n", "\r\n") if crlf else expected).encode("utf-8")
        fd, tmp = tempfile.mkstemp(dir=path.parent, prefix=".roster-", suffix=".tmp")
        try:
            with os.fdopen(fd, "wb") as fh:
                fh.write(data)
            os.replace(tmp, path)
        except BaseException:
            Path(tmp).unlink(missing_ok=True)
            raise
        changed.append(TARGETS[key][0])
    return changed


def main(argv: list[str]) -> int:
    try:
        if argv == ["--check"]:
            stale = check(ROOT)
            for line in stale:
                print(line)
            return 1 if stale else 0
        if argv:
            print(__doc__, file=sys.stderr)
            return 2
        for rel in write(ROOT):
            print(f"roster: wrote {rel}")
        return 0
    except RosterError as exc:
        print(exc, file=sys.stderr)
        return exc.code


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
