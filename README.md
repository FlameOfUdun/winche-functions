# Winche.Functions

JavaScript and TypeScript functions for the Winche stack. They run on the Winche server in [Deno](https://deno.com),
each run in its own sandboxed worker (network yes; the server's files, environment and programs no). They're written
in your editor (VS Code, with `winche functions init`; the Winche extension turns Deno on for the functions folder) and
deployed with `winche functions deploy`. The console shows what's deployed, runs callables, and keeps every deploy in
its change history to roll back to.

```ts
import { callable, documents, files, users, schedule, FieldValue, HttpsError } from "@winche/functions";

// Called by apps: the SDKs' functions.call("hello", { name: "ann" })
export const hello = callable(async (data: { name: string }, ctx) => {
  if (!ctx.caller) throw new HttpsError("unauthenticated", "Sign in first.");
  const person = await ctx.documents.doc(`people/${data.name}`).get();
  return { greeting: `Hello ${person.get("first") ?? data.name}` };
});

// When a document is created: {name} parts of the path are in e.params
export const onVisit = documents.onCreated("patients/{patientId}/visits/{visitId}", async (e, ctx) => {
  await ctx.documents.doc(`patients/${e.params.patientId}`).update({ lastVisit: e.data.get("at"), visits: FieldValue.increment(1) });
});

export const onScan = files.onUploaded("scans/{rest=**}", async (e, ctx) => ctx.log("New scan", e.path, e.file.data?.sizeBytes));
export const welcome = users.onCreated(async (e) => e.user.reference.setCustomClaims({ role: "patient" }));
export const nightly = schedule("0 2 * * *", async (_e, ctx) => {
  await fetch("https://example.com/nightly-report", { method: "POST" });
}, { timeZone: "Europe/London", timeoutSeconds: 300 });
```

| Package | |
| --- | --- |
| `Winche.Functions` | Deploying (bundled with `deno bundle`, every version kept, roll back), running, callables (`POST /functions/v1/call/{name}`), schedules, secrets (`ctx.secrets`), console pages and actions. Deployed through the console (`winche functions deploy`, as you or a CI token). |
| `Winche.Functions.Database` | `ctx.documents`: references, queries, counts and aggregations, transforms, batches, transactions, `expand` — as winche_database, with the server's rights. |
| `Winche.Functions.Storage` | `ctx.files`: `child(path)` references — snapshots, bytes, writes (from a URL too), listings, metadata, links — as winche_storage. |
| `Winche.Functions.Auth` | `ctx.auth`: `user(uid)` references — the account, custom claims (Winche Auth). |
| `Winche.Functions.Assets` | `ctx.assets`: channels, releases and their files (Winche Assets), and the `assets.onReleased` / `onChannelMoved` triggers. |

## Install

```sh
dotnet add package Winche.Functions
dotnet add package Winche.Functions.Database   # triggers and the admin API for each product the app has
dotnet add package Winche.Functions.Storage
dotnet add package Winche.Functions.Auth
dotnet add package Winche.Functions.Assets
```

## Setup

```csharp
builder.Services.AddWincheHooks(o => o.ConnectionString = conn);          // triggers go through the hooks queue
builder.Services.AddWincheFunctions(o => o.ConnectionString = conn);
builder.Services.AddWincheFunctionsForDatabase().AddWincheFunctionsForStorage().AddWincheFunctionsForAuth()
    .AddWincheFunctionsForAssets();
builder.Services.AddWincheCaller(WincheClaims.FromAsync);                // ctx.auth for callables (Winche.Core)
// The events come from the packages themselves: o.UseHooks(h => h.PublishChanges()) in AddWincheDatabase and
// AddWincheStorage; Winche Auth publishes its user events on its own.
app.UseWincheCaller();
app.MapWincheFunctions();
```

Deno must be installed (or `FunctionsOptions.DenoPath` set); Winche Server's image includes it.

## How they run

- **Triggers** on documents, files and users go through Winche.Webhooks' queue: delivered at least once, retried with
  growing gaps, listed under the function's Runs on the console's Functions page. Write them so running twice is harmless; `e.id`
  is the same on retries.
- **Schedules** are cron expressions (minute hour day month weekday), UTC unless `timeZone` is given; one server
  starts each run. Runs missed while every server was down happen once when one is back.
- **Callables** answer the app directly: `{ data }`, or `{ error: { code, message } }` with an HTTP status. An
  `HttpsError`'s message reaches the app; other failures show as "The function failed." (details in the logs).
- **Run time**: a function past its time is stopped. Its time is, in order: its own in the console (**Functions →
  Settings**, with `o.UseSettings()`), else `timeoutSeconds` in its code, else the project's default (60 s unless
  changed there). None can go past the server's longest (`FunctionsOptions.MaxTimeout`, default 9 minutes; Winche
  Server's is a server setting). A long run still ends if the server restarts: long work saves its progress as it goes
  (a cursor document a schedule continues from). While a triggered or scheduled run is going, its server keeps its
  claim on it, so no other server starts it again.
- **Logs**: `console.log` and `ctx.log` go to the server's logs (category `Winche.Functions`), with each run's
  outcome and time.
- **Imports**: relative files, `npm:` and `jsr:` packages, and URLs, resolved when deploying.
- **Sandbox**: runs happen in workers with network access only. A worker can read its own bundle and its deployment's
  resources, nothing else on the machine.
- **Warm workers**: a version's workers are kept with its code loaded and reused, one run at a time, so a run starts in
  well under a millisecond (an HTTP function answers in a couple of milliseconds, like any endpoint). The version in
  force is loaded and a worker readied when the server starts and right after each deploy, so the first caller doesn't
  wait either. A worker that runs out of time or crashes is replaced; idle ones go after five minutes (one is kept).
  Projects and versions never share workers.
- **Module state lasts between runs** on the same worker: keep a client or a parsed file at the top of the module to
  reuse it, but never one caller's data (the next run may be someone else's). A run must `await` what it starts:
  `ctx.documents`, `ctx.files` or `ctx.auth` called after it has answered is refused. `{ fresh: true }` gives a
  function a new sandbox for every run, with nothing kept, at the cost of loading its code each time (milliseconds for
  a small bundle, tens of them for a large one).

