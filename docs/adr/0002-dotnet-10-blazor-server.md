# 2. .NET 10 with Blazor Server

Date: 2026-11-12 · Status: accepted

## Context
Steam support depends on SteamKit2 (.NET only, best-maintained Steam client library). The Battle.net TACT/CASC handling exists as substantial, tuned C#. Rewriting either in another language would be most of the project's effort for no gain.

## Decision
.NET 10 (LTS; .NET 8 support ends Nov 2026) and Blazor Server for the UI — one language, one toolchain, live progress over the built-in SignalR circuit.

## Consequences
- Single slim container image.
- UI is server-rendered and stateful; fine for one admin on the LAN, not designed for many concurrent users.
