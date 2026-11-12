# lancache-prefill-web

Modular .NET 10 / Blazor Server web app that prefills a [Lancache](https://lancache.net) for Steam, Battle.net and Epic Games. Spec: `docs/spec.md`. Vocabulary: `GLOSSARY.md`. Decisions: `docs/adr/`.

## Working rules

- Build and test with `dotnet build` / `dotnet test` from the repo root (`LancachePrefill.slnx`).
- Work test-first, at the seams listed in `docs/spec.md` → *Testing*. No live calls to Steam, Battle.net or Epic in tests.
- Code ported from the upstream `tpill90/*-lancache-prefill` projects keeps a header comment naming its source file; see `NOTICE`.
- No console UI libraries (Spectre.Console etc.) in ported code: report through `IProgress<T>` and `ILogger`.

## Agent skills

### Issue tracker

GitHub Issues on `Bastika07/lancache-prefill-web` via `gh`, always with an explicit `--repo`. See `docs/agents/issue-tracker.md`.

### Triage labels

Default five-role vocabulary (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`). See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `GLOSSARY.md` + `docs/adr/` at the repo root. See `docs/agents/domain.md`.
