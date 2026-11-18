using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Winche.Functions.Tests;

[Collection("postgres")]
public class ConsolePagesTests(PostgresFixture pg)
{
    private const string Code = """
        import { pages, callable } from "@winche-technologies/winche-functions";
        export const reports = pages({ title: "Reports", files: "console/reports", icon: "chart-bar", order: 2 });
        export const setup = pages({ title: "Setup", files: "./console/setup/", minRole: "admin" });
        export const hello = callable(async () => "hi");
        """;

    private static Dictionary<string, string> Files() => new()
    {
        ["index.ts"] = Code,
        ["console/reports/index.html"] = "<!doctype html><html><head></head><body>Reports</body></html>",
        ["console/reports/app.js"] = "console.log('x')",
        ["console/reports/logo.png"] = ConsolePages.Base64Prefix + Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]),
        ["console/setup/index.html"] = "<p>Setup</p>",
    };

    [Fact]
    public async Task Pages_are_checked_listed_by_role_and_their_files_served()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();

        var bad = await Assert.ThrowsAsync<FunctionDeployException>(() => functions.DeployAsync(new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { pages } from "@winche-technologies/winche-functions";
                export const a = pages({ title: "", files: "../outside" });
                export const b = pages({ title: "B", files: "console/missing", icon: "Not An Icon" });
                export const c = pages({ title: "C", files: "console/missing", minRole: "owner" as any });
                """,
        }, null, null));
        Assert.Contains(bad.Problems, p => p.StartsWith("a: give the page a title"));
        Assert.Contains(bad.Problems, p => p.StartsWith("a: files is the folder"));
        Assert.Contains(bad.Problems, p => p.StartsWith("b: there's no console/missing/index.html"));
        Assert.Contains(bad.Problems, p => p.StartsWith("b: icon is a Tabler icon name"));
        Assert.Contains(bad.Problems, p => p.StartsWith("c: minRole is viewer, member or admin"));

        var d = await functions.DeployAsync(Files(), "Pages", "admin@x");
        Assert.Equal(["reports"], ConsolePages.For(d, "viewer").Select(f => f.Name));
        Assert.Equal(["setup", "reports"], ConsolePages.For(d, "admin").Select(f => f.Name));   // order 0 before 2
        var reports = d.Functions.Single(f => f.Name == "reports");
        Assert.Equal(("Reports", "console/reports", "chart-bar", 2), (reports.Page!.Title, reports.Page.Files, reports.Page.Icon, reports.Page.Order));
        Assert.Equal("console/setup", d.Functions.Single(f => f.Name == "setup").Page!.Files);

        var index = ConsolePages.File(d, reports, "")!;
        Assert.Equal("text/html; charset=utf-8", index.ContentType);
        Assert.Contains("Reports", Encoding.UTF8.GetString(index.Content));
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], ConsolePages.File(d, reports, "logo.png")!.Content);
        Assert.Equal("text/javascript; charset=utf-8", ConsolePages.File(d, reports, "app.js")!.ContentType);
        Assert.Null(ConsolePages.File(d, reports, "../setup/index.html"));
        Assert.Null(ConsolePages.File(d, reports, "nope.js"));
    }
}
