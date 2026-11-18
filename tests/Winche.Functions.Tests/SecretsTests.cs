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
            import { callable, secrets } from "@winche-technologies/winche-functions";
            export const a = callable(async () => 1, { secrets: [secrets.define("ses-key")] });
            """), null, null));
        Assert.Contains(bad.Problems, p => p.Contains("'ses-key' isn't a secret name"));
        bad = await Assert.ThrowsAsync<FunctionDeployException>(() => functions.DeployAsync(Code("""
            import { callable } from "@winche-technologies/winche-functions";
            export const b = callable(async () => 1, { secrets: ["SES_KEY"] as any });
            """), null, null));
        Assert.Contains(bad.Problems, p => p.Contains("'SES_KEY' is a name; give its handle"));

        var d = await functions.DeployAsync(Code("""
            import { callable, secrets } from "@winche-technologies/winche-functions";
            const sesKey = secrets.define("SES_KEY"), sesRegion = secrets.define("SES_REGION");
            export const send = callable(async (_d, ctx) => {
              const key = sesKey.value();
              console.log("using", key, "for", sesRegion.isSet() ? "a region" : "the default region");
              return key.length;
            }, { secrets: [sesKey, sesRegion] });
            export const unset = callable(async (_d, ctx) => sesRegion.value(), { secrets: [sesRegion] });
            export const undeclared = callable(async (_d, ctx) => sesKey.value());
            export const leak = callable(async (_d, ctx) => { throw new Error("bad key " + sesKey.value()); }, { secrets: [sesKey] });
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

        Assert.Contains("doesn't list the secret SES_KEY", (await functions.InvokeAsync("undeclared", null, FunctionCaller.Server)).ErrorMessage);
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
