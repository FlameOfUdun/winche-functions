using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Values;

namespace Winche.Functions.Tests;

[Collection("postgres")]
public class QueryTests(PostgresFixture pg)
{
    [Fact]
    public async Task Queries_page_with_startAfter_and_dates_in_where_are_timestamps()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var db = app.Service<DocumentDatabase>();
        var at = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 1; i <= 7; i++)
            await db.WriteAsync([new SetWrite { Path = $"jobs/j{i}", Fields = new Dictionary<string, Value> { ["dueAt"] = new TimestampValue(at.AddDays(i)) } }]);
        await app.Service<WincheFunctions>().DeployAsync(new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { callable } from "@winche-technologies/winche-functions"; import * as admin from "@winche-technologies/winche-admin";
                export const pages = callable(async (_d, ctx) => {
                  const names: string[][] = [];
                  const jobs = admin.documents.collection("jobs");
                  let page = await jobs.limit(3).get();
                  while (!page.empty) {
                    names.push(page.docs.map((d) => d.id));
                    page = await jobs.limit(3).startAfter(page.docs[page.size - 1]).get();
                  }
                  const due = await jobs.where("dueAt", "<=", new Date("2026-11-04T00:00:00Z")).orderBy("dueAt", "desc").get();
                  const after = await jobs.orderBy("dueAt").startAfter(new Date("2026-11-05T00:00:00Z")).limit(2).get();
                  return { names, due: due.docs.map((d) => d.id), after: after.docs.map((d) => d.id), count: await jobs.where("dueAt", ">", new Date("2026-11-04T00:00:00Z")).count(),
                           last: (await jobs.orderBy("dueAt").limitToLast(2).get()).docs.map((d) => d.id) };
                });
                """,
        }, null, null);
        var r = await app.Service<WincheFunctions>().InvokeAsync("pages", null, FunctionCaller.Server);
        Assert.True(r.Ok, r.ErrorMessage);
        Assert.Equal("""[["j1","j2","j3"],["j4","j5","j6"],["j7"]]""", r.Result!["names"]!.ToJsonString());
        Assert.Equal("""["j3","j2","j1"]""", r.Result["due"]!.ToJsonString());
        Assert.Equal("""["j5","j6"]""", r.Result["after"]!.ToJsonString());
        Assert.Equal(4, r.Result["count"]!.GetValue<int>());
        Assert.Equal("""["j6","j7"]""", r.Result["last"]!.ToJsonString());
    }

    [Fact]
    public async Task A_batch_is_all_or_none_and_its_preconditions_hold_against_what_was_read()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        await app.Service<WincheFunctions>().DeployAsync(new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { callable } from "@winche-technologies/winche-functions"; import * as admin from "@winche-technologies/winche-admin"; import { FieldValue, type WriteBatch } from "@winche-technologies/winche-admin";
                export const run = callable(async (_d, ctx) => {
                  const db = admin.documents;
                  await db.doc("queue/q1").set({ n: 1 });
                  const read = await db.doc("queue/q1").get();
                  const codes: string[] = [];
                  const attempt = async (fill: (b: WriteBatch) => void) => {
                    const b = db.batch(); fill(b);
                    try { await b.commit(); codes.push("ok"); } catch (e) { codes.push((e as { code: string }).code); }
                  };

                  // Unchanged since read: summary written, entry removed, together.
                  await attempt((b) => b.set(db.doc("summary/s1"), { at: new Date("2026-11-16T10:00:00Z") }).delete(db.doc("queue/q1"), { precondition: { updateTime: read.updateTimeRaw! } }));
                  // Written again meanwhile: nothing of the batch happens.
                  await db.doc("queue/q2").set({ n: 1 });
                  const stale = await db.doc("queue/q2").get();
                  await db.doc("queue/q2").update({ n: FieldValue.increment(1) });
                  await attempt((b) => b.set(db.doc("summary/s2"), { n: 1 }).delete(db.doc("queue/q2"), { precondition: { updateTime: stale.updateTimeRaw! } }));
                  // Must not exist, but does.
                  await attempt((b) => b.set(db.doc("summary/s1"), { n: 9 }, { precondition: { exists: false } }));
                  await attempt((b) => b.update(db.doc("summary/none"), { n: 1 }));
                  // A transaction: read, then write against what it read.
                  const total = await db.runTransaction(async (tx) => {
                    const q2 = await tx.get(db.doc("queue/q2"));
                    tx.set(db.doc("summary/total"), { n: (q2.get("n") as number) * 10, at: FieldValue.serverTimestamp() });
                    return (q2.get("n") as number) * 10;
                  });
                  const at = (await db.doc("summary/s1").get()).get("at");
                  return { codes, q1: (await db.doc("queue/q1").get()).exists, s2: (await db.doc("summary/s2").get()).exists, q2: (await db.doc("queue/q2").get()).get("n"),
                           s1: at instanceof Date ? at.toISOString() : null, total, totalAt: (await db.doc("summary/total").get()).get("at") instanceof Date };
                });
                """,
        }, null, null);
        var r = await app.Service<WincheFunctions>().InvokeAsync("run", null, FunctionCaller.Server);
        Assert.True(r.Ok, r.ErrorMessage);
        Assert.Equal("""["ok","failed-precondition","already-exists","not-found"]""", r.Result!["codes"]!.ToJsonString());
        Assert.False(r.Result["q1"]!.GetValue<bool>());
        Assert.False(r.Result["s2"]!.GetValue<bool>());
        Assert.Equal(2, r.Result["q2"]!.GetValue<int>());
        Assert.Equal("2026-11-16T10:00:00.000Z", r.Result["s1"]!.GetValue<string>());   // read back as a Date
        Assert.Equal((20, true), (r.Result["total"]!.GetValue<int>(), r.Result["totalAt"]!.GetValue<bool>()));
    }
}
