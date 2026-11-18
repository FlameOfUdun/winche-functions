using Winche.Storage.Services;

namespace Winche.Functions.Tests;

[Collection("postgres")]
public class FileAccessTests(PostgresFixture pg)
{
    [Fact]
    public async Task Functions_write_read_and_link_files_and_triggers_see_the_write()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();
        await functions.DeployAsync(new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { callable, onFile } from "@winche/functions"; import * as admin from "@winche/admin";
                export const make = callable(async (_d, ctx) => {
                  const bytes = new Uint8Array(70000).map((_, i) => i % 251);
                  const week = admin.files.child("reports/u1").child("week.pdf");
                  const pdf = await week.putData(bytes, { mimeType: "application/pdf", metadata: { week: 46 }, contentDisposition: 'attachment; filename="week.pdf"' });
                  await admin.files.child("notes/u1.txt").putData("héllo");
                  const back = await week.getData();
                  const same = back!.length === bytes.length && back!.every((b, i) => b === bytes[i]);
                  const link = await week.downloadUrl();
                  const updated = await week.updateMetadata({ week: null, checked: true }, { cacheControl: "private" });
                  const folder = await admin.files.child("reports").list();
                  const all = await admin.files.child("reports/u1").listChildren();
                  return { size: pdf.data!.sizeBytes, type: pdf.data!.mimeType, week: pdf.data!.metadata.week, same, text: await admin.files.child("notes/u1.txt").getText(),
                           record: (await admin.files.child("notes/u1.txt").getSnapshot()).data?.mimeType, none: await admin.files.child("nope.txt").getData(),
                           missing: (await admin.files.child("nope.txt").getSnapshot()).exists, name: week.name, parent: week.parent?.path,
                           link: { url: link.url, expiresAt: link.expiresAt.toISOString() }, meta: updated.data!.metadata, cache: updated.data!.cacheControl,
                           folders: folder.folders.map((f) => f.path), files: all.files.map((f) => f.reference.name) };
                });
                export const missingLink = callable(async (_d, ctx) => admin.files.child("nope.pdf").downloadUrl());
                export const onReport = onFile.uploaded("reports/{uid}/{name}", async (e, ctx) => {
                  await admin.documents.doc(`seen/${e.params.uid}`).set({ name: e.params.name, size: e.file.data?.sizeBytes ?? 0 });
                });
                """,
        }, "Files", "admin@x");

        var r = await functions.InvokeAsync("make", null, FunctionCaller.Server);
        Assert.True(r.Ok, r.ErrorMessage);
        var o = r.Result!;
        Assert.Equal((70000, "application/pdf", 46, true), (o["size"]!.GetValue<int>(), o["type"]!.GetValue<string>(), o["week"]!.GetValue<int>(), o["same"]!.GetValue<bool>()));
        Assert.Equal(("héllo", "text/plain; charset=utf-8"), (o["text"]!.GetValue<string>(), o["record"]!.GetValue<string>()));
        Assert.Null(o["none"]);
        Assert.False(o["missing"]!.GetValue<bool>());
        Assert.Equal(("week.pdf", "reports/u1"), (o["name"]!.GetValue<string>(), o["parent"]!.GetValue<string>()));
        Assert.Equal("""{"checked":true}""", o["meta"]!.ToJsonString());
        Assert.Equal("private", o["cache"]!.GetValue<string>());
        Assert.Equal("""["reports/u1"]""", o["folders"]!.ToJsonString());
        Assert.Equal("""["week.pdf"]""", o["files"]!.ToJsonString());
        var url = o["link"]!["url"]!.GetValue<string>();
        Assert.Matches(@"^mem://reports/u1/[0-9a-f]+/week\.pdf\?type=application/pdf$", url);   // the stored object, with the file's type
        Assert.StartsWith("2030-01-01", o["link"]!["expiresAt"]!.GetValue<string>());
        Assert.Equal("attachment; filename=\"week.pdf\"", (await app.Service<FileStorage>().GetAsync("reports/u1/week.pdf"))!.ContentDisposition);

        Assert.Equal("not-found", (await functions.InvokeAsync("missingLink", null, FunctionCaller.Server)).ErrorCode);

        // The write is an upload like any other: its trigger runs.
        var db = app.Service<Winche.Database.Runtime.DocumentDatabase>();
        for (var i = 0; i < 200 && await db.GetAsync("seen/u1") is null; i++) await Task.Delay(100);
        Assert.NotNull(await db.GetAsync("seen/u1"));
    }

    [Fact]
    public async Task Before_functions_can_read_files_but_not_write_them()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        var functions = app.Service<WincheFunctions>();
        await functions.DeployAsync(new Dictionary<string, string>
        {
            ["index.ts"] = """
                import { callable, onDocument } from "@winche/functions"; import * as admin from "@winche/admin";
                export const seed = callable(async (_d, ctx) => { await admin.files.child("limits.txt").putData("3"); });
                export const check = onDocument.willBeWritten("orders/{id}", async (_e, ctx) => {
                  if (Number(await admin.files.child("limits.txt").getText()) !== 3) throw new Error("couldn't read the limit");
                  return { set: { limit: 3 } };
                });
                export const sneaky = onDocument.willBeWritten("other/{id}", async (_e, ctx) => { await admin.files.child("x.txt").putData("no"); });
                """,
        }, "Before", "admin@x");
        Assert.True((await functions.InvokeAsync("seed", null, FunctionCaller.Server)).Ok);
        var db = app.Service<Winche.Database.Runtime.DocumentDatabase>();
        await db.WriteAsync([new Winche.Database.Runtime.Writes.SetWrite { Path = "orders/o1", Fields = new Dictionary<string, Winche.Values.Value>() }]);
        Assert.IsType<Winche.Values.IntegerValue>((await db.GetAsync("orders/o1"))!.Fields["limit"]);   // it read the file
        var write = await Assert.ThrowsAnyAsync<Exception>(() => db.WriteAsync(
            [new Winche.Database.Runtime.Writes.SetWrite { Path = "other/o1", Fields = new Dictionary<string, Winche.Values.Value>() }]));
        Assert.Contains("couldn't be checked", write.Message);   // refused: it tried to write a file
        Assert.Null(await app.Service<FileStorage>().GetAsync("x.txt"));
    }
}
