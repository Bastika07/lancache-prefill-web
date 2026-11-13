# lancache-prefill-web — v1 spec

Outcome of the architecture grilling on 2026-11-12. Terms as in `GLOSSARY.md`; the bigger decisions are recorded as ADRs in `docs/adr/`.

## Goal

One self-hosted web app replacing the three CLIs (`steam-`, `battlenet-`, `epic-lancache-prefill`): log in once per platform in the browser, pick Games, and let Schedules keep the Lancache warm.

## Deployment

- Single Docker container on the LAN next to the Lancache; one admin.
- UI protected by an admin password (`ADMIN_PASSWORD` env var, cookie login).
- State in one SQLite database (EF Core) in a mounted volume (`DATA_DIR`, default `/data`). Account tokens encrypted with ASP.NET Core Data Protection; key ring in the same volume.
- Lancache detected via LAN DNS as the upstream tools do; `LANCACHE_IP` overrides detection. The UI shows the detected Lancache and warns loudly when none is found.
- Logging: built-in .NET logging to stdout; each Job's log is also viewable in the UI.
- CI: GitHub Actions builds, tests and publishes a multi-arch (amd64 + arm64) image to `ghcr.io/bastika07/lancache-prefill-web`; a sample `docker-compose.yml` ships in the repo.

## Solution layout

`LancachePrefill.slnx`, .NET 10:

| Project | Responsibility |
| --- | --- |
| `LancachePrefill.Core` | `IPrefillModule`, Job queue, Schedules, Selections, Job results, settings, persistence |
| `LancachePrefill.Modules.Steam` | Port of `SteamPrefill` (SteamKit2) |
| `LancachePrefill.Modules.BattleNet` | Port of `BattleNetPrefill` (TACT/CDN) |
| `LancachePrefill.Modules.Epic` | Port of `EpicPrefill` |
| `LancachePrefill.Web` | Blazor Server host: auth, pages, live progress |
| `LancachePrefill.*.Tests` | xUnit tests per project |

## Module contract

```csharp
interface IPrefillModule
{
    string Id { get; }
    string DisplayName { get; }
    IModuleAuth? Auth { get; }   // null = no login (Battle.net)
    Task<IReadOnlyList<GameInfo>> ListGamesAsync(CancellationToken ct);
    Task<PrefillResult> PrefillAsync(PrefillRequest request, IProgress<PrefillProgress> progress, CancellationToken ct);
}
```

- `PrefillRequest`: the resolved Games, `UpdatesOnly`/`Force` flags, the module's settings.
- `PrefillProgress`: module-neutral — phase, current Game, bytes done/total.
- `PrefillResult`: per-Game status (up to date / downloaded / failed + reason), bytes, duration.
- Each Module exposes its own settings page:
  - Steam: operating systems (multi-select, union of Depots; also decides which Games are prefillable), max concurrent requests, language.
  - Battle.net: product list only.
  - Epic: max concurrent requests.
- Global settings: Lancache IP override, time zone, history retention.
- One Account per Module.

### Logins

- **Steam**: username + password, then a Steam Guard dialog (code entry or mobile-app approval); QR-code login as an alternative. Refresh token persisted.
- **Epic**: button opens the Epic login page; the user pastes the authorization code. Refresh token persisted.
- **Battle.net**: none.

## Jobs and Schedules

- One global FIFO Job queue; at most one Job runs at a time, across all Modules. Queued and running Jobs can be cancelled.
- Selections: explicit Games plus dynamic rules (*all owned*, *recently played*, *recently purchased*, *top N popular*), one saved Selection per Module.
  - The Job queue resolves the rules when the Job starts: explicit Games first, then each rule's Games, duplicates dropped. Enqueueing a rule the Module doesn't support is rejected; a Schedule then skips that Module.
  - A rule that fails (e.g. an unreachable popularity service) becomes a Job warning, kept in the history; the Job fails only if a rule failed and no Game is left.
- Schedules: any number, each a cron expression (Cronos) + set of Modules, run by a `BackgroundService` using `TimeProvider`. Firing enqueues one update-only Job per Module; a Module that already has a queued or running Job is skipped and the skip is recorded. Default: one nightly Schedule for all Modules.
  - Managed on the `/schedules` page (add, edit, delete, preview of the next run times); changes take effect at once, without a restart. A Schedule edited after its time today waits for its next occurrence. *Run now* fires a Schedule immediately, exactly like its cron time would, and is recorded as a firing.
- Job history: per-Game outcome, bytes, duration, average speed; pruned after 90 days.

## Out of scope for v1

Webhooks/notifications beyond the UI, importing CLI config files, multiple Accounts per Module, per-Schedule OS overrides, runtime-loaded plugins.

## Testing

Test-first, only at these seams:

1. **Job queue** (Core) with a fake `IPrefillModule`: ordering, one-at-a-time, cancellation, skip rules, Job results. No network.
2. **Scheduler** with `FakeTimeProvider`: cron firing, skip-on-overlap, replacing Schedules at runtime, run now. No real waiting.
3. **Module parsers/planners** with recorded fixtures (Steam manifests, TACT indexes, Epic API responses): "what needs downloading". Not the login.
4. **UI** with bUnit: login dialogs, the Job view, the Selection editor, the Schedule editor and Module settings forms. Pages that only compose these are not tested.

No live tests against Steam, Battle.net or Epic in CI; a manual smoke test covers that.

## Delivery order

1. Core + Web skeleton + Steam, porting the current Steam flow 1:1 (Games sequential, chunks parallel).
2. Steam pipeline: prefetch metadata/manifests of upcoming Games while one shared chunk downloader runs across Game boundaries. Benchmarked before/after on a real library. Done in #12: up to 8 Games in flight (4 planned in parallel), outcomes still reported in Game order; 30 small Games (2.4 GB, warm Lancache), measured on the Lancache host: 22.7 s → 6.4 s on average over six alternating runs each.
3. Battle.net module.
4. Epic module.