## HTTP functions

`http.onRequest` answers web requests at `/functions/v1/http/{name}` (and any path below it): links in emails, other
services' webhooks. It gets a standard `Request` and returns a `Response`, text, JSON, or nothing (204).

```ts
import { http, HttpsError } from "@winche/functions";

// A link sent by email, valid for days: /functions/v1/http/download/<token>
export const download = http.onRequest(async (req, ctx) => {
  const token = new URL(req.url).pathname.split("/").pop();
  const link = await ctx.documents.doc(`downloadLinks/${token}`).get();
  if (!link.exists) throw new HttpsError("not-found", "This link has ended.");
  return Response.redirect((await ctx.files.child(link.get("path")).downloadUrl()).url, 302);
}, { methods: ["GET"] });

// Another service's webhook, checked with its signing secret.
export const paymentEvents = http.onRequest(async (req, ctx) => {
  const body = await req.text();
  if (!(await validSignature(body, req.headers.get("x-signature"), ctx.secrets.get("PAYMENTS_SECRET"))))
    return new Response("bad signature", { status: 401 });
  // …
  return { ok: true };
}, { methods: ["POST"], secrets: ["PAYMENTS_SECRET"] });
```

- **Open to anyone:** there's no signed-in caller (`ctx.caller` is null). Check what you need yourself: a signature, a
  token in the link, an API key from `ctx.secrets`.
- `methods` limits them (others get 405); every method by default. `req.ip` is the caller's address.
- Request bodies up to 1 MB (`FunctionsOptions.MaxHttpRequestBytes`); larger ones get 413. A thrown `HttpsError` is an
  error answer with its status and message, as for callables.
- They can't be called as callables, and callables can't be reached here.

## Documents, files and users

`ctx.documents`, `ctx.files` and `ctx.auth` read like the Dart SDKs (winche_database, winche_storage): you get a
**reference** from the service, `get()` returns a **snapshot**, and a snapshot's `reference` leads back. They act with
the server's rights: access rules don't apply.

