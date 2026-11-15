using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Values;

namespace Winche.Functions.Tests;

[Collection("postgres")]
public class ExpandTests(PostgresFixture pg)
{
    private static Dictionary<string, Value> F(params (string, Value)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public async Task Ctx_db_get_and_query_follow_references_with_expand()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        await app.Service<Winche.Storage.Services.FileStorage>().SetAsync("scans/a.png", "image/png", 42);
        string uid;
        await using (var scope = app.App.Services.CreateAsyncScope())
            uid = (await scope.ServiceProvider.GetRequiredService<Winche.Auth.IWincheAuth>().CreateUserAsync("ann@example.com", null, "Ann Lee")).Uid;
        await app.Service<DocumentDatabase>().WriteAsync([
            new SetWrite { Path = "doctors/d1", Fields = F(("name", new StringValue("Dr Who"))) },
            new SetWrite { Path = "patients/p1", Fields = F(("name", new StringValue("Ann")), ("doctor", new ReferenceValue("doctors/d1"))) },
            new SetWrite { Path = "visits/v1", Fields = F(("patient", new ReferenceValue("patients/p1")),
                ("team", new ArrayValue([new ReferenceValue("doctors/d1"), new ReferenceValue("doctors/gone")])), ("ward", new StringValue("icu")),
                ("scan", new FileRefValue("scans/a.png")), ("author", new UserRefValue(uid))) },
        ]);
        await app.Service<WincheFunctions>().DeployAsync(new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { callable, ref } from "@winche/functions";
                export const visit = callable(async (_d, ctx) => {
                  const v = await ctx.db.get("visits/v1", { expand: ["patient.doctor", "team"] });
                  const plain = await ctx.db.get("visits/v1");
                  const linked = await ctx.db.get("visits/v1", { expand: ["scan", "author"] });
                  await ctx.db.set("notes/n1", { visit: ref.doc("visits/v1"), scan: ref.file("scans/a.png"), by: ref.user("u9") });
                  const q = await ctx.db.query("visits", { where: [["ward", "==", "icu"]], expand: ["patient"] });
                  const patient = v!.fields.patient as any;
                  return {
                    patient: patient.fields.name,
                    doctor: patient.fields.doctor.fields.name,
                    team: (v!.fields.team as any[]).map((t) => typeof t === "string" ? t : t.fields.name),
                    plain: plain!.fields.patient,
                    queried: (q[0].fields.patient as any).fields.name,
                    queriedDoctor: (q[0].fields.patient as any).fields.doctor,
                    scanSize: (linked!.fields.scan as any).sizeBytes,
                    author: (linked!.fields.author as any).displayName,
                    authorEmail: (linked!.fields.author as any).email,
                  };
                });
                """,
        }, null, null);

        using var response = await app.Http.PostAsJsonAsync($"{app.Url}/functions/v1/call/visit", new { data = (object?)null });
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var data = body["data"] ?? throw new Xunit.Sdk.XunitException(body.ToJsonString());
        Assert.Equal("Ann", (string?)data["patient"]);
        Assert.Equal("Dr Who", (string?)data["doctor"]);
        Assert.Equal(["Dr Who", "doctors/gone"], data["team"]!.AsArray().Select(t => (string?)t));   // a missing document stays a path
        Assert.Equal("patients/p1", (string?)data["plain"]);
        var note = await app.Service<DocumentDatabase>().GetAsync("notes/n1");   // ref.* store typed references
        Assert.Equal((new ReferenceValue("visits/v1"), new FileRefValue("scans/a.png"), new UserRefValue("u9")),
            ((ReferenceValue)note!.Fields["visit"], (FileRefValue)note.Fields["scan"], (UserRefValue)note.Fields["by"]));
        Assert.Equal("Ann", (string?)data["queried"]);
        Assert.Equal("doctors/d1", (string?)data["queriedDoctor"]);   // only "patient" was expanded
        Assert.Equal(42, (int?)data["scanSize"]);
        Assert.Equal("Ann Lee", (string?)data["author"]);
        Assert.Equal("ann@example.com", (string?)data["authorEmail"]);   // functions run with the server's rights
    }
}
