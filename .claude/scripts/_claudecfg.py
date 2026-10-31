"""Shared parsing for lint.py and roster.py, and the one
loader of the project facts in .claude/project.json for the hooks and scripts.

Standard library only; no import-time side effects beyond constants and re.compile.
The project loader has no defaults, no cache and no environment: a missing, unreadable, malformed
or wrong-type file raises ProjectConfigError, whose text names the file, the key path and the
expected type but never a value from the file.
"""
from __future__ import annotations

import json
import os
import re
import stat
from pathlib import Path

SKILL_REF = re.compile(r"`([a-z0-9][a-z0-9-]*)` skill|\bskill `([a-z0-9][a-z0-9-]*)`|\bthe `([a-z0-9][a-z0-9-]*)`\s+skill")


def parse_frontmatter(text: str) -> tuple[dict[str, str], int, list[tuple[int, str]]]:
    """(key/value pairs, index of the first body line, [(line, problem)]). The rules match what
    Claude Code's YAML loader rejects: unquoted values containing ': ' or starting with a YAML
    indicator, malformed or unclosed quotes, block lists (a line that is not 'key: value')."""
    lines = text.splitlines()
    problems: list[tuple[int, str]] = []
    if not lines or lines[0] != "---":
        return {}, 0, [(1, "missing frontmatter (--- on line 1)")]
    data: dict[str, str] = {}
    for i, line in enumerate(lines[1:], start=2):
        if line == "---":
            return data, i, problems
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        m = re.match(r"^([A-Za-z][\w-]*):\s*(.*)$", line)
        if not m:
            problems.append((i, f"frontmatter line is not 'key: value': {line[:60]}"))
            continue
        key, value = m.groups()
        if value.startswith('"'):
            try:
                value = json.loads(value)
            except json.JSONDecodeError:
                problems.append((i, f"'{key}' is a malformed double-quoted string"))
        elif value.startswith("'"):
            if not value.endswith("'"):
                problems.append((i, f"'{key}' single-quoted string is not closed"))
            value = value[1:-1].replace("''", "'")
        elif ": " in value or value.endswith(":") or value[:1] in "[{&*!|>%@`":
            problems.append((i, f"'{key}' value must be quoted (contains ': ' or a YAML indicator; the file would fail to load)"))
        data[key] = value
    problems.append((1, "frontmatter is not closed with ---"))
    return data, 0, problems


def skill_refs(body: str) -> list[str]:
    """Skill names referenced in a text, in order of first appearance, deduplicated."""
    seen: list[str] = []
    for m in SKILL_REF.finditer(body):
        name = next(g for g in m.groups() if g)
        if name not in seen:
            seen.append(name)
    return seen


# --- the project layer, .claude/project.json ---
PROJECT_REL = ".claude/project.json"
PROJECT_MAX_BYTES = 65536
PROJECT_MAX_DEPTH = 16
# C0/C1 controls, zero-width and bidi characters, BOM: the same class as _hooklib._BAD_CHARS.
_BAD_CHARS = re.compile("[\x00-\x1f\x7f-\x9f\u200b-\u200f\u202a-\u202e\u2066-\u2069\ufeff]")
_REL_PATH = re.compile(r"[A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]+)*")
_FOLDER = re.compile(r"\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+")
_PROJECT_ID = re.compile(r"GL-[0-9]{4}")  # the tracking project's id, as the capture format 1.0.0 requires
_PACK_NAME = re.compile(r"[a-z][a-z0-9-]{0,29}")
_ENV_NAME = re.compile(r"[A-Z][A-Z0-9_]{0,63}")
PACK_MAX_BYTES = 16384
PACK_MAX_KEYS = 20
PACK_KINDS = ("string", "string_list", "pairs", "object", "env_name")
PACK_TOP_KEYS = {"schema", "name", "keys", "agent_tools", "prepush_steps", "prepush_env", "allowed_options", "allowed_verbs",
                 "lint_patterns"}
_NAME = re.compile(r"[a-z][a-z0-9-]{0,39}")
_TAG = re.compile(r"[a-z][a-z0-9-]{0,30}")
_BOT_BRANCH = re.compile(r"[a-z][a-z0-9-]{0,30}/")  # a bot's branch prefix, such as dependabot/
_BOT_LOGIN = re.compile(r"[A-Za-z0-9][A-Za-z0-9-]{0,38}\[bot\]")  # a GitHub App bot login, such as dependabot[bot]
# a person who may approve a skipped gate: precomposed (NFC) letters, joined by one or two of space . ' -; combining
# marks are refused, so a name in a script that needs them is written without them or not at all
_APPROVER = re.compile(r"[^\W\d_]+(?:[ .'-]{1,2}[^\W\d_]+)*\.?")
# the word `gates.py init` writes before anyone approves; as a name it would approve every skip
_APPROVER_PLACEHOLDER = "pending"
_TOOL = re.compile(r"[A-Za-z0-9][A-Za-z0-9 .+#-]{0,39}")
_INLINE_FLAG = re.compile(r"\(\?[aiLmsux-]+[:)]|\(\?#")
# a back-reference, named group or conditional in one item would point at another item's
# group once the items are joined, so a valid-looking item could silently never match.
_GROUP_REFERENCE = re.compile(r"\\[1-9]|\\g<|\(\?P<|\(\?P=|\(\?\(")
# a path-class item is anchored and matches none of these (no catch-all).
_PATH_CLASS_PROBES = ("", "x", "x/y", "x/y.z", "zz/zz9.zz9")
_TYPE_NAMES = {bool: "a boolean", int: "an integer", float: "a number", str: "a string", list: "an array",
               dict: "an object", type(None): "null"}


class ProjectConfigError(Exception):
    """.claude/project.json cannot be used. str() is safe to show: fixed text, key paths, JSON type
    names and positions only, never a value, key or other content of the file."""

    def __init__(self, kind: str, problem: str, line: int = 1):
        super().__init__(f"{PROJECT_REL} {problem}")
        self.kind, self.problem, self.line = kind, problem, line


