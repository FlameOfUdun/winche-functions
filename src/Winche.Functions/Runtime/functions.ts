/**
 * @winche/functions — write Winche functions.
 *
 *   import { documents, callable, schedule, FieldValue } from "@winche/functions";
 *
 *   export const onVisit = documents.onCreated("patients/{patientId}/visits/{visitId}", async (e, ctx) => {
 *     await ctx.documents.doc(`patients/${e.params.patientId}`).update({ lastVisit: e.data.get("at"), visits: FieldValue.increment(1) });
 *   });
 *
 *   export const hello = callable(async (data: { name: string }, ctx) => ({ greeting: `Hello ${data.name}`, uid: ctx.caller?.uid }));
 *
 *   export const nightly = schedule("0 2 * * *", async (_e, ctx) => ctx.log("Nightly clean-up"));
 *
 * Functions run on the Winche server in a sandbox: they can use the network (fetch) but not its files or settings.
 * `ctx.documents`, `ctx.files` and `ctx.auth` act with the server's own rights (access rules don't apply), and read
 * like the Dart SDKs: references (`doc`, `collection`, `child`, `user`) and the snapshots their reads return.
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
 * `Link.document(path)`, `Link.file(path)`, `Link.user(uid)`, or a reference (`ctx.documents.doc(...)`, …).
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

/** @internal A call into the server (the runtime's bridge). */
type Call = (op: string, args: { [k: string]: unknown }) => Promise<any>;

