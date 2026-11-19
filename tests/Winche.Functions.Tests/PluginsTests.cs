using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Values;

namespace Winche.Functions.Tests;

[Collection("postgres")]
public class PluginsTests(PostgresFixture pg)
{
    private const string Manifest = """
        {
          "title": "Text messages",
          "description": "Sends texts from a queue collection or a callable.",
          "icon": "message",
          "settings": {
            "FROM": { "label": "From number", "type": "string", "required": true },
            "QUEUE": { "label": "Queue collection", "type": "string", "default": "sms" },
            "RETRIES": { "label": "Retries", "type": "integer", "default": 3 }
          },
          "secrets": { "AUTH_TOKEN": { "label": "Auth token" } },
          "access": ["Updates documents in the queue collection."]
        }
        """;

    private const string Code = """
        import { callable, onDocument, plugin } from "@winche-technologies/winche-functions";
        import { documents } from "@winche-technologies/winche-admin";
        export default plugin((p) => {
          const token = p.secret("AUTH_TOKEN");
          return {
            send: callable(async (d, ctx) => ({ from: p.setting("FROM"), to: d.to, token: token.isSet() ? token.value() : null, id: p.id, name: ctx.name }), { secrets: [token] }),
            queued: onDocument.created(`${p.setting("QUEUE")}/{id}`, async (e) => { await documents.doc(e.path).update({ status: "sent", by: p.id }); }),
          };
        });
        """;