def load_project(path) -> dict:
    """The validated project facts; a new dict on every call. Raises ProjectConfigError only."""
    try:
        mode = os.lstat(path).st_mode
    except FileNotFoundError:
        raise ProjectConfigError("missing", "is missing") from None
    except OSError as exc:
        raise ProjectConfigError("unreadable", f"is unreadable ({type(exc).__name__})") from None
    if stat.S_ISLNK(mode):
        raise ProjectConfigError("unreadable", "is unreadable (a symbolic link)")
    if not stat.S_ISREG(mode):
        raise ProjectConfigError("unreadable", "is unreadable (not a regular file)")
    try:
        with open(path, "rb") as fh:
            if not stat.S_ISREG(os.fstat(fh.fileno()).st_mode):  # swapped after the check above
                raise ProjectConfigError("unreadable", "is unreadable (not a regular file)")
            data = fh.read(PROJECT_MAX_BYTES + 1)
    except OSError as exc:
        raise ProjectConfigError("unreadable", f"is unreadable ({type(exc).__name__})") from None
    if len(data) > PROJECT_MAX_BYTES:
        raise ProjectConfigError("unreadable", f"is unreadable (larger than {PROJECT_MAX_BYTES} bytes)")
    try:
        text = data.decode("utf-8-sig")
    except UnicodeDecodeError:
        raise ProjectConfigError("malformed", "is malformed (not UTF-8 text)") from None
    return parse_project(text, Path(path).parent / "packs")


class _DuplicateKey(Exception):
    pass


def _no_duplicate_keys(pairs: list[tuple[str, object]]) -> dict:
    """Unlike _hooklib's helper, the error carries no key."""
    seen = set()
    for key, _ in pairs:
        if key in seen:
            raise _DuplicateKey()
        seen.add(key)
    return dict(pairs)


def _nesting_depth(text: str) -> int:
    """The deepest [ or { nesting outside JSON strings, found without recursion."""
    depth = deepest = 0
    in_string = escaped = False
    for ch in text:
        if in_string:
            if escaped:
                escaped = False
            elif ch == "\\":
                escaped = True
            elif ch == '"':
                in_string = False
        elif ch == '"':
            in_string = True
        elif ch in "[{":
            depth += 1
            deepest = max(deepest, depth)
        elif ch in "]}":
            depth -= 1
    return deepest


def parse_project(text: str, packs_dir: Path | None = None) -> dict:
    """Validate project.json text. `packs_dir` is the folder holding the data file of each installed pack;
    without it a project that declares a pack fails closed."""
    if _nesting_depth(text) > PROJECT_MAX_DEPTH:
        raise ProjectConfigError("malformed", f"is malformed (nested deeper than {PROJECT_MAX_DEPTH} levels)")
    try:
        data = json.loads(text, object_pairs_hook=_no_duplicate_keys)
    except _DuplicateKey:
        raise ProjectConfigError("malformed", "is malformed (a duplicate key)") from None
    except json.JSONDecodeError as exc:
        raise ProjectConfigError("malformed", f"is malformed (not valid JSON at line {exc.lineno}, column {exc.colno})",
                                 exc.lineno) from None
    except (ValueError, RecursionError):  # e.g. an integer past Python's digit limit
        raise ProjectConfigError("malformed", "is malformed (not valid JSON)") from None
    if not isinstance(data, dict):
        raise ProjectConfigError("wrong-type", f"has a wrong type: the top level must be an object, not {_type_name(data)}")
    _check_characters(data)
    _keys(data, ["schema", "repository", "packs", "agent_boundaries", "secret_guard", "gates", "prepush", "prereqs",
                 "lock", "templates", "usage"], "", optional={"$comment"})
    if "$comment" in data:
        _string(data["$comment"], "$comment", 1, 2000)
    if _integer(data["schema"], "schema") != 1:
        _invalid("schema", "must be 1")
    _project_sections(data)
    # derived, never read from the file: the key check above refuses it in project.json
    for key, value in _packs(data["packs"], packs_dir).items():
        data["pack_" + key] = value
    _check_opt_ins(data, packs_dir)
    return data


# --- GitHub features by visibility (core/.claude/github-features.json) ---
# An opt-in setting that relies on a GitHub feature is refused when GitHub Pro does not include that feature for the
# project's repository.visibility, so a private project is never set up for something it would pay for or not get.
FEATURES_REL = "github-features.json"
FEATURE_STATUSES = {"included", "not-included", "not-applicable"}
VISIBILITIES = ("public", "private")
# core opt-ins: (key path, GitHub feature it needs, whether it is set)
CORE_OPT_INS = (("gates.dependency_bot_branches", "dependabot", lambda d: bool(d["gates"]["dependency_bot_branches"])),)


def load_features(path: Path) -> dict:
    """The feature map; ProjectConfigError (kind feature-map) when it cannot be used."""
    try:
        data = json.loads(Path(path).read_text(encoding="utf-8-sig"), object_pairs_hook=_no_duplicate_keys)
        features = data["features"]
        ok = data.get("schema") == 1 and isinstance(features, dict) and all(
            isinstance(f, dict) and f.get("public") in FEATURE_STATUSES and f.get("private") in FEATURE_STATUSES
            for f in features.values())
    except (OSError, ValueError, KeyError, TypeError, AttributeError, _DuplicateKey, RecursionError):
        ok = False
    if not ok:
        raise ProjectConfigError("feature-map", "cannot be checked: the GitHub feature map .claude/github-features.json "
                                                "is missing or invalid")
    return features


def _check_opt_ins(data: dict, packs_dir: Path | None) -> None:
    visibility = data["repository"]["visibility"]
    needed = [(where, feature) for where, feature, is_set in CORE_OPT_INS if is_set(data)]
    needed += data.pop("pack__feature_opt_ins", [])  # internal: never part of the returned facts
    if not needed:
        return
    if packs_dir is None:
        raise ProjectConfigError("feature-map", "cannot be checked: the GitHub feature map is not available")
    features = load_features(Path(packs_dir).parent / FEATURES_REL)
    for where, feature in needed:
        status = features.get(feature, {}).get(visibility)
        if status != "included":
            _invalid(where, f"needs a GitHub feature that GitHub Pro does not include for a {visibility} repository "
                            "(see .claude/github-features.json)")


def _type_name(value: object) -> str:
    return _TYPE_NAMES.get(type(value), "another type")


def _wrong(path: str, expected: str, value: object) -> None:
    raise ProjectConfigError("wrong-type", f"has a wrong type: {path} must be {expected}, not {_type_name(value)}")


def _invalid(path: str, rule: str) -> None:
    raise ProjectConfigError("invalid-value", f"has an invalid value at {path}: {rule}")


def _check_characters(data: object) -> None:
    """No control, zero-width or bidi character in any key or string, walked without recursion."""
    stack = [data]
    while stack:
        value = stack.pop()
        if isinstance(value, str):
            if _BAD_CHARS.search(value):
                raise ProjectConfigError("invalid-value", "has an invalid value: a key or string contains a control, "
                                                          "zero-width or bidi character")
        elif isinstance(value, dict):
            stack.extend(value.keys())
            stack.extend(value.values())
        elif isinstance(value, list):
            stack.extend(value)


