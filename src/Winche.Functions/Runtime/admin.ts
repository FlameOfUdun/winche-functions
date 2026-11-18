// SPDX-License-Identifier: MIT
/**
 * @winche/admin — a Winche project's data: documents, files, the app's users, assets, and calling functions.
 *
 *   import { documents, files, FieldValue } from "@winche/admin";
 *
 *   await documents.doc("patients/p1").update({ visits: FieldValue.increment(1) });
 *   const report = await files.child("reports/a.pdf").getData();
 *
 * The same API wherever it runs; only who it acts as differs:
 *
 * - in functions: the server (full rights, access rules don't apply);
 * - in a console page: the person who opened it, limited by their role in the project (viewer reads, member writes
 *   data, admin does everything);
 * - anywhere else: `initializeAdmin({ server, project, token })`, limited by the token's role.
 *
 * `as(user)` gives the same API acting as one of the app's users: every documents and files call is checked against
 * the project's access rules for them, as if the app had made it.
 *
 * References (`doc`, `collection`, `child`, `user`) and the snapshots their reads return read like the Dart SDKs.
 */

export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Values
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/** A latitude / longitude pair (geoPointValue). */
export class GeoPoint {
  constructor(readonly latitude: number, readonly longitude: number) {}
  isEqual(other: GeoPoint) { return other instanceof GeoPoint && other.latitude === this.latitude && other.longitude === this.longitude; }
}

export type LinkKind = "document" | "file" | "user";

/**
 * A typed reference stored in a document: to a document, a file or a user. Reads give every stored reference back as a
 * Link; `data()` is what it points to when the read expanded it (`expand(["patient"])`), else null. Write one with
 * `Link.document(path)`, `Link.file(path)`, `Link.user(uid)`, or a reference (`documents.doc(...)`, …).
 */
export class Link<T = unknown> {
  /** @internal */ constructor(readonly kind: LinkKind, readonly target: string, private readonly resolved: (() => T | null) | null = null) {}
  static document(path: string): Link<DocumentData> { return new Link("document", trimSlashes(path)); }
  static file(path: string): Link<FileData> { return new Link("file", trimSlashes(path)); }
  static user(uid: string): Link<UserData> { return new Link("user", uid); }
  /** What it points to, when the read expanded it and the target exists; null otherwise. */
  data(): T | null { return this.resolved?.() ?? null; }
  isEqual(other: unknown) { return other instanceof Link && other.kind === this.kind && other.target === this.target; }
  toString() { return `${this.kind}:${this.target}`; }
}

type FieldValueKind = "delete" | "serverTimestamp" | "increment" | "maximum" | "minimum" | "arrayUnion" | "arrayRemove";

/** Sentinels for set / update: server-side transforms, and removing a field. */
export class FieldValue {
  private constructor(/** @internal */ readonly kind: FieldValueKind, /** @internal */ readonly operand?: unknown) {}
  /** Removes the field (update, or set with merge / mergeFields). */
  static delete() { return new FieldValue("delete"); }
  /** The write's commit time. */
  static serverTimestamp() { return new FieldValue("serverTimestamp"); }
  /** Adds n (integers stay integers; a double makes a double). A missing or non-number field becomes n. */
  static increment(n: number | bigint) { return new FieldValue("increment", n); }
  /** Keeps the larger of the field and n. */
  static maximum(n: number | bigint) { return new FieldValue("maximum", n); }
  /** Keeps the smaller of the field and n. */
  static minimum(n: number | bigint) { return new FieldValue("minimum", n); }
  /** Appends the elements not already in the array. */
  static arrayUnion(...elements: unknown[]) { return new FieldValue("arrayUnion", elements); }
  /** Removes every element equal to one of these. */
  static arrayRemove(...elements: unknown[]) { return new FieldValue("arrayRemove", elements); }
}

/** A document's fields, decoded: numbers, strings, booleans, null, Date (timestamps), Uint8Array (bytes), GeoPoint, Link, arrays and maps. */
export type DocumentData = { [field: string]: any };

type Tagged = { [tag: string]: any };

const base64 = {
  encode(bytes: Uint8Array) { let s = ""; for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000)); return btoa(s); },
  decode(text: string) { return Uint8Array.from(atob(text), (c) => c.charCodeAt(0)); },
};

/** Timestamps on the wire: UTC, microseconds. */
const wireTime = (d: Date) => {
  if (isNaN(d.getTime())) throw new Error("An invalid Date can't be stored.");
  return d.toISOString().replace(/\.(\d{3})Z$/, ".$1000Z");
};

/** Where links read from a document find what they point to (the read's included documents and resolved files/users). */
interface Expansion { included: { [path: string]: any }; resolved: { [key: string]: any } }
const NO_EXPANSION: Expansion = { included: {}, resolved: {} };

/** @internal JS value → tagged wire value. */
export function encodeValue(v: unknown): Tagged {
  if (v === null || v === undefined) return { nullValue: null };
  if (typeof v === "boolean") return { booleanValue: v };
  if (typeof v === "bigint") return { integerValue: v.toString() };
  if (typeof v === "number") {
    if (Number.isNaN(v)) return { doubleValue: "NaN" };
    if (v === Infinity) return { doubleValue: "Infinity" };
    if (v === -Infinity) return { doubleValue: "-Infinity" };
    return Number.isInteger(v) && Number.isSafeInteger(v) ? { integerValue: String(v) } : { doubleValue: v };
  }
  if (typeof v === "string") return { stringValue: v };
  if (v instanceof Date) return { timestampValue: wireTime(v) };
  if (v instanceof Uint8Array) return { bytesValue: base64.encode(v) };
  if (v instanceof GeoPoint) return { geoPointValue: { latitude: v.latitude, longitude: v.longitude } };
  if (v instanceof Link) return v.kind === "document" ? { documentReferenceValue: v.target } : v.kind === "file" ? { fileReferenceValue: v.target } : { userReferenceValue: v.target };
  if (v instanceof DocumentReference) return { documentReferenceValue: v.path };
  if (v instanceof ChildReference) return { fileReferenceValue: v.path };
  if (v instanceof UserReference) return { userReferenceValue: v.uid };
  if (v instanceof FieldValue) throw new Error(`FieldValue.${v.kind}() can't be used here (only as a field of set or update, not inside an array).`);
  if (Array.isArray(v)) return { arrayValue: { values: v.map(encodeValue) } };
  if (typeof v === "object") return { mapValue: { fields: encodeFields(v as { [k: string]: unknown }) } };
  throw new Error(`A ${typeof v} can't be stored in a document.`);
}

