using Winche.Core;
using Winche.Secrets;

namespace Winche.Functions.Tests;

[Collection("postgres")]
public class SecretsTests(PostgresFixture pg)
{
    private static Dictionary<string, string> Code(string index) => new() { ["index.ts"] = index };

    [Fact]
    public async Task Functions_read_the_secrets_they_declare_and_their_logs_hide_them()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();
        var secrets = app.Service<FunctionSecrets>();

        var bad = await Assert.ThrowsAsync<FunctionDeployException>(() => functions.DeployAsync(Code("""
            import { callable } from "@winche/functions";
            export const a = callable(async () => 1, { secrets: ["ses-key"] });
            export const b = callable(async () => 1, { secrets: "SES_KEY" as any });
            """), null, null));
        Assert.Contains(bad.Problems, p => p.StartsWith("a: 'ses-key' isn't a secret name"));
        Assert.Contains(bad.Problems, p => p.StartsWith("b: secrets is a list of names"));

        var d = await functions.DeployAsync(Code("""
            import { callable } from "@winche/functions";
            export const send = callable(async (_d, ctx) => {
              const key = ctx.secrets.get("SES_KEY");
              console.log("using", key, "for", ctx.secrets.has("SES_REGION") ? "a region" : "the default region");
              return key.length;
            }, { secrets: ["SES_KEY", "SES_REGION"] });
            export const unset = callable(async (_d, ctx) => ctx.secrets.get("SES_REGION"), { secrets: ["SES_REGION"] });
            export const undeclared = callable(async (_d, ctx) => ctx.secrets.get("SES_KEY"));
            export const leak = callable(async (_d, ctx) => { throw new Error("bad key " + ctx.secrets.get("SES_KEY")); }, { secrets: ["SES_KEY"] });
            """), "Secrets", "admin@x");
        Assert.Equal(["SES_KEY", "SES_REGION"], d.Functions.Single(f => f.Name == "send").Secrets);

        var listed = await secrets.ListAsync(d);
        Assert.Equal([("SES_KEY", false), ("SES_REGION", false)], listed.Select(s => (s.Name, s.IsSet)));
        Assert.Equal(["leak", "send"], listed[0].UsedBy);

        var missing = await functions.InvokeAsync("send", null, FunctionCaller.Server);
        Assert.Contains("The secret SES_KEY isn't set", missing.ErrorMessage);

        await secrets.SetAsync("SES_KEY", "AKIA-very-secret-value", "admin@x");
        var ran = await functions.InvokeAsync("send", null, FunctionCaller.Server);
        Assert.True(ran.Ok, ran.ErrorMessage);
        Assert.Equal(22, ran.Result!.GetValue<int>());
        Assert.Equal("using •••• for the default region", ran.Logs.Single().Message);

        var leak = await functions.InvokeAsync("leak", null, FunctionCaller.Server);
        Assert.Equal("bad key ••••", leak.ErrorMessage);

        Assert.Contains("doesn't declare the secret SES_KEY", (await functions.InvokeAsync("undeclared", null, FunctionCaller.Server)).ErrorMessage);
        Assert.Contains("isn't set", (await functions.InvokeAsync("unset", null, FunctionCaller.Server)).ErrorMessage);

        // Kept encrypted in Winche.Core's store, never readable through the listing.
        Assert.Equal("AKIA-very-secret-value", await app.Service<SecretStore>().GetAsync("functions.secret.SES_KEY"));
        var set = (await secrets.ListAsync(functions.Current)).Single(s => s.Name == "SES_KEY");
        Assert.Equal((true, "admin@x"), (set.IsSet, set.SetBy));

        // A saved secret no function declares any more still shows, so it can be removed.
        // A reference: read when the function runs, hidden the same; one that can't be read fails the run with the reason.
        MemorySecretSource.Values["ses"] = """{"key":"AKIA-from-elsewhere"}""";
        await secrets.SetReferenceAsync("SES_KEY", SecretReference.Parse("memory:ses#key"), "admin@x", allowAppCredentials: true);
        Assert.Equal("memory:ses#key", (await secrets.ListAsync(functions.Current)).Single(s => s.Name == "SES_KEY").Reference?.ToString());
        var viaRef = await functions.InvokeAsync("send", null, FunctionCaller.Server);
        Assert.True(viaRef.Ok, viaRef.ErrorMessage);
        Assert.Equal("using •••• for the default region", viaRef.Logs.Single().Message);
        Assert.True((await secrets.TestAsync("SES_KEY")).Ok);
        await secrets.SetReferenceAsync("SES_KEY", SecretReference.Parse("memory:gone"), "admin@x", allowAppCredentials: true);
        var gone = await functions.InvokeAsync("send", null, FunctionCaller.Server);
        Assert.Equal(("failed-precondition", false), (gone.ErrorCode, gone.Ok));
        Assert.Contains("Memory has nothing at gone", gone.ErrorMessage);
        Assert.False((await secrets.TestAsync("SES_KEY")).Ok);

        await secrets.SetAsync("OLD_TOKEN", "old-value", null);
        Assert.Contains(await secrets.ListAsync(functions.Current), s => s is { Name: "OLD_TOKEN", IsSet: true, UsedBy.Count: 0 });
        Assert.True(await secrets.RemoveAsync("OLD_TOKEN"));
        Assert.DoesNotContain(await secrets.ListAsync(functions.Current), s => s.Name == "OLD_TOKEN");
        await Assert.ThrowsAsync<ArgumentException>(() => secrets.SetAsync("bad name", "x", null));
    }
}