```ts
import { schedule, callable, FieldValue, Filter, Aggregate, Link } from "@winche/functions";

export const tidy = schedule("0 3 * * *", async (_e, ctx) => {
  const db = ctx.documents;
  const visits = db.collection("visits");                               // or db.doc("patients/p1").collection("visits")
  const old = await visits.where("at", "<", daysAgo(90)).orderBy("at").limit(500).get();
  const batch = db.batch();
  old.docs.forEach((d) => batch.delete(d.reference));
  await batch.commit();                                                 // up to 500, all or none

  const open = await visits.where(Filter.or(Filter.where("status", "==", "open"), Filter.where("urgent", "==", true))).count();
  const { n, total } = await db.collection("orders").where("paid", "==", true).aggregate({ n: Aggregate.count(), total: Aggregate.sum("amount") });
  const everyWard = await db.collectionGroup("beds").where("free", "==", true).get();   // every "beds" collection

  await db.doc("stats/today").set({ open, n, total, at: FieldValue.serverTimestamp(), runs: FieldValue.increment(1) }, { merge: true });
  await db.runTransaction(async (tx) => {                               // read, then write against what was read
    const ward = await tx.get(db.doc("wards/w1"));
    tx.update(db.doc("wards/w1"), { free: ward.get("free") - 1 });
  });
});
```

- **Documents:** `doc(path)`, `collection(path)`, `collectionGroup(id)`, `getAll(refs)`, `batch()`, `runTransaction(fn)`.
  A `DocumentReference` has `get()`, `set(data, { merge, mergeFields, precondition })`, `update(data)` (dotted paths),
  `delete({ cascade })`, `collection()`, `expand(paths)`. A `CollectionReference` is a query, with `doc(id?)` and
  `add(data)`. Queries chain `where(field, op, value)` (`==`, `!=`, `<`, `<=`, `>`, `>=`, `in`, `not-in`,
  `array-contains`, `array-contains-any`, `array-contains-all`, `contains`, `starts-with`, `ends-with`, `matches`) or
  `where(Filter.or(…))`, `orderBy`, `limit`, `limitToLast`, `offset`, `startAt` / `startAfter` / `endAt` /
  `endBefore` (values or a snapshot), `select`, `expand`, then `get()`, `count()`, `sum()`, `average()`, `aggregate()`.
- **Snapshots:** `DocumentSnapshot` has `exists`, `data()`, `get("a.b")`, `id`, `reference`, `createTime`, `updateTime`,
  `updateTimeRaw` (for `precondition: { updateTime }`), `version`. `QuerySnapshot` has `docs`, `size`, `empty`, `hasMore`.
- **Values:** numbers (whole ones are integers; `bigint` for large ones), strings, booleans, null, `Date` (timestamps),
  `Uint8Array` (bytes), `GeoPoint`, `Link`, arrays and maps. `FieldValue.delete()`, `serverTimestamp()`,
  `increment(n)`, `maximum(n)`, `minimum(n)`, `arrayUnion(…)`, `arrayRemove(…)` in `set` and `update`.
- **Transactions** are optimistic: the documents a transaction read and then writes must be unchanged when it commits,
  else it runs again (up to `maxAttempts`, default 5). Read before writing.

```ts
export const weeklyReport = schedule("0 6 * * 1", async (_e, ctx) => {
  const report = ctx.files.child("reports").child("weekly.pdf");        // a ChildReference
  await report.putData(await makePdf(), { mimeType: "application/pdf", contentDisposition: 'attachment; filename="weekly.pdf"' });
  const { url, expiresAt } = await report.downloadUrl();                // a link to send on
  const clip = await ctx.files.child("media/clip").putFromUrl(signedUrl, { headers: { authorization: `Bearer ${token}` } });
  const folder = await ctx.files.child("reports").listChildren();       // files and folders
});
```

- **Files:** `child(path)` gives a `ChildReference` (`name`, `path`, `parent`, `child()`): `getSnapshot()`
  (`exists`, `data`: size, type, metadata, version, …), `getData()` (bytes) / `getText()`, `putData(bytes | text,
  { mimeType, metadata, contentDisposition, cacheControl })`, `putFromUrl(url, { headers, … })`, `list()` (one page)
  / `listChildren()` (all), `updateMetadata(metadata, { cacheControl, ifVersion })`, `delete()` (a folder: all of
  it), `downloadUrl()`.
