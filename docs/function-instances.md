# Plan: function instances (memory, isolation, scale)

## Why

Since 2.1 runs reuse warm workers, so a function answers in about 2 ms. Today:

- **No memory limit.** A worker's heap grows with whatever its module keeps (an unbounded list keeps growing from
  run to run) until the worker is retired for some other reason.
- **One process per project.** Every function of a project runs in the same Deno process. One worker's heap past
  V8's ceiling aborts that process: every run in flight in the project fails. Past the container's limit, the kernel
  may kill the server instead.
- **Scale is project-wide.** `MaxConcurrency` caps all runs together; a busy function can take every slot.
- **Nothing shows** how many workers a function has, how much memory they hold, or why one was replaced.

## The model

An **instance** is a worker with a function's code loaded, running one run at a time. Each function has its own
instances, its own limits, and a **process** it runs in (shared or its own).

### Per-function options (in code) and settings (in the console, over the code's)

| Option | Default | What |
| --- | --- | --- |
| `memory` | 256 (MB) | The most an instance's heap may hold after a run. Over it, the instance is retired (its memory freed); the next run gets a new one. |
| `minInstances` | 0 (1 for HTTP functions and callables) | Instances kept loaded even when idle: no wait for the first call. |
| `maxInstances` | 10 | The most runs of this function at once; more wait their turn (up to the timeout). |
| `isolate` | false | Its own process, so nothing it does can stop another function's runs. On automatically when `memory` is past the shared process's limit. |
| `timeoutSeconds` | (as now) | — |
| `fresh` | false | (as now) a new instance for every run. |

The console's per-function **Settings** tab changes each (saved with a reason, applied from the next run), like the
run time today. The project's defaults (Functions → Settings) and the server's limits apply where neither says.

### Server limits (Winche Server: Server settings; a library app: `FunctionsOptions`)

- The most memory a function may ask for (default 1024 MB) and the shared process's heap limit (default 1024 MB).
- The most processes per project (default 4: the shared one and three isolated) and runs at once per project
  (`MaxConcurrency`, as now).

### Processes

- **The shared process** runs every function that isn't isolated. Started with a heap limit
  (`--max-old-space-size`, from the server limit); if one run blows past it, that process restarts and its runs in
  flight fail with `resource-exhausted` ("ran out of memory"), not the whole project.
- **An isolated function's process** is started with its own heap limit (its `memory` plus room for the code), on
  first need, and stops after its instances have been idle a while (none kept unless `minInstances`).
- A process that dies is restarted on the next run; repeated crashes in a short time are logged loudly and shown in
  the console.

### Instances

- Pools per function and version: `minInstances` loaded when the version comes into force (as the warm-up does now)
  and kept; idle ones above that stop after 5 minutes.
- After each run the instance reports its heap. Over `memory`: retired, with a log line ("sendReport: instance
  retired at 312 MB, over its 256 MB"). A run that pushes its process past the process's limit crashes that process
  only.
- `maxInstances`: a run waits for a free instance of its function (counted against its timeout), so one busy
  function can't take every slot of the project.

### What the console shows (a function's dialog: **Instances**)

Instances now (busy, idle), each one's heap, runs since it started; recent retirements (memory, timeout, crash) and
process restarts; the function's limits in force and where each comes from (code, setting, default).

## Steps

1. **Options and settings**: `memory`, `minInstances`, `maxInstances`, `isolate` in the SDK's options and
   `FunctionInfo` (checked on deploy against the server's limits); the settings area gains per-function overrides and
   project defaults; the console's Settings tab edits them. Nothing is enforced yet.
2. **Instances per function**: pools by function (min warm, max at once, idle stop), heap reported after each run,
   retirement over `memory`, log lines. Tests with a function that grows.
3. **Processes**: the runtime runs several Deno processes per project (shared + isolated), each with its heap limit;
   a crash fails only that process's runs (`resource-exhausted`) and restarts it. Tests: a runaway function in its own
   process doesn't touch others; in the shared one, others' runs in flight fail and the next ones work.
4. **Console**: the Instances view (live counts, heap, retirements, restarts), and the limits in force with their
   source. The server's limits in Server settings.
5. **CLI emulator, README, release** (Functions 2.2, Server 2.2, CLI 2.3).

## Open questions

- Several runs at once in one instance (a `concurrency` option) for I/O-bound functions: later, if one instance per
  run turns out to cost too much memory.
- CPU: there's no per-worker CPU limit in Deno; a busy loop holds its instance until its timeout. Isolated processes
  could be started with a lower OS priority.
