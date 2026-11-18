using System.Text.Json.Nodes;

namespace Winche.Functions.Tests;

[Collection("postgres")]
public class AdminSdkTests(PostgresFixture pg)
{
    [Fact]
    public async Task Functions_call_callables_as_the_server_or_as_a_user_and_as_is_refused_where_rules_dont_apply()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();
        await functions.DeployAsync(new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { callable, HttpsError } from "@winche/functions";
                import { functions, as, auth, documents } from "@winche/admin";
                export const whoami = callable(async (_d, ctx) => ({ uid: ctx.caller?.uid ?? null, name: ctx.name }));
                export const refuse = callable(async () => { throw new HttpsError("failed-precondition", "not now", { why: 1 }); });
                export const outer = callable(async (_d, ctx) => ({
                  server: await functions.call("whoami"),
                  user: await as(ctx.caller!).functions.call("whoami"),
                  refused: await functions.call("refuse").catch((e) => `${e.code}: ${e.message}`),
                  missing: await functions.call("nope").catch((e) => e.code),
                  authAs: await as(ctx.caller!).auth.user("u1").get().catch((e) => e.code),
                }));
                // Outside a run (a module's top level) the SDK says how to use it.
                let early = "";
                try { await documents.doc("a/b").get(); } catch (e) { early = (e as Error).message; }
                export const topLevel = callable(async () => early);
                """,
        }, null, null);

        var r = await functions.InvokeAsync("outer", null, new FunctionCaller("ann", new Dictionary<string, object?> { ["sub"] = "ann" }));
        Assert.True(r.Ok, r.ErrorMessage);
        Assert.Equal("""{"uid":null,"name":"whoami"}""", r.Result!["server"]!.ToJsonString());
        Assert.Equal("ann", r.Result["user"]!["uid"]!.GetValue<string>());
        Assert.Equal("failed-precondition: not now", r.Result["refused"]!.GetValue<string>());
        Assert.Equal("not-found", r.Result["missing"]!.GetValue<string>());
        Assert.Equal("invalid-argument", r.Result["authAs"]!.GetValue<string>());
        Assert.Contains("isn't signed in here", (await functions.InvokeAsync("topLevel", null, FunctionCaller.Server)).Result!.GetValue<string>());
    }

    [Fact]
    public async Task Acting_as_a_user_applies_the_database_and_storage_rules_and_the_server_still_does_anything()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync(),
            databaseRules: """
                match /notes/{uid}/{doc=**} { allow read, write: if request.auth != null && request.auth.uid == uid; }
                match /staff/{doc} { allow read: if request.auth.token.role == "staff"; }
                """,
            storageRules: """
                match /{owner} { allow list: if request.auth != null && request.auth.uid == owner; }
                match /{owner}/{rest=**} {
                  allow get, list, create, update, delete: if request.auth != null && request.auth.uid == owner;
                }
                """);
        var functions = app.Service<WincheFunctions>();
        await functions.DeployAsync(new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { callable } from "@winche/functions";
                import * as admin from "@winche/admin";
                const code = (p: Promise<unknown>) => p.then(() => "ok", (e) => e.code);
                export const tryAll = callable(async (_d, ctx) => {
                  const me = admin.as(ctx.caller!);
                  await admin.documents.doc("staff/x").set({ n: 1 });
                  return {
                    ownWrite: await code(me.documents.doc(`notes/${ctx.caller!.uid}/a/1`).set({ t: "mine" })),
                    ownRead: await code(me.documents.doc(`notes/${ctx.caller!.uid}/a/1`).get()),
                    otherWrite: await code(me.documents.doc("notes/someone/a/1").set({ t: "theirs" })),
                    otherRead: await code(me.documents.doc("notes/someone/a/1").get()),
                    query: await code(me.documents.collection("notes/someone/a").get()),
                    claimRead: await code(admin.as({ uid: "x", token: { role: "staff" } }).documents.doc("staff/x").get()),
                    noClaimRead: await code(me.documents.doc("staff/x").get()),
                    recursive: await code(me.documents.recursiveDelete(me.documents.doc(`notes/${ctx.caller!.uid}`))),
                    serverWrite: await code(admin.documents.doc("notes/someone/a/2").set({ t: "server" })),
                    ownFile: await code(me.files.child(`${ctx.caller!.uid}/a.txt`).putData("hi")),
                    ownFileRead: await me.files.child(`${ctx.caller!.uid}/a.txt`).getText(),
                    otherFile: await code(me.files.child("someone/a.txt").putData("no")),
                    otherFileRead: await code(me.files.child("someone/b.txt").getData()),
                    folderDelete: await code(me.files.child(`${ctx.caller!.uid}`).delete()),
                    serverFile: await code(admin.files.child("someone/b.txt").putData("server")),
                  };
                });
                """,
        }, null, null);

        var r = await functions.InvokeAsync("tryAll", null, new FunctionCaller("ann", new Dictionary<string, object?> { ["sub"] = "ann" }));
        Assert.True(r.Ok, r.ErrorMessage);
        Assert.Equal("""
            {"ownWrite":"ok","ownRead":"ok","otherWrite":"permission-denied","otherRead":"permission-denied","query":"permission-denied","claimRead":"ok","noClaimRead":"permission-denied","recursive":"invalid-argument","serverWrite":"ok","ownFile":"ok","ownFileRead":"hi","otherFile":"permission-denied","otherFileRead":"permission-denied","folderDelete":"ok","serverFile":"ok"}
            """.Trim(), r.Result!.ToJsonString());
    }
}
