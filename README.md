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
| `Winche.Functions` | Deploying (bundled with `deno bundle`, every version kept, roll back), running, callables (`POST /functions/v1/call/{name}`), schedules. Deployed through the console (`winche functions deploy`, as you or a CI token). |
| `Winche.Functions.Database` | `ctx.db`: get, set, update, delete (recursive too), query — with the server's rights; `get` and `query` take `expand`. |
| `Winche.Functions.Storage` | `ctx.files`: list, delete, deleteUnder. |
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
- **Timeouts**: 60 seconds unless `timeoutSeconds` says otherwise (at most 540). A function past its time is stopped.
- **Logs**: `console.log` and `ctx.log` go to the server's logs (category `Winche.Functions`), with each run's
  outcome and time.
- **Imports**: relative files, `npm:` and `jsr:` packages, and URLs, resolved when deploying.

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
