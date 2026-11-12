# 1. New repo; port upstream code instead of wrapping it

Date: 2026-11-12 · Status: accepted

## Context
The three `tpill90/*-lancache-prefill` CLIs share a CLI/console front end (`Spectre.Console`, `IAnsiConsole` in 16 files of SteamPrefill alone) wrapped tightly around their download logic. A web app replaces exactly that layer and treats all three platforms as equal Modules.

## Decision
Start a new MIT repo, `lancache-prefill-web`. Copy the upstream download logic once and port it: remove console coupling, report via `IProgress<T>`/`ILogger`. Credit upstream in `NOTICE` and per-file source headers. No submodules of the upstream repos.

## Consequences
- Clean seams and testable modules from day one.
- Upstream fixes (e.g. SteamKit2 protocol changes) are pulled in by hand; the fork `Bastika07/steam-lancache-prefill` stays as porting reference.
