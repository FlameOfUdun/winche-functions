/**
 * @winche/functions — write Winche functions.
 *
 *   import { documents, callable, schedule } from "@winche/functions";
 *
 *   export const onVisit = documents.onCreated("patients/{patientId}/visits/{visitId}", async (e, ctx) => {
 *     await ctx.db.update(`patients/${e.params.patientId}`, { lastVisit: e.data?.fields.at ?? null });
 *   });
 *
 *   export const hello = callable(async (data: { name: string }, ctx) => ({ greeting: `Hello ${data.name}`, uid: ctx.auth?.uid }));
 *
 *   export const nightly = schedule("0 2 * * *", async (_e, ctx) => ctx.log("Nightly clean-up"));
 *
 * Functions run on the Winche server in a sandbox: they can use the network (fetch) but not its files or settings.
 * `ctx.db`, `ctx.files` and `ctx.users` act with the server's own rights (access rules don't apply).
 */

export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

/** What can be written to a document: JSON, with Dates as timestamps (and references from `ref`). */
export type Writable = null | boolean | number | string | Date | Writable[] | { [key: string]: Writable };

/** A document as functions see it: fields as plain JSON (timestamps as ISO text, references as paths). */
export interface DocumentData {
  path: string;
  id: string;
  collection: string;
  createTime: string;
  updateTime: string;
  fields: { [field: string]: Json };
}

export interface Auth {
  uid: string;
  /** The caller's token claims (custom claims among them). */
  token: { [claim: string]: Json };
}

export interface DocumentEvent {
  type: "created" | "updated" | "deleted";
  path: string;
  /** The path pattern's captures, e.g. { patientId: "p1" }. */
  params: { [name: string]: string };
  /** The document now (null when deleted). */
  data: DocumentData | null;
  /** The document before (null when created). */
  before: DocumentData | null;
  /** The same on retries: use it to ignore repeats. */
  id: string;
  at: string;
}

export interface FileRecord {
  path: string;
  directory: string;
  mimeType: string;
  sizeBytes: number;
  metadata?: { [key: string]: Json } | null;
  [key: string]: unknown;
}

export interface FileEvent {
  type: "uploaded" | "deleted" | "metadataUpdated";
  path: string;
  params: { [name: string]: string };
  /** The file's record (null when deleted). */
  file: FileRecord | null;
  id: string;
  at: string;
}

export interface UserInfo {
  uid: string;
  email: string | null;
  emailVerified: boolean;
  displayName: string | null;
  disabled: boolean;
  customClaims: { [claim: string]: Json };
  [key: string]: unknown;
}

export interface UserEvent {
  type: "created" | "deleted" | "disabled" | "enabled" | "claimsChanged" | "signedIn";
  uid: string;
  user: UserInfo;
  /** For signedIn: how (password, google, emailLink, …). */
  provider: string | null;
  id: string;
  at: string;
}

/** A write about to be saved, for documents.beforeWrite. Throw HttpsError to refuse it; return changes to make. */
export interface BeforeDocumentEvent {
  operation: "create" | "update" | "delete";
  path: string;
  params: { [name: string]: string };
  /** The document as stored now (null when created). */
  before: DocumentData | null;
  /** The fields it will have (null when deleted), with earlier before-functions' changes. */
  data: { fields: { [field: string]: Json } } | null;
  /** Who made the request (null when signed out, or the app's own server code). */
  auth: Auth | null;
}

/** Changes to the document being written: fields to set (dotted paths; Dates are timestamps) and to remove. */
export interface DocumentChanges {
  set?: { [fieldPath: string]: unknown };
  remove?: string[];
}

/** A file write about to be saved, for files.beforeWrite (registering, a metadata change, a delete). */
export interface BeforeFileEvent {
  operation: "create" | "update" | "delete";
  path: string;
  params: { [name: string]: string };
  /** The file as stored now (null when new). */
  before: FileRecord | null;
  /** What will be saved (null when deleted). */
  file: { mimeType: string; sizeBytes: number; metadata: { [key: string]: Json }; contentDisposition: string | null; cacheControl: string | null } | null;
  auth: Auth | null;
}