    /// <summary>A plugin package in a folder: package.json, winche-plugin.json and its code.</summary>
    private static string Package(string name, string version, string manifest, string code)
    {
        var dir = Path.Combine(Path.GetTempPath(), "winche-plugin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"), $$"""{ "name": "{{name}}", "version": "{{version}}", "type": "module", "main": "./index.js" }""");
        File.WriteAllText(Path.Combine(dir, "winche-plugin.json"), manifest);
        File.WriteAllText(Path.Combine(dir, "index.js"), code);
        return dir;
    }

    private static Dictionary<string, JsonNode?> Settings(params (string Key, JsonNode? Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);

    private static async Task Until(Func<Task<bool>> condition, string what, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until) { if (await condition()) return; await Task.Delay(100); }
        throw new TimeoutException($"Waited {seconds} s for {what}.");
    }

    [Fact]
    public async Task Installed_copies_have_their_own_settings_secrets_and_functions_beside_the_projects_code()
    {
        var folder = Package("text-plugin", "1.0.0", Manifest, Code);
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync(), o => o.ProvidedPlugins["text-plugin"] = folder);
        var functions = app.Service<WincheFunctions>();
        var secrets = app.Service<FunctionSecrets>();

        // A project with no code of its own can install one.
        var bad = await Assert.ThrowsAsync<FunctionDeployException>(() => functions.InstallPluginAsync(new("sms", "text-plugin", Settings: Settings(("RETRIES", 1.5))), "admin@x"));
        Assert.Contains(bad.Problems, p => p.Contains("From number (FROM) is needed"));
        Assert.Contains(bad.Problems, p => p.Contains("Retries (RETRIES): a whole number"));
        var d = await functions.InstallPluginAsync(new("sms", "text-plugin", Settings: Settings(("FROM", "+441"))), "admin@x");
        Assert.Equal("Installed sms (text-plugin 1.0.0)", d.Note);
        Assert.Equal(["sms.queued", "sms.send"], d.Functions.Select(f => f.Name).Order());
        Assert.All(d.Functions, f => Assert.Equal("sms", f.Plugin));
        var installed = Assert.Single(d.PluginList);
        // An optional setting given empty stays empty (its feature off); not given, it's the default.
        var cleared = PluginSettings.Resolve(PluginManifest.Parse(Manifest, "t"), Settings(("FROM", "+1"), ("QUEUE", "")), out var clearedProblems);
        Assert.Empty(clearedProblems);
        Assert.Equal("", cleared["QUEUE"]!.GetValue<string>());
        PluginSettings.Resolve(PluginManifest.Parse(Manifest, "t"), Settings(("FROM", "")), out var needed);
        Assert.Contains(needed, p => p.Contains("From number (FROM) is needed"));
        Assert.Equal(("1.0.0", "console", "sms", 3), (installed.Version, installed.Source, installed.Settings["QUEUE"]!.GetValue<string>(), installed.Settings["RETRIES"]!.GetValue<int>()));

        // A second copy, under another id, with its own settings and secret.
        await functions.InstallPluginAsync(new("alerts", "text-plugin", "1.0.0", Settings(("FROM", "+442"), ("QUEUE", "alerts"))), "admin@x");
        await Assert.ThrowsAsync<FunctionDeployException>(() => functions.InstallPluginAsync(new("sms", "text-plugin", Settings: Settings(("FROM", "+3"))), "admin@x"));
        await secrets.SetAsync("SMS__AUTH_TOKEN", "token-one", "admin@x");
        var sms = await functions.InvokeAsync("sms.send", JsonNode.Parse("""{ "to": "+1" }"""), FunctionCaller.Server);
        Assert.True(sms.Ok, sms.ErrorMessage);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"from":"+441","to":"+1","token":"token-one","id":"sms","name":"sms.send"}"""), sms.Result), sms.Result!.ToJsonString());
        var alerts = await functions.InvokeAsync("alerts.send", JsonNode.Parse("""{ "to": "+1" }"""), FunctionCaller.Server);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"from":"+442","to":"+1","token":null,"id":"alerts","name":"alerts.send"}"""), alerts.Result), alerts.Result!.ToJsonString());
        var listed = await functions.PluginsAsync();
        Assert.Equal([("alerts", "1.0.0", "ALERTS__AUTH_TOKEN"), ("sms", "1.0.0", null)],
            listed.Select(p => (p.Install.Id, p.Latest, p.SecretsNeeded.SingleOrDefault())).OrderBy(x => x.Id));
        Assert.Equal(["SMS__AUTH_TOKEN"], (await secrets.ListAsync(functions.Current)).Where(s => s.IsSet).Select(s => s.Name));

        // Triggers on the paths its settings give.
        var db = app.Service<DocumentDatabase>();
        await db.WriteAsync([new SetWrite { Path = "alerts/a1", Fields = new Dictionary<string, Value> { ["to"] = new StringValue("+1") } }]);
        await Until(async () => (await db.GetAsync("alerts/a1"))?.Fields.ContainsKey("status") == true, "the plugin's trigger");
        Assert.Equal("alerts", ((StringValue)(await db.GetAsync("alerts/a1"))!.Fields["by"]).Value);

        // The project's own code deploys beside them; the plugins stay.
        var own = await functions.DeployAsync(new Dictionary<string, string> { ["index.ts"] = """
            import { callable } from "@winche-technologies/winche-functions";
            export const hello = callable(async () => "hi");
            """ }, "Own code", "dev@x");
        Assert.Equal(["alerts.queued", "alerts.send", "hello", "sms.queued", "sms.send"], own.Functions.Select(f => f.Name).Order());
        Assert.Equal(2, own.PluginList.Count);

        // Settings change; an uninstall takes its functions out and, when asked, its secrets.
        var changed = await functions.ConfigurePluginAsync("sms", Settings(("FROM", "+449")), "admin@x");
        Assert.Equal("+449", changed.PluginList.Single(p => p.Id == "sms").Settings["FROM"]!.GetValue<string>());
        Assert.Equal("+449", (await functions.InvokeAsync("sms.send", JsonNode.Parse("{}"), FunctionCaller.Server)).Result!["from"]!.GetValue<string>());
        var removed = await functions.UninstallPluginAsync("sms", "admin@x", removeSecrets: true);
        Assert.Equal(["alerts.queued", "alerts.send", "hello"], removed.Functions.Select(f => f.Name).Order());
        Assert.Equal("not-found", (await functions.InvokeAsync("sms.send", null, FunctionCaller.Server)).ErrorCode);
        Assert.DoesNotContain(await secrets.ListAsync(functions.Current), s => s.Name == "SMS__AUTH_TOKEN" && s.IsSet);

        // A project with only plugins removing its last one: a version with no functions.
        var onlyPlugins = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync(), o => o.ProvidedPlugins["text-plugin"] = folder);
        await using (onlyPlugins)
        {
            var f2 = onlyPlugins.Service<WincheFunctions>();
            await f2.InstallPluginAsync(new("sms", "text-plugin", Settings: Settings(("FROM", "+441"))), "admin@x");
            var none = await f2.UninstallPluginAsync("sms", "admin@x");
            Assert.Empty(none.Functions);
            Assert.Equal("not-found", (await f2.InvokeAsync("sms.send", null, FunctionCaller.Server)).ErrorCode);
            await f2.InstallPluginAsync(new("sms", "text-plugin", Settings: Settings(("FROM", "+441"))), "admin@x");   // and back again
            Assert.True((await f2.InvokeAsync("sms.send", JsonNode.Parse("{}"), FunctionCaller.Server)).Ok);
        }

        // Rolling back brings back the plugins of that version.
        var back = await functions.RollBackAsync(own.Version, null, "admin@x");
        Assert.Equal(["alerts", "sms"], back.PluginList.Select(p => p.Id).Order());
        Assert.True((await functions.InvokeAsync("sms.send", JsonNode.Parse("{}"), FunctionCaller.Server)).Ok);
    }

    [Fact]
    public async Task A_plugin_reads_only_its_own_secrets_and_upgrades_keep_its_settings()
    {
        var sneaky = Package("sneaky-plugin", "1.0.0", """{ "title": "Sneaky" }""", """
            import { callable, plugin, secrets } from "@winche-technologies/winche-functions";
            const mail = secrets.define("MAIL_KEY");
            export default plugin(() => ({ grab: callable(async () => mail.value(), { secrets: [mail] }) }));
            """);
        var v1 = Package("text-plugin", "1.0.0", Manifest, Code);
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync(), o =>
        {
            o.ProvidedPlugins["sneaky-plugin"] = sneaky;
            o.ProvidedPlugins["text-plugin"] = v1;
        });
        var functions = app.Service<WincheFunctions>();
        var refused = await Assert.ThrowsAsync<FunctionDeployException>(() => functions.InstallPluginAsync(new("x", "sneaky-plugin"), "admin@x"));
        Assert.Contains(refused.Problems, p => p.Contains("may only read the secrets its winche-plugin.json lists (MAIL_KEY isn't one)"));
        Assert.Contains("isn't a plugin id", (await Assert.ThrowsAsync<FunctionDeployException>(() => functions.InstallPluginAsync(new("Bad_Id", "text-plugin"), "admin@x"))).Message);

        await functions.InstallPluginAsync(new("sms", "text-plugin", Settings: Settings(("FROM", "+441"), ("RETRIES", 5))), "admin@x");
        // Version 2: RETRIES gone, a new required setting.
        var v2 = Package("text-plugin", "2.0.0", Manifest.Replace("\"RETRIES\": { \"label\": \"Retries\", \"type\": \"integer\", \"default\": 3 }",
            "\"REGION\": { \"label\": \"Region\", \"type\": \"select\", \"options\": [\"eu\", \"us\"], \"required\": true }"), Code);
        app.Service<FunctionsOptions>().ProvidedPlugins["text-plugin"] = v2;
        app.Service<FunctionsOptions>().ProvidedPlugins["text-plugin@1.0.0"] = v1;   // still there: the copy on 1.0.0 keeps changing
        await functions.ConfigurePluginAsync("sms", Settings(("FROM", "+441"), ("RETRIES", 5)), "admin@x");
        Assert.Equal("2.0.0", (await functions.PluginsAsync()).Single().Latest);
        var needs = await Assert.ThrowsAsync<FunctionDeployException>(() => functions.UpgradePluginAsync("sms", null, "admin@x"));
        Assert.Contains(needs.Problems, p => p.Contains("Region (REGION) is needed"));
        var up = await functions.UpgradePluginAsync("sms", "2.0.0", "admin@x", Settings(("REGION", "eu")));
        var now = up.PluginList.Single();
        Assert.Equal(("2.0.0", "+441", "eu", false), (now.Version, now.Settings["FROM"]!.GetValue<string>(), now.Settings["REGION"]!.GetValue<string>(), now.Settings.ContainsKey("RETRIES")));
        Assert.Equal("sms: text-plugin 1.0.0 → 2.0.0", up.Note);
    }

    [Fact]
    public async Task Packages_come_from_npm_checked_against_their_integrity()
    {
        var tgz = Tarball(("package/package.json", """{ "name": "@acme/text-plugin", "version": "1.2.0" }"""), ("package/winche-plugin.json", Manifest));
        var integrity = "sha512-" + Convert.ToBase64String(SHA512.HashData(tgz));
        var npm = new FakeRegistry
        {
            ["/@acme%2Ftext-plugin/latest"] = Json($$"""{ "version": "1.2.0", "dist": { "tarball": "https://registry.test/t.tgz", "integrity": "{{integrity}}" } }"""),
            ["/@acme%2Ftext-plugin/1.1.0"] = Json($$"""{ "version": "1.1.0", "dist": { "tarball": "https://registry.test/t.tgz", "integrity": "sha512-{{Convert.ToBase64String(new byte[64])}}" } }"""),
            ["/t.tgz"] = tgz,
        };
        var packages = new PluginPackages(new FunctionsOptions { PluginRegistry = "https://registry.test/" }, new PluginRegistryHttp(new HttpClient(npm)));
        var found = await packages.GetAsync("@acme/text-plugin", null);
        Assert.Equal(("1.2.0", "Text messages", "npm:@acme/text-plugin@1.2.0"), (found.Version, found.Manifest.Title, found.Specifier));
        Assert.Equal("1.2.0", await packages.LatestAsync("@acme/text-plugin"));
        Assert.Contains("integrity", (await Assert.ThrowsAsync<FunctionDeployException>(() => packages.GetAsync("@acme/text-plugin", "1.1.0"))).Message);
        Assert.Contains("npm has no package nope", (await Assert.ThrowsAsync<FunctionDeployException>(() => packages.GetAsync("nope", null))).Message);
        Assert.Null(await packages.LatestAsync("nope"));
        Assert.Contains("isn't an exact version", (await Assert.ThrowsAsync<FunctionDeployException>(() => packages.GetAsync("nope", "^1"))).Message);
    }

    [Fact]
    public async Task A_plugins_http_functions_get_the_raw_request_at_their_id_and_name()
    {
        var folder = Package("hook-plugin", "1.0.0", """{ "title": "Hooks", "secrets": { "SIGNING": { "label": "Signing secret" } } }""", """
            import { http, plugin } from "@winche-technologies/winche-functions";
            export default plugin((p) => {
              const signing = p.secret("SIGNING");
              return { events: http.onRequest(async (req) => ({ ok: (await req.text()) === `signed:${signing.value()}`, path: new URL(req.url).pathname }), { methods: ["POST"], secrets: [signing] }) };
            });
            """);
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync(), o => o.ProvidedPlugins["hook-plugin"] = folder);
        await app.Service<WincheFunctions>().InstallPluginAsync(new("billing", "hook-plugin"), "admin@x");
        await app.Service<FunctionSecrets>().SetAsync("BILLING__SIGNING", "s3cret", "admin@x");
        var r = await app.Http.PostAsync($"{app.Url}/functions/v1/http/billing.events/incoming", new StringContent("signed:s3cret"));
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, text);
        Assert.Contains("\"ok\":true", text);
        Assert.Contains("/functions/v1/http/billing.events/incoming", text);
    }

    [Fact]
    public async Task Callables_know_whether_the_server_or_an_app_called()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();
        await functions.DeployAsync(new Dictionary<string, string> { ["index.ts"] = """
            import { callable, http } from "@winche-technologies/winche-functions";
            import * as admin from "@winche-technologies/winche-admin";
            export const who = callable(async (_d, ctx) => ({ server: ctx.byServer, uid: ctx.caller?.uid ?? null }));
            export const relay = callable(async () => admin.functions.call("who", null));
            export const hook = http.onRequest(async (_r, ctx) => ({ server: ctx.byServer }));
            """ }, null, null);
        Assert.Equal("""{"server":true,"uid":null}""", (await functions.InvokeAsync("who", null, FunctionCaller.Server)).Result!.ToJsonString());
        Assert.Equal("""{"server":true,"uid":null}""", (await functions.InvokeAsync("relay", null, FunctionCaller.Server with { FromApp = true })).Result!.ToJsonString());
        async Task<string> App(string? uid)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{app.Url}/functions/v1/call/who") { Content = System.Net.Http.Json.JsonContent.Create(new { data = (object?)null }) };
            if (uid is not null) request.Headers.Add("X-Test-Uid", uid);
            return JsonNode.Parse(await (await app.Http.SendAsync(request)).Content.ReadAsStringAsync())!["data"]!.ToJsonString();
        }
        Assert.Equal("""{"server":false,"uid":null}""", await App(null));   // signed out: not the server
        Assert.Equal("""{"server":false,"uid":"ann"}""", await App("ann"));
        Assert.Contains("\"server\":false", await (await app.Http.PostAsync($"{app.Url}/functions/v1/http/hook", new StringContent(""))).Content.ReadAsStringAsync());
    }

    [Fact]
    public void Settings_show_and_are_needed_only_where_their_condition_holds_and_setup_steps_name_what_exists()
    {
        var m = PluginManifest.Parse("""
            { "title": "Calls", "billing": "The service charges per call.",
              "settings": {
                "MODE": { "label": "Mode", "type": "select", "options": ["account", "office"], "default": "account", "section": "Calls" },
                "OFFICE": { "label": "Office phone", "required": true, "section": "Calls", "showIf": { "setting": "MODE", "is": "office" } },
                "URL": { "label": "Address" }
              },
              "secrets": { "KEY": { "label": "Key", "showIf": { "setting": "URL", "set": true } } },
              "setup": [{ "text": "Point the service at {{function:hook}} with {{secret:KEY}}.", "showIf": { "setting": "URL", "set": true } }] }
            """, "t");
        Assert.Equal("Calls", m.Settings["OFFICE"].Section);
        PluginSettings.Resolve(m, Settings(), out var asAccount);
        Assert.Empty(asAccount);                                    // OFFICE isn't shown: not needed
        PluginSettings.Resolve(m, Settings(("MODE", "office")), out var asOffice);
        Assert.Contains(asOffice, p => p.Contains("Office phone (OFFICE) is needed"));
        Assert.True(m.Setup[0].ShowIf!.Holds(Settings(("URL", "https://x"))));
        Assert.False(m.Setup[0].ShowIf!.Holds(Settings(("URL", ""))));
        Assert.True(new PluginCondition { Setting = "MODE", Is = new JsonArray("a", "office") }.Holds(Settings(("MODE", "office"))));

        var bad = Assert.Throws<FunctionDeployException>(() => PluginManifest.Parse("""
            { "title": "x", "settings": { "A": { "label": "A", "showIf": { "setting": "NOPE", "is": 1 } }, "B": { "label": "B", "showIf": { "setting": "A" } } },
              "setup": [{ "text": "{{setting:GONE}} and {{secret:NONE}}" }] }
            """, "p"));
        Assert.Contains(bad.Problems, p => p.Contains("showIf names NOPE"));
        Assert.Contains(bad.Problems, p => p.Contains("showIf says is"));
        Assert.Contains(bad.Problems, p => p.Contains("{{setting:GONE}} isn't one of its settings"));
        Assert.Contains(bad.Problems, p => p.Contains("{{secret:NONE}} isn't one of its secrets"));
    }

    [Fact]
    public async Task Packages_say_what_they_are_their_versions_and_searches_find_them()
    {
        var v1 = Package("text-plugin", "1.0.0", Manifest, Code);
        var v2 = Package("text-plugin", "1.2.0", Manifest, Code);
        File.WriteAllText(Path.Combine(v2, "README.md"), "# Text messages\n\nSends texts.");
        File.WriteAllText(Path.Combine(v2, "package.json"), """{ "name": "text-plugin", "version": "1.2.0", "main": "./index.js", "description": "Texts.", "author": { "name": "Acme" }, "repository": { "url": "git+https://github.com/acme/text-plugin.git" }, "license": "MIT" }""");
        var options = new FunctionsOptions { PluginRegistry = "https://registry.test/" };
        options.ProvidedPlugins["text-plugin"] = v2;
        options.ProvidedPlugins["text-plugin@1.0.0"] = v1;
        options.ProvidedPlugins["text-plugin@1.2.0"] = v2;
        var npm = new FakeRegistry
        {
            ["/-/v1/search"] = Json("""
                { "objects": [
                  { "package": { "name": "@acme/payments", "version": "2.1.0", "description": "Payments.", "publisher": { "username": "acme" }, "date": "2026-11-01T00:00:00Z" },
                    "downloads": { "monthly": 120, "weekly": 30 }, "score": { "detail": { "popularity": 0.2 } } },
                  { "package": { "name": "@acme/texts", "version": "1.0.0" }, "downloads": { "monthly": 5400 }, "score": { "detail": { "popularity": 0.9 } } }
                ] }
                """),
        };
        var packages = new PluginPackages(options, new PluginRegistryHttp(new HttpClient(npm)));
        var about = (await packages.GetAsync("text-plugin", null)).About;
        Assert.Equal(("Texts.", "Acme", "https://github.com/acme/text-plugin", "MIT"), (about.Description, about.Author, about.Repository, about.License));
        Assert.StartsWith("# Text messages", about.Readme);
        Assert.Equal(["1.2.0", "1.0.0"], (await packages.VersionsAsync("text-plugin")).Select(v => v.Version));
        var found = await packages.SearchAsync("");
        Assert.Equal([("@acme/texts", false), ("@acme/payments", false), ("text-plugin", true)], found.Select(f => (f.Package, f.Provided)));   // most downloaded first
        Assert.Equal(("acme", 120L), (found[1].Publisher, found[1].Downloads));
        Assert.Equal("popularity=1.0", npm.LastQuery?.Split('&').FirstOrDefault(x => x.StartsWith("popularity")));
        Assert.Equal(["text-plugin"], (await packages.SearchAsync("text")).Where(f => f.Provided).Select(f => f.Package));
        // npm answers every package with the keyword whatever the words: only those the words are in are kept.
        Assert.Equal(["@acme/payments"], (await packages.SearchAsync("payments")).Where(f => !f.Provided).Select(f => f.Package));
        Assert.Empty((await packages.SearchAsync("zzzz")).Where(f => !f.Provided));
        Assert.True(PluginPackages.Matches("sms twilio", "@acme/x", "Texts.", ["twilio", "SMS"]));
        Assert.False(PluginPackages.Matches("sms stripe", "@acme/x", "Texts.", ["twilio", "sms"]));

        // From npm: the README and package.json come out of the package file too.
        var tgz = Tarball(("package/package.json", """{ "name": "@acme/payments", "version": "2.1.0", "homepage": "https://acme.test" }"""), ("package/winche-plugin.json", Manifest), ("package/readme.md", "Hello"));
        var registry = new FakeRegistry
        {
            ["/@acme%2Fpayments/latest"] = Json($$"""{ "version": "2.1.0", "dist": { "tarball": "https://registry.test/p.tgz", "integrity": "sha512-{{Convert.ToBase64String(SHA512.HashData(tgz))}}" } }"""),
            ["/p.tgz"] = tgz,
            ["/@acme%2Fpayments"] = Json("""{ "versions": { "2.0.0": {}, "2.1.0": {} }, "time": { "2.0.0": "2026-10-01T00:00:00Z", "2.1.0": "2026-11-01T00:00:00Z" } }"""),
        };
        var fromNpm = new PluginPackages(new FunctionsOptions { PluginRegistry = "https://registry.test/" }, new PluginRegistryHttp(new HttpClient(registry)));
        var pay = await fromNpm.GetAsync("@acme/payments", null);
        Assert.Equal(("https://acme.test", "Hello"), (pay.About.Homepage, pay.About.Readme));
        Assert.Equal([("2.1.0", 2026, 11), ("2.0.0", 2026, 10)], (await fromNpm.VersionsAsync("@acme/payments")).Select(v => (v.Version, v.At!.Value.Year, v.At.Value.Month)));
    }

    [Fact]
    public async Task An_installed_copy_has_its_history()
    {
        var folder = Package("text-plugin", "1.0.0", Manifest, Code);
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync(), o => o.ProvidedPlugins["text-plugin"] = folder);
        var functions = app.Service<WincheFunctions>();
        await functions.InstallPluginAsync(new("sms", "text-plugin", Settings: Settings(("FROM", "+441"))), "ann@x");
        await functions.DeployAsync(new Dictionary<string, string> { ["index.ts"] = "export {};" }, "Own code", "dev@x");
        await functions.ConfigurePluginAsync("sms", Settings(("FROM", "+442"), ("QUEUE", "texts")), "ann@x");
        await functions.UninstallPluginAsync("sms", "bob@x");
        var history = await functions.PluginHistoryAsync("sms");
        Assert.Equal(["Removed", "Settings changed: FROM, QUEUE", "Installed text-plugin 1.0.0"], history.Select(h => h.What));
        Assert.Equal(["bob@x", "ann@x", "ann@x"], history.Select(h => h.By));
    }

    [Fact]
    public void Plugins_from_npm_and_the_SDKs_are_exempt_from_Denos_wait_and_share_the_deployments_SDK()
    {
        var none = new JsonObject();
        FunctionBundler.ExemptFromAge(none, ["npm:@winche-technologies/winche-functions", "npm:@acme/texts"]);
        Assert.Equal("""{"minimumDependencyAge":{"age":"P1D","exclude":["npm:@winche-technologies/winche-functions","npm:@acme/texts"]}}""", none.ToJsonString());
        var own = JsonNode.Parse("""{ "minimumDependencyAge": { "age": "P3D", "exclude": ["npm:@acme/texts"] } }""")!.AsObject();
        FunctionBundler.ExemptFromAge(own, ["npm:@acme/texts", "npm:@acme/pay"]);
        Assert.Equal("""{"minimumDependencyAge":{"age":"P3D","exclude":["npm:@acme/texts","npm:@acme/pay"]}}""", own.ToJsonString());
        var length = JsonNode.Parse("""{ "minimumDependencyAge": "P2D" }""")!.AsObject();
        FunctionBundler.ExemptFromAge(length, ["npm:@acme/pay"]);
        Assert.Equal("""{"minimumDependencyAge":{"age":"P2D","exclude":["npm:@acme/pay"]}}""", length.ToJsonString());

        var install = new PluginInstall("sms", "@acme/texts", "1.0.0", new Dictionary<string, JsonNode?>(), PluginManifest.Parse(Manifest, "t"), "console", null, DateTimeOffset.UtcNow);
        Assert.StartsWith("import \"@winche-technologies/winche-functions\";", PluginEntry.Source(null, [(install, "npm:@acme/texts@1.0.0")]));
        Assert.DoesNotContain("import \"@winche-technologies/winche-functions\"", PluginEntry.Source(null, [(install, "./plugins/acme__texts/index.js")]));
    }

    [Fact]
    public void Manifests_are_checked()
    {
        var bad = Assert.Throws<FunctionDeployException>(() => PluginManifest.Parse("""
            { "title": "", "settings": { "a-b": { "label": "x" }, "MODE": { "label": "Mode", "type": "select" }, "N": { "label": "N", "type": "integer", "default": "3" } },
              "secrets": { "lower": { "label": "x" } } }
            """, "p"));
        Assert.Contains(bad.Problems, p => p.Contains("Give it a title"));
        Assert.Contains(bad.Problems, p => p.Contains("'a-b' isn't a setting name"));
        Assert.Contains(bad.Problems, p => p.Contains("a select lists its options"));
        Assert.Contains(bad.Problems, p => p.Contains("settings.N: its default: a whole number"));
        Assert.Contains(bad.Problems, p => p.Contains("'lower' isn't a secret name"));
    }

    private static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Tarball(params (string Path, string Text)[] files)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz))
            foreach (var (path, text) in files)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
        return output.ToArray();
    }

    private sealed class FakeRegistry : HttpMessageHandler, System.Collections.IEnumerable
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        public string? LastQuery { get; private set; }
        public byte[] this[string path] { set => _files[path] = value; }
        public System.Collections.IEnumerator GetEnumerator() => _files.GetEnumerator();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult((LastQuery = request.RequestUri!.Query.TrimStart('?')) is var _ && _files.TryGetValue(request.RequestUri!.AbsolutePath, out var b)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
