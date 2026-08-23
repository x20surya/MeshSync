# The Mesh Sync vault

This folder is an [Obsidian](https://obsidian.md) vault as well as a documentation folder.
Open `docs/` as a vault and the links, the graph and the search all work; read it as plain
Markdown on GitHub and nothing is lost.

It exists because the three root documents each answer a different question and none of them
answers "what is this feature, where does it live, and which platforms have it".

| Document | Question it answers |
|---|---|
| [README.md](../README.md) | What is Mesh Sync, from outside |
| [AGENTS.md](../AGENTS.md) | What are the architecture rules, and what must never be broken |
| [HANDOFF.md](../HANDOFF.md) | What did we learn the hard way, in the order we learned it |
| **this vault** | What is each feature, where is it implemented, and what still does not work |

Those three stay the source of truth for rules and history.
This vault is the map over the top of them, and every note links back rather than restating.

## Start here

- [[Home]] is the map of content.
- [[platform-matrix]] is the single table saying which head has which feature.
- `reference/` holds the exact, code-derived detail: wire formats byte by byte, every timeout in
  the project, the test coverage map, and how to build each head. Those pages were written by
  reading the source rather than by paraphrasing the root documents, which is the difference
  between a page you can implement against and a page you can only orient with.
- [[_meta/vault-guide]] is how an agent is meant to use this, and is worth reading before writing
  a note into it.

## The one rule

**Code is the source of truth. These notes are an index over it.**
A note that restates what a file already says is a note that will be wrong in a month.
Every note carries the file paths instead, so the answer to "is this still true" is one jump away.
