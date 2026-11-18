// Winche functions runtime: started by the Winche server as one Deno process. It reads requests as JSON lines on
// stdin and answers on stdout. Runs happen in workers that may use the network but not the files, environment or
// programs of the machine. Each function has its own instances: workers with its code loaded, reused one run at a time.
// One that runs out of time, crashes, or holds more memory than its function allows after a run is stopped; idle ones
// go after a while, but a function's `minInstances` are kept. A function with `fresh: true` gets a new worker per run.
//
// In:  {t:"code", version, url, file, resources}         — a deployment's bundle file and its resources' files (once per version)
//      {t:"analyze", id, version}                     — list the exported functions and their triggers
//      {t:"invoke", id, version, name, arg, context, timeoutMs, memoryMb?, fresh?}
//      {t:"warm", id, version, functions: {name: count}} — keep that many instances of each function loaded (done when they are)
//      {t:"stats", id}                                — the instances now (answered as done, result: [...])
//      {t:"reply", id, callId, result | error}        — the answer to a @winche/admin call
//      {t:"forget", version}                          — drop a bundle (e.g. one only analyzed)
// Out: {t:"log", id, level, message} · {t:"call", id, callId, op, args} · {t:"ready"}
//      {t:"done", id, result | error, heapMb, retired?: {reason: "memory" | "timeout" | "crash", heapMb?, limitMb?}}

const bundles = new Map();   // version -> { url, file, resources: { path: file } }
const workers = new Map();   // request id -> Worker

const SANDBOX = { read: false, write: false, env: false, run: false, ffi: false, sys: false };

