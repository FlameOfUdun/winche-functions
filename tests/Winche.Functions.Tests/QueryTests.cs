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
}