/** @internal */
export function encodeFields(fields: { [k: string]: unknown }): { [k: string]: Tagged } {
  const out: { [k: string]: Tagged } = {};
  for (const [k, v] of Object.entries(fields)) if (v !== undefined) out[k] = encodeValue(v);
  return out;
}

/** @internal Tagged wire value → JS value. */
export function decodeValue(t: Tagged | null | undefined, x: Expansion = NO_EXPANSION): any {
  if (!t || typeof t !== "object") return null;
  if ("nullValue" in t) return null;
  if ("booleanValue" in t) return t.booleanValue;
  if ("integerValue" in t) { const n = Number(t.integerValue); return Number.isSafeInteger(n) ? n : BigInt(t.integerValue); }
  if ("doubleValue" in t) return typeof t.doubleValue === "number" ? t.doubleValue : Number(t.doubleValue);
  if ("stringValue" in t) return t.stringValue;
  if ("timestampValue" in t) return new Date(t.timestampValue);
  if ("bytesValue" in t) return base64.decode(t.bytesValue);
  if ("geoPointValue" in t) return new GeoPoint(t.geoPointValue.latitude, t.geoPointValue.longitude);
  if ("documentReferenceValue" in t) {
    const path = t.documentReferenceValue;
    return new Link("document", path, () => (x.included[path] ? decodeFields(x.included[path].fields, x) : null));
  }
  if ("fileReferenceValue" in t) {
    const path = t.fileReferenceValue;
    return new Link("file", path, () => (x.resolved[`file:${path}`] ? fileData(x.resolved[`file:${path}`]) : null));
  }
  if ("userReferenceValue" in t) {
    const uid = t.userReferenceValue;
    return new Link("user", uid, () => (x.resolved[`user:${uid}`] ? userData(x.resolved[`user:${uid}`]) : null));
  }
  if ("arrayValue" in t) return (t.arrayValue?.values ?? []).map((v: Tagged) => decodeValue(v, x));
  if ("mapValue" in t) return decodeFields(t.mapValue?.fields ?? {}, x);
  return null;
}

/** @internal */
export function decodeFields(fields: { [k: string]: Tagged } | null | undefined, x: Expansion = NO_EXPANSION): DocumentData {
  const out: DocumentData = {};
  for (const [k, v] of Object.entries(fields ?? {})) out[k] = decodeValue(v, x);
  return out;
}

const isPlainObject = (v: unknown): v is { [k: string]: unknown } =>
  !!v && typeof v === "object" && !Array.isArray(v) && Object.getPrototypeOf(v) === Object.prototype;

/**
 * Splits data into wire fields and transforms: FieldValue transforms anywhere in plain maps become transforms on their
 * dotted path; FieldValue.delete() becomes the deleteField sentinel. `dotted`: top-level keys are field paths (update).
 */
function writeFields(data: { [k: string]: unknown }, dotted: boolean) {
  const transforms: Tagged[] = [];
  const walk = (obj: { [k: string]: unknown }, prefix: string): { [k: string]: Tagged } => {
    const out: { [k: string]: Tagged } = {};
    for (const [k, v] of Object.entries(obj)) {
      if (v === undefined) continue;
      const path = prefix ? `${prefix}.${k}` : k;
      if (v instanceof FieldValue) {
        if (v.kind === "delete") out[k] = { deleteField: true } as Tagged;
        else transforms.push(v.kind === "serverTimestamp" ? { field: path, kind: v.kind }
          : { field: path, kind: v.kind, operand: v.kind === "arrayUnion" || v.kind === "arrayRemove" ? encodeValue(v.operand) : encodeValue(v.operand) });
      } else if (isPlainObject(v)) {
        const inner = walk(v, path);
        if (Object.keys(inner).length > 0 || Object.keys(v).length === 0) out[k] = { mapValue: { fields: inner } };
      } else out[k] = encodeValue(v);
    }
    return out;
  };
  if (!dotted) return { fields: walk(data, ""), transforms };
  // update: each key is a dotted path; its value is written (or transformed) as a whole there
  const fields: { [k: string]: Tagged } = {};
  for (const [k, v] of Object.entries(data)) {
    if (v === undefined) continue;
    if (v instanceof FieldValue) {
      if (v.kind === "delete") fields[k] = { deleteField: true } as Tagged;
      else transforms.push(v.kind === "serverTimestamp" ? { field: k, kind: v.kind } : { field: k, kind: v.kind, operand: encodeValue(v.operand) });
    } else if (isPlainObject(v)) {
      const inner = walk(v, k);
      fields[k] = { mapValue: { fields: inner } };
    } else fields[k] = encodeValue(v);
  }
  return { fields, transforms };
}

const trimSlashes = (p: string) => p.replace(/^\/+|\/+$/g, "");
const segments = (p: string) => trimSlashes(p).split("/").filter((s) => s.length > 0);

/** @internal A call into the server: the runtime's bridge in functions, the console's channel in pages, HTTP elsewhere. */
export type Call = (op: string, args: { [k: string]: unknown }) => Promise<any>;