def _keys(obj: dict, required: list[str], path: str, optional: frozenset | set = frozenset()) -> None:
    for key in required:
        if key not in obj:
            raise ProjectConfigError("missing-key", f"is missing the required key {path + '.' if path else ''}{key}")
    if any(key not in required and key not in optional for key in obj):
        raise ProjectConfigError("unknown-key", f"has a key that is not in the schema in {path or 'the top level'}")


def _object(value: object, path: str) -> dict:
    if not isinstance(value, dict):
        _wrong(path, "an object", value)
    return value


def _integer(value: object, path: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        _wrong(path, "an integer", value)
    return value


def _string(value: object, path: str, low: int = 1, high: int = 200) -> str:
    if not isinstance(value, str):
        _wrong(path, "a string", value)
    if not low <= len(value) <= high:
        _invalid(path, f"must hold {low} to {high} characters")
    return value


def _array(value: object, path: str, low: int, high: int, unique: bool = False) -> list:
    if not isinstance(value, list):
        _wrong(path, "an array", value)
    if not low <= len(value) <= high:
        _invalid(path, f"must hold {low} to {high} items")
    if unique and len(set(map(str, value))) != len(value):
        _invalid(path, "must not repeat an item")
    return value


def _strings(value: object, path: str, low: int, high: int, unique: bool = False, length: int = 200) -> list[str]:
    for i, item in enumerate(_array(value, path, low, high, unique)):
        _string(item, f"{path}[{i}]", 1, length)
    return value


def _match(value: str, pattern: re.Pattern[str], path: str, label: str) -> None:
    if not pattern.fullmatch(value):
        _invalid(path, f"must match {label}")


def _rel_path(value: str, path: str) -> None:
    _match(value, _REL_PATH, path, "a repository-relative path")
    if any(segment in (".", "..") for segment in value.split("/")):
        _invalid(path, "must not have a '.' or '..' segment")


_REPEATS = {"MAX_REPEAT", "MIN_REPEAT", "POSSESSIVE_REPEAT"}


def _repeats_ambiguously(pattern: str) -> bool:
    """True when a repeat that can run more than once contains another repeat or an alternation, or when the
    pattern has more than six such repeats: the shapes that make a regular expression take exponential or high
    polynomial time on a crafted input (for example `(a|aa)+`, `(a+)+`). Fails closed when the pattern cannot
    be parsed or is nested very deeply."""
    try:
        from re import _parser
        tree = _parser.parse(pattern)
    except Exception:  # noqa: BLE001 - any parse problem means the pattern is not accepted
        return True
    count = 0

    def walk(items, under_repeat: bool, depth: int) -> bool:
        nonlocal count
        if depth > 30:
            return True
        for op, arg in items:
            name = str(op)
            if name in _REPEATS:
                _low, high, sub = arg
                multi = high > 1
                if multi and under_repeat:
                    return True
                count += multi
                if walk(sub, under_repeat or multi, depth + 1):
                    return True
            elif name == "BRANCH":
                if under_repeat:
                    return True
                if any(walk(alt, under_repeat, depth + 1) for alt in arg[1]):
                    return True
            elif name == "SUBPATTERN":
                if walk(arg[3], under_repeat, depth + 1):
                    return True
            elif name in ("ASSERT", "ASSERT_NOT"):
                if walk(arg[1], under_repeat, depth + 1):
                    return True
            elif name == "ATOMIC_GROUP":
                if walk(arg, under_repeat, depth + 1):
                    return True
            elif name == "GROUPREF_EXISTS":
                return True
        return False

    return walk(tree, False, 0) or count > 6


def _pattern(value: str, path: str) -> re.Pattern[str]:
    if _INLINE_FLAG.search(value):
        _invalid(path, "must not use an inline flag")
    if _GROUP_REFERENCE.search(value.replace("\\\\", "")):
        _invalid(path, "must not use a back-reference, named group or conditional")
    try:
        compiled = re.compile(value)
    except (re.error, OverflowError, RecursionError, ValueError):  # e.g. x{9999999999} overflows
        _invalid(path, "must compile as a regular expression")
    if _repeats_ambiguously(value):
        _invalid(path, "must not repeat an alternation or another repeat")
    return compiled


def _patterns(value: object, path: str, low: int, path_class: bool, probes: bool = False) -> list[str]:
    for i, item in enumerate(_strings(value, path, low, 50)):
        where = f"{path}[{i}]"
        compiled = _pattern(item, where)
        if path_class:
            if not item.startswith(("^", "(^|/)")):
                _invalid(where, "must start with '^' or '(^|/)'")
            if any(compiled.search(probe) for probe in _PATH_CLASS_PROBES):
                _invalid(where, "must not match an empty or arbitrary path")
        elif probes and any(compiled.search(probe) for probe in _PATH_CLASS_PROBES):
            _invalid(where, "must not match an empty or arbitrary path")
    # the exact string an engine compiles: path classes and ignore lists are joined with "|" and searched
    joined = "|".join(value) if (path_class or probes) else "^(" + "|".join(value) + ")$"
    try:
        re.compile(joined)
    except (re.error, OverflowError, RecursionError, ValueError):
        _invalid(path, "must compile when its items are joined")
    return value


def guard_blocks(folder: str, blocked: list[str]) -> bool:
    """Whether the secret guard's pattern for one of the blocked folders matches this folder name: the stem `.<a>.`
    of a folder `.<a>.<b>`, or `.local.` and the first letter of `<b>` (hooks/secret_guard.py, patterns_for; a test
    keeps the two rules equal)."""
    for name in blocked:
        stem = name[:name.index(".", 1)].lower()
        key = stem + "." + (name[len(stem) + 1].lower() if stem == ".local" else "")
        if folder.lower().startswith(key):
            return True
    return False


def _project_sections(data: dict) -> None:
    section = _object(data["agent_boundaries"], "agent_boundaries")
    _keys(section, ["cd_targets"], "agent_boundaries")
    for i, item in enumerate(_strings(section["cd_targets"], "agent_boundaries.cd_targets", 0, 20, unique=True)):
        _rel_path(item, f"agent_boundaries.cd_targets[{i}]")

    section = _object(data["secret_guard"], "secret_guard")
    _keys(section, ["blocked_folders"], "secret_guard")
    for i, item in enumerate(_strings(section["blocked_folders"], "secret_guard.blocked_folders", 0, 10, unique=True)):
        _match(item, _FOLDER, f"secret_guard.blocked_folders[{i}]", "a dot-folder name with one inner dot")

    section = _object(data["repository"], "repository")
    _keys(section, ["visibility"], "repository")
    if _string(section["visibility"], "repository.visibility", 1, 10) not in VISIBILITIES:
        _invalid("repository.visibility", "must be public or private")

    section = _object(data["gates"], "gates")
    _keys(section, ["approvers", "ci_tooling_paths", "dependency_paths", "review_required_paths", "docs_line_from",
                    "dependency_bot_branches"], "gates")
    # the people who may approve a skipped gate; gates.py accepts "approved: <one of them> YYYY-MM-DD"
    approvers = _strings(section["approvers"], "gates.approvers", 1, 10, length=60)
    for i, item in enumerate(approvers):
        _match(item, _APPROVER, f"gates.approvers[{i}]", "a person's name (letters joined by spaces or . ' -)")
        if item.casefold() == _APPROVER_PLACEHOLDER:
            _invalid(f"gates.approvers[{i}]", "must be a person's name, not the placeholder gates.py init writes")
    if len({a.casefold() for a in approvers}) != len(approvers):
        _invalid("gates.approvers", "each name may appear once")
    # each entry names the bot's branch prefix and its GitHub login: anyone can name a branch, only the bot opens a
    # pull request as that login
    bots = _array(section["dependency_bot_branches"], "gates.dependency_bot_branches", 0, 5)
    for i, item in enumerate(bots):
        path = f"gates.dependency_bot_branches[{i}]"
        item = _object(item, path)
        _keys(item, ["branch", "login"], path)
        _match(_string(item["branch"], path + ".branch", 1, 40), _BOT_BRANCH, path + ".branch",
               "a branch prefix such as dependabot/")
        _match(_string(item["login"], path + ".login", 1, 45), _BOT_LOGIN, path + ".login",
               "a bot login such as dependabot[bot]")
    if len({b["branch"] for b in bots}) != len(bots):
        _invalid("gates.dependency_bot_branches", "each branch prefix may appear once")
    if not 0 <= _integer(section["docs_line_from"], "gates.docs_line_from") <= 1_000_000:
        _invalid("gates.docs_line_from", "must be between 0 and 1000000")
    _patterns(section["ci_tooling_paths"], "gates.ci_tooling_paths", 1, path_class=True)
    _patterns(section["dependency_paths"], "gates.dependency_paths", 1, path_class=True)
    _patterns(section["review_required_paths"], "gates.review_required_paths", 0, path_class=False)

    section = _object(data["prepush"], "prepush")
    _keys(section, ["commit_message_check", "ignore_paths", "ignore_markdown"], "prepush")
    check = _string(section["commit_message_check"], "prepush.commit_message_check")
    _rel_path(check, "prepush.commit_message_check")
    if not check.endswith(".py"):
        _invalid("prepush.commit_message_check", "must name a .py file")
    for key in ("ignore_paths", "ignore_markdown"):
        _patterns(section[key], f"prepush.{key}", 0, path_class=False, probes=True)

    section = _object(data["lock"], "lock")
    _keys(section, ["signers_file"], "lock")
    _rel_path(_string(section["signers_file"], "lock.signers_file"), "lock.signers_file")

    # the usage capture (ADR 0006): its folder leaves the machine through a copy job, so it is git-ignored (lint.py),
    # outside .claude/ and .git/, and never a folder the secret guard blocks
    section = _object(data["usage"], "usage")
    _keys(section, ["capture_folder", "project_id"], "usage")
    folder = _string(section["capture_folder"], "usage.capture_folder", 1, 100)
    _rel_path(folder, "usage.capture_folder")
    if folder.split("/")[0].lower() in {".claude", ".git"}:
        _invalid("usage.capture_folder", "must be outside .claude/ and .git/")
    if any(guard_blocks(part, data["secret_guard"]["blocked_folders"]) for part in folder.split("/")):
        _invalid("usage.capture_folder", "must not be a folder the secret guard blocks")
    _match(_string(section["project_id"], "usage.project_id", 7, 7), _PROJECT_ID, "usage.project_id",
           "GL- and four digits")

    skills = _object(data["prereqs"], "prereqs")
    if len(skills) > 50:
        _invalid("prereqs", "must hold 0 to 50 items")
    for s, (skill, phases) in enumerate(skills.items(), start=1):
        where = f"prereqs entry {s}"
        _match(skill, _NAME, where, "a skill name")
        _object(phases, where)
        if not 1 <= len(phases) <= 10:
            _invalid(where, "must hold 1 to 10 items")
        for p, (phase, checks) in enumerate(phases.items(), start=1):
            at = f"{where} phase {p}"
            _match(phase, _NAME, at, "a phase name")
            for c, item in enumerate(_array(checks, at, 1, 20), start=1):
                cell = f"{at} check {c}"
                _keys(_object(item, cell), ["kind", "target", "label", "issue"], cell)
                kind = _string(item["kind"], f"{cell}.kind", 1, 10)
                target = _string(item["target"], f"{cell}.target", 1, 300)
                if kind == "path":
                    _rel_path(target, f"{cell}.target")
                elif kind == "type":
                    _pattern(target, f"{cell}.target")
                else:
                    _invalid(f"{cell}.kind", "must be path or type")
                _string(item["label"], f"{cell}.label", 1, 120)
                _string(item["issue"], f"{cell}.issue", 1, 120)

    section = _object(data["templates"], "templates")
    _keys(section, ["adr_tags", "runbook_tools"], "templates")
    for i, item in enumerate(_strings(section["adr_tags"], "templates.adr_tags", 1, 20, unique=True)):
        _match(item, _TAG, f"templates.adr_tags[{i}]", "a lower-case tag")
    for i, item in enumerate(_strings(section["runbook_tools"], "templates.runbook_tools", 1, 4, unique=True)):
        _match(item, _TOOL, f"templates.runbook_tools[{i}]", "a tool name")

# --- installed packs: the project declares them, each pack's data file says what it may configure ---
# A pack ships `.claude/packs/<name>.json`. The loader runs no pack code; it only reads this data and checks
# `packs.<name>` in project.json against it with a fixed set of kinds (PACK_KINDS). Error text names a pack by its
# position in the `packs` object ("packs entry 2"), never by the name taken from the file.
_KIND_PARAMS = {
    "string": {"pattern", "min", "max"},
    "string_list": {"item_pattern", "min_items", "max_items", "unique", "max_length"},
    "pairs": {"options", "value_pattern", "max_pairs"},
    "object": {"keys"},
    "env_name": set(),
}
_RESERVED_NAMES = {"con", "prn", "aux", "nul", *(f"com{i}" for i in range(1, 10)), *(f"lpt{i}" for i in range(1, 10))}
# Environment variables a pack must not name: they change how programs load code or find programs, or where uv,
# pip and the Python tools read packages, plugins and configuration (UV_INDEX, UV_ENV_FILE, PYTEST_ADDOPTS, ...).
_ENV_DENY = re.compile(r"(?:GIT_|PYTHON|LD_|DYLD_|NODE_|DOTNET_|PATH$|PATHEXT$|HOME$|USERPROFILE$|SYSTEMROOT$|COMSPEC$"
                       r"|PROGRAMDATA$|ALLUSERSPROFILE$|UV_|PIP_|PIPAPI_|PYTEST_|MYPY|RUFF_|COV_CORE_|VIRTUAL_ENV$"
                       r"|XDG_|APPDATA$|LOCALAPPDATA$|NPM_CONFIG_)")


# --- extra Bash grants for agents (pack data `agent_tools`, boundaries.json `tools`) ---
# The same grammar as a Bash(...) entry of an agent's tools: line: an exact command, or a prefix ending in " *". A
# grant must start with a program and subcommand pair from GRANT_PROGRAMS, a list reviewed in core: names are
# matched exactly, so no path, suffix (`.exe`, `3.12`), other case or leading option can reach a program that runs
# other programs or skip the hook's option checks. Adding a pair is a core change with a security review.
# Accepted residual: build and test commands run code from the repository, so an agent granted them can run what it
# has written into its own write lanes (see the pack's documentation).
# EXACT_GRANTS are whole commands a grant may name exactly (never as a prefix): read-only queries whose subcommand
# also has writing forms (`dotnet new <template>`, `npm audit fix`), so no pair for them is on the list.
_GRANT = re.compile(r"[A-Za-z0-9._/=-]+(?: [A-Za-z0-9._/=-]+)*(?: \*)?")
GRANT_MAX_LENGTH = 200
GRANT_MAX_AGENTS = 10
GRANT_MAX_ITEMS = 20
GRANT_PROGRAMS = {
    "dotnet": frozenset({"build", "test", "format"}),
    "npm": frozenset({"test"}),
    "git": frozenset({"status", "diff", "log", "show"}),
}
# `uv run` runs any program, so it never joins GRANT_PROGRAMS (ADR 0005, amendment of 2026-10-29). Its reviewed form
# is a fixed prefix: uv's locked run without a sync (nothing is installed or fetched; the pre-push `uv sync --locked`
# step is what checks the lock, since `--no-sync` skips that check) of one reviewed tool and, where the tool has one,
# its subcommand. The hook then allows only the tool's options that the installed packs select from
# REVIEWED_OPTIONS[tool].
UV_RUN_PREFIX = ("uv", "run", "--locked", "--no-sync", "--offline", "--no-python-downloads")
UV_RUN_TOOLS = {"ruff": (("check",), ("format", "--check")), "mypy": ((),), "pytest": ((),), "pip-audit": ((),)}
# Tools that take no bare word after the prefix: pip-audit reads a path argument as a project to install into a fresh
# environment from the index, so its options take their values only in the --name=value form.
UV_RUN_NO_ARGUMENTS = frozenset({"pip-audit"})
# Tools that run only from the repository root, never after a `cd`: pip-audit runs `python -m pip` from the working
# folder, so a `pip.py` or `pip/` there (bootstrap denies them only at the root) would run instead of pip.
UV_RUN_ROOT_ONLY = frozenset({"pip-audit"})
GRANT_PREFIXES = tuple(UV_RUN_PREFIX + (tool,) + sub for tool, subs in UV_RUN_TOOLS.items() for sub in subs)
EXACT_GRANTS = frozenset({
    "dotnet --version", "dotnet --list-sdks", "dotnet new list",
    "dotnet list package --vulnerable", "dotnet list package --vulnerable --include-transitive",
    "npm audit",
    "uv --version", " ".join(UV_RUN_PREFIX + ("ruff", "--version")), " ".join(UV_RUN_PREFIX + ("mypy", "--version")),
})


def uv_run_tool(words: list[str] | tuple[str, ...]) -> tuple[str, list[str]] | None:
    """(tool, the words after its reviewed prefix) when `words` start with a reviewed `uv run` prefix, else None."""
    for prefix in sorted(GRANT_PREFIXES, key=len, reverse=True):
        if tuple(words[:len(prefix)]) == prefix:
            return prefix[len(UV_RUN_PREFIX)], list(words[len(prefix):])
    return None


def grant_problem(grant: object) -> str | None:
    """Why one grant is not allowed, or None. Fixed text only, never the grant itself."""
    if not isinstance(grant, str) or not 1 <= len(grant) <= GRANT_MAX_LENGTH or not _GRANT.fullmatch(grant):
        return "a grant must be an exact command or a prefix ending in ' *' (letters, digits, ._/=- only)"
    if grant in EXACT_GRANTS:
        return None
    words = grant.split(" ")
    if uv_run_tool(words) is not None:
        return None
    if len(words) < 2 or words[1] not in GRANT_PROGRAMS.get(words[0], ()):
        return "a grant must start with a program and subcommand pair from the reviewed list in core"
    return None


def grants_problem(value: object) -> str | None:
    """Why a whole {agent: [grant, ...]} map is not allowed, or None."""
    if not isinstance(value, dict) or len(value) > GRANT_MAX_AGENTS:
        return f"must map at most {GRANT_MAX_AGENTS} agent names to lists of grants"
    for agent, grants in value.items():
        if not isinstance(agent, str) or not _NAME.fullmatch(agent):
            return "an agent name is not allowed"
        if not isinstance(grants, list) or not 1 <= len(grants) <= GRANT_MAX_ITEMS or len(set(map(str, grants))) != len(grants):
            return f"each agent needs 1 to {GRANT_MAX_ITEMS} distinct grants"
        for grant in grants:
            problem = grant_problem(grant)
            if problem:
                return problem
    return None


# --- build and test steps a pack adds to the pre-push check (pack data `prepush_steps`) ---
# Data, not code: each step is a title and an argument list that starts with a reviewed program and subcommand pair
# (STEP_PROGRAMS) or a reviewed `uv run` prefix (GRANT_PREFIXES). Other words are plain literals or a placeholder for
# one of the pack's own keys: `{key}` for a string key (one argument), `{key...}` for a string_list or pairs key (its
# items, each one argument).
_STEP_TITLE = re.compile(r"[A-Za-z0-9 ,.()'_-]{1,60}")
_STEP_WORD = re.compile(r"[A-Za-z0-9._/=:-]{1,60}")
STEP_PLACEHOLDER = re.compile(r"\{([a-z][a-z0-9_]{0,39})(\.\.\.)?\}")
STEP_MAX = 5
STEP_MAX_WORDS = 20
# Steps may also restore or sync packages, which agent grants never may (both fetch from the network).
STEP_PROGRAMS = {**GRANT_PROGRAMS, "dotnet": GRANT_PROGRAMS["dotnet"] | {"restore"}, "uv": frozenset({"sync"})}
_ENV_VALUE = re.compile(r"[A-Za-z0-9._-]{1,40}")


def _spec_at(keys: dict, path: object) -> dict | None:
    """The key spec at a dotted path such as `locked_restore.env_name`, or None."""
    if not isinstance(path, str) or not re.fullmatch(r"[a-z][a-z0-9_]{0,39}(\.[a-z][a-z0-9_]{0,39})?", path):
        return None
    first, _, rest = path.partition(".")
    spec = keys.get(first)
    if rest:
        spec = spec.get("keys", {}).get(rest) if isinstance(spec, dict) and spec.get("kind") == "object" else None
    return spec if isinstance(spec, dict) else None


def prepush_env_problem(env: object, keys: dict) -> str | None:
    """`prepush_env` = {"name_from": "<key path of an env_name key>", "value": "<literal>"}: when the project sets
    that key, every pre-push step of the pack runs with that variable set to the value."""
    if not isinstance(env, dict) or set(env) != {"name_from", "value"}:
        return "prepush_env must have exactly name_from and value"
    spec = _spec_at(keys, env["name_from"])
    if spec is None or spec.get("kind") != "env_name":
        return "prepush_env.name_from must name an env_name key of the pack"
    if not isinstance(env["value"], str) or not _ENV_VALUE.fullmatch(env["value"]):
        return "prepush_env.value is not allowed"
    return None


def steps_problem(steps: object, keys: dict) -> str | None:
    """Why a pack's `prepush_steps` list is not allowed, or None. Fixed text only."""
    if not isinstance(steps, list) or not 1 <= len(steps) <= STEP_MAX:
        return f"prepush_steps must hold 1 to {STEP_MAX} steps"
    for step in steps:
        if not isinstance(step, dict) or not {"title", "argv"} <= set(step) <= {"title", "argv", "only_with"}:
            return "a step must have title and argv, and may have only_with"
        if "only_with" in step and _spec_at(keys, step["only_with"]) is None:
            return "a step's only_with must name a key of the pack"
        if not isinstance(step["title"], str) or not _STEP_TITLE.fullmatch(step["title"]):
            return "a step title is not allowed"
        argv = step["argv"]
        if not isinstance(argv, list) or not 2 <= len(argv) <= STEP_MAX_WORDS or not all(isinstance(w, str) for w in argv):
            return f"a step argv must hold 2 to {STEP_MAX_WORDS} strings"
        if argv[1] not in STEP_PROGRAMS.get(argv[0], ()) and uv_run_tool(argv) is None:
            return "a step must start with a program and subcommand pair from the reviewed list in core"
        for word in argv[2:]:
            m = STEP_PLACEHOLDER.fullmatch(word)
            if m is None:
                if not _STEP_WORD.fullmatch(word):
                    return "a step word is not allowed"
                continue
            spec = keys.get(m.group(1))
            want = ("string_list", "pairs") if m.group(2) else ("string",)
            if not isinstance(spec, dict) or spec.get("kind") not in want:
                return "a step placeholder must name a key of the pack of the matching kind"
    return None


# --- what a pack may allow inside the hook's command checks, and the lint patterns it adds ---
# The engines stay in the core; a pack only selects from these reviewed lists. Without a pack that selects them, a
# listed agent may use no dotnet option and no npm verb. A new entry is a core change with a security review.
REVIEWED_OPTIONS = {"dotnet": frozenset({
    # MSBuild and the .NET CLI keep adding options that write a chosen file (-getResultOutputFile, --artifacts-path,
    # --packages), so the hook allows only listed options.
    "warnaserror", "c", "configuration", "v", "verbosity", "nologo", "no-restore", "no-incremental", "no-build",
    "no-dependencies", "f", "framework", "r", "runtime", "a", "arch", "os", "m", "maxcpucount", "interactive",
    "project", "filter-trait", "filter-not-trait", "filter-class", "filter-not-class", "filter-method",
    "filter-not-method", "filter-namespace", "filter-not-namespace", "treenode-filter", "list-tests",
    "minimum-expected-tests", "ignore-exit-code", "verify-no-changes", "include", "exclude", "severity",
    # shorter test output (Microsoft.Testing.Platform): an agent's tool shows only so much of it
    "no-progress", "no-ansi",
    "locked-mode", "force-evaluate", "vulnerable", "include-transitive", "outdated", "deprecated", "format",
    "highest-minor", "highest-patch", "version", "info", "list-sdks", "list-runtimes", "help", "h",
}),
    # The tools a reviewed `uv run` prefix starts (UV_RUN_TOOLS); the hook accepts `--name`, `--name=value` or exactly
    # `-X`, never a cluster or an attached value. Never what loads code, reads another configuration,
    # writes a chosen file or fetches: pytest -p, -c, -o/--override-ini, --rootdir, --confcutdir, --basetemp,
    # --junitxml; ruff --fix, --unsafe-fixes, --config, --output-file, --add-noqa, --watch; mypy --config-file,
    # --python-executable, --install-types, --cache-dir and the report options; pip-audit --fix, -o/--output,
    # --cache-dir and the index options.
    "pytest": frozenset({
        "q", "quiet", "v", "verbose", "x", "exitfirst", "k", "m", "maxfail", "tb", "r", "l", "showlocals",
        "no-header", "strict-markers", "strict-config", "co", "collect-only", "lf", "last-failed", "ff",
        "failed-first", "durations", "s", "capture", "color", "version", "h", "help",
    }),
    "ruff": frozenset({
        "output-format", "statistics", "exit-zero", "select", "ignore", "extend-select", "extend-ignore",
        "show-fixes", "diff", "quiet", "q", "no-cache", "version", "h", "help",
    }),
    "mypy": frozenset({
        "strict", "show-error-codes", "pretty", "no-error-summary", "show-column-numbers", "no-incremental",
        "warn-unused-ignores", "p", "package", "m", "module", "version", "h", "help",
    }),
    # no -s/--vulnerability-service: it can send the package list to a commercial service
    "pip-audit": frozenset({
        "strict", "desc", "format", "progress-spinner", "skip-editable", "l", "local", "aliases", "ignore-vuln",
        "version", "h", "help",
    }),
}
# No -v/--version: npm reads a following true/false as that flag's value and the next word as the command.
REVIEWED_VERBS = {"npm": frozenset({"run", "run-script", "test", "ls", "outdated", "audit"})}
LINT_PATTERNS_MAX = 30
_LINT_MESSAGE = re.compile(r"[ -~]{1,200}")


def selection_problem(value: object, reviewed: dict[str, frozenset], what: str) -> str | None:
    """Why a pack's `{program: [names]}` selection is not allowed, or None."""
    if not isinstance(value, dict) or not value or not set(value) <= set(reviewed):
        return f"{what} must map reviewed programs to lists"
    for program, names in value.items():
        if not isinstance(names, list) or not 1 <= len(names) <= 200 or len(set(map(str, names))) != len(names):
            return f"{what} must hold 1 to 200 distinct names per program"
        if not all(isinstance(n, str) and n in reviewed[program] for n in names):
            return f"{what} may only select names from the reviewed list in core"
    return None


def _lint_patterns(entry: int, value: object) -> list[tuple[str, str, str | None]]:
    """A pack's lint patterns, [{pattern, message, allowed_if?}], each pattern held to the pack pattern rules."""
    if not isinstance(value, list) or not 1 <= len(value) <= LINT_PATTERNS_MAX:
        _pack_invalid(entry, f"lint_patterns must hold 1 to {LINT_PATTERNS_MAX} items")
    out = []
    for item in value:
        if not isinstance(item, dict) or not {"pattern", "message"} <= set(item) <= {"pattern", "message", "allowed_if"}:
            _pack_invalid(entry, "a lint pattern must have pattern and message, and may have allowed_if")
        if not isinstance(item["message"], str) or not _LINT_MESSAGE.fullmatch(item["message"]):
            _pack_invalid(entry, "a lint message is not allowed")
        _pack_pattern(entry, item["pattern"])
        if "allowed_if" in item:
            _pack_pattern(entry, item["allowed_if"])
        out.append((item["pattern"], item["message"], item.get("allowed_if")))
    return out


def _pack_invalid(entry: int, reason: str) -> None:
    raise ProjectConfigError("pack-invalid", f"has an installed pack (packs entry {entry}) whose data file is "
                                             f"invalid ({reason})") from None


def _packs(value: object, packs_dir: Path | None) -> dict:
    """Check every installed pack; returns what the packs add, merged: agent grants, pre-push steps and environment
    (per pack, in the order the packs are declared), allowed options and verbs, and lint patterns."""
    packs = _object(value, "packs")
    if len(packs) > 10:
        _invalid("packs", "must hold 0 to 10 items")
    grants: dict[str, set[str]] = {}
    steps: dict[str, list[dict]] = {}
    envs: dict[str, dict] = {}
    options: dict[str, set[str]] = {}
    verbs: dict[str, set[str]] = {}
    lint: list[tuple[str, str, str | None]] = []
    opt_ins: list[tuple[str, str]] = []  # (key path, GitHub feature) for each set key whose spec names a feature
    for entry, (name, config) in enumerate(packs.items(), start=1):
        _match(name, _PACK_NAME, "packs", "a pack name")
        if name in _RESERVED_NAMES:
            _invalid("packs", "must not use a reserved device name")
        spec = _load_pack_data(name, packs_dir, entry)
        _check_object(_object(config, f"packs entry {entry}"), spec["keys"], f"packs entry {entry}")
        for agent, items in spec.get("agent_tools", {}).items():
            grants.setdefault(agent, set()).update(items)
        if "prepush_steps" in spec:
            steps[name] = spec["prepush_steps"]
        if "prepush_env" in spec:
            envs[name] = spec["prepush_env"]
        for program, names in spec.get("allowed_options", {}).items():
            options.setdefault(program, set()).update(names)
        for program, names in spec.get("allowed_verbs", {}).items():
            verbs.setdefault(program, set()).update(names)
        lint += spec.get("_lint_patterns", [])
        opt_ins += [(f"packs entry {entry}.{key}", sub["github_feature"]) for key, sub in spec["keys"].items()
                    if "github_feature" in sub and key in config]
    return {"agent_tools": {agent: sorted(items) for agent, items in sorted(grants.items())},
            "prepush_steps": steps, "prepush_env": envs,
            "allowed_options": {p: sorted(n) for p, n in sorted(options.items())},
            "allowed_verbs": {p: sorted(n) for p, n in sorted(verbs.items())},
            "lint_patterns": lint, "_feature_opt_ins": opt_ins}


def _load_pack_data(name: str, packs_dir: Path | None, entry: int) -> dict:
    if packs_dir is None:
        raise ProjectConfigError("pack-missing", "declares a pack but the pack data folder is not available")
    path = Path(packs_dir) / f"{name}.json"
    try:
        mode = os.lstat(path).st_mode
    except FileNotFoundError:
        raise ProjectConfigError("pack-missing", f"declares a pack (packs entry {entry}) whose data file in "
                                                 ".claude/packs/ is missing") from None
    except OSError:
        _pack_invalid(entry, "unreadable")
    if stat.S_ISLNK(mode) or not stat.S_ISREG(mode):
        _pack_invalid(entry, "not a regular file")
    try:
        with open(path, "rb") as fh:
            if not stat.S_ISREG(os.fstat(fh.fileno()).st_mode):  # the file was swapped after the check above
                _pack_invalid(entry, "not a regular file")
            raw = fh.read(PACK_MAX_BYTES + 1)
    except OSError:
        _pack_invalid(entry, "unreadable")
    if len(raw) > PACK_MAX_BYTES:
        _pack_invalid(entry, "too large")
    try:
        text = raw.decode("utf-8-sig")
    except UnicodeDecodeError:
        _pack_invalid(entry, "not UTF-8 text")
    if _nesting_depth(text) > PROJECT_MAX_DEPTH:
        _pack_invalid(entry, "nested too deep")
    try:
        spec = json.loads(text, object_pairs_hook=_no_duplicate_keys)
    except (_DuplicateKey, ValueError, RecursionError):
        _pack_invalid(entry, "not valid JSON or a duplicate key")
    if not isinstance(spec, dict) \
            or not {"schema", "name", "keys"} <= set(spec) <= PACK_TOP_KEYS \
            or spec["schema"] != 1 or spec["name"] != name or not isinstance(spec["keys"], dict) \
            or len(spec["keys"]) > PACK_MAX_KEYS:
        _pack_invalid(entry, "wrong top level")
    if "agent_tools" in spec and grants_problem(spec["agent_tools"]):
        _pack_invalid(entry, "agent_tools: " + grants_problem(spec["agent_tools"]))
    for key, sub in spec["keys"].items():
        if not isinstance(key, str) or not _NAME.fullmatch(key.replace("_", "-")):
            _pack_invalid(entry, "a key name is not allowed")
        _check_spec(entry, sub, 1)
    if "prepush_env" in spec and prepush_env_problem(spec["prepush_env"], spec["keys"]):
        _pack_invalid(entry, prepush_env_problem(spec["prepush_env"], spec["keys"]))
    if "prepush_steps" in spec and steps_problem(spec["prepush_steps"], spec["keys"]):
        _pack_invalid(entry, steps_problem(spec["prepush_steps"], spec["keys"]))
    for field, reviewed in (("allowed_options", REVIEWED_OPTIONS), ("allowed_verbs", REVIEWED_VERBS)):
        if field in spec and selection_problem(spec[field], reviewed, field):
            _pack_invalid(entry, selection_problem(spec[field], reviewed, field))
    if "lint_patterns" in spec:
        spec["_lint_patterns"] = _lint_patterns(entry, spec["lint_patterns"])
    return spec


def _pack_pattern(entry: int, text: object) -> None:
    if not isinstance(text, str) or not 1 <= len(text) <= 300 or _INLINE_FLAG.search(text) \
            or _GROUP_REFERENCE.search(text.replace("\\\\", "")):
        _pack_invalid(entry, "a pattern is not allowed")
    try:
        re.compile(text)
    except (re.error, OverflowError, RecursionError, ValueError):
        _pack_invalid(entry, "a pattern does not compile")
    if _repeats_ambiguously(text):
        _pack_invalid(entry, "a pattern repeats an alternation or another repeat")


def _check_spec(entry: int, spec: object, depth: int) -> None:
    if not isinstance(spec, dict) or spec.get("kind") not in PACK_KINDS or depth > 3:
        _pack_invalid(entry, "a key spec is not allowed")
    kind = spec["kind"]
    if not set(spec) <= {"kind", "required", "github_feature"} | _KIND_PARAMS[kind]:
        _pack_invalid(entry, "a key spec has an unknown parameter")
    if "github_feature" in spec and not (isinstance(spec["github_feature"], str) and _NAME.fullmatch(spec["github_feature"])):
        _pack_invalid(entry, "github_feature must name a feature of the GitHub feature map")
    if "required" in spec and not isinstance(spec["required"], bool):
        _pack_invalid(entry, "required must be true or false")
    for param in ("pattern", "item_pattern", "value_pattern"):
        if param in spec:
            _pack_pattern(entry, spec[param])
    for param in ("min", "max", "min_items", "max_items", "max_pairs", "max_length"):
        if param in spec and (isinstance(spec[param], bool) or not isinstance(spec[param], int)
                              or not 0 <= spec[param] <= 1000):
            _pack_invalid(entry, "a limit is not allowed")
    if "unique" in spec and not isinstance(spec["unique"], bool):
        _pack_invalid(entry, "unique must be true or false")
    if spec.get("min", 0) > spec.get("max", 1000) or spec.get("min_items", 0) > spec.get("max_items", 1000):
        _pack_invalid(entry, "a minimum is above its maximum")
    if kind == "string" and "pattern" not in spec:
        _pack_invalid(entry, "a string key needs a pattern")
    if kind == "pairs":
        options = spec.get("options")
        if not isinstance(options, list) or not 1 <= len(options) <= 50 \
                or not all(isinstance(o, str) and 1 <= len(o) <= 60 for o in options) or "value_pattern" not in spec:
            _pack_invalid(entry, "pairs need options and a value pattern")
    if kind == "object":
        keys = spec.get("keys")
        if not isinstance(keys, dict) or not 1 <= len(keys) <= PACK_MAX_KEYS:
            _pack_invalid(entry, "an object spec needs keys")
        for key, sub in keys.items():
            if not isinstance(key, str) or not _NAME.fullmatch(key.replace("_", "-")):
                _pack_invalid(entry, "a key name is not allowed")
            _check_spec(entry, sub, depth + 1)


def _check_object(obj: dict, keys: dict, path: str) -> None:
    for key, sub in keys.items():
        if sub.get("required", False) and key not in obj:
            raise ProjectConfigError("missing-key", f"is missing the required key {key} in {path}")
    if any(key not in keys for key in obj):
        raise ProjectConfigError("unknown-key", f"has a key that is not in the schema in {path}")
    for key, value in obj.items():
        _check_value(value, keys[key], f"{path}.{key}")


def _check_value(value: object, spec: dict, path: str) -> None:
    kind = spec["kind"]
    if kind == "string":
        _match(_string(value, path, spec.get("min", 1), spec.get("max", 200)), re.compile(spec["pattern"], re.S),
               path, "the allowed form")
    elif kind == "env_name":
        name = _string(value, path, 1, 64)
        _match(name, _ENV_NAME, path, "an upper-case environment variable name")
        if _ENV_DENY.match(name):
            _invalid(path, "must not name an environment variable that changes how programs load or run")
    elif kind == "string_list":
        items = _strings(value, path, spec.get("min_items", 0), spec.get("max_items", 50), spec.get("unique", False),
                         spec.get("max_length", 200))
        if "item_pattern" in spec:
            pattern = re.compile(spec["item_pattern"], re.S)
            for i, item in enumerate(items):
                _match(item, pattern, f"{path}[{i}]", "the allowed form")
    elif kind == "pairs":
        flat = _strings(value, path, 0, spec.get("max_pairs", 20) * 2, length=100)
        if len(flat) % 2:
            _invalid(path, "must hold option and value pairs")
        pattern = re.compile(spec["value_pattern"], re.S)
        for i in range(0, len(flat), 2):
            if flat[i] not in spec["options"]:
                _invalid(f"{path}[{i}]", "must be one of the allowed options")
            _match(flat[i + 1], pattern, f"{path}[{i + 1}]", "the allowed form")
    else:  # object
        _check_object(_object(value, path), spec["keys"], path)
