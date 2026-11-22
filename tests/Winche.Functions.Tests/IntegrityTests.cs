using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Winche.Functions.Tests;

/// <summary>Integrity in functions: ctx.app from the host's proof, and callables that answer only the project's own apps.</summary>
[Collection("postgres")]
public class IntegrityTests(PostgresFixture pg)
{
    private static async Task<(HttpStatusCode Status, JsonNode Body)> Call(FunctionsApp app, string name, string? proof = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{app.Url}/functions/v1/call/{name}") { Content = JsonContent.Create(new { data = (object?)null }) };
        if (proof is not null) request.Headers.Add("X-Test-App", proof);
        using var response = await app.Http.SendAsync(request);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    [Fact]
    public async Task Callables_see_the_apps_proof_and_can_require_it()
    {
        // The host's Integrity, stood in for by a header: "clinic-app/appAttest".
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync(), o => o.AppProof = http =>
            http.Request.Headers["X-Test-App"].ToString().Split('/') is [var id, var provider] ? new FunctionAppProof(id, provider) : null);
        var functions = app.Service<WincheFunctions>();
        await functions.DeployAsync(new Dictionary<string, string> { ["index.ts"] = """
            import { callable } from "@winche-technologies/winche-functions";
            export const who = callable((_d, ctx) => ({ app: ctx.app }));
            export const pay = callable((_d, ctx) => ({ paidFrom: ctx.app!.id }), { integrity: true });
            """ }, "Integrity", "a@x");

        Assert.Equal("clinic-app", (await Call(app, "who", "clinic-app/appAttest")).Body["data"]!["app"]!["id"]!.GetValue<string>());
        Assert.Equal("appAttest", (await Call(app, "who", "clinic-app/appAttest")).Body["data"]!["app"]!["provider"]!.GetValue<string>());
        Assert.Null((await Call(app, "who")).Body["data"]!["app"]);

        var refused = await Call(app, "pay");
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal("permission-denied", refused.Body["error"]!["code"]!.GetValue<string>());
        Assert.Contains("only the project's own apps", refused.Body["error"]!["message"]!.GetValue<string>());
        Assert.Equal("clinic-app", (await Call(app, "pay", "clinic-app/playIntegrity")).Body["data"]!["paidFrom"]!.GetValue<string>());

        // The project's own code calls it as ever (here with no app: it fails in the function, not at the door).
        var own = await functions.InvokeAsync("pay", null, FunctionCaller.Server);
        Assert.NotEqual("permission-denied", own.ErrorCode);
    }

    [Fact]
    public async Task Integrity_is_an_option_for_callables_only()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();
        var bad = await Assert.ThrowsAsync<FunctionDeployException>(() => functions.DeployAsync(new Dictionary<string, string> { ["index.ts"] = """
            import { schedule, callable } from "@winche-technologies/winche-functions";
            export const nightly = schedule("0 2 * * *", () => {}, { integrity: true } as never);
            export const odd = callable(() => 1, { integrity: "yes" } as never);
            """ }, null, null));
        Assert.Contains(bad.Problems, p => p.Contains("nightly: integrity is for callables"));
        Assert.Contains(bad.Problems, p => p.Contains("odd: integrity is true"));
    }
}