- `getData` and `putData` move up to 32 MB through the function; `putFromUrl` copies up to 5 GB on the server
  (from a bucket's signed or authorized link), never through the function. A write is an upload like any other:
  `files.onUploaded` triggers and webhooks run.

```ts
export const promote = callable(async (data: { uid: string }, ctx) => {
  const user = ctx.auth.user(data.uid);                                  // a UserReference
  await user.setCustomClaims({ ...(await user.getCustomClaims()), role: "admin" });
  const snap = await user.get();                                         // a UserSnapshot
  return { email: snap.data?.email, claims: snap.customClaims };
});
```

- **Users:** `ctx.auth.user(uid)` gives a `UserReference`: `get()` (`exists`, `data`: email, name, providers,
  claims, …), `getCustomClaims()`, `setCustomClaims(claims)` (in the user's next token).
- **References in documents:** a reference written into a document (`db.doc(…)`, `files.child(…)`,
  `auth.user(…)`, or `Link.document / file / user`) is stored typed and reads back as a `Link` (`kind`, `target`).
  `expand(["patient", "patient.doctor", "scan", "author"])` on a get or query makes `link.data()` what it points to:
  the document's fields, the file's record, the user's account. Paths walk maps, lists and references; 10 paths,
  4 deep, the database's `ExpandLimit` (default 100) per call at most.
- **Trigger events** carry the same snapshots: a document event's `data` and `before` (`exists` false when created or
  deleted), a file event's `file`, a user event's `user`.

## Resources

The functions folder's `resources/` folder is deployed with the code: files the code needs, such as fonts, templates
and translations it renders with. Functions read them with `ctx.resources`:

```ts
const de = JSON.parse((await ctx.resources.readText("translations/de.json"))!);
const font = await ctx.resources.read("fonts/Roboto-400.ttf");      // Uint8Array, or null when there's none
const all = await ctx.resources.list("translations");                // ["translations/de.json", …]
```

- Read-only, and part of the deployment: they change only when it does, and rolling back brings the old ones back.
- `winche functions deploy` sends each file's SHA-256 first and uploads only contents the server doesn't have, so a
  large font goes up once, not on every deploy. Contents are kept once, in the project's database, however many
  versions use them. Up to 2000 resources, 64 MB each.
- Files under `resources/` aren't code: import JSON your code needs from elsewhere in the folder.
- In the emulators, saving a file under `resources/` deploys again, as code does.

## Assets

`ctx.assets` reads the app's assets (Winche Assets: content published from the CLI as releases, followed through
channels) with the server's rights — no rules. Not to be confused with resources, which are the function's own files.

```ts
import { assets, callable } from "@winche/functions";

export const strings = callable(async (p: { lang: string }, ctx) => {
  const release = await ctx.assets.channel("production").get();   // the release it points at, or null
  return release?.file(`translations/${p.lang}.json`).json();     // also bytes(), text(), downloadUrl()
});

// A release published (released), or a channel pointed at one (channelMoved); { channel } narrows it.
export const announce = assets.onReleased(async (e, ctx) => {
  ctx.log(`Release #${e.release} on ${e.channel}: ${e.note}`);
}, { channel: "production" });
```

`ctx.assets.release(n)`, `channels()` and `releases()` list the rest. A file is read whole, up to 32 MB; larger ones
through `downloadUrl()`.

## Describing a function

Options say what a function does and what it takes and returns. The console shows them, and starts a run by hand from
`example`, or from an input made from the `input` schema. They're documentation: nothing is checked.

```ts
export const strings = callable(async (data: { lang: string }, ctx) => …, {
  description: "A language's strings from the production assets.",                 // up to 300 characters
  input: { type: "object", properties: { lang: { enum: ["en", "de"] } }, required: ["lang"] },   // JSON Schema
  output: { type: "object", additionalProperties: { type: "string" } },
  example: { lang: "de" },                                                             // optional: else made from input
});
```

## Secrets

API keys and passwords a function needs (an email service's key, another service's client secret) are **secrets**: set
in the console (the **Secrets** page) or with `winche functions secrets:set NAME`, kept encrypted with the master key, and
never shown again. A function declares the ones it reads, and only gets those:

```ts
export const sendReport = documents.onCreated("reports/{id}", async (e, ctx) => {
  const key = ctx.secrets.get("MAIL_API_KEY");          // throws when it isn't declared or isn't set
  await fetch("https://mail.example.com/send", { method: "POST", headers: { authorization: `Bearer ${key}` }, body: "…" });
}, { secrets: ["MAIL_API_KEY"] });
```

- **Names:** capitals, digits and underscores, starting with a letter (`MAIL_API_KEY`).
- **Changes** apply from the next run; there's nothing to deploy.
- **Logs and errors** show `••••` in place of a secret's value (values of 4 characters or more).
- **The console lists** each secret's name, when and by whom it was set, and which functions declare it, including the
  ones a function declares but nobody has set yet.
- **References:** a secret can instead say where it's kept (`aws-ssm:/app/mail-key`, `aws-sm:app/ses#secretAccessKey`,
  with Winche.Secrets.Aws): read when a function runs, kept a few minutes. One that can't be read fails the run with
  `failed-precondition` and the reason (never a value). See Winche.Secrets.
