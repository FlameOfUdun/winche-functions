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
                import { callable } from "@winche/functions";
                export const pages = callable(async (_d, ctx) => {
                  const names: string[][] = [];
                  let last: string | undefined;
                  while (true) {
                    const page = await ctx.db.query("jobs", { limit: 3, startAfter: last });
                    if (page.length === 0) break;
                    names.push(page.map((d) => d.id));
                    last = page[page.length - 1].path;
                  }
                  const due = await ctx.db.query("jobs", { where: [["dueAt", "<=", new Date("2026-11-04T00:00:00Z")]], orderBy: [["dueAt", "desc"]] });
                  return { names, due: due.map((d) => d.id) };
                });
                """,
        }, null, null);
        var r = await app.Service<WincheFunctions>().InvokeAsync("pages", null, FunctionCaller.Server);
        Assert.True(r.Ok, r.ErrorMessage);
        Assert.Equal("""[["j1","j2","j3"],["j4","j5","j6"],["j7"]]""", r.Result!["names"]!.ToJsonString());
        Assert.Equal("""["j3","j2","j1"]""", r.Result["due"]!.ToJsonString());
    }

    [Fact]
    public async Task A_batch_is_all_or_none_and_its_preconditions_hold_against_what_was_read()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        await app.Service<WincheFunctions>().DeployAsync(new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { callable } from "@winche/functions";
                export const run = callable(async (_d, ctx) => {
                  await ctx.db.set("queue/q1", { n: 1 });
                  const read = (await ctx.db.get("queue/q1"))!;
                  const codes: string[] = [];
                  const attempt = async (w: Parameters<typeof ctx.db.batch>[0]) => { try { await ctx.db.batch(w); codes.push("ok"); } catch (e) { codes.push((e as { code: string }).code); } };

                  // Unchanged since read: summary written, entry removed, together.
                  await attempt([{ set: "summary/s1", data: { at: new Date("2026-11-16T10:00:00Z") } }, { delete: "queue/q1", precondition: { updateTime: read.updateTime } }]);
                  // Written again meanwhile: nothing of the batch happens.
                  await ctx.db.set("queue/q2", { n: 1 });
                  const stale = (await ctx.db.get("queue/q2"))!;
                  await ctx.db.update("queue/q2", { n: 2 });
                  await attempt([{ set: "summary/s2", data: { n: 1 } }, { delete: "queue/q2", precondition: { updateTime: stale.updateTime } }]);
                  // Must not exist, but does.
                  await attempt([{ set: "summary/s1", data: { n: 9 }, precondition: { exists: false } }]);
                  await attempt([{ update: "summary/none", data: { n: 1 } }]);
                  return { codes, q1: await ctx.db.get("queue/q1"), s2: await ctx.db.get("summary/s2"), q2: (await ctx.db.get("queue/q2"))?.fields.n,
                           s1: (await ctx.db.get("summary/s1"))?.fields.at };
                });
                """,
        }, null, null);
        var r = await app.Service<WincheFunctions>().InvokeAsync("run", null, FunctionCaller.Server);
        Assert.True(r.Ok, r.ErrorMessage);
        Assert.Equal("""["ok","failed-precondition","already-exists","not-found"]""", r.Result!["codes"]!.ToJsonString());
        Assert.Null(r.Result["q1"]);
        Assert.Null(r.Result["s2"]);
        Assert.Equal(2, r.Result["q2"]!.GetValue<int>());
        Assert.StartsWith("2026-11-16T10:00:00", r.Result["s1"]!.GetValue<string>());   // a timestamp
    }
}