/** Changes to the file being written: metadata keys to set (null removes one), and its download headers (null clears). */
export interface FileChanges {
  metadata?: { [key: string]: Json };
  contentDisposition?: string | null;
  cacheControl?: string | null;
}

/** Someone about to become a user (users.beforeCreate). Throw HttpsError to refuse; its message reaches them. */
export interface BeforeCreateEvent {
  /** Null for guests and accounts without an email. */
  email: string | null;
  /** password, anonymous, emailLink, custom, or a provider (google, apple, keycloak, …). */
  provider: string;
  ipAddress: string | null;
}

/** A user about to be signed in (users.beforeSignIn), their credentials already checked. */
export interface BeforeSignInEvent {
  user: UserInfo;
  provider: string;
  ipAddress: string | null;
}

export interface ScheduleEvent {
  /** When this run was due. */
  scheduledAt: string;
  id: string;
}

export type Where = [field: string, op: "==" | "!=" | "<" | "<=" | ">" | ">=" | "array-contains" | "in", value: Json | Date];

export interface Db {
  /**
   * The document, or null. With `expand`, the references at those paths ("patient", "patient.doctor", "team",
   * "scan", "author") are what they point to: a document (DocumentData, its own fields expanded along the rest of
   * the path), a file's record, a user's account; one that doesn't exist or can't be read stays a path or uid.
   * 10 paths, 4 deep, 100 at most.
   */
  get(path: string, options?: { expand?: string[] }): Promise<DocumentData | null>;
  /** Writes the whole document (or only the given fields with { merge: true }). */
  set(path: string, data: { [field: string]: Writable }, options?: { merge?: boolean }): Promise<void>;
  /** Changes the given fields of an existing document. */
  update(path: string, data: { [field: string]: Writable }): Promise<void>;
  /** Deletes the document; with { recursive: true } everything under it too. */
  delete(path: string, options?: { recursive?: boolean }): Promise<number>;
  /** Documents of a collection, filtered and sorted. */
  /**
   * Documents of a collection, filtered and sorted (by document name when no orderBy), up to `limit` (default 100,
   * at most 1000). For the next page, pass the last document's path as `startAfter`. Dates in `where` are timestamps.
   */
  query(collection: string, query?: { where?: Where[]; orderBy?: [field: string, direction?: "asc" | "desc"][]; limit?: number; startAfter?: string; expand?: string[] }): Promise<DocumentData[]>;
}

export interface WriteFileOptions {
  /** Default: text/plain for text, application/octet-stream for bytes. */
  mimeType?: string;
  metadata?: { [key: string]: Json };
  /** What browsers do with it when downloaded, e.g. 'attachment; filename="report.pdf"'. */
  contentDisposition?: string;
  cacheControl?: string;
}

export interface Files {
  /** The files directly in a folder. */
  list(folder: string): Promise<FileRecord[]>;
  /** A file's record (size, type, metadata), or null. */
  get(path: string): Promise<FileRecord | null>;
  /** A file's bytes (up to 32 MB), or null when there's no file there. */
  read(path: string): Promise<Uint8Array | null>;
  /** A file's contents as UTF-8 text, or null. */
  readText(path: string): Promise<string | null>;
  /** Writes a file (new or replacing one), up to 32 MB: its triggers and hooks run as for an upload. */
  write(path: string, data: Uint8Array | ArrayBuffer | string, options?: WriteFileOptions): Promise<FileRecord>;
  /** A link anyone holding it can download the file with, until expiresAt (the storage's link lifetime). */
  downloadUrl(path: string): Promise<{ url: string; expiresAt: string }>;
  delete(path: string): Promise<boolean>;
  /** Deletes every file whose path starts with the folder. */
  deleteUnder(folder: string): Promise<number>;
}