- They're kept by Winche.Secrets, which needs Winche.Core's `AddWincheConfig` with a master key (Winche Server has it);
  a deployment that declares secrets without it is refused.

## Console pages

A project can add pages of its own to the console: dashboards, admin tools, anything its team needs. The page is
yours: plain HTML, JavaScript and CSS, or the build output of any framework, in a folder next to `index.ts`. The console
shows it in its sidebar (under **Project pages**) and has no say in how it looks. What it shows comes from **console
actions**: functions only the console can run, for someone whose role in the project is high enough.

```ts
import { consolePage, consoleAction, HttpsError } from "@winche/functions";

// console/reports/index.html (and its scripts, styles, images) is the page.
export const reports = consolePage({ title: "Reports", files: "console/reports", icon: "chart-bar", minRole: "viewer" });

export const weeklyCounts = consoleAction(async (data: { weeks: number }, ctx) => {
  const visits = await ctx.documents.collection("visits").where("at", ">=", weeksAgo(data.weeks)).count();
  return { visits };
}, { minRole: "viewer" });

export const closeWard = consoleAction(async (data: { ward: string }, ctx) => {
  if (!data.ward) throw new HttpsError("invalid-argument", "Which ward?");
  await ctx.documents.doc(`wards/${data.ward}`).update({ closedBy: ctx.console!.user.email, closedAt: new Date() });
}, { minRole: "admin" });
```

```html
<!-- console/reports/index.html -->
<!doctype html>
<html>
  <head><link rel="stylesheet" href="style.css" /></head>
  <body>
    <h1>Visits</h1> <p id="count">…</p> <button id="close">Close ward 3</button>
    <script type="module" src="app.js"></script>
  </body>
</html>
```

```js
// console/reports/app.js — window.winche is there before your scripts run.
await winche.ready;
const { visits } = await winche.call("weeklyCounts", { weeks: 4 });
document.querySelector("#count").textContent = `${visits} in the last 4 weeks`;
document.querySelector("#close").hidden = winche.user.role !== "admin";
document.querySelector("#close").onclick = async () => {
  if (!(await winche.confirm("Close ward 3? Nobody can be admitted there until it's opened again.", { danger: true }))) return;
  try { await winche.call("closeWard", { ward: "3" }); winche.toast("Ward 3 closed", "success"); }
  catch (e) { winche.toast(e.message, "error"); }   // e.code: permission-denied, invalid-argument, …
};
```

**`window.winche`** in a page:

| | |
| --- | --- |
| `ready` | A promise: resolves once the page is connected to the console (`user` and `theme` are set). |
| `call(name, data)` | Runs a console action as the signed-in person: its result, or an error with `code` and `message`. |
| `user` | `{ email, role }`: role is `viewer`, `member` or `admin`, to show only what they can use. |
| `theme` | `{ scheme: "light" \| "dark", colors, font }`, also as CSS variables (`--winche-page`, `--winche-card`, `--winche-text`, `--winche-muted`, `--winche-border`, `--winche-accent`, `--winche-font`…) and `data-winche-theme` on `<html>`; a `winche:theme` event when it changes. Use them or not. |
| `confirm(text, { danger, confirmLabel })` | The console's own confirmation dialog: true or false. |
| `toast(text, kind)` | A notice in the console (`info`, `success`, `error`). |
| `navigate(path)` | Goes to a console page, e.g. `/database` or `/authentication/users`. |
| `setTitle(text)` | A line under the page's title. |

