---
type: meta
updated: 2026-08-23
---

# How to use this vault

For agents, and for humans acting like one.

## Reading

Start at [[Home]] or [[platform-matrix]], follow the links, and stop when you have the answer.
Do not read the whole vault.
The whole point of it is that three notes and a file path beat 67 KB of `AGENTS.md` plus
`HANDOFF.md` read in full.

Every note has the same four sections, in the same order:

1. **Where it lives** - the files that actually implement it.
2. **Which heads have it** - because this differs per platform far more than anyone expects.
3. **How it works** - short, and links out to the mechanism notes rather than repeating them.
4. **Why it is shaped this way, and what is still open** - the part that is not in the code.

## Writing

- **One note, one thing.** A feature, a mechanism, a head, or a reference page.
- **A `reference/` page may restate the code**, because being exact is its job: byte layouts,
  constants, timings. Everywhere else, cite the file instead.
- **Never restate code.** Cite `src/CoreLib/Transport/MeshLinks.cs` and let the reader jump.
- **Never restate `AGENTS.md` or `HANDOFF.md`.** Link to the heading.
  Those two are the source of truth for rules and findings; this vault indexes them.
- **Link generously.** A wikilink to a note that does not exist yet is a to-do, not an error -
  Obsidian shows it as unresolved, which is a useful list to work from.
- **Update the frontmatter.** `status` and `platforms` are what make this vault worth having;
  a stale one is worse than none. The fields are defined in [[note-schema]].
- **One sentence per line.** It keeps diffs readable, which is the whole reason this is in git.

## Updating

A feature note changes in the same commit as the feature.
That is the entire reason this vault lives in the repo rather than in a personal Obsidian folder:
if it drifts from the code it is worse than nothing, and the only reliable defence against drift
is that a reviewer sees both diffs side by side.

If you change a file listed under **Where it lives** in any note, open that note before you commit.

## What does not go here

- Findings and war stories. Those are `HANDOFF.md`.
- Rules and prohibitions. Those are `AGENTS.md`.
- Anything secret. This folder is public with the repo.