/** An error from the server: its code (e.g. "permission-denied", "not-found") and message. */
export class WincheError extends Error {
  constructor(public code: string, message: string) { super(message); this.name = "WincheError"; }
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Documents (as winche_database)
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/** A write condition: the document exists (or not), or is unchanged since a read (`snapshot.updateTimeRaw`). */
export interface Precondition { exists?: boolean; updateTime?: string }

export interface SetOptions {
  /** Merge into the existing document (deep, maps into maps) instead of replacing it. */
  merge?: boolean;
  /** Write only these field paths (others are left as they are); a listed path missing from data is removed. */
  mergeFields?: string[];
  precondition?: Precondition;
}

export type WhereOp =
  | "==" | "!=" | "<" | "<=" | ">" | ">=" | "in" | "not-in"
  | "array-contains" | "array-contains-any" | "array-contains-all"
  | "contains" | "starts-with" | "ends-with" | "matches";

const OPS: { [op in WhereOp]: string } = {
  "==": "eq", "!=": "ne", "<": "lt", "<=": "lte", ">": "gt", ">=": "gte", "in": "in", "not-in": "notIn",
  "array-contains": "arrayContains", "array-contains-any": "arrayContainsAny", "array-contains-all": "arrayContainsAll",
  "contains": "contains", "starts-with": "startsWith", "ends-with": "endsWith", "matches": "regex",
};

/** Filters to combine: `query.where(Filter.or(Filter.where("a", "==", 1), Filter.where("b", "==", 2)))`. */
export class Filter {
  private constructor(/** @internal */ readonly wire: Tagged) {}
  static where(field: string, op: WhereOp, value: unknown) {
    if (!(op in OPS)) throw new Error(`'${op}' isn't an operator (${Object.keys(OPS).join(", ")}).`);
    return new Filter({ field, op: OPS[op], value: encodeValue(value) });
  }
  static and(...filters: Filter[]) { return new Filter({ and: filters.map((f) => f.wire) }); }
  static or(...filters: Filter[]) { return new Filter({ or: filters.map((f) => f.wire) }); }
  static not(filter: Filter) { return new Filter({ not: filter.wire }); }
  /** The field is null. */
  static isNull(field: string) { return new Filter({ unary: "isNull", field }); }
  /** The field is NaN. */
  static isNan(field: string) { return new Filter({ unary: "isNan", field }); }
  /** The field is there (any value, null included). */
  static exists(field: string) { return new Filter({ unary: "exists", field }); }
}

export type Aggregate = { kind: "count" } | { kind: "sum"; field: string } | { kind: "average"; field: string };
export const Aggregate = {
  count: (): Aggregate => ({ kind: "count" }),
  sum: (field: string): Aggregate => ({ kind: "sum", field }),
  average: (field: string): Aggregate => ({ kind: "average", field }),
};

interface QueryState {
  collection: string;
  group: boolean;
  filters: Tagged[];
  orderBy: { field: string; direction: "asc" | "desc" }[];
  limit?: number;
  limitToLast?: number;
  offset?: number;
  start?: { values: unknown[] | DocumentSnapshot; before: boolean };
  end?: { values: unknown[] | DocumentSnapshot; before: boolean };
  select?: string[];
  expand?: string[];
}

/** A read of a collection (or a collection group): chain filters and order, then get(), count(), sum(), … */
export class Query<T = DocumentData> {
  /** @internal */ constructor(protected readonly call: Call, /** @internal */ protected readonly state: QueryState) {}

  private with(change: Partial<QueryState>): Query<T> { return new Query<T>(this.call, { ...this.state, ...change }); }

  /** `where("age", ">=", 18)`, or a combined `Filter`. */
  where(field: string, op: WhereOp, value: unknown): Query<T>;
  where(filter: Filter): Query<T>;
  where(a: string | Filter, op?: WhereOp, value?: unknown): Query<T> {
    const f = a instanceof Filter ? a : Filter.where(a, op!, value);
    return this.with({ filters: [...this.state.filters, f.wire] });
  }
  orderBy(field: string, direction: "asc" | "desc" = "asc") { return this.with({ orderBy: [...this.state.orderBy, { field, direction }] }); }
  limit(n: number) { return this.with({ limit: n, limitToLast: undefined }); }
  /** The last n of an ordered query (in order). */
  limitToLast(n: number) { return this.with({ limitToLast: n, limit: undefined }); }
  offset(n: number) { return this.with({ offset: n }); }
  /** Cursors: the orderBy fields' values, or a snapshot (its values for them, and its path). */
  startAt(...at: unknown[]) { return this.with({ start: { values: cursor(at), before: true } }); }
  startAfter(...at: unknown[]) { return this.with({ start: { values: cursor(at), before: false } }); }
  endAt(...at: unknown[]) { return this.with({ end: { values: cursor(at), before: false } }); }
  endBefore(...at: unknown[]) { return this.with({ end: { values: cursor(at), before: true } }); }
  /** Only these fields in the results (the server leaves the rest out). */
  select(...fields: string[]) { return this.with({ select: fields }); }
  /** Follow these reference fields: their Links' data() is what they point to. */
  expand(paths: string[]) { return this.with({ expand: paths }); }

  /** @internal The query in the wire format. */
  toWire(): Tagged {
    const s = this.state;
    const cursorWire = (c: QueryState["start"]) => {
      if (!c) return undefined;
      if (c.values instanceof DocumentSnapshot) {
        const snap = c.values;
        const values = s.orderBy.map((o) => encodeValue(snap.get(o.field)));
        values.push({ documentReferenceValue: snap.reference.path });
        return { values, before: c.before };
      }
      return { values: c.values.map(encodeValue), before: c.before };
    };
    return {
      collection: s.collection,
      ...(s.group ? { collectionGroup: true } : {}),
      ...(s.filters.length === 1 ? { where: s.filters[0] } : s.filters.length > 1 ? { where: { and: s.filters } } : {}),
      ...(s.orderBy.length ? { orderBy: s.orderBy } : {}),
      ...(s.limit !== undefined ? { limit: s.limit } : {}),
      ...(s.limitToLast !== undefined ? { limitToLast: s.limitToLast } : {}),
      ...(s.offset !== undefined ? { offset: s.offset } : {}),
      ...(s.start ? { start: cursorWire(s.start) } : {}),
      ...(s.end ? { end: cursorWire(s.end) } : {}),
      ...(s.select ? { select: s.select } : {}),
    };
  }