- **Options:** `consolePage({ title, files, icon, minRole, order })`. `files` is the folder; it must have an
  `index.html`. `icon` is a [Tabler icon](https://tabler.io/icons) name. `minRole` (default `viewer`) decides who sees
  it. `order` (default 0) sorts the sidebar. `consoleAction(handler, { minRole, timeoutSeconds, secrets })`: `minRole`
  defaults to `admin`.
- **Roles are checked on the server** for every action (hiding a button is only for looks). An admin-only action asks
  for two-step sign-in where the console requires it for settings. Every run is logged with who ran it, and
  `ctx.console.user` says who in the action. Actions run with the server's rights, like every function: `ctx.documents`,
  `ctx.files`, `ctx.auth`, `ctx.secrets`.
- **Apps can't call actions**, and pages can't call callables: `/functions/v1/call` only runs callables.
- **Sandboxed:** the page runs in a frame with an origin of its own and no cookies of the console's, so it can't call
  the console's API or read its session. Its scripts, styles, images and fonts come from its own folder (or inline);
  it can't fetch from the network (`connect-src 'none'`): everything goes through `winche.call`. Links it opens go
  to a new tab. If it navigates the frame elsewhere, it's disconnected and asks to be reloaded.
- **Files:** `.html`, `.js`, `.mjs`, `.css`, `.json`, `.map`, `.svg`, `.txt`, `.png`, `.jpg`, `.gif`, `.webp`, `.ico`,
  `.woff`, `.woff2`, deployed with `winche functions deploy` (images and fonts as base64). They're versioned and
  rolled back with the functions.
- The emulators have no console: test a page's actions by calling them from your tests.

## Before-functions

They run **before** a write is saved, inside it, and can refuse it or change it:

```ts
import { documents, files, users, HttpsError } from "@winche/functions";

export const guardRecords = documents.beforeWrite("records/{id}", async (e, ctx) => {
  if (e.before.get("signedOff")) throw new HttpsError("permission-denied", "This record is signed off.");
  if (e.operation !== "delete") return { set: { updatedBy: e.caller?.uid ?? "server" }, remove: ["draft"] };
});

export const scans = files.beforeWrite("scans/{rest=**}", async (e) => ({ metadata: { checked: false }, cacheControl: "private" }));

export const closedDomains = users.beforeCreate(async (e) => {
  if (e.email?.endsWith("@example.org")) throw new HttpsError("permission-denied", "Sign-ups from that domain are closed.");
});
export const gate = users.beforeSignIn(async (e) => { /* e.user, e.provider, e.ipAddress */ });
```

- **When:** `documents.beforeWrite` and `files.beforeWrite` run after the access rules, for apps' writes and the server's own
  (each document of a batch or recursive delete, each file under a deleted folder). `users.beforeCreate` and
  `users.beforeSignIn` run on every sign-up and sign-in (after the credentials check out).
- **Refusing:** throw `HttpsError`: the caller gets `permissionDenied` (sign-ups and sign-ins: `DENIED`) with its message,
  and nothing is saved.
- **Changing:** documents return `{ set: { "dotted.path": value }, remove: ["field"] }`; files return
  `{ metadata: { key: value | null }, contentDisposition, cacheControl }`. A delete can only be refused. Several
  before-functions on one path run in export order, each seeing the previous one's changes.
- **Quick, and reading only:** 5 seconds unless `timeoutSeconds` says otherwise (at most 30): the write, and its locks,
  wait for it. `ctx.documents`, `ctx.files` and `ctx.auth` only read here; side effects belong in `onCreated`… triggers.
- **Failures:** a function that crashes or runs out of time refuses the write ("couldn't be checked"), unless it's
  `{ failOpen: true }`, which lets the write through and logs a warning.
- Needs `AddWincheFunctionsForDatabase()` / `ForStorage()` / `ForAuth()` (they run them); without them a deploy says so.

## Upgrading to 2.0

- Secrets are kept by [Winche.Secrets](https://github.com/FlameOfUdun/winche-secrets) (a package of its own, out of
  Winche.Core 2.0); `AddWincheFunctions` adds it. A secret can now be a reference to AWS Secrets Manager or Parameter
  Store (Winche.Secrets.Aws): functions read it with `ctx.secrets.get` as before.
- New: Winche.Functions.Assets (`ctx.assets` and the assets triggers), `AddWincheFunctionsForAssets()`.
- Built on Winche.Core 2.0, Winche.Auth 2.0, Winche.Storage 11 and Winche.Webhooks 2.0.

## Upgrading to 2.1

- Runs reuse warm workers (see How they run): a call starts in well under a millisecond instead of loading the code
  each time. What a module keeps at the top level now lasts between runs on the same worker, and a call to `ctx.*`
  after a run has answered is refused. `{ fresh: true }` keeps the old behaviour (a new sandbox every run) for a
  function.
