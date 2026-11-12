# 3. Modules are compile-time projects registered via DI

Date: 2026-11-12 · Status: accepted

## Decision
Each platform is a project in this repo implementing `IPrefillModule` and registered via dependency injection. No runtime plugin loading.

## Consequences
- Type-safe, simple, trimming/AOT-friendly.
- Third parties cannot drop in modules without a rebuild — acceptable for v1.