  async get(): Promise<QuerySnapshot<T>> {
    const r = await this.call("db.query", { query: this.toWire(), ...(this.state.expand ? { expand: this.state.expand } : {}) });
    const x: Expansion = { included: r.included ?? {}, resolved: r.resolved ?? {} };
    return new QuerySnapshot<T>((r.documents ?? []).map((d: Tagged) => DocumentSnapshot.of<T>(this.call, d, x)), r.hasMore === true);
  }
  /** How many documents match (no limit). */
  async count(): Promise<number> { return (await this.call("db.count", { query: this.toWire() })).count; }
  /** The sum of a numeric field over the matches. */
  async sum(field: string): Promise<number> { return (await this.aggregate({ v: Aggregate.sum(field) })).v as number; }
  /** The average of a numeric field over the matches (null when none). */
  async average(field: string): Promise<number | null> { return (await this.aggregate({ v: Aggregate.average(field) })).v as number | null; }
  /** Several at once: `aggregate({ n: Aggregate.count(), total: Aggregate.sum("amount") })`. */
  async aggregate<K extends string>(aggregates: { [alias in K]: Aggregate }): Promise<{ [alias in K]: number | null }> {
    const r = await this.call("db.aggregate", { query: this.toWire(), aggregations: Object.entries(aggregates).map(([alias, a]) => ({ alias, ...(a as Aggregate) })) });
    return decodeFields(r.result) as { [alias in K]: number | null };
  }
}

const cursor = (at: unknown[]) => (at.length === 1 && at[0] instanceof DocumentSnapshot ? at[0] : at);

/** A collection: a query over its documents, and where new ones go. */
export class CollectionReference<T = DocumentData> extends Query<T> {
  /** @internal */ constructor(call: Call, readonly path: string) { super(call, { collection: path, group: false, filters: [], orderBy: [] }); }
  get id() { return this.path.slice(this.path.lastIndexOf("/") + 1); }
  /** The document this collection is under (null at the top). */
  get parent(): DocumentReference | null { const i = this.path.lastIndexOf("/"); return i < 0 ? null : new DocumentReference(this.call, this.path.slice(0, i)); }
  /** A document in it; without an id, a new random one. */
  doc(id?: string): DocumentReference<T> { return new DocumentReference<T>(this.call, `${this.path}/${id ?? newId()}`); }
  /** Adds a document with a new id. */
  async add(data: T & DocumentData): Promise<DocumentReference<T>> {
    const r = await this.call("db.add", { collection: this.path, fields: encodeFields(data as DocumentData) });
    return new DocumentReference<T>(this.call, r.document.path);
  }
}

const ID_CHARS = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
const newId = () => [...crypto.getRandomValues(new Uint8Array(20))].map((b) => ID_CHARS[b % 62]).join("");

/** A document: read it, write it, reach its sub-collections. */
export class DocumentReference<T = DocumentData> {
  /** @internal */ constructor(private readonly call: Call, readonly path: string, /** @internal */ private readonly expandPaths?: string[]) {}
  get id() { return this.path.slice(this.path.lastIndexOf("/") + 1); }
  get parent(): CollectionReference<T> { return new CollectionReference<T>(this.call, this.path.slice(0, this.path.lastIndexOf("/"))); }
  collection(path: string): CollectionReference { return new CollectionReference(this.call, `${this.path}/${trimSlashes(path)}`); }
  /** Follow these reference fields on get(): their Links' data() is what they point to. */
  expand(paths: string[]): DocumentReference<T> { return new DocumentReference<T>(this.call, this.path, paths); }

  async get(): Promise<DocumentSnapshot<T>> {
    const r = await this.call("db.get", { path: this.path, ...(this.expandPaths ? { expand: this.expandPaths } : {}) });
    return r.document ? DocumentSnapshot.of<T>(this.call, r.document, { included: r.included ?? {}, resolved: r.resolved ?? {} })
      : new DocumentSnapshot<T>(this, false, null, null);
  }
  /** Replaces the document (or merges into it, or writes the mergeFields only). */
  async set(data: T & DocumentData, options?: SetOptions): Promise<void> { await commit(this.call, [setWrite(this.path, data, options)]); }
  /** Changes these fields (dotted paths reach into maps) of an existing document. */
  async update(data: { [fieldPath: string]: unknown }, options?: { precondition?: Precondition }): Promise<void> {
    await commit(this.call, [updateWrite(this.path, data, options?.precondition)]);
  }
  /** Deletes it; with cascade, everything under it too (in one write: for big trees, use documents.recursiveDelete). */
  async delete(options?: { cascade?: boolean; precondition?: Precondition }): Promise<void> {
    await commit(this.call, [deleteWrite(this.path, options)]);
  }
  isEqual(other: unknown) { return other instanceof DocumentReference && other.path === this.path; }
}

const setWrite = (path: string, data: DocumentData, o?: SetOptions) => {
  const { fields, transforms } = writeFields(data, false);
  return { set: { path, fields, ...(o?.merge ? { merge: true } : {}), ...(o?.mergeFields ? { mergeFields: o.mergeFields } : {}),
    ...(transforms.length ? { transforms } : {}), ...(o?.precondition ? { precondition: o.precondition } : {}) } };
};
const updateWrite = (path: string, data: { [k: string]: unknown }, precondition?: Precondition) => {
  const { fields, transforms } = writeFields(data, true);
  return { update: { path, fields, ...(transforms.length ? { transforms } : {}), ...(precondition ? { precondition } : {}) } };
};
const deleteWrite = (path: string, o?: { cascade?: boolean; precondition?: Precondition }) =>
  ({ delete: { path, ...(o?.cascade ? { cascade: true } : {}), ...(o?.precondition ? { precondition: o.precondition } : {}) } });
const commit = (call: Call, writes: Tagged[]) => call("db.write", { writes });

/** A document as read: `exists`, `data()`, and its metadata. */
export class DocumentSnapshot<T = DocumentData> {
  /** @internal */ constructor(readonly reference: DocumentReference<T>, readonly exists: boolean, private readonly fields: DocumentData | null,
    private readonly meta: { createTime: string; updateTime: string; version: number } | null) {}

  /** @internal From the wire (with the read's expansion for its links). */
  static of<T>(call: Call, d: Tagged, x: Expansion = NO_EXPANSION): DocumentSnapshot<T> {
    return new DocumentSnapshot<T>(new DocumentReference<T>(call, d.path), true, decodeFields(d.fields, x),
      { createTime: d.createTime, updateTime: d.updateTime, version: d.version });
  }

