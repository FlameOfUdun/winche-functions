// Winche functions runtime: started by the Winche server as one Deno process. It reads requests as JSON lines on
// stdin and answers on stdout. Each run happens in its own worker that may use the network but not the files,
// environment or programs of the machine, and is stopped when its time is up.
//
// In:  {t:"code", version, url, file, resources}         — a deployment's bundle file and its resources' files (once per version)
//      {t:"analyze", id, version}                     — list the exported functions and their triggers
//      {t:"invoke", id, version, name, arg, context, timeoutMs}
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
const call = (op, args) => new Promise((resolve, reject) => {
  const callId = next++;
  pending.set(callId, { resolve, reject });
  postMessage({ t: "call", callId, op, args });
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
const context = (c) => ({
  call,
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
      let out = await fn.handler(request, context(d.context));
      if (!(out instanceof Response))
        out = out === undefined || out === null ? new Response(null, { status: 204 })
          : typeof out === "string" ? new Response(out, { headers: { "content-type": "text/plain; charset=utf-8" } })
          : Response.json(out);
      const bytes = new Uint8Array(await out.arrayBuffer());
      postMessage({ t: "done", result: { status: out.status, headers: [...out.headers], body: bytes.length ? toBase64(bytes) : null } });
      return;
    }
    const result = await fn.handler(d.arg, context(d.context));
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

function start(msg, network) {
  const bundle = bundles.get(msg.version);
  if (!bundle) { out({ t: "done", id: msg.id, error: { code: "unknown-version", message: "Send the code first." } }); return; }
  // It may read its own bundle and its deployment's resources, and nothing else on the machine.
  const read = [bundle.file, ...Object.values(bundle.resources)];
  const worker = new Worker(BOOT_URL, { type: "module", deno: { permissions: { ...SANDBOX, read, net: network, import: network } } });
  workers.set(msg.id, worker);
  const finish = (reply) => {
    clearTimeout(timer);
    worker.terminate();
    workers.delete(msg.id);
    out({ ...reply, t: "done", id: msg.id });
  };
  const timer = setTimeout(() => finish({ error: { code: "deadline-exceeded", message: `Stopped after ${msg.timeoutMs / 1000} s (its timeout).` } }), msg.timeoutMs);
  worker.onmessage = (e) => {
    const d = e.data;
    if (d.t === "done") finish(d);
    else out({ ...d, id: msg.id });
  };
  worker.onerror = (e) => {
    e.preventDefault();
    finish({ error: { code: "internal", message: e.message ?? "The function crashed.", stack: null } });
  };
  worker.postMessage({ ...msg, url: bundle.url, resources: bundle.resources });
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
  else if (msg.t === "forget") bundles.delete(msg.version);
}
Deno.exit(0);   // the server closed our input: it's stopping
