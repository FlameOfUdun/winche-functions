// Winche functions runtime: started by the Winche server as one Deno process. It reads requests as JSON lines on
// stdin and answers on stdout. Runs happen in workers that may use the network but not the files, environment or
// programs of the machine. A version's workers are kept warm (its code already loaded) and reused, one run at a time;
// one that runs out of time or crashes is stopped, and idle ones go after a while. A function with `fresh: true` gets a
// new worker for each run.
//
// In:  {t:"code", version, url, file, resources}         — a deployment's bundle file and its resources' files (once per version)
//      {t:"analyze", id, version}                     — list the exported functions and their triggers
//      {t:"invoke", id, version, name, arg, context, timeoutMs, fresh?}
//      {t:"warm", version}                            — have a worker ready with the version's code loaded
//      {t:"reply", id, callId, result | error}        — the answer to a ctx.db / ctx.files / ctx.users call
//      {t:"forget", version}                          — drop a bundle (e.g. one only analyzed)
// Out: {t:"log", id, level, message} · {t:"call", id, callId, op, args} · {t:"done", id, result | error} · {t:"ready"}

const bundles = new Map();   // version -> { url, file, resources: { path: file } }
const workers = new Map();   // request id -> Worker

const SANDBOX = { read: false, write: false, env: false, run: false, ffi: false, sys: false };

// What each worker runs first: the bridge for ctx.* calls and console output, then the request.
const BOOT = `
const pending = new Map();
let next = 1;
// A run's ctx calls carry its id: one made after its run has finished (work it didn't await) is refused, never served
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
// Secrets the function declared (options.secrets), read from the console's encrypted store for this run.
const secretsOf = (s, fn) => ({
  get: (name) => {
    if (!Object.hasOwn(s, name)) throw Object.assign(new Error(fn + " doesn't declare the secret " + name + ": add it to the function's options (secrets: ['" + name + "'])."), { code: "failed-precondition" });
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
// The run's raw context: the SDK (functions.ts, bundled with the code) builds ctx.documents, ctx.files and ctx.auth on it.
const context = (c, run) => ({
  call: callFor(run),
  functionName: c.functionName,
  readOnly: !!c.readOnly,
  auth: c.auth ?? null,
  console: c.console ?? null,
  secrets: secretsOf(c.secrets ?? {}, c.functionName),
  resources: resourcesOf(c.resources ?? {}),
  log: (...a) => console.log(...a),
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
  try {
    const mod = await import(d.url);
    if (d.context) d.context.resources = d.resources;
    if (d.t === "analyze") {
      const list = [];
      for (const [name, v] of Object.entries(mod))
        if (v && v.__winche === 1) list.push({ name, trigger: v.trigger, options: v.options ?? {} });
      postMessage({ t: "done", result: list });
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
      let out = await fn.handler(request, context(d.context, d.run));
      if (!(out instanceof Response))
        out = out === undefined || out === null ? new Response(null, { status: 204 })
          : typeof out === "string" ? new Response(out, { headers: { "content-type": "text/plain; charset=utf-8" } })
          : Response.json(out);
      const bytes = new Uint8Array(await out.arrayBuffer());
      postMessage({ t: "done", result: { status: out.status, headers: [...out.headers], body: bytes.length ? toBase64(bytes) : null } });
      return;
    }
    const result = await fn.handler(d.arg, context(d.context, d.run));
    postMessage({ t: "done", result: result === undefined ? null : JSON.parse(JSON.stringify(result)) });
  } catch (e) {
    postMessage({ t: "done", error: {
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

// Warm workers by version: idle ones with the code loaded, ready for a run.
const pools = new Map();   // version -> { idle: Worker[], warm: boolean }
const IDLE_MS = 5 * 60 * 1000;   // an idle worker is stopped after this (but one, for a warmed version)

function spawn(bundle, network) {
  // It may read its own bundle and its deployment's resources, and nothing else on the machine.
  const read = [bundle.file, ...Object.values(bundle.resources)];
  return new Worker(BOOT_URL, { type: "module", deno: { permissions: { ...SANDBOX, read, net: network, import: network } } });
}

function poolOf(version) {
  let pool = pools.get(version);
  if (!pool) pools.set(version, pool = { idle: [], warm: false });
  return pool;
}

// Between runs: logs are dropped, and a late ctx call (from a finished run) is refused.
function idle(worker, version) {
  worker.onmessage = (e) => {
    const d = e.data;
    if (d.t === "call") worker.postMessage({ t: "reply", callId: d.callId, error: { code: "failed-precondition", message: "The run that made this call had already finished: await everything a function starts before it returns." } });
  };
  worker.onerror = (e) => { e.preventDefault(); drop(worker, version); };
}

function release(worker, version) {
  if (!bundles.has(version)) { worker.terminate(); return; }   // the version was dropped meanwhile
  worker.idleSince = Date.now();
  idle(worker, version);
  poolOf(version).idle.push(worker);
}

function drop(worker, version) {
  worker.terminate();
  const pool = pools.get(version);
  if (pool) pool.idle = pool.idle.filter((w) => w !== worker);
}

function warm(version) {
  const bundle = bundles.get(version);
  if (!bundle) return;
  const pool = poolOf(version);
  pool.warm = true;
  if (pool.idle.length > 0) return;
  const worker = spawn(bundle, true);
  worker.onmessage = (e) => {
    if (e.data.t !== "loaded") return;
    if (e.data.error) worker.terminate(); else release(worker, version);
  };
  worker.onerror = (e) => { e.preventDefault(); worker.terminate(); };
  worker.postMessage({ t: "load", url: bundle.url });
}

// Idle workers past their time go; a warmed version keeps one.
setInterval(() => {
  const now = Date.now();
  for (const pool of pools.values()) {
    const keep = pool.warm ? 1 : 0;
    pool.idle.sort((a, b) => a.idleSince - b.idleSince);
    while (pool.idle.length > keep && now - pool.idle[0].idleSince > IDLE_MS) pool.idle.shift().terminate();
  }
}, 30_000);

function start(msg, network) {
  const bundle = bundles.get(msg.version);
  if (!bundle) { out({ t: "done", id: msg.id, error: { code: "unknown-version", message: "Send the code first." } }); return; }
  // A run takes a warm worker of its version (or makes one, kept afterwards); analysis and fresh functions get their own.
  const pooled = network && !msg.fresh;
  const worker = (pooled && poolOf(msg.version).idle.pop()) || spawn(bundle, network);
  workers.set(msg.id, worker);
  let finished = false;
  const finish = (reply, reusable) => {
    if (finished) return;
    finished = true;
    clearTimeout(timer);
    workers.delete(msg.id);
    if (pooled && reusable) release(worker, msg.version); else worker.terminate();
    out({ ...reply, t: "done", id: msg.id });
  };
  // Out of time: the worker is stopped (that's the only way to stop a run), not kept.
  const timer = setTimeout(() => finish({ error: { code: "deadline-exceeded", message: `Stopped after ${msg.timeoutMs / 1000} s (its timeout).` } }, false), msg.timeoutMs);
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
    finish({ error: { code: "internal", message: e.message ?? "The function crashed.", stack: null } }, false);
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
  else if (msg.t === "warm") warm(msg.version);
  else if (msg.t === "forget") {
    bundles.delete(msg.version);
    // Its idle workers go now; busy ones when their run ends.
    for (const w of pools.get(msg.version)?.idle ?? []) w.terminate();
    pools.delete(msg.version);
  }
}
Deno.exit(0);   // the server closed our input: it's stopping
