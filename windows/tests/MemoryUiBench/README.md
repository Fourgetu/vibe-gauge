# WPF memory comparison

This isolated harness runs the actual WPF window and five-second coordinator
against a synthetic home. It switches tabs, languages and visibility over
72 seconds, recording allocation, CPU, GC and process counters every two
seconds. It does not start the normal tray/runtime or terminate another app.

First generate a fixture with `MemoryBench`, then publish this runner:

```powershell
dotnet run --project windows/tests/MemoryBench -c Release -- C:/Temp/vg-memory core C:/Temp/core.json
dotnet publish windows/tests/MemoryUiBench -c Release --self-contained true -p:PublishSingleFile=false -o C:/Temp/vg-ui
C:/Temp/vg-ui/MemoryUiBench.exe C:/Temp/vg-memory candidate C:/Temp/ui.jsonl
```

Keep a separate published directory for the baseline before editing product
code. Use the same fixture and distinct labels for before/after processes;
each label gets its own `ui-<label>` data directory. The fixture marker is
required and the current user's home is rejected. Use synthetic data only.

The JSONL `WorkingSet` includes shared pages and `Committed` means private
committed bytes. For Task Manager-style private resident memory, sample each
runner's PID with `Win32_PerfRawData_PerfProc_Process.WorkingSetPrivate` from
another process. Compare matched warmed-up time windows, repeat with launch
order reversed, and verify displayed totals. Allocation reductions are not
resident-memory reductions. Do not run builds or tests during sampling.

No forced GC, working-set trim, or GC tuning is applied. These short synthetic
runs cannot establish a real user's long-term memory ceiling.