// What each worker runs first: the bridge for @winche/admin calls and console output, then the request.
const BOOT = `
import { AsyncLocalStorage } from "node:async_hooks";
// The run in progress, across its awaits: @winche/admin, secrets and resources (@winche/functions) find it here.
const runs = new AsyncLocalStorage();
globalThis[Symbol.for("winche.run")] = () => runs.getStore();
const pending = new Map();
let next = 1;
// V8's collector (the runtime is started with --expose-gc): kept here, not left for functions to call.
const collect = globalThis.gc;
try { delete globalThis.gc; } catch { /* not deletable: leave it */ }
// The run's memory limit, in bytes (0: none). After a run the heap is measured; over the limit, garbage is collected
// first so only what the function keeps counts.
let limitBytes = 0;
const done = (p) => {
  let heap = Deno.memoryUsage().heapUsed;
  if (limitBytes && heap > limitBytes && collect) { collect(); heap = Deno.memoryUsage().heapUsed; }
  postMessage({ ...p, t: "done", heap });
};
// A run's calls carry its id: one made after its run has finished (work it didn't await) is refused, never served
// as part of the worker's next run.
const callFor = (run) => (op, args) => new Promise((resolve, reject) => {
  const callId = next++;
  pending.set(callId, { resolve, reject });
  postMessage({ t: "call", run, callId, op, args });
});
const text = (args) => args.map((a) => {
  if (typeof a === "string") return a;
  if (a instanceof Error) return a.stack ?? a.message;
  try { return JSON.stringify(a); } catch { return String(a); }
}).join(" ");
for (const level of ["log", "info", "warn", "error", "debug"]) console[level] = (...a) => postMessage({ t: "log", level, message: text(a) });
// Request and response bodies cross to the server as base64.
const toBase64 = (bytes) => {
  let bin = "";
  for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(bin);
};
const fromBase64 = (text) => Uint8Array.from(atob(text), (c) => c.charCodeAt(0));
// Secrets the function lists (options.secrets), read from the console's encrypted store for this run.
const secretsOf = (s, fn) => ({
  get: (name) => {
    if (!Object.hasOwn(s, name)) throw Object.assign(new Error(fn + " doesn't list the secret " + name + ": give its handle in the function's options ({ secrets: [handle] }, the handle from secrets.define('" + name + "'))."), { code: "failed-precondition" });
    if (s[name] == null) throw Object.assign(new Error("The secret " + name + " isn't set: set it in the console (the Secrets page) or with: winche functions secrets:set " + name), { code: "failed-precondition" });
    return s[name];
  },
  has: (name) => Object.hasOwn(s, name) && s[name] != null,
});
// The deployment's resources folder: read-only files that came with the code (fonts, translations, templates).
const resourcePath = (path) => {
  let p = String(path ?? "");
  if (p.startsWith("./")) p = p.slice(2);
  while (p.startsWith("/")) p = p.slice(1);
  return p;
};
const resourcesOf = (files) => ({
  read: async (path) => { const f = files[resourcePath(path)]; return f ? await Deno.readFile(f) : null; },
  readText: async (path) => { const f = files[resourcePath(path)]; return f ? await Deno.readTextFile(f) : null; },
  list: async (folder) => {
    let prefix = resourcePath(folder);
    while (prefix.endsWith("/")) prefix = prefix.slice(0, -1);
    return Object.keys(files).filter((p) => prefix === "" || p.startsWith(prefix + "/")).sort();
  },
  has: (path) => Object.hasOwn(files, resourcePath(path)),
});
// The run's raw context (functions.ts makes ctx of it), and its state for @winche/admin, secrets and resources.
const context = (c, run) => ({
  call: callFor(run),
  functionName: c.functionName,
  auth: c.auth ?? null,
  log: (...a) => console.log(...a),
});
const stateOf = (c, run) => ({
  call: callFor(run),
  name: c.functionName,
  secrets: secretsOf(c.secrets ?? {}, c.functionName),
  resources: resourcesOf(c.resources ?? {}),
});
self.onmessage = async (m) => {
  const d = m.data;
  if (d.t === "reply") {
    const p = pending.get(d.callId);
    pending.delete(d.callId);
    if (!p) return;
    if (d.error) p.reject(Object.assign(new Error(d.error.message), { code: d.error.code }));
    else p.resolve(d.result);
    return;
  }
  if (d.t === "load") {
    // Warming up: the code loaded now, so the first run doesn't wait for it.
    try { await import(d.url); postMessage({ t: "loaded" }); }
    catch (e) { postMessage({ t: "loaded", error: String(e?.message ?? e) }); }
    return;
  }
  limitBytes = (d.memoryMb ?? 0) * 1024 * 1024;
  try {
    const mod = await import(d.url);
    if (d.context) d.context.resources = d.resources;
    if (d.t === "analyze") {
      const list = [];
      for (const [name, v] of Object.entries(mod))
        if (v && v.__winche === 1) list.push({ name, trigger: v.trigger, options: v.options ?? {} });
      done({ result: list });
      return;
    }
    const fn = mod[d.name];
    if (!fn || fn.__winche !== 1) throw Object.assign(new Error("There's no function " + d.name + " in this deployment."), { code: "not-found" });
    if (fn.trigger.kind === "http") {
      // A standard Request in, a standard Response (or text, or JSON) out.
      const a = d.arg;
      const request = new Request(a.url, { method: a.method, headers: a.headers,
        body: a.body && a.method !== "GET" && a.method !== "HEAD" ? fromBase64(a.body) : undefined });
      Object.defineProperty(request, "ip", { value: a.ip ?? null });
      let out = await runs.run(stateOf(d.context, d.run), () => fn.handler(request, context(d.context, d.run)));
      if (!(out instanceof Response))
        out = out === undefined || out === null ? new Response(null, { status: 204 })
          : typeof out === "string" ? new Response(out, { headers: { "content-type": "text/plain; charset=utf-8" } })
          : Response.json(out);
      const bytes = new Uint8Array(await out.arrayBuffer());
      done({ result: { status: out.status, headers: [...out.headers], body: bytes.length ? toBase64(bytes) : null } });
      return;
    }
    const result = await runs.run(stateOf(d.context, d.run), () => fn.handler(d.arg, context(d.context, d.run)));
    done({ result: result === undefined ? null : JSON.parse(JSON.stringify(result)) });
  } catch (e) {
    done({ error: {
      code: e?.name === "HttpsError" ? e.code : (e?.code && typeof e.code === "string" ? e.code : "internal"),
      message: String(e?.message ?? e), https: e?.name === "HttpsError",
      details: e?.name === "HttpsError" && e.details !== undefined ? JSON.parse(JSON.stringify(e.details)) : undefined,
      stack: e?.stack ? String(e.stack).split(d.url).join("<function>") : null,
    } });
  }
};
`;
const BOOT_URL = "data:application/javascript;base64," + btoa(unescape(encodeURIComponent(BOOT)));