  get id() { return this.reference.id; }
  /** The fields (undefined when it doesn't exist). */
  data(): T | undefined { return (this.fields ?? undefined) as T | undefined; }
  /** One field, by dotted path ("address.city"); undefined when it isn't there. */
  get(fieldPath: string): any {
    let v: any = this.fields;
    for (const k of fieldPath.split(".")) { if (v === null || typeof v !== "object" || !(k in v)) return undefined; v = v[k]; }
    return v;
  }
  get createTime(): Date | null { return this.meta ? new Date(this.meta.createTime) : null; }
  get updateTime(): Date | null { return this.meta ? new Date(this.meta.updateTime) : null; }
  /** The update time exactly as the server has it (microseconds), for `precondition: { updateTime }`. */
  get updateTimeRaw(): string | null { return this.meta?.updateTime ?? null; }
  get version(): number | null { return this.meta?.version ?? null; }
}

export class QuerySnapshot<T = DocumentData> {
  /** @internal */ constructor(readonly docs: DocumentSnapshot<T>[], /** True when more matched past the limit. */ readonly hasMore: boolean) {}
  get size() { return this.docs.length; }
  get empty() { return this.docs.length === 0; }
  forEach(fn: (doc: DocumentSnapshot<T>) => void) { this.docs.forEach(fn); }
}

/** Up to 500 writes, all or none. */
export class WriteBatch {
  private readonly writes: Tagged[] = [];
  /** @internal */ constructor(private readonly call: Call) {}
  set<T>(ref: DocumentReference<T>, data: T & DocumentData, options?: SetOptions) { this.writes.push(setWrite(ref.path, data, options)); return this; }
  update(ref: DocumentReference<any>, data: { [fieldPath: string]: unknown }, options?: { precondition?: Precondition }) {
    this.writes.push(updateWrite(ref.path, data, options?.precondition)); return this;
  }
  delete(ref: DocumentReference<any>, options?: { cascade?: boolean; precondition?: Precondition }) { this.writes.push(deleteWrite(ref.path, options)); return this; }
  get size() { return this.writes.length; }
  async commit(): Promise<void> { if (this.writes.length > 0) await commit(this.call, this.writes); }
}

/**
 * A transaction's reads and writes. Optimistic: each document it read and then writes must be unchanged when it
 * commits (else the whole function runs again, up to maxAttempts). Reads must come before writes.
 */
export class Transaction {
  private readonly writes: Tagged[] = [];
  /** @internal path → updateTimeRaw (null: it didn't exist) */ readonly seen = new Map<string, string | null>();
  /** @internal */ constructor(private readonly call: Call) {}
  async get<T>(ref: DocumentReference<T>): Promise<DocumentSnapshot<T>> {
    if (this.writes.length > 0) throw new Error("In a transaction, read before you write.");
    const snap = await ref.get();
    this.seen.set(ref.path, snap.updateTimeRaw);
    return snap;
  }
  private guard(path: string, given?: Precondition): Precondition | undefined {
    if (given) return given;
    if (!this.seen.has(path)) return undefined;
    const at = this.seen.get(path);
    return at === null ? { exists: false } : { updateTime: at! };
  }
  set<T>(ref: DocumentReference<T>, data: T & DocumentData, options?: SetOptions) {
    this.writes.push(setWrite(ref.path, data, { ...options, precondition: this.guard(ref.path, options?.precondition) })); return this;
  }
  update(ref: DocumentReference<any>, data: { [fieldPath: string]: unknown }, options?: { precondition?: Precondition }) {
    this.writes.push(updateWrite(ref.path, data, this.guard(ref.path, options?.precondition))); return this;
  }
  delete(ref: DocumentReference<any>, options?: { precondition?: Precondition }) {
    this.writes.push(deleteWrite(ref.path, { precondition: this.guard(ref.path, options?.precondition) })); return this;
  }
  /** @internal */ async commit() { if (this.writes.length > 0) await commit(this.call, this.writes); }
}

export class Documents {
  /** @internal */ constructor(private readonly call: Call) {}
  /** A collection by path ("patients", "patients/p1/visits"). */
  collection(path: string): CollectionReference {
    if (segments(path).length % 2 !== 1) throw new Error(`'${path}' isn't a collection path (an odd number of parts).`);
    return new CollectionReference(this.call, segments(path).join("/"));
  }
  /** A document by path ("patients/p1"). */
  doc(path: string): DocumentReference {
    if (segments(path).length % 2 !== 0 || segments(path).length === 0) throw new Error(`'${path}' isn't a document path (an even number of parts).`);
    return new DocumentReference(this.call, segments(path).join("/"));
  }
  /** Every collection with this id, under any parent ("sessionHistory"). */
  collectionGroup(id: string): Query {
    return new Query(this.call, { collection: id, group: true, filters: [], orderBy: [] });
  }
  /** Several documents in one call, in order. */
  async getAll(refs: DocumentReference<any>[]): Promise<DocumentSnapshot[]> {
    const r = await this.call("db.getAll", { paths: refs.map((ref) => ref.path) });
    return (r.documents as (Tagged | null)[]).map((d, i) => (d ? DocumentSnapshot.of(this.call, d) : new DocumentSnapshot(refs[i], false, null, null)));
  }
  batch(): WriteBatch { return new WriteBatch(this.call); }
  /** Deletes a document and everything under it, in batches (any size); how many documents went. */
  async recursiveDelete(ref: DocumentReference<any>): Promise<number> { return await this.call("db.deleteTree", { path: ref.path }); }
  /** Runs `fn` with a transaction and commits it; runs it again when something it read changed meanwhile. */
  async runTransaction<R>(fn: (tx: Transaction) => Promise<R>, options?: { maxAttempts?: number }): Promise<R> {
    const attempts = options?.maxAttempts ?? 5;
    for (let i = 1; ; i++) {
      const tx = new Transaction(this.call);
      const result = await fn(tx);
      try { await tx.commit(); return result; }
      catch (e) {
        const code = (e as { code?: string })?.code;
        if (i >= attempts || !(code === "failed-precondition" || code === "already-exists" || code === "not-found" || code === "aborted")) throw e;
      }
    }
  }
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Files (as winche_storage)
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/** A file's record: what it is, not its bytes. */
export interface FileData {
  id: string;
  path: string;
  directory: string;
  mimeType: string;
  sizeBytes: number;
  uploadStatus: "pending" | "complete";
  contentDisposition: string | null;
  cacheControl: string | null;
  contentHash: string | null;
  metadata: { [key: string]: Json };
  version: number;
  createdAt: Date;
  updatedAt: Date;
}

/** @internal */
export function fileData(r: any): FileData {
  return {
    id: r.id, path: r.path, directory: r.directory, mimeType: r.mimeType, sizeBytes: r.sizeBytes,
    uploadStatus: (typeof r.uploadStatus === "string" ? r.uploadStatus.toLowerCase() : r.uploadStatus === 1 ? "complete" : "pending") as FileData["uploadStatus"],
    contentDisposition: r.contentDisposition ?? null, cacheControl: r.cacheControl ?? null, contentHash: r.contentHash ?? null,
    metadata: r.metadata ?? {}, version: r.version, createdAt: new Date(r.createdAt), updatedAt: new Date(r.updatedAt),
  };
}

export interface PutOptions {
  /** Default: text/plain for text, application/octet-stream for bytes. */
  mimeType?: string;
  metadata?: { [key: string]: Json };
  /** What browsers do with it when downloaded, e.g. 'attachment; filename="report.pdf"'. */
  contentDisposition?: string;
  cacheControl?: string;
}

/** A file (or a folder) by path. */
export class ChildReference {
  /** @internal */ constructor(private readonly call: Call, readonly path: string) {}
  /** The last part of the path. */
  get name() { return this.path.slice(this.path.lastIndexOf("/") + 1); }
  get parent(): ChildReference | null { const i = this.path.lastIndexOf("/"); return this.path === "" ? null : new ChildReference(this.call, i < 0 ? "" : this.path.slice(0, i)); }
  child(path: string) { return new ChildReference(this.call, segments(`${this.path}/${path}`).join("/")); }

