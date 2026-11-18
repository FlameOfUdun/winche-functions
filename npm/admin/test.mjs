// A smoke test of the built package from Node: initializeAdmin sends each call to the project's admin API with the
// token, and answers come back as the SDK's objects; errors keep their code.
import assert from "node:assert/strict";
import { initializeAdmin, FieldValue, WincheError } from "./dist/index.js";

const sent = [];
const fakeFetch = async (url, init) => {
  sent.push({ url, auth: init.headers.authorization, body: JSON.parse(init.body) });
  const { op, args } = JSON.parse(init.body);
  if (op === "db.get") return Response.json({ result: { document: { path: args.path, id: "a", collection: "notes", fields: { text: { stringValue: "hi" } }, createTime: "2026-01-01T00:00:00.000000Z", updateTime: "2026-01-01T00:00:00.000000Z", version: 1 } } });
  if (op === "db.write") return Response.json({ result: { writeResults: [{}] } });
  return Response.json({ error: { code: "permission-denied", message: `${op} needs the admin role.` } }, { status: 403 });
};
const admin = initializeAdmin({ server: "https://winche.example.com/", project: "clinic", token: () => "wct_test", fetch: fakeFetch });

const doc = await admin.documents.doc("notes/a").get();
assert.equal(doc.get("text"), "hi");
assert.deepEqual(sent[0], { url: "https://winche.example.com/console/v1/projects/clinic/admin", auth: "Bearer wct_test", body: { op: "db.get", args: { path: "notes/a" } } });

await admin.documents.doc("notes/a").update({ count: FieldValue.increment(1) });
assert.equal(sent[1].body.op, "db.write");

await admin.as({ uid: "ann" }).documents.doc("notes/a").get();
assert.deepEqual(sent[2].body.args.as, { uid: "ann", token: {} });

await assert.rejects(admin.functions.call("wipe"), (e) => e instanceof WincheError && e.code === "permission-denied" && /admin role/.test(e.message));
console.log("ok: @winche/admin from Node");