const encoder = new TextEncoder();
const out = (msg) => Deno.stdout.writeSync(encoder.encode(JSON.stringify(msg) + "\n"));

// Instances by function: idle workers with the code loaded, ready for a run of that function.
const pools = new Map();   // "version\nname" -> { version, idle: Worker[], min: number }
const IDLE_MS = 5 * 60 * 1000;   // an idle instance is stopped after this (but a function's minInstances are kept)
const MB = 1024 * 1024;

// Every instance, for stats: its function, when it started, its runs, its heap after its last run.
const instances = new Set();

function spawn(bundle, network, name) {
  // It may read its own bundle and its deployment's resources, and nothing else on the machine.
  const read = [bundle.file, ...Object.values(bundle.resources)];
  const worker = new Worker(BOOT_URL, { type: "module", deno: { permissions: { ...SANDBOX, read, net: network, import: network } } });
  worker.meta = { name, since: Date.now(), runs: 0, heapMb: null, state: "loading", busySince: null };
  instances.add(worker);
  const terminate = worker.terminate.bind(worker);
  worker.terminate = () => { instances.delete(worker); terminate(); };
  return worker;
}

function stats() {
  const now = Date.now();
  return [...instances].filter((w) => w.meta.name).map((w) => ({
    function: w.meta.name, state: w.meta.state, heapMb: w.meta.heapMb, runs: w.meta.runs,
    ageSeconds: Math.round((now - w.meta.since) / 1000), busySeconds: w.meta.busySince ? Math.round((now - w.meta.busySince) / 1000) : null,
  }));
}

function poolOf(version, name) {
  const key = version + "\n" + name;
  let pool = pools.get(key);
  if (!pool) pools.set(key, pool = { version, idle: [], min: 0 });
  return pool;
}

// Between runs: logs are dropped, and a late ctx call (from a finished run) is refused.
function idle(worker, pool) {
  worker.onmessage = (e) => {
    const d = e.data;
    if (d.t === "call") worker.postMessage({ t: "reply", callId: d.callId, error: { code: "failed-precondition", message: "The run that made this call had already finished: await everything a function starts before it returns." } });
  };
  worker.onerror = (e) => { e.preventDefault(); worker.terminate(); pool.idle = pool.idle.filter((w) => w !== worker); };
}

function release(worker, pool) {
  if (!bundles.has(pool.version)) { worker.terminate(); return; }   // the version was dropped meanwhile
  worker.idleSince = Date.now();
  worker.meta.state = "idle";
  worker.meta.busySince = null;
  idle(worker, pool);
  pool.idle.push(worker);
}

// Keeps `count` instances of each named function loaded (its minInstances); the rest come and go with runs.
function warm(version, functions) {
  const bundle = bundles.get(version);
  const loads = [];
  if (!bundle) return loads;
  for (const [name, count] of Object.entries(functions ?? {})) {
    const pool = poolOf(version, name);
    pool.min = count;
    for (let i = pool.idle.length + (pool.loading ?? 0); i < count; i++) {
      pool.loading = (pool.loading ?? 0) + 1;
      const worker = spawn(bundle, true, name);
      loads.push(new Promise((loaded) => {
        worker.onmessage = (e) => {
          if (e.data.t !== "loaded") return;
          pool.loading--;
          if (e.data.error) worker.terminate(); else release(worker, pool);
          loaded();
        };
        worker.onerror = (e) => { e.preventDefault(); pool.loading--; worker.terminate(); loaded(); };
      }));
      worker.postMessage({ t: "load", url: bundle.url });
    }
  }
  return loads;
}

// Idle instances past their time go, down to their function's minInstances.
setInterval(() => {
  const now = Date.now();
  for (const pool of pools.values()) {
    pool.idle.sort((a, b) => a.idleSince - b.idleSince);
    while (pool.idle.length > pool.min && now - pool.idle[0].idleSince > IDLE_MS) pool.idle.shift().terminate();
  }
}, 30_000);