export interface Users {
  get(uid: string): Promise<UserInfo | null>;
  /** Replaces the user's custom claims (in their next token). */
  setClaims(uid: string, claims: { [claim: string]: Json } | null): Promise<void>;
}

export interface Secrets {
  /**
   * A secret's value (an API key, a password), set in the console (Functions → Secrets) or with
   * `winche functions secrets set NAME`. The function must declare it: `{ secrets: ["NAME"] }` in its options.
   * Throws when it isn't declared or isn't set. Values are hidden in the function's logs.
   */
  get(name: string): string;
  /** Whether a declared secret is set. */
  has(name: string): boolean;
}

/** Who ran a console action: their console account and their role in this project. */
export interface ConsoleUser {
  email: string | null;
  role: ConsoleRole;
}

export type ConsoleRole = "viewer" | "member" | "admin";

export interface Context {
  /** This function's name (its export name). */
  functionName: string;
  /** For callables: who called (null when signed out). */
  auth: Auth | null;
  db: Db;
  files: Files;
  users: Users;
  /** The secrets this function declared in its options. */
  secrets: Secrets;
  /** For console actions: who ran it from the console (null otherwise). */
  console: { user: ConsoleUser } | null;
  /** Writes to the server's logs (Logs → functions); console.log does too. */
  log(...args: unknown[]): void;
}

export interface FunctionOptions {
  /**
   * Seconds it may run: default 60 (the project's default), at most the server's longest (9 minutes unless the server
   * allows more). The console can give a function its own time over this. Before-functions: default 5, at most 30.
   */
  timeoutSeconds?: number;
  /** The secrets it reads with ctx.secrets.get: names in capitals, digits and underscores (e.g. "SES_SECRET_KEY"). */
  secrets?: string[];
}

export interface BeforeOptions {
  /** Default 5, at most 30: the write (and its locks) waits for it. */
  timeoutSeconds?: number;
  /** The secrets it reads with ctx.secrets.get. */
  secrets?: string[];
  /**
   * When the function crashes or runs out of time: refuse the write (the default), or let it through as it is.
   * A thrown HttpsError always refuses it.
   */
  failOpen?: boolean;
}

/**
 * References to store in documents and follow with `expand`: `{ patient: ref.doc("patients/p1"),
 * scan: ref.file("scans/a.png"), author: ref.user(ctx.auth!.uid) }`. Read back, they are their paths and uids.
 */
export const ref = {
  doc: (path: string) => ({ $documentReference: path }),
  file: (path: string) => ({ $fileReference: path }),
  user: (uid: string) => ({ $userReference: uid }),
};

/** The kinds of error every Winche API and SDK shares: the `code` an app receives. */
export type FunctionsErrorCode =
  | "cancelled" | "unknown" | "invalid-argument" | "deadline-exceeded" | "not-found" | "already-exists"
  | "permission-denied" | "resource-exhausted" | "failed-precondition" | "aborted" | "out-of-range"
  | "unimplemented" | "internal" | "unavailable" | "data-loss" | "unauthenticated";

/**
 * Throw it from a callable to send the app a code, a message and, optionally, details (anything JSON can carry).
 * Other errors reach the app as "internal" with no message; theirs stays in the logs.
 */
export class HttpsError extends Error {
  constructor(public code: FunctionsErrorCode, message: string, public details?: unknown) {
    super(message);
    this.name = "HttpsError";
  }
}

/** A page of the project's own in the console: its files are yours (HTML, JS, CSS), shown in a sandboxed frame. */
export interface ConsolePageOptions {
  /** In the console's sidebar. */
  title: string;
  /** The folder of the page's files, next to index.ts (e.g. "console/reports"): it must hold an index.html. */
  files: string;
  /** A Tabler icon name (e.g. "chart-bar"); the default is a page icon. */
  icon?: string;
  /** Who sees it (default viewer). Actions check their own role too. */
  minRole?: ConsoleRole;
  /** Where it goes in the sidebar, lowest first (default 0, then by title). */
  order?: number;
}