  /** The file's record; `exists` is false when there's none. */
  async getSnapshot(): Promise<FileSnapshot> {
    const r = await this.call("files.get", { path: this.path });
    return new FileSnapshot(this, r ? fileData(r) : null);
  }
  /** The bytes (up to 32 MB), or null when there's no file. */
  async getData(): Promise<Uint8Array | null> {
    const r = await this.call("files.read", { path: this.path });
    return r === null ? null : base64.decode(r.base64);
  }
  /** The contents as UTF-8 text, or null. */
  async getText(): Promise<string | null> {
    const bytes = await this.getData();
    return bytes === null ? null : new TextDecoder().decode(bytes);
  }
  /** Writes the file (new or replacing one), up to 32 MB; its triggers and hooks run as for an upload. */
  async putData(data: Uint8Array | ArrayBuffer | string, options?: PutOptions): Promise<FileSnapshot> {
    const bytes = typeof data === "string" ? new TextEncoder().encode(data) : data instanceof Uint8Array ? data : new Uint8Array(data);
    const r = await this.call("files.write", {
      path: this.path, base64: base64.encode(bytes), mimeType: options?.mimeType ?? (typeof data === "string" ? "text/plain; charset=utf-8" : null),
      metadata: options?.metadata ?? null, contentDisposition: options?.contentDisposition ?? null, cacheControl: options?.cacheControl ?? null,
    });
    return new FileSnapshot(this, fileData(r));
  }
  /**
   * Writes the file from a URL (a GET with these headers), up to 5 GB: the bytes go from the URL to storage on the
   * server, never through the function. The type is the one given, else the response's.
   */
  async putFromUrl(url: string, options?: PutOptions & { headers?: { [name: string]: string } }): Promise<FileSnapshot> {
    const r = await this.call("files.copyFromUrl", {
      path: this.path, url, headers: options?.headers ?? null, mimeType: options?.mimeType ?? null, metadata: options?.metadata ?? null,
      contentDisposition: options?.contentDisposition ?? null, cacheControl: options?.cacheControl ?? null,
    });
    return new FileSnapshot(this, fileData(r));
  }
  /** One page of the folder: files and sub-folders in name order. */
  async list(options?: { maxResults?: number; pageToken?: string; mimeType?: string }): Promise<DirectorySnapshot> {
    const r = await this.call("files.page", { folder: this.path, maxResults: options?.maxResults ?? null, pageToken: options?.pageToken ?? null, mimeType: options?.mimeType ?? null });
    return new DirectorySnapshot(this, (r.items ?? []).map((f: any) => new FileSnapshot(new ChildReference(this.call, f.path), fileData(f))),
      (r.prefixes ?? []).map((p: string) => new ChildReference(this.call, p)), r.nextPageToken ?? null);
  }
  /** Every file and sub-folder of the folder (all pages). */
  async listChildren(options?: { mimeType?: string }): Promise<DirectorySnapshot> {
    const files: FileSnapshot[] = [], folders: ChildReference[] = [];
    let token: string | undefined;
    do {
      const page = await this.list({ maxResults: 1000, pageToken: token, mimeType: options?.mimeType });
      files.push(...page.files); folders.push(...page.folders);
      token = page.nextPageToken ?? undefined;
    } while (token);
    return new DirectorySnapshot(this, files, folders, null);
  }
  /** Merges metadata (null removes a key); optionally changes the download headers ("" clears one), only at ifVersion. */
  async updateMetadata(metadata: { [key: string]: Json }, options?: { contentDisposition?: string; cacheControl?: string; ifVersion?: number }): Promise<FileSnapshot> {
    const r = await this.call("files.updateMetadata", { path: this.path, metadata, contentDisposition: options?.contentDisposition ?? null,
      cacheControl: options?.cacheControl ?? null, ifVersion: options?.ifVersion ?? null });
    return new FileSnapshot(this, fileData(r));
  }
  /** Deletes the file, or everything under the folder. False when nothing was there. */
  async delete(): Promise<boolean> {
    // Acting as a user (as(user)), the rules may refuse the path as a file yet allow the files under it as a folder.
    let one = false, refused: unknown = null;
    try { one = (await this.call("files.delete", { path: this.path })) === true; }
    catch (e) { if ((e as { code?: string })?.code !== "permission-denied") throw e; refused = e; }
    const under = await this.call("files.deleteUnder", { folder: this.path });
    if (refused && !(under > 0)) throw refused;
    return one || under > 0;
  }
  /** A link anyone holding it can download the file with, until expiresAt. */
  async downloadUrl(): Promise<{ url: string; expiresAt: Date }> {
    const r = await this.call("files.downloadUrl", { path: this.path });
    return { url: r.url, expiresAt: new Date(r.expiresAt) };
  }
  isEqual(other: unknown) { return other instanceof ChildReference && other.path === this.path; }
}

export class FileSnapshot {
  /** @internal */ constructor(readonly reference: ChildReference, readonly data: FileData | null) {}
  get exists() { return this.data !== null; }
}

export class DirectorySnapshot {
  /** @internal */ constructor(readonly reference: ChildReference, readonly files: FileSnapshot[], readonly folders: ChildReference[], readonly nextPageToken: string | null) {}
}

export class Files {
  /** @internal */ constructor(private readonly call: Call) {}
  /** A file or folder by path; "" is the top. */
  child(path: string): ChildReference { return new ChildReference(this.call, segments(path).join("/")); }
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Auth
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/** A user's account. */
export interface UserData {
  uid: string;
  email: string | null;
  emailVerified: boolean;
  displayName: string | null;
  photoUrl: string | null;
  isAnonymous: boolean;
  disabled: boolean;
  mfaEnabled: boolean;
  /** How they sign in: password, anonymous, google, … */
  providers: string[];
  customClaims: { [claim: string]: Json };
  createdAt: Date | null;
  lastSignInAt: Date | null;
}

/** @internal */
export function userData(u: any): UserData {
  return {
    uid: u.uid, email: u.email ?? null, emailVerified: u.emailVerified === true, displayName: u.displayName ?? null, photoUrl: u.photoUrl ?? null,
    isAnonymous: u.isAnonymous === true, disabled: u.disabled === true, mfaEnabled: u.mfaEnabled === true, providers: u.providers ?? [],
    customClaims: u.customClaims ?? {}, createdAt: u.createdAt ? new Date(u.createdAt) : null, lastSignInAt: u.lastSignInAt ? new Date(u.lastSignInAt) : null,
  };
}

/** A user, by uid. */
export class UserReference {
  /** @internal */ constructor(private readonly call: Call, readonly uid: string) {}
  /** Their account; `exists` is false when there's no such user. */
  async get(): Promise<UserSnapshot> {
    const r = await this.call("users.get", { uid: this.uid });
    return new UserSnapshot(this, r ? userData(r) : null);
  }
  /** Their custom claims ({} when none). Throws not-found when there's no such user. */
  async getCustomClaims(): Promise<{ [claim: string]: Json }> {
    const r = await this.call("users.get", { uid: this.uid });
    if (!r) throw new WincheError("not-found", `There's no user ${this.uid}.`);
    return r.customClaims ?? {};
  }
  /** Replaces their custom claims (null clears them); they're in the user's next token. */
  async setCustomClaims(claims: { [claim: string]: Json } | null): Promise<void> { await this.call("users.setClaims", { uid: this.uid, claims }); }
  isEqual(other: unknown) { return other instanceof UserReference && other.uid === this.uid; }
}

export class UserSnapshot {
  /** @internal */ constructor(readonly reference: UserReference, readonly data: UserData | null) {}
  get uid() { return this.reference.uid; }
  get exists() { return this.data !== null; }
  /** Their custom claims ({} when none, or no user). */
  get customClaims(): { [claim: string]: Json } { return this.data?.customClaims ?? {}; }
}

export class Auth {
  /** @internal */ constructor(private readonly call: Call) {}
  user(uid: string): UserReference { return new UserReference(this.call, uid); }
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Assets (as winche_assets, with the server's rights)
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/** A file of a release: its path, its content's SHA-256, size and type. */
export interface AssetEntry { path: string; hash: string; size: number; mimeType: string }

/** A file of a release, to read. */
export class AssetFileReference {
  /** @internal */ constructor(private readonly call: Call, readonly release: number, readonly path: string) {}
  /** Its bytes (up to 32 MB: larger ones through downloadUrl). */
  async bytes(): Promise<Uint8Array> {
    const r = await this.call("assets.read", { release: this.release, path: this.path });
    if (!r) throw new WincheError("not-found", `There's no ${this.path} in release ${this.release}.`);
    return base64.decode(r.base64);
  }
  /** Its contents as UTF-8 text. */
  async text(): Promise<string> { return new TextDecoder().decode(await this.bytes()); }
  /** Its contents decoded from JSON. */
  async json<T = Json>(): Promise<T> { return JSON.parse(await this.text()) as T; }
  /** A short-lived link to its bytes (to hand to someone, or to fetch a large one). */
  async downloadUrl(): Promise<{ url: string; expiresAt: Date }> {
    const r = await this.call("assets.downloadUrl", { release: this.release, path: this.path });
    return { url: r.url, expiresAt: new Date(r.expiresAt) };
  }
}

/** A published release: numbered, never changed. */
export class AssetRelease {
  /** @internal */ constructor(private readonly call: Call, readonly number: number, readonly note: string | null, readonly by: string | null,
    readonly at: Date, readonly files: AssetEntry[]) {}
  /** @internal */ static of(call: Call, r: any): AssetRelease { return new AssetRelease(call, r.number, r.note ?? null, r.by ?? null, new Date(r.at), r.files ?? []); }
  /** Whether it has a file at path. */
  has(path: string): boolean { return this.files.some((f) => f.path === path); }
  /** The paths under a folder ("translations"), or all of them. */
  paths(folder?: string): string[] {
    const prefix = folder ? trimSlashes(folder) + "/" : "";
    return this.files.map((f) => f.path).filter((p) => p.startsWith(prefix));
  }
  /** A file of this release (reading one it hasn't throws not-found). */
  file(path: string): AssetFileReference { return new AssetFileReference(this.call, this.number, trimSlashes(path)); }
}

/** A channel: a name pointing at a release. */
export class AssetChannelReference {
  /** @internal */ constructor(private readonly call: Call, readonly name: string) {}
  /** The release it points at now, or null when there's no such channel. */
  async get(): Promise<AssetRelease | null> {
    const r = await this.call("assets.channel", { name: this.name });
    return r ? AssetRelease.of(this.call, r) : null;
  }
}

export class Assets {
  /** @internal */ constructor(private readonly call: Call) {}
  /** A channel by name ("production"). */
  channel(name: string): AssetChannelReference { return new AssetChannelReference(this.call, name); }
  /** A release by number, or null. */
  async release(number: number): Promise<AssetRelease | null> {
    const r = await this.call("assets.release", { number });
    return r ? AssetRelease.of(this.call, r) : null;
  }
  /** The channels and the release each points at. */
  async channels(): Promise<{ name: string; release: number; updatedAt: Date; by: string | null }[]> {
    return ((await this.call("assets.channels", {})) as any[]).map((c) => ({ name: c.name, release: c.release, updatedAt: new Date(c.updatedAt), by: c.by ?? null }));
  }
  /** The releases, newest first, without their files. */
  async releases(limit = 50): Promise<{ number: number; note: string | null; by: string | null; at: Date; files: number; size: number }[]> {
    return ((await this.call("assets.releases", { limit })) as any[]).map((r) => ({ ...r, at: new Date(r.at) }));
  }
}


// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Functions (calling callables)
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

export class Functions {
  /** @internal */ constructor(private readonly send: Call) {}
  /** Runs the callable `name` with `data` and returns what it returns (its HttpsError as a WincheError with its code). */
  async call<R = Json>(name: string, data?: Json): Promise<R> {
    return (await this.send("functions.call", { name, data: data === undefined ? null : data })) as R;
  }
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Signing in
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/** One of the app's users, to act as with `as(user)`: their uid and token claims (custom claims among them). */
export interface AppUser {
  uid: string;
  token?: { [claim: string]: Json };
}

/** A project's data, acting as someone: `documents`, `files`, `auth`, `assets`, `functions`. */
export class Admin {
  readonly documents: Documents;
  readonly files: Files;
  readonly auth: Auth;
  readonly assets: Assets;
  readonly functions: Functions;

  /** @internal */ constructor(private readonly transport: Call) {
    this.documents = new Documents(transport);
    this.files = new Files(transport);
    this.auth = new Auth(transport);
    this.assets = new Assets(transport);
    this.functions = new Functions(transport);
  }

  /**
   * The same API acting as one of the app's users (`ctx.caller` in a callable): every documents and files call is
   * checked against the project's access rules for them, and refused with permission-denied as an app would be.
   * Auth, assets and functions calls are refused (rules don't cover them).
   */
  as(user: AppUser): Admin {
    if (!user || typeof user.uid !== "string" || !user.uid) throw new WincheError("invalid-argument", "as(user): give a user with a uid (e.g. ctx.caller).");
    const who = { uid: user.uid, token: user.token ?? {} };
    return new Admin((op, args) => this.transport(op, { ...args, as: who }));
  }
}

/** How `initializeAdmin` reaches a Winche server. */
export interface AdminOptions {
  /** The server's address, e.g. "https://winche.example.com". */
  server: string;
  /** The project's id. */
  project: string;
  /** A token for the project (or a function that returns the current one): its role limits what the SDK may do. */
  token: string | (() => string | Promise<string>);
  /** The fetch to use (default: the global one). */
  fetch?: typeof fetch;
}

/** The SDK for a project on a server, from anywhere (another server, a script): calls go over HTTPS with the token. */
export function initializeAdmin(options: AdminOptions): Admin {
  const server = (options?.server ?? "").replace(/\/+$/, "");
  if (!/^https?:\/\//.test(server)) throw new WincheError("invalid-argument", "initializeAdmin: give the server's address, e.g. https://winche.example.com.");
  if (!options.project) throw new WincheError("invalid-argument", "initializeAdmin: give the project's id.");
  if (!options.token) throw new WincheError("invalid-argument", "initializeAdmin: give a token.");
  const url = `${server}/console/v1/projects/${encodeURIComponent(options.project)}/admin`;
  const send = options.fetch ?? fetch;
  return new Admin(async (op, args) => {
    const token = typeof options.token === "function" ? await options.token() : options.token;
    let response: Response;
    try {
      response = await send(url, { method: "POST", headers: { "content-type": "application/json", authorization: `Bearer ${token}` }, body: JSON.stringify({ op, args }) });
    } catch (e) {
      throw new WincheError("unavailable", `Couldn't reach ${server}: ${(e as Error)?.message ?? e}`);
    }
    const body = await response.json().catch(() => null);
    if (!response.ok || body?.error) {
      const error = body?.error ?? {};
      throw new WincheError(error.code ?? (response.status === 401 ? "unauthenticated" : response.status === 403 ? "permission-denied" : "internal"),
        error.message ?? `The server answered ${response.status}.`);
    }
    return body?.result ?? null;
  });
}

/**
 * @internal Where the SDK runs without initializeAdmin: a function's run (the runtime's bridge), a test
 * (@winche/functions/testing), or a console page (the console's channel).
 */
const ambient: Call = (op, args) => {
  const g = globalThis as { [k: symbol]: any };
  const run = g[Symbol.for("winche.run")]?.() ?? g[Symbol.for("winche.test")]?.();
  if (run?.call) return run.call(op, args);
  const page = g[Symbol.for("winche.page")];
  if (typeof page === "function") return page(op, args);
  return Promise.reject(new WincheError("failed-precondition",
    run ? "This test gave no server: use withServer(...) from @winche/functions/testing to reach one."
      : "@winche/admin isn't signed in here. Functions and console pages are signed in already (call it while a function runs, not at a module's top level); anywhere else use initializeAdmin({ server, project, token })."));
};

const signedIn = new Admin(ambient);

/** Documents (in functions: with the server's rights; in a page: the person's role). */
export const documents: Documents = signedIn.documents;
/** Files. */
export const files: Files = signedIn.files;
/** The app's users. */
export const auth: Auth = signedIn.auth;
/** The app's assets (releases and channels). */
export const assets: Assets = signedIn.assets;
/** Calling callables. */
export const functions: Functions = signedIn.functions;
/** The same API acting as one of the app's users: their access rules apply. */
export const as = (user: AppUser): Admin => signedIn.as(user);
