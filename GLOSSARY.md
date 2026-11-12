# Glossary

| Term | Meaning | Avoid |
| --- | --- | --- |
| **Lancache** | The caching proxy on the LAN that the app fills. Found via LAN DNS, overridable with `LANCACHE_IP`. | cache server, proxy |
| **Prefill** | Downloading game content through the Lancache and discarding it, so the Lancache holds it for later clients. | pre-download, warm-up |
| **Module** | One game platform integration (Steam, Battle.net, Epic) implementing `IPrefillModule`. Registered at compile time via DI. | plugin, provider, platform adapter |
| **Game** | An installable title a Module can prefill (Steam app, Battle.net product, Epic catalog item). | app, title, product (except in module-internal code) |
| **Account** | The single logged-in user identity of a Module. Battle.net has none. | user, profile |
| **Selection** | Which Games a Module prefills: an explicit list plus optional dynamic rules. | wishlist |
| **Dynamic selection** | A rule resolved at Job start: *all owned*, *recently played*, *top N popular*. | smart list |
| **Job** | One run of one Module's Prefill. Queued, run, cancelled, recorded. | task, run |
| **Job queue** | The single global FIFO of Jobs; at most one Job runs at a time. | scheduler |
| **Job result** | Per-Game outcome of a Job (up to date / downloaded / failed + reason) plus bytes, duration, speed. | report, summary |
| **Schedule** | A cron expression plus a set of Modules; when it fires it enqueues one update-only Job per Module, skipping Modules that already have a queued or running Job. | timer, cron job |
| **Depot** | (Steam) A content bucket of a Game, possibly OS-specific. Up-to-date state is tracked per Depot. | — |
