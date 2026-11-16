// Winche functions runtime: started by the Winche server as one Deno process. It reads requests as JSON lines on
// stdin and answers on stdout. Each run happens in its own worker that may use the network but not the files,
// environment or programs of the machine, and is stopped when its time is up.
//
// In:  {t:"code", version, code}                     — a deployment's bundle (sent once per version)
//      {t:"analyze", id, version}                     — list the exported functions and their triggers
//      {t:"invoke", id, version, name, arg, context, timeoutMs}
//      {t:"reply", id, callId, result | error}        — the answer to a ctx.db / ctx.files / ctx.users call
//      {t:"forget", version}                          — drop a bundle (e.g. one only analyzed)
// Out: {t:"log", id, level, message} · {t:"call", id, callId, op, args} · {t:"done", id, result | error} · {t:"ready"}

const bundles = new Map();   // version -> data: URL of the bundle
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
// Dates become timestamps in documents; everything else is plain JSON.
const encode = (v) => v instanceof Date ? { $timestamp: v.toISOString() }
  : Array.isArray(v) ? v.map(encode)
  : v && typeof v === "object" ? Object.fromEntries(Object.entries(v).map(([k, x]) => [k, encode(x)]))
  : v;
// In before-functions ctx.* only reads: a write there would run outside the write being checked.
const readOnly = (what) => () => Promise.reject(Object.assign(
  new Error(what + " can't change anything in a before-function: it would run outside the write being checked. Use an onCreated… trigger."),
  { code: "failed-precondition" }));
const context = (c) => c.readOnly ? lock(full(c)) : full(c);
const lock = (ctx) => ({
  ...ctx,
  db: { ...ctx.db, set: readOnly("ctx.db.set"), update: readOnly("ctx.db.update"), delete: readOnly("ctx.db.delete") },
  files: { ...ctx.files, delete: readOnly("ctx.files.delete"), deleteUnder: readOnly("ctx.files.deleteUnder") },
  users: { ...ctx.users, setClaims: readOnly("ctx.users.setClaims") },
});
// Secrets the function declared (options.secrets), read from the console's encrypted store for this run.
const secretsOf = (s, fn) => ({
  get: (name) => {
    if (!Object.hasOwn(s, name)) throw Object.assign(new Error(fn + " doesn't declare the secret " + name + ": add it to the function's options (secrets: ['" + name + "'])."), { code: "failed-precondition" });
    if (s[name] == null) throw Object.assign(new Error("The secret " + name + " isn't set: set it in the console (Functions → Secrets) or with: winche functions secrets set " + name), { code: "failed-precondition" });
    return s[name];
  },
  has: (name) => Object.hasOwn(s, name) && s[name] != null,
});
const full = (c) => ({
  functionName: c.functionName,
  auth: c.auth ?? null,
  secrets: secretsOf(c.secrets ?? {}, c.functionName),
  console: c.console ?? null,
  log: (...a) => console.log(...a),
  db: {
    get: (path, o) => call("db.get", { path, ...(o?.expand ? { expand: o.expand } : {}) }),
    set: (path, data, o) => call("db.set", { path, data: encode(data), merge: !!o?.merge }),
    update: (path, data) => call("db.update", { path, data: encode(data) }),
    delete: (path, o) => call("db.delete", { path, recursive: !!o?.recursive }),
    query: (collection, q) => call("db.query", { collection, ...(q ?? {}) }),
  },
  files: {
    list: (folder) => call("files.list", { folder }),
    delete: (path) => call("files.delete", { path }),
    deleteUnder: (folder) => call("files.deleteUnder", { folder }),
  },
  users: {
    get: (uid) => call("users.get", { uid }),
    setClaims: (uid, claims) => call("users.setClaims", { uid, claims }),
  },
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
    if (d.t === "analyze") {
      const list = [];
      for (const [name, v] of Object.entries(mod))
        if (v && v.__winche === 1) list.push({ name, trigger: v.trigger, options: v.options ?? {} });
      postMessage({ t: "done", result: list });
      return;
    }
    const fn = mod[d.name];
    if (!fn || fn.__winche !== 1) throw Object.assign(new Error("There's no function " + d.name + " in this deployment."), { code: "not-found" });
    const result = await fn.handler(d.arg, context(d.context));
    postMessage({ t: "done", result: result === undefined ? null : JSON.parse(JSON.stringify(result)) });
  } catch (e) {
    postMessage({ t: "done", error: {
      code: e?.name === "HttpsError" ? e.code : (e?.code && typeof e.code === "string" ? e.code : "internal"),
      message: String(e?.message ?? e), https: e?.name === "HttpsError",
      details: e?.name === "HttpsError" && e.details !== undefined ? JSON.parse(JSON.stringify(e.details)) : undefined,
      stack: e?.stack ? String(e.stack).replace(new RegExp("data:application.javascript;base64,[A-Za-z0-9+=./]+", "g"), "<function>") : null,
    } });
  }
};
`;
const BOOT_URL = "data:application/javascript;base64," + btoa(unescape(encodeURIComponent(BOOT)));

const encoder = new TextEncoder();
const out = (msg) => Deno.stdout.writeSync(encoder.encode(JSON.stringify(msg) + "\n"));

function toDataUrl(code) {
  const bytes = encoder.encode(code);
  let bin = "";
  for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return "data:application/javascript;base64," + btoa(bin);
}

function start(msg, network) {
  const url = bundles.get(msg.version);
  if (!url) { out({ t: "done", id: msg.id, error: { code: "unknown-version", message: "Send the code first." } }); return; }
  const worker = new Worker(BOOT_URL, { type: "module", deno: { permissions: { ...SANDBOX, net: network, import: network } } });
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
  worker.postMessage({ ...msg, url });
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
  if (msg.t === "code") bundles.set(msg.version, toDataUrl(msg.code));
  else if (msg.t === "analyze") start({ ...msg, timeoutMs: 20000 }, false);
  else if (msg.t === "invoke") start(msg, true);
  else if (msg.t === "reply") workers.get(msg.id)?.postMessage(msg);
  else if (msg.t === "forget") bundles.delete(msg.version);
}
Deno.exit(0);   // the server closed our input: it's stopping
