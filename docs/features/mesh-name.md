---
type: feature
status: partial
platforms: [windows, android, linux, macos]
tier: either
code:
  - src/CoreLib/Identity/PeerRegistry.cs
updated: 2026-08-23
---

# Mesh name

A set of devices is called something, like "Surya's Mesh", and every head says that rather than
naming whichever device happened to answer.

## Why it exists

With three devices, showing a peer name picks one arbitrarily and reads as though the app pairs
with a single machine.
"Connected to MSI-SURYANSHU" is wrong the moment there are three of you.

## How it works

There is no coordinator to hold the name, so the rule is that the device which starts the mesh
names it and devices that join adopt it.

It travels in the [[pairing]] code and in both hellos, and is adopted **only** by a device that
has none of its own.
That last clause is what stops two devices that disagree from overwriting each other in a loop.

Lives in `src/CoreLib/Identity/PeerRegistry.cs`.

## The finding behind the second delivery route

**A name that reaches a device only at pairing time never reaches one already paired.**
The mesh name went in the QR code first, and every device that had paired before that shipped sat
there calling it "your mesh" for ever.
It is in both hellos now.

## What is still open

**Renaming is local.**
It propagates on joining and not afterwards, because every simple rule for which of two names wins
either ping-pongs or lets a stale device overwrite the rest.
A last-changed timestamp in the hello would fix it, and that is the open decision recorded in
`HANDOFF.md`.

## See also

[[pairing]] · [[peer-registry]] · [[wire-formats]]
