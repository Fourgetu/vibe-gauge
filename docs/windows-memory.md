# Windows memory optimization (2026-09-28)

## Changes

- No buffer allocation for unchanged JSONL streams. Active readers reuse a
  64 KiB pooled buffer instead of allocating 1 MiB per call; returned buffers
  are cleared. Irrelevant lines are filtered before UTF-8 decoding.
- The incremental cache is written only after changes, with retries on write
  failure. No history is deleted or truncated and the cache format is unchanged.
- History aggregation uses cached records directly. File-qualified IDs are
  generated only for displayed Codex turns. Daily/model/hour totals are built
  in one pass without retaining multiple grouped copies of the records.
- Codex quota scanning caches small parsed results by file length, modification
  time, and creation time. Each refresh still checks for changes, new/deleted
  files, and the existing 48-hour cutoff. Changed files use a bounded, pooled
  tail buffer and skip unrelated lines before UTF-8 decoding. Cached entries
  never retain conversation text or JSON documents; buffers are cleared on return.
- Unchanged collection rows retain their existing controls. Hidden statistics,
  sessions, and network panels update when selected, using the latest snapshot.
- WMI results (including paging metrics), current-process handles, and owned
  Task Scheduler COM objects are disposed deterministically.
- The five-second refresh interval, provider monitoring, API proxy, themes,
  total-token calculations, and top-edge animation are unchanged.
- No forced garbage collections, working-set trimming, GC tuning, or reduced
  monitoring frequency are used in the application.

## Repeatable benchmark

`windows/tests/MemoryBench` creates 20,000 synthetic history records. The same
fixture was run against the previous core assembly and the optimized one.
Before/after results have the identical statistics checksum:

`89208C171DF5815E06ECAD5D000CD40A023851B432E66F052794B9B433712DDF`

| Operation | Bytes allocated before/run | After/run | Before ms/run | After ms/run |
| --- | ---: | ---: | ---: | ---: |
| Unchanged JSONL reader | 1,048,664 | 0 | 0.025 | <0.001 |
| Unchanged usage scan | 9,450,742 | 2,340,826 | 116.323 | 19.953 |
| Unchanged session scan | 10,548,960 | 55,534 | 7.241 | 4.926 |
| Unchanged quota scan | 9,376,916 | 9,994 | 22.444 | 1.991 |
| Statistics, 20,000 records | 1,592,483 | 53,027 | 7.405 | 2.645 |

These figures measure allocation pressure and elapsed time, not permanent RAM
savings. The reports are `memory-before-quota.json` and `memory-after-quota.json`.
The earlier ui5 build, without the quota-tail fix, did not reduce resident RAM
in a paired hidden-window test (56-60 MiB before, 60-62.5 MiB after).

## Live diagnostics

Only aggregate System.Runtime counters were collected, never a heap dump.
Separate 24-second captures on this machine showed:

| Metric | ui5, before quota fix | ui6, after quota fix |
| --- | ---: | ---: |
| Mean allocation rate | 16.69 MiB/s | 6.64 MiB/s |
| Largest two-second allocation sample | 91.97 MiB | 31.51 MiB |
| Mean GC committed memory | 83.38 MiB | 68.28 MiB |

These are sequential live observations, not an identical-workload benchmark.
The initial ui6 private working set was 106-124 MiB at 37-98 seconds after
startup, compared with 132-162 MiB shortly before replacing ui5. Short startup
samples alone do not prove sustained savings: the instrumented ui6 process
later reached about 138 MiB. WPF/runtime overhead, active logs, page selection,
GC timing, and the diagnostic session itself affect the result. Do not report
allocation savings as a percentage reduction in Task Manager RAM.

The final ui7 build, which also releases paging-query and current-process
handles, was measured without a runtime counter session attached:

| Private working set | Before replacement (ui5) | Final build (ui7) |
| --- | ---: | ---: |
| Samples | 4 | 16 |
| Minimum | 131.71 MiB | 111.52 MiB |
| Maximum | 161.78 MiB | 142.36 MiB |
| Mean | 145.74 MiB | 127.63 MiB |

The ui7 samples cover 41-204 seconds after launch; a further check at about
5.5 minutes was 142.47 MiB. Different process ages and active workloads mean
this is a short live check, not a promise of a fixed long-term reduction.
All samples showed a responsive GUI. The existing proxy PID and start time
remained unchanged, and its health check stayed successful.

`MemoryBench --serialization` also checks actual stream write sizes with
20,000 synthetic records. The current synchronous serializer already writes
in roughly 14.8 KB chunks (365 writes for 5,377,781 bytes), so cache persistence
was not changed to an asynchronous implementation merely on speculation.

## Verification

- 170 automated tests passed, including unchanged cache writes, UTF-8/CRLF and
  incomplete-line handling, date-specific statistics, retained row identity,
  and the previous animation checks. Added quota checks cover append, same-size
  rewrite, truncation, deletion, the 48-hour cutoff, incomplete JSON, BOMs,
  line endings, multiple files, and bounded tails.
- Captures of all five tabs verify that deferred panels still show current
  information. Native checks cover acrylic, theme switching, moving/resizing,
  and repeated top-edge hide/reveal cycles.
- Local reports are under `windows/artifacts/memory-*.json` and captures under
  `windows/artifacts/memory-ui*-checks`; fixtures never use real credentials.

The pre-release measurements above used the local build in
`windows/artifacts/release-v1.4.0-ui7`. The same optimizations are included in
[v1.5.0](releases/v1.5.0.md), with versioned packages built separately. No
credentials, user histories, proxy settings, or monitoring workloads were removed.