class WincheError extends Error {
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
    const one = await this.call("files.delete", { path: this.path });
    const under = await this.call("files.deleteUnder", { folder: this.path });
    return one === true || under > 0;
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
// Context and events
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/** Who called: a callable's signed-in caller, or the request a before-function checks. */
export interface Caller {
  uid: string;
  /** The caller's token claims (custom claims among them). */
  token: { [claim: string]: Json };
}

export interface Secrets {
  /**
   * A secret's value (an API key, a password), set in the console (the Secrets page) or with
   * `winche functions secrets:set NAME`. The function must declare it: `{ secrets: ["NAME"] }` in its options.
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

/**
 * The files of the functions folder's resources/ folder, deployed with the code (fonts, translations, templates, images).
 * Read-only; paths are relative to resources/ ("fonts/Roboto.ttf"). They change only with a deployment, and rolling back
 * a deployment brings its resources back too.
 */
export interface Resources {
  /** A file's bytes, or null when there's no such resource. */
  read(path: string): Promise<Uint8Array | null>;
  /** A file's contents as UTF-8 text, or null. */
  readText(path: string): Promise<string | null>;
  /** The resource paths in a folder and its subfolders (all of them without one), sorted. */
  list(folder?: string): Promise<string[]>;
  /** Whether there's an resource at that path. */
  has(path: string): boolean;
}

export interface Context {
  /** This function's name (its export name). */
  functionName: string;
  /** For callables: who called (null when signed out). */
  caller: Caller | null;
  /** Documents, with the server's rights. */
  documents: Documents;
  /** Files, with the server's rights. */
  files: Files;
  /** Users' accounts. */
  auth: Auth;
  /** The app's assets (releases and channels), with the server's rights. */
  assets: Assets;
  /** The deployment's resources/ folder (read-only). */
  resources: Resources;
  /** The secrets this function declared in its options. */
  secrets: Secrets;
  /** For console actions: who ran it from the console (null otherwise). */
  console: { user: ConsoleUser } | null;
  /** Writes to the server's logs (Logs → functions); console.log does too. */
  log(...args: unknown[]): void;
}

/** @internal What the runtime hands a run: the bridge, and the run's own facts. */
interface RawContext {
  call: Call;
  functionName: string;
  readOnly: boolean;
  auth: Caller | null;
  console: { user: ConsoleUser } | null;
  secrets: Secrets;
  resources: Resources;
  log(...args: unknown[]): void;
}

const READS = new Set(["db.get", "db.getAll", "db.query", "db.count", "db.aggregate", "files.get", "files.read", "files.page", "files.list", "users.get",
  "assets.channels", "assets.channel", "assets.release", "assets.releases", "assets.read", "assets.downloadUrl"]);

/** @internal The context a handler gets. */
export function contextOf(raw: RawContext): Context {
  const call: Call = raw.readOnly
    ? (op, args) => (READS.has(op) ? raw.call(op, args) : Promise.reject(new WincheError("failed-precondition",
      `ctx: ${op} can't change anything in a before-function: it would run outside the write being checked. Use an onCreated… trigger.`)))
    : raw.call;
  return {
    functionName: raw.functionName, caller: raw.auth ?? null, console: raw.console ?? null, secrets: raw.secrets, resources: raw.resources, log: raw.log,
    documents: new Documents(call), files: new Files(call), auth: new Auth(call), assets: new Assets(call),
  };
}

/** A document as a change event carries it (plain fields beside typed ones). @internal */
function changedDocument(call: Call, path: string, d: any): DocumentSnapshot {
  if (!d) return new DocumentSnapshot(new DocumentReference(call, path), false, null, null);
  if (d.typedFields) return DocumentSnapshot.of(call, { ...d, fields: d.typedFields });
  return new DocumentSnapshot(new DocumentReference(call, path), true, d.fields ?? {}, { createTime: d.createTime, updateTime: d.updateTime, version: d.version });
}

export interface DocumentEvent {
  type: "created" | "updated" | "deleted";
  path: string;
  /** The path pattern's captures, e.g. { patientId: "p1" }. */
  params: { [name: string]: string };
  /** The document now (exists is false when deleted). */
  data: DocumentSnapshot;
  /** The document before (exists is false when created). */
  before: DocumentSnapshot;
  /** The same on retries: use it to ignore repeats. */
  id: string;
  at: Date;
}

export interface FileEvent {
  type: "uploaded" | "deleted" | "metadataUpdated";
  path: string;
  params: { [name: string]: string };
  /** The file now (exists is false when deleted). */
  file: FileSnapshot;
  id: string;
  at: Date;
}

export interface UserEvent {
  type: "created" | "deleted" | "disabled" | "enabled" | "claimsChanged" | "signedIn";
  uid: string;
  user: UserSnapshot;
  /** For signedIn: how (password, google, emailLink, …). */
  provider: string | null;
  id: string;
  at: Date;
}

/** A write about to be saved, for documents.beforeWrite. Throw HttpsError to refuse it; return changes to make. */
export interface BeforeDocumentEvent {
  operation: "create" | "update" | "delete";
  path: string;
  params: { [name: string]: string };
  /** The document as stored now (exists is false when it's being created). */
  before: DocumentSnapshot;
  /** The fields it will have (null when deleted), with earlier before-functions' changes. */
  data: DocumentData | null;
  /** Who made the request (null when signed out, or the app's own server code). */
  caller: Caller | null;
}

/** Changes to the document being written: fields to set (dotted paths) and to remove. */
export interface DocumentChanges {
  set?: { [fieldPath: string]: unknown };
  remove?: string[];
}

/** A file write about to be saved, for files.beforeWrite (registering, a metadata change, a delete). */
export interface BeforeFileEvent {
  operation: "create" | "update" | "delete";
  path: string;
  params: { [name: string]: string };
  /** The file as stored now (exists is false when new). */
  before: FileSnapshot;
  /** What will be saved (null when deleted). */
  file: { mimeType: string; sizeBytes: number; metadata: { [key: string]: Json }; contentDisposition: string | null; cacheControl: string | null } | null;
  caller: Caller | null;
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
  user: UserSnapshot;
  provider: string;
  ipAddress: string | null;
}

/** A release published (released) or a channel pointed at a release (channelMoved). */
export interface AssetEvent {
  type: "released" | "channelMoved";
  /** The release published, or the one the channel points at now. */
  release: number;
  /** The channel moved (for released: the one published to, or null). */
  channel: string | null;
  /** The release's note (released). */
  note: string | null;
  /** Who published it (released). */
  by: string | null;
  id: string;
  at: Date;
}

export interface ScheduleEvent {
  /** When this run was due. */
  scheduledAt: Date;
  id: string;
}

/** @internal The event a handler gets, from what the server sent. */
function eventOf(trigger: Trigger, a: any, call: Call): unknown {
  switch (trigger.kind) {
    case "documents":
      return { type: a.type, path: a.path, params: a.params ?? {}, data: changedDocument(call, a.path, a.data), before: changedDocument(call, a.path, a.before), id: a.id, at: new Date(a.at) };
    case "files": {
      const ref = new ChildReference(call, a.path);
      return { type: a.type, path: a.path, params: a.params ?? {}, file: new FileSnapshot(ref, a.file ? fileData(a.file) : null), id: a.id, at: new Date(a.at) };
    }
    case "users": {
      const ref = new UserReference(call, a.uid ?? a.user?.uid);
      return { type: a.type, uid: ref.uid, user: new UserSnapshot(ref, a.user ? userData(a.user) : null), provider: a.provider ?? null, id: a.id, at: new Date(a.at) };
    }
    case "schedule":
      return { scheduledAt: new Date(a.scheduledAt), id: a.id };
    case "assets":
      return { type: a.type, release: a.release, channel: a.channel ?? null, note: a.note ?? null, by: a.by ?? null, id: a.id, at: new Date(a.at) };
    case "before":
      if (trigger.on === "documents")
        return { operation: a.operation, path: a.path, params: a.params ?? {}, caller: a.auth ?? null,
          before: a.before ? DocumentSnapshot.of(call, a.before) : new DocumentSnapshot(new DocumentReference(call, a.path), false, null, null),
          data: a.data ? decodeFields(a.data.fields) : null };
      if (trigger.on === "files") {
        const ref = new ChildReference(call, a.path);
        return { operation: a.operation, path: a.path, params: a.params ?? {}, caller: a.auth ?? null, before: new FileSnapshot(ref, a.before ? fileData(a.before) : null), file: a.file };
      }
      if (a.user) {
        const ref = new UserReference(call, a.user.uid);
        return { user: new UserSnapshot(ref, userData(a.user)), provider: a.provider, ipAddress: a.ipAddress ?? null };
      }
      return a;
    default:
      return a;
  }
}

/** @internal A before-function's answer, for the server. */
function changesOut(trigger: Trigger, r: any): unknown {
  if (trigger.kind === "before" && trigger.on === "documents" && r && typeof r === "object" && r.set)
    return { ...r, set: Object.fromEntries(Object.entries(r.set).map(([k, v]) => [k, encodeValue(v)])) };
  return r;
}

export interface FunctionOptions {
  /**
   * Seconds it may run: default 60 (the project's default), at most the server's longest (9 minutes unless the server
   * allows more). The console can give a function its own time over this. Before-functions: default 5, at most 30.
   */
  timeoutSeconds?: number;
  /** The secrets it reads with ctx.secrets.get: names in capitals, digits and underscores (e.g. "SES_SECRET_KEY"). */
  secrets?: string[];
  /** What it does, in a sentence (up to 300 characters): shown in the console. */
  description?: string;
  /**
   * The shape of what it takes, as JSON Schema: shown in the console, which starts a run by hand from an input made
   * from it (unless `example` is given). Not checked: the function checks its own input.
   */
  input?: JsonSchema;
  /** The shape of what it returns, as JSON Schema: shown in the console. Not checked. */
  output?: JsonSchema;
  /** A sample input, to start a run by hand from (instead of one made from `input`). */
  example?: Json;
  /**
   * A new sandbox for every run, with nothing kept from the one before. Without it, runs reuse a warm worker (the code
   * already loaded, so a run starts in well under a millisecond), and what the module keeps at the top level (a client,
   * a parsed file) stays from run to run: never keep one caller's data there. `fresh` costs each run the time to load
   * the code again.
   */
  fresh?: boolean;
  /**
   * The most memory, in MB, an instance (a worker with this function's code loaded) may hold after a run: past it the
   * instance is replaced, its memory freed. Default 256 (the project's default), 64 to the server's most (1024 unless
   * it allows more). The console can set it over this.
   */
  memory?: number;
  /** Instances kept loaded even when idle, so a call never waits for one. Default 1 for callables and HTTP functions, else 0. */
  minInstances?: number;
  /** The most runs of this function at once; more wait their turn (within their timeout). Default 10 (the project's default). */
  maxInstances?: number;
  /** Runs of the project's limit kept for this function, so busy functions can't take them all. Default 0. */
  reservedInstances?: number;
  /**
   * A process of its own: a run that uses too much memory, or crashes, stops only this function's runs, never another's.
   * Costs the memory of a second runtime. A function asking for more memory than the shared process allows gets one anyway.
   */
  isolate?: boolean;
}

/** A JSON Schema, e.g. { type: "object", properties: { lang: { enum: ["en", "de"] } }, required: ["lang"] }. */
export type JsonSchema = { [keyword: string]: Json };

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
  | { kind: "assets"; events: string[]; channel?: string }
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
  /** @internal Called by the runtime with what the server sent and the run's raw context. */
  handler: (arg: unknown, raw: never) => Promise<unknown>;
  /** The function as written, for tests: call it with an event and a context of your own. */
  run: (arg: A, ctx: Context) => R | Promise<R>;
}

const define = <A, R>(trigger: Trigger, run: (arg: A, ctx: Context) => R | Promise<R>, options: FunctionOptions = {}): WincheFunction<A, R> => ({
  __winche: 1, trigger, options, run,
  handler: async (arg: unknown, raw: never) => {
    const r = raw as unknown as RawContext;
    return changesOut(trigger, await run(eventOf(trigger, arg, r.call) as A, contextOf(r)));
  },
});

type Handler<E> = (event: E, ctx: Context) => unknown;

/**
 * Before-functions run before a write is saved, inside it: the write waits for them, so keep them quick. They can
 * refuse it (throw HttpsError: its message reaches the caller) or change it (return the changes). In them
 * ctx.documents, ctx.files and ctx.auth only read. Side effects (emails, other writes) belong in the onCreated… triggers.
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

/** Asset triggers: a release published, a channel moved. With options.channel, only that channel's. */
export const assets = {
  onReleased: (handler: Handler<AssetEvent>, options?: FunctionOptions & { channel?: string }) =>
    onAssets(["released"], handler, options),
  onChannelMoved: (handler: Handler<AssetEvent>, options?: FunctionOptions & { channel?: string }) =>
    onAssets(["channelMoved"], handler, options),
};

const onAssets = (events: string[], handler: Handler<AssetEvent>, options?: FunctionOptions & { channel?: string }) =>
  define<AssetEvent, unknown>({ kind: "assets", events, channel: options?.channel },
    (e, ctx) => (options?.channel && e.channel !== options.channel ? null : handler(e, ctx)), options);

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