export interface ConsoleActionOptions extends FunctionOptions {
  /** Who may run it from the console (default admin). */
  minRole?: ConsoleRole;
}

export type Trigger =
  | { kind: "documents"; events: string[]; path: string }
  | { kind: "files"; events: string[]; path: string }
  | { kind: "users"; events: string[] }
  | { kind: "schedule"; cron: string; timeZone?: string }
  | { kind: "callable" }
  | { kind: "consolePage" }
  | { kind: "http"; events?: string[] }
  | { kind: "consoleAction" }
  | { kind: "before"; on: "documents" | "files" | "users"; events: string[]; path?: string };

/** What an exported function is: Winche reads these when the code is deployed. */
export interface WincheFunction<A = unknown, R = unknown> {
  __winche: 1;
  trigger: Trigger;
  options: FunctionOptions;
  handler: (arg: A, ctx: Context) => R | Promise<R>;
}

const define = <A, R>(trigger: Trigger, handler: (arg: A, ctx: Context) => R | Promise<R>, options: FunctionOptions = {}): WincheFunction<A, R> =>
  ({ __winche: 1, trigger, options, handler });

type Handler<E> = (event: E, ctx: Context) => unknown;

/**
 * Before-functions run before a write is saved, inside it: the write waits for them, so keep them quick. They can
 * refuse it (throw HttpsError: its message reaches the caller) or change it (return the changes). In them ctx.db,
 * ctx.files and ctx.users only read. Side effects (emails, other writes) belong in the onCreated… triggers.
 */
const before = <E, R>(on: "documents" | "files" | "users", events: string[], path: string | undefined, handler: (e: E, ctx: Context) => R | Promise<R>,
  options?: BeforeOptions) => define({ kind: "before", on, events, path }, handler, options as FunctionOptions);


/** Document triggers. Paths are patterns: {name} matches one part (in event.params), {rest=**} the rest. */
export const documents = {
  onCreated: (path: string, handler: Handler<DocumentEvent>, options?: FunctionOptions) => define({ kind: "documents", events: ["created"], path }, handler, options),
  onUpdated: (path: string, handler: Handler<DocumentEvent>, options?: FunctionOptions) => define({ kind: "documents", events: ["updated"], path }, handler, options),
  onDeleted: (path: string, handler: Handler<DocumentEvent>, options?: FunctionOptions) => define({ kind: "documents", events: ["deleted"], path }, handler, options),
  /** Created, updated or deleted. */
  onWritten: (path: string, handler: Handler<DocumentEvent>, options?: FunctionOptions) => define({ kind: "documents", events: ["created", "updated", "deleted"], path }, handler, options),
  /** Before a document is created, updated or deleted (each document of a batch, and of a recursive delete, once). */
  beforeWrite: (path: string, handler: (e: BeforeDocumentEvent, ctx: Context) => DocumentChanges | void | Promise<DocumentChanges | void>, options?: BeforeOptions) =>
    before("documents", ["write"], path, handler, options),
};

/** File triggers. */
export const files = {
  onUploaded: (path: string, handler: Handler<FileEvent>, options?: FunctionOptions) => define({ kind: "files", events: ["uploaded"], path }, handler, options),
  onDeleted: (path: string, handler: Handler<FileEvent>, options?: FunctionOptions) => define({ kind: "files", events: ["deleted"], path }, handler, options),
  onMetadataUpdated: (path: string, handler: Handler<FileEvent>, options?: FunctionOptions) => define({ kind: "files", events: ["metadataUpdated"], path }, handler, options),
  /** Before a file is registered or replaced, its metadata changes, or it's deleted (each file under a deleted folder too). */
  beforeWrite: (path: string, handler: (e: BeforeFileEvent, ctx: Context) => FileChanges | void | Promise<FileChanges | void>, options?: BeforeOptions) =>
    before("files", ["write"], path, handler, options),
};