function start(msg, network) {
  const bundle = bundles.get(msg.version);
  if (!bundle) { out({ t: "done", id: msg.id, error: { code: "unknown-version", message: "Send the code first." } }); return; }
  // A run takes a warm worker of its version (or makes one, kept afterwards); analysis and fresh functions get their own.
  const pooled = network && !msg.fresh;
  const pool = pooled ? poolOf(msg.version, msg.name) : null;
  const worker = pool?.idle.pop() || spawn(bundle, network, network ? msg.name : null);
  worker.meta.state = "busy";
  worker.meta.busySince = Date.now();
  workers.set(msg.id, worker);
  let finished = false;
  const finish = (reply, reusable) => {
    if (finished) return;
    finished = true;
    clearTimeout(timer);
    workers.delete(msg.id);
    const heapMb = reply.heap !== undefined ? Math.round(reply.heap / MB) : undefined;
    delete reply.heap;
    worker.meta.runs++;
    if (heapMb !== undefined) worker.meta.heapMb = heapMb;
    // Holding more than its function allows, after its garbage was collected: replaced, its memory freed.
    if (reusable && msg.memoryMb && heapMb > msg.memoryMb) {
      reusable = false;
      reply.retired = { reason: "memory", heapMb, limitMb: msg.memoryMb };
    }
    if (pool && reusable) release(worker, pool); else worker.terminate();
    out({ ...reply, heapMb, t: "done", id: msg.id });
  };
  // Out of time: the worker is stopped (that's the only way to stop a run), not kept.
  const timer = setTimeout(() => finish({ error: { code: "deadline-exceeded", message: `Stopped after ${msg.timeoutMs / 1000} s (its timeout).` }, retired: msg.name ? { reason: "timeout" } : undefined }, false), msg.timeoutMs);
  worker.onmessage = (e) => {
    const d = e.data;
    if (d.t === "done") { const reply = { ...d }; delete reply.t; delete reply.run; finish(reply, true); }
    else if (d.t === "call" && d.run !== msg.id)
      worker.postMessage({ t: "reply", callId: d.callId, error: { code: "failed-precondition", message: "The run that made this call had already finished: await everything a function starts before it returns." } });
    else if (d.t === "call") { const call = { ...d }; delete call.run; out({ ...call, id: msg.id }); }
    else out({ ...d, id: msg.id });
  };
  worker.onerror = (e) => {
    e.preventDefault();
    finish({ error: { code: "internal", message: e.message ?? "The function crashed.", stack: null }, retired: msg.name ? { reason: "crash" } : undefined }, false);
  };
  worker.postMessage({ ...msg, run: msg.id, url: bundle.url, resources: bundle.resources });
}

out({ t: "ready" });
async function* readLines() {
  let buffer = "";
  for await (const chunk of Deno.stdin.readable.pipeThrough(new TextDecoderStream())) {
    buffer += chunk;
    let nl;
    while ((nl = buffer.indexOf("\n")) >= 0) {
      yield buffer.slice(0, nl);
      buffer = buffer.slice(nl + 1);
    }
  }
}
for await (const line of readLines()) {
  if (!line.trim()) continue;
  let msg;
  try { msg = JSON.parse(line); } catch { continue; }
  if (msg.t === "code") bundles.set(msg.version, { url: msg.url, file: msg.file, resources: msg.resources ?? {} });
  else if (msg.t === "analyze") start({ ...msg, timeoutMs: 20000 }, false);
  else if (msg.t === "invoke") start(msg, true);
  else if (msg.t === "reply") workers.get(msg.id)?.postMessage(msg);
  else if (msg.t === "warm") {
    const loads = warm(msg.version, msg.functions);
    if (msg.id !== undefined) Promise.all(loads).then(() => out({ t: "done", id: msg.id, result: loads.length }));
  }
  else if (msg.t === "stats") out({ t: "done", id: msg.id, result: stats() });
  else if (msg.t === "forget") {
    bundles.delete(msg.version);
    // Its idle instances go now; busy ones when their run ends.
    for (const [key, pool] of pools) if (pool.version === msg.version) { for (const w of pool.idle) w.terminate(); pools.delete(key); }
  }
}
Deno.exit(0);   // the server closed our input: it's stopping
