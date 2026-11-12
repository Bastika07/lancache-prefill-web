# 4. One global Job queue, one Job at a time

Date: 2026-11-12 · Status: accepted

## Context
All Modules download over the same uplink into the same Lancache. Within a Job, downloads are already parallel at chunk level (`MaxConcurrentRequests`), which saturates the line.

## Decision
A single FIFO Job queue across all Modules; at most one Job runs at a time. Schedules enqueue update-only Jobs and skip a Module that already has a queued or running Job. Steam operating systems are a Module setting (union of Depots in one Job), not separate parallel Jobs.

## Consequences
- No bandwidth contention or interleaved progress; simple mental model.
- Throughput gains come from pipelining inside a Module (planned for Steam after v1), not from parallel Jobs.
