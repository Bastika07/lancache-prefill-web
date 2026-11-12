# Steam fixtures

Hand-written in Steam's PICS appinfo text format (VDF), modelled on real appinfo dumps.
**Not recorded** — recording needs a logged-in Steam session (#9); replace with recorded
dumps once that exists. App and depot ids are made up, except the excluded redistributable
depot 228986, which is real and on the exclusion list.

| App | What it exercises |
|---|---|
| 1000 Example Game | every depot variant the filter knows |
| 1100 Example Game – Soundtrack (DLC, owned) | DLC depots are added to the game |
| 1200 Example Game – Expansion (DLC, not owned) | unowned DLC depots are left out |
| 2000 Shared Runtime | target of a `depotfromapp` link |
| 228980 Steamworks Common Redistributables | owned by every account, like on real Steam, so only the exclusion list keeps depot 228986 out |
| 3000 Example Tool, 3100 Example Mod, 3200 Delisted Game, 480 Spacewar | availability filter |
