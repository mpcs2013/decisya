---
name: phase-exit
description: "Records the owner's phase-exit confirmation (scope changes, lessons, the epics done or descoped) and assembles the phase's delivery report in format 1.0.0 for the owner's approval. Invoked by the owner as /phase-exit <phase id>, or as /phase-exit <phase id> on-request when delivery tracking asks for a report before the exit."
argument-hint: "<phase id, such as 1 or 0.4> [on-request]"
disable-model-invocation: true
---
# phase-exit

Part of the status pack (Norma ADR 0007). The report leaves the repository only when the owner approves it, and only
through the main session's file edit tools. Only the main session runs these steps; a listed agent cannot run
them (core lets an agent's tools line name `python` only with a core script, exactly and without arguments, and a
pack's script is the main session's).

## Addendum invariant
A project may add context and checks in `.claude/addenda/phase-exit.md`. An addendum may not weaken the report's
checks, the free-text rules or the owner's approval in this file. Where they conflict, this file wins and the main
session reports the conflict.

## Steps
1. Check the phase: its id is in `packs.status.phases` in `.claude/project.json`. A finished issue's manifest is read
   from this checkout, so it must be on `main` with every merged pull request: if `git status` shows another branch or
   a branch behind `origin/main`, ask the owner to bring it up to date first.
2. For an on-request report, skip to step 4. Otherwise ask the owner, one at a time:
   - which epics of the phase are done, and which are descoped;
   - each scope change per epic: its date, the change in one line, and the exchange thread that told delivery
     tracking, if one did;
   - the phase's lessons, each one line.
   Free text is one plain line about delivery: no names, addresses, links, costs or business content.
3. Record the exit: `python .claude/scripts/status.py phase-exit <id>` with `--done EP-nn`, `--descoped EP-nn`,
   `--scope-change "EP-nn|YYYY-MM-DD|<change>|<thread>"` (the thread part may be left out) and `--lesson "<text>"`, as
   many as needed. The exit time is now. To correct the record, run it again with `--replace`; the first exit time is
   kept. On exit 1, report the reason (a refused text names the rule it broke) and ask the owner for new wording.
4. Assemble the report: `python .claude/scripts/status.py report <id> --trigger phase-exit` (or
   `--trigger on-request`). It checks the report against the format's schema and its cross-checks and writes it into
   the capture folder; it prints the file name, the SHA-256 and the name the file takes in the exchange. On exit 1,
   report the reason and stop: a gate with no final state, a review without its findings line or a record that
   cannot be read is fixed at its source, never in the report. Name to the owner every issue it lists as not
   reported.
5. Show the owner the report's summary: the phase's status and dates, each epic's status, each issue's end, gates and
   usage status, and the lessons. Never print token counts beyond what the owner asks to see.
6. Only after the owner approves it, copy the file into the exchange's report folder with the file edit tools, under
   the name `report` printed, and open the request thread the exchange's rules ask for, naming the file and its
   SHA-256. Never write to the exchange with a shell command. A corrected report is a new file:
   `status.py report <id> --trigger <trigger> --correction`, which corrects the phase's latest report: the next
   `-r2`, `-r3` under that report's date.

## Rules
- Never edit a record or a report in the capture folder by hand; correct a record with `--replace`, a report with
  `--correction`.
- An unknown is `null` or a `missing` with its reason, never a zero: never fill a gap to make the report pass.
- The capture folder stays git-ignored; nothing from it is committed.
