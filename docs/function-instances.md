# Plan: function instances (memory, isolation, scale)

**Status:** done in Winche.Functions 2.2, Winche Server 2.2 and the CLI 2.3. The Instances view shows the server that
answered (not every server together).

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
| `reservedInstances` | 0 | Runs of the project's `MaxConcurrency` kept for this function, so busy ones can't starve it (others share what's left). The total reserved can't pass the project's limit. |
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
  function can't take every slot of the project. `reservedInstances`: the project's slots are split into each
  function's reserved ones and a shared rest; a run takes its function's reserved slot first, then a shared one.

### What the console shows (a function's dialog: **Instances**)

Instances now (busy, idle), each one's heap, runs since it started; recent retirements (memory, timeout, crash) and
process restarts; the function's limits in force and where each comes from (code, setting, default).

## In a container (AWS Fargate, Kubernetes)

A Fargate task has a fixed CPU and memory (e.g. 2 vCPU and 4 GB) and is one micro-VM: past its memory, the whole task
is killed (the server and every project with it). Several tasks behind a load balancer each run every project.

- **A memory budget from the container.** At start the server reads its container's limit (cgroup `memory.max`;
  `Winche__FunctionsMemoryMB` to set it outright) and keeps functions within a share of it (default 60%: the rest for
  the server, the databases' pools, and headroom). Every process's heap limit counts against it: the shared process's
  limit plus each isolated process started. An isolated process that wouldn't fit isn't started; its function runs in
  the shared process and the console says why. Settings asking for more than the budget are refused when saved, with
  the task's size in the message.
- **Instances are per task.** `minInstances` is kept loaded on every task (3 tasks with `minInstances: 1` hold 3);
  `maxInstances` and `reservedInstances` are per task too. The console's Instances view shows each task's, by task.
  Triggers and schedules already run once across tasks (queue claims); a deploy reaches every task within the sync
  interval.
- **Ready only when warm.** `/healthz` answers `starting` (503) until projects have started and the live versions'
  `minInstances` are loaded (the 2.1 warm-up), so the load balancer only sends a new task traffic once its first
  callers won't wait. A separate `/livez` answers as soon as the process runs (for the container's own check).
- **Stopping cleanly.** On SIGTERM (a deploy, a scale-in, a Spot interruption) the task stops taking new runs, lets
  runs in flight finish within the stop timeout (Fargate's `stopTimeout`, at most 120 s), then stops. Triggered and
  scheduled runs that couldn't finish are picked up by another task from the queue (their claims lapse); a callable
  cut off answers `unavailable`, which apps retry. Runs longer than the stop timeout should save progress as they go
  (already the rule for long work).
- **Scaling out.** ECS service auto scaling on CPU, memory or the load balancer's request count adds tasks; each new
  task warms up before it's ready. A new task takes tens of seconds (image, .NET start, warm-up): `minInstances` and
  a minimum task count keep the first calls quick, autoscaling handles the rest.
- **CPU.** Instances share the task's vCPUs; a project's `MaxConcurrency` default follows the task's CPU count (e.g.
  8 runs per vCPU) rather than a fixed 16.
- **What's the security boundary.** The task is the micro-VM: on one Winche Server, projects share it (separate Deno
  processes and workers, not separate VMs). Tenants that must not share a machine get separate servers.
- **Files and caches.** Bundles and Deno's cache go on the task's ephemeral storage (rebuilt on a new task); project
  files belong in S3 (the `/data` volume would need EFS, slower and shared).

## Steps

1. **Options and settings**: `memory`, `minInstances`, `maxInstances`, `reservedInstances`, `isolate` in the SDK's options and
   `FunctionInfo` (checked on deploy against the server's limits); the settings area gains per-function overrides and
   project defaults; the console's Settings tab edits them. Nothing is enforced yet.
2. **Instances per function**: pools by function (min warm, max at once, reserved slots, idle stop), heap reported
   after each run, retirement over `memory`, log lines. Tests with a function that grows, and with a busy function
   that mustn't starve one with reserved slots.
3. **Processes and the memory budget**: the container's limit read at start; the runtime runs several Deno processes
   per project (shared + isolated) within the budget, each with its heap limit;
   a crash fails only that process's runs (`resource-exhausted`) and restarts it. Tests: a runaway function in its own
   process doesn't touch others; in the shared one, others' runs in flight fail and the next ones work.
4. **Console**: the Instances view (live counts, heap, retirements, restarts; by server when there are several), and
   the limits in force with their source. The server's limits and memory budget in Server settings.
5. **Running in a container**: `/healthz` ready only when warm, `/livez`, a clean stop on SIGTERM (no new runs, runs
   in flight finish within the stop timeout), `MaxConcurrency` from the CPU count; a Fargate example (task
   definition, health checks, stop timeout, autoscaling) in the server's README.
6. **CLI emulator, README, release** (Functions 2.2, Server 2.2, CLI 2.3).

## Open questions

- Several runs at once in one instance (a `concurrency` option) for I/O-bound functions: later, if one instance per
  run turns out to cost too much memory.
- CPU: there's no per-worker CPU limit in Deno; a busy loop holds its instance until its timeout. Isolated processes
  could be started with a lower OS priority.