/** User triggers (Winche Auth). */
export const users = {
  onCreated: (handler: Handler<UserEvent>, options?: FunctionOptions) => define({ kind: "users", events: ["created"] }, handler, options),
  onDeleted: (handler: Handler<UserEvent>, options?: FunctionOptions) => define({ kind: "users", events: ["deleted"] }, handler, options),
  onSignedIn: (handler: Handler<UserEvent>, options?: FunctionOptions) => define({ kind: "users", events: ["signedIn"] }, handler, options),
  onDisabled: (handler: Handler<UserEvent>, options?: FunctionOptions) => define({ kind: "users", events: ["disabled"] }, handler, options),
  /** Before someone becomes a user: throw HttpsError to refuse the sign-up. */
  beforeCreate: (handler: (e: BeforeCreateEvent, ctx: Context) => void | Promise<void>, options?: BeforeOptions) =>
    before("users", ["create"], undefined, handler, options),
  /** Before a sign-in succeeds: throw HttpsError to refuse it. */
  beforeSignIn: (handler: (e: BeforeSignInEvent, ctx: Context) => void | Promise<void>, options?: BeforeOptions) =>
    before("users", ["signIn"], undefined, handler, options),
};

/** Runs on a schedule: a cron expression ("minute hour day month weekday"), in UTC unless timeZone is given. */
export const schedule = (cron: string, handler: Handler<ScheduleEvent>, options?: FunctionOptions & { timeZone?: string }) =>
  define({ kind: "schedule", cron, timeZone: options?.timeZone }, handler, options);

/** Called by apps (the SDKs' functions.call("name", data)); returns JSON to the caller. */
export const callable = <D = Json, R = Json>(handler: (data: D, ctx: Context) => R | Promise<R>, options?: FunctionOptions) =>
  define<D, R>({ kind: "callable" }, handler, options);

/**
 * A page in the console made of your own files: `consolePage({ title: "Reports", files: "console/reports" })` shows
 * console/reports/index.html in a sandboxed frame, with `window.winche` to run console actions (see the README).
 */
export const consolePage = (options: ConsolePageOptions): WincheFunction<never, null> =>
  define({ kind: "consolePage" }, () => null, options as unknown as FunctionOptions);

/**
 * Run from a console page (`winche.call("name", data)`) by someone whose role in the project is at least minRole
 * (default admin); apps can't call it. ctx.console.user says who. Returns JSON to the page.
 */
export const consoleAction = <D = Json, R = Json>(handler: (data: D, ctx: Context) => R | Promise<R>, options?: ConsoleActionOptions) =>
  define<D, R>({ kind: "consoleAction" }, handler, options);

export interface HttpOptions extends FunctionOptions {
  /** The methods it answers (GET, POST, PUT, PATCH, DELETE, HEAD); every method when not given. */
  methods?: ("GET" | "POST" | "PUT" | "PATCH" | "DELETE" | "HEAD")[];
}

/** The request an HTTP function gets: a standard Request, with the caller's address. */
export type HttpRequest = Request & { ip: string | null };

/**
 * Answers web requests at /functions/v1/http/{name} (and below it): links in emails, other services' webhooks. It gets
 * a standard Request and returns a Response (or text, or JSON). Anyone can call it: check what you need yourself (a
 * signature with ctx.secrets, a token in the link). Bodies up to 1 MB.
 */
export const http = {
  onRequest: (handler: (req: HttpRequest, ctx: Context) => Response | string | Json | void | Promise<Response | string | Json | void>, options?: HttpOptions) =>
    define({ kind: "http", events: options?.methods }, handler, options),
};
