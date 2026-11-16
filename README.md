# Winche.Functions

JavaScript and TypeScript functions for the Winche stack. They run on the Winche server in [Deno](https://deno.com),
each run in its own sandboxed worker (network yes; the server's files, environment and programs no). They're written
in your editor (VS Code, with `winche functions init`) and deployed with `winche functions deploy`. The
console shows what's deployed, its versions (roll back) and runs callables.

```ts
import { callable, documents, files, users, schedule, HttpsError } from "@winche/functions";

// Called by apps: the SDKs' functions.call("hello", { name: "ann" })
export const hello = callable(async (data: { name: string }, ctx) => {
  if (!ctx.auth) throw new HttpsError("unauthenticated", "Sign in first.");
  const person = await ctx.db.get(`people/${data.name}`);
  return { greeting: `Hello ${person?.fields.first ?? data.name}` };
});

// When a document is created: {name} parts of the path are in e.params
export const onVisit = documents.onCreated("patients/{patientId}/visits/{visitId}", async (e, ctx) => {
  await ctx.db.update(`patients/${e.params.patientId}`, { lastVisit: new Date() });
});

export const onScan = files.onUploaded("scans/{rest=**}", async (e, ctx) => ctx.log("New scan", e.path));
export const welcome = users.onCreated(async (e, ctx) => ctx.users.setClaims(e.uid, { role: "patient" }));
export const nightly = schedule("0 2 * * *", async (_e, ctx) => {
  await fetch("https://example.com/nightly-report", { method: "POST" });
}, { timeZone: "Europe/London", timeoutSeconds: 300 });
```

| Package | |
| --- | --- |
| `Winche.Functions` | Deploying (bundled with `deno bundle`, every version kept, roll back), running, callables (`POST /functions/v1/call/{name}`), schedules, secrets (`ctx.secrets`), console pages and actions. Deployed through the console (`winche functions deploy`, as you or a CI token). |
| `Winche.Functions.Database` | `ctx.db`: get, set, update, delete (recursive too), query — with the server's rights; `get` and `query` take `expand`. |
| `Winche.Functions.Storage` | `ctx.files`: list, get, read / readText, write, downloadUrl, delete, deleteUnder. |
| `Winche.Functions.Auth` | `ctx.users`: get, setClaims (Winche Auth). |

## Install

```sh
dotnet add package Winche.Functions
dotnet add package Winche.Functions.Database   # triggers and the admin API for each product the app has
dotnet add package Winche.Functions.Storage
dotnet add package Winche.Functions.Auth
```

## Setup

```csharp
builder.Services.AddWincheHooks(o => o.ConnectionString = conn);          // triggers go through the hooks queue
builder.Services.AddWincheFunctions(o => o.ConnectionString = conn);
builder.Services.AddWincheFunctionsForDatabase().AddWincheFunctionsForStorage().AddWincheFunctionsForAuth();
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
- **Sandbox**: each run is a fresh worker with network access only. It can read its own bundle and its deployment's
  assets, nothing else on the machine. The bundle is kept as a file named by its content, so Deno compiles it once per
  server and every later run starts from its code cache: a large bundle (a PDF library) costs milliseconds per run,
  not the time to compile it again.

## HTTP functions

`http.onRequest` answers web requests at `/functions/v1/http/{name}` (and any path below it): links in emails, other
services' webhooks. It gets a standard `Request` and returns a `Response`, text, JSON, or nothing (204).

```ts
import { http, HttpsError } from "@winche/functions";

// A link sent by email, valid for days: /functions/v1/http/download/<token>
export const download = http.onRequest(async (req, ctx) => {
  const token = new URL(req.url).pathname.split("/").pop();
  const link = await ctx.db.get(`downloadLinks/${token}`);
  if (!link) throw new HttpsError("not-found", "This link has ended.");
  return Response.redirect((await ctx.files.downloadUrl(String(link.fields.path))).url, 302);
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

- **Open to anyone:** there's no signed-in caller (`ctx.auth` is null). Check what you need yourself: a signature, a
  token in the link, an API key from `ctx.secrets`.
- `methods` limits them (others get 405); every method by default. `req.ip` is the caller's address.
- Request bodies up to 1 MB (`FunctionsOptions.MaxHttpRequestBytes`); larger ones get 413. A thrown `HttpsError` is an
  error answer with its status and message, as for callables.
- They can't be called as callables, and callables can't be reached here.

## Files

`ctx.files` reads and writes the project's files with the server's rights:

```ts
export const weeklyReport = schedule("0 6 * * 1", async (_e, ctx) => {
  const pdf = await makePdf(await ctx.db.query("visits"));                       // a Uint8Array
  await ctx.files.write("reports/weekly.pdf", pdf, { mimeType: "application/pdf", contentDisposition: 'attachment; filename="weekly.pdf"' });
  const { url, expiresAt } = await ctx.files.downloadUrl("reports/weekly.pdf"); // a link to send on
});
```

- `read(path)` gives the bytes (`Uint8Array`), `readText(path)` UTF-8 text, `get(path)` the record (size, type,
  metadata); each is null when there's no file. `write(path, data, { mimeType, metadata, contentDisposition,
  cacheControl })` takes bytes or text, new or replacing a file, and returns its record.
- Up to 32 MB per read or write. A write is an upload like any other: `files.onUploaded` triggers and webhooks run.
- `downloadUrl(path)` lasts as long as the storage's links do (S3: its presigned link lifetime).
- Before-functions can `get` and `read`, not `write` or make links.

## Assets

The functions folder's `assets/` folder is deployed with the code: fonts, translations, templates, images, HTML
pages, any files. Functions read them with `ctx.assets`:

```ts
const de = JSON.parse((await ctx.assets.readText("translations/de.json"))!);
const font = await ctx.assets.read("fonts/Roboto-400.ttf");          // Uint8Array, or null when there's none
const all = await ctx.assets.list("translations");                    // ["translations/de.json", …]
if (ctx.assets.has("demos/intro.html")) return new Response(await ctx.assets.readText("demos/intro.html"), { headers: { "content-type": "text/html" } });
```

- Read-only, and part of the deployment: they change only when it does, and rolling back brings the old ones back.
- `winche functions deploy` sends each file's SHA-256 first and uploads only contents the server doesn't have, so a
  large font goes up once, not on every deploy. Contents are kept once, in the project's database, however many
  versions use them. Up to 2000 assets, 64 MB each.
- Files under `assets/` aren't code: import JSON your code needs from elsewhere in the folder.
- In the emulators, saving a file under `assets/` deploys again, as code does.

## Secrets

API keys and passwords a function needs (an email service's key, another service's client secret) are **secrets**: set
in the console (Functions → Secrets) or with `winche functions secrets set NAME`, kept encrypted with the master key, and
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
- They need Winche.Core's `AddWincheConfig` with a master key (Winche Server has it); a deployment that declares secrets
  without it is refused.

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
  const visits = await ctx.db.query("visits", { where: [["at", ">=", weeksAgo(data.weeks)]] });
  return { visits: visits.length };
}, { minRole: "viewer" });

export const closeWard = consoleAction(async (data: { ward: string }, ctx) => {
  if (!data.ward) throw new HttpsError("invalid-argument", "Which ward?");
  await ctx.db.update(`wards/${data.ward}`, { closedBy: ctx.console!.user.email, closedAt: new Date() });
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
  `ctx.console.user` says who in the action. Actions run with the server's rights, like every function: `ctx.db`,
  `ctx.files`, `ctx.users`, `ctx.secrets`.
- **Apps can't call actions**, and pages can't call callables: `/functions/v1/call` only runs callables.
- **Sandboxed:** the page runs in a frame with an origin of its own and no cookies of the console's, so it can't call
  the console's API or read its session. Its scripts, styles, images and fonts come from its own folder (or inline);
  it can't fetch from the network (`connect-src 'none'`): everything goes through `winche.call`. Links it opens go
  to a new tab. If it navigates the frame elsewhere, it's disconnected and asks to be reloaded.
- **Files:** `.html`, `.js`, `.mjs`, `.css`, `.json`, `.map`, `.svg`, `.txt`, `.png`, `.jpg`, `.gif`, `.webp`, `.ico`,
  `.woff`, `.woff2`, deployed with `winche functions deploy` (images and fonts as base64). They're versioned and
  rolled back with the functions.
- The emulators have no console: test a page's actions by calling them from your tests.

## Following references

Store references with `ref`: `ctx.db.set("visits/v1", { patient: ref.doc("patients/p1"), scan: ref.file("scans/a.png"),
author: ref.user(ctx.auth!.uid) })` (read back, they are their paths and uids).

`ctx.db.get(path, { expand: ["patient", "patient.doctor", "team", "scan", "author"] })` and
`ctx.db.query(collection, { …, expand })` return the references at those paths as what they point to: a document
(`DocumentData`, its own fields expanded along the rest of the path), a file's record, a user (`WincheUserInfo`).
Paths walk maps, lists (each item) and document references; one that doesn't exist stays a path or uid. With the
server's rights, like the rest of `ctx.db`: rules don't apply. 10 paths, 4 deep, and the database's `ExpandLimit` (default 100) per call at most.

## Before-functions

They run **before** a write is saved, inside it, and can refuse it or change it:

```ts
import { documents, files, users, HttpsError } from "@winche/functions";

export const guardRecords = documents.beforeWrite("records/{id}", async (e, ctx) => {
  if (e.before?.fields.signedOff) throw new HttpsError("permission-denied", "This record is signed off.");
  if (e.operation !== "delete") return { set: { updatedBy: e.auth?.uid ?? "server" }, remove: ["draft"] };
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
  wait for it. `ctx.db`, `ctx.files` and `ctx.users` only read here; side effects belong in `onCreated`… triggers.
- **Failures:** a function that crashes or runs out of time refuses the write ("couldn't be checked"), unless it's
  `{ failOpen: true }`, which lets the write through and logs a warning.
- Needs `AddWincheFunctionsForDatabase()` / `ForStorage()` / `ForAuth()` (they run them); without them a deploy says so.
