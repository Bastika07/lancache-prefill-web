# lancache-prefill-web

A self-hosted web app that prefills your [Lancache](https://lancache.net) with games from **Steam**, **Battle.net** and **Epic Games**, on demand or on a schedule.

> Status: early development. See [docs/spec.md](docs/spec.md) for the v1 design.

Built on the excellent CLI tools by [tpill90](https://github.com/tpill90) — see [NOTICE](NOTICE).

## Running it with Docker

Images for amd64 and arm64 are published to `ghcr.io/bastika07/lancache-prefill-web` (`latest` from `main`, plus version tags).

1. While the repository is private, log the Docker host in once with a GitHub personal access token that has `read:packages`:
   ```sh
   echo <token> | docker login ghcr.io -u <github-user> --password-stdin
   ```
2. Next to [`docker-compose.yml`](docker-compose.yml), create a `.env`:
   ```sh
   ADMIN_PASSWORD=choose-a-long-password
   # Optional: skip DNS detection and use this Lancache.
   LANCACHE_IP=192.168.1.10
   TZ=Europe/Berlin
   ```
3. `docker compose up -d` and open `http://<host>:8080`.

| Variable | Required | Meaning |
| --- | --- | --- |
| `ADMIN_PASSWORD` | yes | Password for the web UI. |
| `LANCACHE_IP` | no | Use this Lancache instead of finding it via the LAN DNS. |
| `TZ` | no | Time zone for Schedules (default in the sample: `Europe/Berlin`). |
| `DATA_DIR` | no | Where state is kept inside the container (default `/data`). |

**The data volume.** `/data` holds the SQLite database, the Steam/Epic login tokens and the ASP.NET Core Data Protection key ring that encrypts those tokens. The key ring is stored unencrypted next to them, so **anyone with access to the volume can use the stored logins**. Treat it like a password file and back it up as a whole.

The container runs as the non-root user `1654`. The sample uses a named volume, which Docker makes writable for it. To use a host directory instead, give it to that user first: `chown -R 1654:1654 /path/to/data`.

The UI speaks plain HTTP on port 8080 and is meant for the LAN; put a reverse proxy with TLS in front of it if it should be reachable from elsewhere.

## License

MIT
