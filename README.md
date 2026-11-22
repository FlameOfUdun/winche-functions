# Winche.Functions

JavaScript and TypeScript functions for the Winche stack. They run on the Winche server in [Deno](https://deno.com),
each run in its own sandboxed worker (network yes; the server's files, environment and programs no). They're written
in your editor (VS Code, with `winche functions init`; the Winche extension turns Deno on for the functions folder) and
deployed with `winche functions deploy`. The console shows what's deployed, runs callables, and keeps every deploy in
its change history to roll back to.

```ts
import { callable, onDocument, onFile, onUser, schedule, HttpsError } from "@winche-technologies/winche-functions";
import { documents, FieldValue } from "@winche-technologies/winche-admin";

// Called by apps: the SDKs' functions.call("hello", { name: "ann" })
export const hello = callable(async (data: { name: string }, ctx) => {
  if (!ctx.caller) throw new HttpsError("unauthenticated", "Sign in first.");
  const person = await documents.doc(`people/${data.name}`).get();
  return { greeting: `Hello ${person.get("first") ?? data.name}` };
});

// When a document is created: {name} parts of the path are in e.params
export const onVisit = onDocument.created("patients/{patientId}/visits/{visitId}", async (e) => {
  await documents.doc(`patients/${e.params.patientId}`).update({ lastVisit: e.data.get("at"), visits: FieldValue.increment(1) });
});

export const onScan = onFile.uploaded("scans/{rest=**}", async (e, ctx) => ctx.log("New scan", e.path, e.file.data?.sizeBytes));
export const welcome = onUser.created(async (e) => e.user.reference.setCustomClaims({ role: "patient" }));
export const nightly = schedule("0 2 * * *", async () => {
  await fetch("https://example.com/nightly-report", { method: "POST" });
}, { timeZone: "Europe/London", timeoutSeconds: 300 });
```

Two npm packages, which the project pins itself in its `deno.json` (`winche functions init` writes it):
**`@winche-technologies/winche-functions`** defines functions (triggers, callables, HTTP functions, schedules, pages), their `ctx` (the
call: `name`, `caller`, `log`), secrets and resources; **`@winche-technologies/winche-admin`** is the project's data (documents, files, the
app's users, assets, calling functions), the same SDK console pages and other servers use. In functions it acts with
the server's rights. Each has its own repository (winche-functions-typescript-sdk, winche-admin-typescript-sdk, MIT).

```json
{
  "imports": {
    "@winche-technologies/winche-functions": "npm:@winche-technologies/winche-functions@^3",
    "@winche-technologies/winche-functions/testing": "npm:@winche-technologies/winche-functions@^3/testing",
    "@winche-technologies/winche-admin": "npm:@winche-technologies/winche-admin@^3"
  },
  "minimumDependencyAge": {
    "age": "P1D",
    "exclude": ["npm:@winche-technologies/winche-functions", "npm:@winche-technologies/winche-admin"]
  }
}
```

(Deno doesn't install npm versions younger than a day, against compromised releases; the SDKs are exempt, so a new
one can be used at once, and every other package keeps the wait.)

Code imports them by their npm names (`npm install @winche-technologies/winche-admin` for Node and bundlers). The server bundles the versions the project names; it doesn't bring them. A deployment that imports one without
naming it is refused, saying what to add. A server that can't reach npm (or a test) can provide them:
`FunctionsOptions.ProvidedPackages["@winche-technologies/winche-admin"] = folder` (the package's `package.json` and built files), used only
where `deno.json` doesn't name the package. **Protocol:** the SDKs speak protocol 1 with this server; a function or
call for another is refused, saying which version to use.

| Package | |
| --- | --- |
| `Winche.Functions` | Deploying (bundled with `deno bundle`, every version kept, roll back), running, callables (`POST /functions/v1/call/{name}`), schedules, secrets, console pages; `functions.call` for @winche-technologies/winche-admin. Deployed through the console (`winche functions deploy`, as you or a CI token). |
| `Winche.Functions.Database` | @winche-technologies/winche-admin's `documents`: references, queries, counts and aggregations, transforms, batches, transactions, `expand` — as winche_database. |
| `Winche.Functions.Storage` | @winche-technologies/winche-admin's `files`: `child(path)` references — snapshots, bytes, writes (from a URL too), listings, metadata, links — as winche_storage. |
| `Winche.Functions.Auth` | @winche-technologies/winche-admin's `auth`: `user(uid)` references — the account, custom claims (Winche Auth). |
| `Winche.Functions.Assets` | @winche-technologies/winche-admin's `assets`: channels, releases and their files (Winche Assets), and the `onAssets.released` / `channelMoved` triggers. |

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
builder.Services.AddWincheCaller(WincheClaims.FromAsync);                // ctx.caller for callables (Winche.Core)
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
- **Who called:** `ctx.caller` is the signed-in user (null when signed out); `ctx.byServer` is true only when the
  server itself called: another function (`functions.call`), a run from the console, a server with the admin SDK, a
  trigger or schedule. Never for apps or HTTP requests: a callable only the project's own code may use checks it.
- **Which app called:** `ctx.app` is the app that proved it's the project's own (Integrity: `{ id, provider }`), or
  null; the host supplies it (`FunctionsOptions.AppProof`; Winche Server does). A callable with `{ integrity: true }`
  refuses apps' calls without one (`permission-denied`, details `reason: integrity-required`), whatever the project
  enforces; the project's own code calls it as ever. Only callables take it.
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
- **Instances**: each function has its own instances (workers with its code loaded), reused one run at a time, so a
  run starts in well under a millisecond (an HTTP function answers in a couple of milliseconds, like any endpoint).
  Its `minInstances` are loaded when the server starts and right after each deploy, so the first caller doesn't wait
  either. An instance that runs out of time, crashes, or holds more than its `memory` after a run is replaced; idle
  ones go after five minutes, down to `minInstances`. Projects, versions and functions never share instances.
- **Module state lasts between runs** on the same worker: keep a client or a parsed file at the top of the module to
  reuse it, but never one caller's data (the next run may be someone else's). A run must `await` what it starts:
  @winche-technologies/winche-admin called after it has answered is refused. `{ fresh: true }` gives a
  function a new sandbox for every run, with nothing kept, at the cost of loading its code each time (milliseconds for
  a small bundle, tens of them for a large one).

## Memory and instances

Each function may say, in its options (and each can be changed in the console's **Settings** tab, over its code's,
or for all functions in **Functions → Settings**):

| Option | Default | |
| --- | --- | --- |
| `memory` | 256 (MB) | What an instance may hold after a run (garbage collected first). Over it, the instance is replaced and its memory freed, with a log line. |
| `minInstances` | 1 for callables and HTTP functions, else 0 | Instances kept loaded even when idle. |
| `maxInstances` | 10 | Runs at once; more wait their turn (the wait counts against their time; `resource-exhausted` if none came). |
| `reservedInstances` | 0 | Runs of the project's limit kept for this function, so busy ones can't take them all. |
| `isolate` | false | A process of its own: a run that blows past its memory stops only that process. On by itself when `memory` is over the shared process's limit. |

```ts
export const report = callable(handler, { memory: 512, maxInstances: 2, isolate: true });
```

**Processes.** A project's functions run in one shared Deno process, plus one per isolated function. Every run's
heap is capped (`FunctionsOptions.SharedProcessMemoryMb`, default 1024 MB; an isolated one's is its `memory` + 128): a
run past it stops its process, whose runs in flight fail with `resource-exhausted` ("Ran out of memory…"), and the
process starts again for the next. The processes' caps must fit the server's functions memory (`MemoryBudget`: 60%
of the container's or machine's memory, shared by every project; `WINCHE_FUNCTIONS_MEMORY_MB` sets it). An isolated
function that doesn't fit runs in the shared process (logged); settings asking for one that never could are refused.

**Server limits** (`FunctionsOptions`; Winche Server's **Server settings**): `MaxMemoryMb` (1024), `MaxConcurrency`
(runs at once per project, 16), `MaxProcesses` (4), `SharedProcessMemoryMb` (1024). The console's **Instances** tab
shows a function's instances, their heap, and what was replaced or restarted lately.

**Stopping.** When the app stops, functions take no new runs (`unavailable`: apps retry, triggered runs are delivered
again) and let the ones going on end within the host's shutdown timeout. `WincheFunctions.Warmed` completes once the
deployment's `minInstances` are loaded: a readiness check can wait for it.

## HTTP functions

`http.onRequest` answers web requests at `/functions/v1/http/{name}` (and any path below it): links in emails, other
services' webhooks. It gets a standard `Request` and returns a `Response`, text, JSON, or nothing (204).

```ts
import { http, HttpsError, secrets } from "@winche-technologies/winche-functions";
import { documents, files } from "@winche-technologies/winche-admin";

// A link sent by email, valid for days: /functions/v1/http/download/<token>
export const download = http.onRequest(async (req) => {
  const token = new URL(req.url).pathname.split("/").pop();
  const link = await documents.doc(`downloadLinks/${token}`).get();
  if (!link.exists) throw new HttpsError("not-found", "This link has ended.");
  return Response.redirect((await files.child(link.get("path")).downloadUrl()).url, 302);
}, { methods: ["GET"] });

// Another service's webhook, checked with its signing secret.
const paymentsSecret = secrets.define("PAYMENTS_SECRET");
export const paymentEvents = http.onRequest(async (req) => {
  const body = await req.text();
  if (!(await validSignature(body, req.headers.get("x-signature"), paymentsSecret.value())))
    return new Response("bad signature", { status: 401 });
  // …
  return { ok: true };
}, { methods: ["POST"], secrets: [paymentsSecret] });
```

- **Open to anyone:** there's no signed-in caller (`ctx.caller` is null). Check what you need yourself: a signature, a
  token in the link, an API key from a secret.
- `methods` limits them (others get 405); every method by default. `req.ip` is the caller's address.
- Request bodies up to 1 MB (`FunctionsOptions.MaxHttpRequestBytes`); larger ones get 413. A thrown `HttpsError` is an
  error answer with its status and message, as for callables.
- They can't be called as callables, and callables can't be reached here.

## Documents, files and users (@winche-technologies/winche-admin)

`documents`, `files` and `auth` from `@winche-technologies/winche-admin` read like the Dart SDKs (winche_database, winche_storage): you get
a **reference** from the service, `get()` returns a **snapshot**, and a snapshot's `reference` leads back. In functions
they act with the server's rights: access rules don't apply, unless the function acts as one of the app's users
(below).

```ts
import { schedule } from "@winche-technologies/winche-functions";
import { documents as db, FieldValue, Filter, Aggregate } from "@winche-technologies/winche-admin";

export const tidy = schedule("0 3 * * *", async () => {
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
export const weeklyReport = schedule("0 6 * * 1", async () => {
  const report = files.child("reports").child("weekly.pdf");            // a ChildReference
  await report.putData(await makePdf(), { mimeType: "application/pdf", contentDisposition: 'attachment; filename="weekly.pdf"' });
  const { url, expiresAt } = await report.downloadUrl();                // a link to send on
  const clip = await files.child("media/clip").putFromUrl(signedUrl, { headers: { authorization: `Bearer ${token}` } });
  const folder = await files.child("reports").listChildren();           // files and folders
});
```

- **Files:** `child(path)` gives a `ChildReference` (`name`, `path`, `parent`, `child()`): `getSnapshot()`
  (`exists`, `data`: size, type, metadata, version, …), `getData()` (bytes) / `getText()`, `putData(bytes | text,
  { mimeType, metadata, contentDisposition, cacheControl })`, `putFromUrl(url, { headers, … })`, `list()` (one page)
  / `listChildren()` (all), `updateMetadata(metadata, { cacheControl, ifVersion })`, `delete()` (a folder: all of
  it), `downloadUrl()`.
- `getData` and `putData` move up to 32 MB through the function; `putFromUrl` copies up to 5 GB on the server
  (from a bucket's signed or authorized link), never through the function. A write is an upload like any other:
  `onFile.uploaded` triggers and webhooks run.

```ts
export const promote = callable(async (data: { uid: string }) => {
  const user = auth.user(data.uid);                                      // a UserReference
  await user.setCustomClaims({ ...(await user.getCustomClaims()), role: "admin" });
  const snap = await user.get();                                         // a UserSnapshot
  return { email: snap.data?.email, claims: snap.customClaims };
});
```

- **Users:** `auth.user(uid)` gives a `UserReference`: `get()` (`exists`, `data`: email, name, providers,
  claims, …), `getCustomClaims()`, `setCustomClaims(claims)` (in the user's next token).
- **References in documents:** a reference written into a document (`db.doc(…)`, `files.child(…)`,
  `auth.user(…)`, or `Link.document / file / user`) is stored typed and reads back as a `Link` (`kind`, `target`).
  `expand(["patient", "patient.doctor", "scan", "author"])` on a get or query makes `link.data()` what it points to:
  the document's fields, the file's record, the user's account. Paths walk maps, lists and references; 10 paths,
  4 deep, the database's `ExpandLimit` (default 100) per call at most.
- **Trigger events** carry the same snapshots: a document event's `data` and `before` (`exists` false when created or
  deleted), a file event's `file`, a user event's `user`.
- **Calling functions:** `functions.call("name", data)` runs a callable and returns its result (an `HttpsError`
  arrives as a `WincheError` with its code).

### Acting as one of the app's users

`as(user)` gives the same API acting as one of the app's users (`ctx.caller` in a callable): every documents and files
call is checked against the project's access rules for them, as if the app had made it, and refused with
`permission-denied`; `functions.call` passes them as the caller. The plain SDK stays available beside it:

```ts
import { callable } from "@winche-technologies/winche-functions";
import * as admin from "@winche-technologies/winche-admin";

export const leaveWard = callable(async (data: { ward: string }, ctx) => {
  const me = admin.as(ctx.caller!);
  await me.documents.doc(`wards/${data.ward}/members/${ctx.caller!.uid}`).delete();          // the rules decide
  await admin.documents.collection("audit").add({ who: ctx.caller!.uid, left: data.ward });   // the function's own record
});
```

`recursiveDelete` stays the server's; a folder delete as a user goes file by file, each checked. `auth` and `assets`
have no rules for app users and refuse `as(user)`.

### From console pages and other servers

The same SDK works outside functions: in a console page it acts as the person who opened the page (see Console
pages); anywhere else `initializeAdmin({ server, project, token })` acts as the token's account. Either way their role
in the project limits it: a viewer reads; a member also writes documents, files and the app's users; an admin may also
call functions. A page brings its own copy, like any dependency (a bundler, or the built file in the page's folder).

## Resources

The functions folder's `resources/` folder is deployed with the code: files the code needs, such as fonts, templates
and translations it renders with. Declare one (a handle) and read it while a function runs:

```ts
import { resources } from "@winche-technologies/winche-functions";

const german = resources.define("translations/de.json");
const font = resources.define("fonts/Roboto-400.ttf");

// in a function:
const de = JSON.parse((await german.text())!);
const bytes = await font.bytes();                                     // Uint8Array, or null when there's none
const all = await resources.list("translations");                    // ["translations/de.json", …]
```

- Read-only, and part of the deployment: they change only when it does, and rolling back brings the old ones back.
- Read while a function runs: at a module's top level (before any run) they throw.
- `winche functions deploy` sends each file's SHA-256 first and uploads only contents the server doesn't have, so a
  large font goes up once, not on every deploy. Contents are kept once, in the project's database, however many
  versions use them. Up to 2000 resources, 64 MB each.
- Files under `resources/` aren't code: import JSON your code needs from elsewhere in the folder.
- In the emulators, saving a file under `resources/` deploys again, as code does.

## Assets

`assets` (@winche-technologies/winche-admin) reads the app's assets (Winche Assets: content published from the CLI as releases, followed through
channels) with the server's rights — no rules. Not to be confused with resources, which are the function's own files.

```ts
import { callable, onAssets } from "@winche-technologies/winche-functions";
import { assets } from "@winche-technologies/winche-admin";

export const strings = callable(async (p: { lang: string }) => {
  const release = await assets.channel("production").get();       // the release it points at, or null
  return release?.file(`translations/${p.lang}.json`).json();     // also bytes(), text(), downloadUrl()
});

// A release published (released), or a channel pointed at one (channelMoved); { channel } narrows it.
export const announce = onAssets.released(async (e, ctx) => {
  ctx.log(`Release #${e.release} on ${e.channel}: ${e.note}`);
}, { channel: "production" });
```

`assets.release(n)`, `channels()` and `releases()` list the rest. A file is read whole, up to 32 MB; larger ones
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
in the console (the **Secrets** page) or with `winche secrets:set NAME`, kept encrypted with the master key, and
never shown again. Declare each once (a handle), give it to the functions that read it, and read its value while one
runs:

```ts
import { onDocument, secrets } from "@winche-technologies/winche-functions";

const mailKey = secrets.define("MAIL_API_KEY");

export const sendReport = onDocument.created("reports/{id}", async () => {
  const key = mailKey.value();                         // the current value; throws when not listed or not set
  await fetch("https://mail.example.com/send", { method: "POST", headers: { authorization: `Bearer ${key}` }, body: "…" });
}, { secrets: [mailKey] });
```

- **Names:** capitals, digits and underscores, starting with a letter (`MAIL_API_KEY`).
- **Only the functions that list it** may read it: another one's `value()` throws, naming both. `isSet()` says whether
  it's set. The admin SDK never has secrets, so pages and other servers can't read them.
- **Changes** apply from the next run; there's nothing to deploy.
- **Logs and errors** show `••••` in place of a secret's value (values of 4 characters or more).
- **The console lists** each secret's name, when and by whom it was set, and which functions list it, including the
  ones a function lists but nobody has set yet.
- **References:** a secret can instead say where it's kept (`aws-ssm:/app/mail-key`, `aws-sm:app/ses#secretAccessKey`,
  with Winche.Secrets.Aws): read when a function runs, kept a few minutes. One that can't be read fails the run with
  `failed-precondition` and the reason (never a value). See Winche.Secrets.
- They're kept by Winche.Secrets, which needs Winche.Core's `AddWincheConfig` with a master key (Winche Server has it);
  a deployment whose functions list secrets without it is refused.

## Console pages

A project can add pages of its own to the console: dashboards, admin tools, anything its team needs. The page is
yours: plain HTML, JavaScript and CSS, or the build output of any framework, in a folder next to `index.ts`. The console
shows it in its sidebar (under **Project pages**) and has no say in what it does or how it looks. In it,
`@winche-technologies/winche-admin` acts as the person who opened it, limited by their role in the project.

```ts
import { pages } from "@winche-technologies/winche-functions";

// console/reports/index.html (and its scripts, styles, images) is the page.
export const reports = pages({ title: "Reports", files: "console/reports", icon: "chart-bar", minRole: "viewer" });
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
// console/reports/app.js — the page's own copy of @winche-technologies/winche-admin (or bundled with it).
import { documents } from "./admin.js";

const visits = await documents.collection("visits").where("at", ">=", weeksAgo(4)).count();
document.querySelector("#count").textContent = `${visits} in the last 4 weeks`;
document.querySelector("#close").onclick = async () => {
  if (!confirm("Close ward 3? Nobody can be admitted there until it's opened again.")) return;
  try { await documents.doc("wards/3").update({ closedAt: new Date() }); }
  catch (e) { alert(e.message); }   // e.code: permission-denied (a viewer), …
};
```

- **Options:** `pages({ title, files, icon, minRole, order })`. `files` is the folder; it must have an `index.html`.
  `icon` is a [Tabler icon](https://tabler.io/icons) name. `minRole` (default `viewer`) decides who sees it. `order`
  (default 0) sorts the sidebar.
- **Roles are checked on the server** for every call: a viewer reads; a member also writes documents, files and the
  app's users. Pages can't call functions (callables are the apps'). Writes show in the document history as the
  person's.
- **Work that needs the server's rights** (another service's API with a secret, a long job) is asked for with a
  document the page writes, e.g. `jobs/{id}` with `{ action, data }`: an `onDocument.created` trigger does it with the
  server's rights and writes the answer or progress back to that document, which the page reads. Whoever may write
  there may ask.
- **Sandboxed:** the page runs in a frame with an origin of its own and no cookies of the console's, so it can't call
  the console's API or read its session: its @winche-technologies/winche-admin calls go to the console over a channel only it holds. Its
  scripts, styles, images and fonts come from its own folder (or inline); it can't fetch from the network
  (`connect-src 'none'`). Links it opens go to a new tab. If it navigates the frame elsewhere, it's disconnected and
  asks to be reloaded.
- **The SDK is the page's:** bundle `@winche-technologies/winche-admin` with the page (Vite: `base: "./"`), or put its built file in the
  page's folder and import it from there; the console gives a page only the channel the SDK finds by itself. The CLI
  builds pages named in `winche.json` (`"pages"`) when it deploys.
- **Files:** `.html`, `.js`, `.mjs`, `.css`, `.json`, `.map`, `.svg`, `.txt`, `.png`, `.jpg`, `.gif`, `.webp`, `.ico`,
  `.woff`, `.woff2`, deployed with `winche functions deploy` (images and fonts as base64). They're versioned and
  rolled back with the functions.
- The emulators have no console.

## Plugins

A plugin is a package of functions a project installs instead of writing them: an integration with another service,
say, set up with a few settings and its API key. It's an npm package with a `winche-plugin.json` and a default export
made with `plugin()`; each project installs copies of it under ids of its own, from the console (**Functions →
Plugins**), the CLI (`winche plugins:install`) or `winche.json`.

```ts
// The plugin package's index.ts
import { callable, onDocument, plugin, HttpsError } from "@winche-technologies/winche-functions";
import { documents } from "@winche-technologies/winche-admin";

export default plugin((p) => {
  const token = p.secret("AUTH_TOKEN");                     // this copy's own secret
  const send = async (to: string, body: string) => { /* fetch the service's API with token.value(), p.setting("FROM") */ };
  return {
    // Apps call it as functions.call("sms.send", …) for the copy installed as "sms".
    send: callable(async (d: { to: string; body: string }, ctx) => {
      if (!ctx.caller) throw new HttpsError("unauthenticated", "Sign in first.");
      return { id: await send(d.to, d.body) };
    }, { secrets: [token] }),
    // Or queued: a document in the collection the installer chose; sent when the device is back online.
    queued: onDocument.created(`${p.setting("QUEUE")}/{id}`, async (e) => {
      await documents.doc(e.path).update({ sent: await send(e.data.get("to"), e.data.get("body")) });
    }, { secrets: [token] }),
  };
});
```

```json
// winche-plugin.json, beside its package.json
{
  "title": "Text messages",
  "description": "Sends texts from a callable or a queue collection.",
  "icon": "message",
  "settings": {
    "FROM": { "label": "From number", "type": "string", "required": true },
    "QUEUE": { "label": "Queue collection", "type": "string", "default": "sms" }
  },
  "secrets": { "AUTH_TOKEN": { "label": "Auth token" } },
  "access": ["Updates the documents of the queue collection.", "Calls the text message service's API."]
}
```

- **The manifest** can also say:
  - `billing`: what it may cost beside the server, shown before installing.
  - `section` on a setting: settings of one section are shown together.
  - `showIf` on a setting or secret, `{ "setting": "MODE", "is": "office" }` (or a list) or `{ "setting": "URL", "set": true }`:
    shown, and needed when required, only then.
  - `setup`: steps to do after installing (add a webhook in the other service…), `{ "text": "…", "showIf": … }` each, shown
    on the installed copy's page with `{{function:name}}` (that function's address), `{{setting:NAME}}`,
    `{{secret:NAME}}` (the copy's project secret) and `{{id}}` filled in.
  The console's page about a package shows its README.md and its package.json's description, author, homepage, repository
  and license; npm packages with the keyword `winche-plugin` are found by its search.
- **Installing** a copy under an id (lowercase letters, digits and `-`, e.g. `sms`) is a new functions version: its
  functions run beside the project's own as `<id>.<name>` (`sms.send`), with the same instances, logs and limits.
  Two copies of a package (other ids, other settings) are two sets of functions.
- **Settings** are `string`, `number`, `integer`, `boolean` or `select` (one of `options`), with `label`,
  `description`, `required` and `default`. They're checked when installing; the code reads them with
  `p.setting(name)` (or `p.settings`) when `plugin()` makes the copy, so trigger paths can come from them.
- **Secrets:** each copy has its own, `<ID>__<NAME>` among the project's (`SMS__AUTH_TOKEN`): set like any other
  (console, `winche secrets:set`), values or references. A plugin reads only the ones its manifest lists:
  a deployment where it lists another (`secrets.define("MAIL_KEY")`) is refused.
- **Versions:** a copy stays on its exact version. The console shows when a newer one is out, and what it changes;
  upgrading keeps the settings it still has and asks for new required ones. Every change (install, settings, upgrade,
  removal) is in the version list, and rolling back brings back that version's plugins with its code.
- **Removing** one takes its functions out; what it wrote stays. Its secrets go too when asked.
- **From winche.json:** `"plugins": { "sms": { "package": "…", "version": "1.2.0", "settings": { … } } }` makes a
  `functions deploy` install exactly these (the emulators too). Without `"plugins"`, a deploy keeps what's installed.
- **Where they come from:** npm (`FunctionsOptions.PluginRegistry`), each package checked against npm's integrity hash
  and its manifest read before anything runs; or folders the server provides (`FunctionsOptions.ProvidedPlugins`, for
  servers without npm, and tests). The chosen version is exempt from Deno's wait for new releases; its own dependencies
  aren't.
- **Rights:** a plugin's functions act with the server's rights, as the project's own do: install only packages you
  trust. The manifest's `access` is what it says it does, shown before installing; it isn't enforced.
- Plugins can't add console pages. `WincheFunctions.InstallPluginAsync`, `ConfigurePluginAsync`, `UpgradePluginAsync`,
  `UninstallPluginAsync` and `PluginsAsync` are the API.

## Before-functions

They run **before** a write is saved, inside it, and can refuse it or change it:

```ts
import { onDocument, onFile, onUser, HttpsError } from "@winche-technologies/winche-functions";

export const guardRecords = onDocument.willBeWritten("records/{id}", async (e, ctx) => {
  if (e.before.get("signedOff")) throw new HttpsError("permission-denied", "This record is signed off.");
  if (e.operation !== "delete") return { set: { updatedBy: e.caller?.uid ?? "server" }, remove: ["draft"] };
});

export const scans = onFile.willBeWritten("scans/{rest=**}", async (e) => ({ metadata: { checked: false }, cacheControl: "private" }));

export const closedDomains = onUser.willBeCreated(async (e) => {
  if (e.email?.endsWith("@example.org")) throw new HttpsError("permission-denied", "Sign-ups from that domain are closed.");
});
export const gate = onUser.willSignIn(async (e) => { /* e.user, e.provider, e.ipAddress */ });
```

- **When:** `onDocument.willBeWritten` and `onFile.willBeWritten` run after the access rules, for apps' writes and the server's own
  (each document of a batch or recursive delete, each file under a deleted folder). `onUser.willBeCreated` and
  `onUser.willSignIn` run on every sign-up and sign-in (after the credentials check out).
- **Refusing:** throw `HttpsError`: the caller gets `permissionDenied` (sign-ups and sign-ins: `DENIED`) with its message,
  and nothing is saved.
- **Changing:** documents return `{ set: { "dotted.path": value }, remove: ["field"] }`; files return
  `{ metadata: { key: value | null }, contentDisposition, cacheControl }`. A delete can only be refused. Several
  before-functions on one path run in export order, each seeing the previous one's changes.
- **Quick, and reading only:** 5 seconds unless `timeoutSeconds` says otherwise (at most 30): the write, and its locks,
  wait for it. @winche-technologies/winche-admin only reads here; side effects belong in `onDocument.created`… triggers.
- **Failures:** a function that crashes or runs out of time refuses the write ("couldn't be checked"), unless it's
  `{ failOpen: true }`, which lets the write through and logs a warning.
- Needs `AddWincheFunctionsForDatabase()` / `ForStorage()` / `ForAuth()` (they run them); without them a deploy says so.

## Upgrading to 3.2

- New: `ctx.app` and `{ integrity: true }` for callables (above), with @winche-technologies/winche-functions 3.2. Nothing
  changes for code without them; a host without `AppProof` gives every call `ctx.app` null.
- A callable with `{ integrity: true }` is marked protocol 2 (`FunctionsSdk.IntegrityProtocol`): hosts on Winche.Functions
  3.1 or older refuse to deploy it instead of running it open to anyone. This host takes protocols 1 and 2.

## Upgrading to 3.1

- New: plugins (above), with `plugin()` from @winche-technologies/winche-functions 3.1; `ctx.byServer` (who called). Deployments keep their plugins
  (a new column, added by itself). Nothing changes for code without them.

## Upgrading to 3.0

Data leaves `ctx` for its own SDK, and the trigger names say when they run. A deploy that still uses the old API is
refused, naming each use and what replaces it.

| 2.x | 3.0 |
| --- | --- |
| `ctx.documents`, `ctx.files`, `ctx.auth`, `ctx.assets` | `import { documents, files, auth, assets } from "@winche-technologies/winche-admin"` |
| `ctx.secrets.get("KEY")`, `{ secrets: ["KEY"] }` | `const key = secrets.define("KEY")` (top level), `{ secrets: [key] }`, `key.value()` / `key.isSet()` |
| `ctx.resources.read(path)` / `readText` / `list` / `has` | `resources.define(path).bytes()` / `text()` / `exists()`, `resources.list(folder)` |
| `ctx.functionName` | `ctx.name` |
| `documents.onCreated` / `onUpdated` / `onDeleted` / `onWritten` / `beforeWrite` | `onDocument.created` / `updated` / `deleted` / `written` / `willBeWritten` |
| `files.onUploaded` / `onDeleted` / `onMetadataUpdated` / `beforeWrite` | `onFile.uploaded` / `deleted` / `metadataUpdated` / `willBeWritten` |
| `users.onCreated` / `onDeleted` / `onSignedIn` / `onDisabled` / `beforeCreate` / `beforeSignIn` | `onUser.created` / `deleted` / `signedIn` / `disabled` / `willBeCreated` / `willSignIn` |
| `assets.onReleased` / `onChannelMoved` | `onAssets.released` / `channelMoved` |
| `consolePage(…)` | `pages(…)` |
| `consoleAction(…)`, `ctx.console`, `window.winche` in pages | gone: pages use @winche-technologies/winche-admin as their viewer; server work is asked for with a document a trigger answers |
| `FieldValue`, `Filter`, `Link`, `DocumentSnapshot`, … from `@winche-technologies/winche-functions` | from `@winche-technologies/winche-admin` |

New: `as(user)` (the access rules applied for one of the app's users), `functions.call`, `@winche-technologies/winche-functions/testing`
(`withSecrets`, `withResources`, `withServer`, `testContext`) and the `@winche-technologies/winche-admin` npm package for other servers
(its own repository, winche-admin-typescript-sdk). The SDKs are npm packages the project pins in `deno.json`
(`winche functions init`, CLI 3, writes it); the server no longer brings them.

## Upgrading to 2.0

- Secrets are kept by [Winche.Secrets](https://github.com/FlameOfUdun/winche-secrets) (a package of its own, out of
  Winche.Core 2.0); `AddWincheFunctions` adds it. A secret can now be a reference to AWS Secrets Manager or Parameter
  Store (Winche.Secrets.Aws): functions read it with `ctx.secrets.get` as before.
- New: Winche.Functions.Assets (`ctx.assets` and the assets triggers), `AddWincheFunctionsForAssets()`.
- Built on Winche.Core 2.0, Winche.Auth 2.0, Winche.Storage 11 and Winche.Webhooks 2.0.

## Upgrading to 2.2

- Memory and instances (above): functions that set nothing get 256 MB per instance and at most 10 runs at once; one
  holding more than 256 MB after a run is now replaced. Set `memory` on functions that keep large caches.
- Each run's heap is capped (1024 MB in the shared process): a runaway run stops the shared process's runs in flight,
  not the server. Isolate functions that might.
- Hosts: `WincheFunctions.Warmed` for readiness; a stop waits for runs going on (raise the host's `ShutdownTimeout`
  for long runs).

## Upgrading to 2.1

- Runs reuse warm workers (see How they run): a call starts in well under a millisecond instead of loading the code
  each time. What a module keeps at the top level now lasts between runs on the same worker, and a call to `ctx.*`
  after a run has answered is refused. `{ fresh: true }` keeps the old behaviour (a new sandbox every run) for a
  function.

## License

Elastic License 2.0 (`LICENSE`). The TypeScript SDKs functions are written with are MIT, in their own repositories;
the tests carry built copies (`scripts/update-test-packages.sh`).
