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
                import { callable, files } from "@winche/functions";
                export const make = callable(async (_d, ctx) => {
                  const bytes = new Uint8Array(70000).map((_, i) => i % 251);
                  const pdf = await ctx.files.write("reports/u1/week.pdf", bytes, {
                    mimeType: "application/pdf", metadata: { week: 46 }, contentDisposition: 'attachment; filename="week.pdf"' });
                  await ctx.files.write("notes/u1.txt", "héllo");
                  const back = await ctx.files.read("reports/u1/week.pdf");
                  const same = back!.length === bytes.length && back!.every((b, i) => b === bytes[i]);
                  const link = await ctx.files.downloadUrl("reports/u1/week.pdf");
                  return { size: pdf.sizeBytes, type: pdf.mimeType, week: pdf.metadata?.week, same, text: await ctx.files.readText("notes/u1.txt"),
                           record: (await ctx.files.get("notes/u1.txt"))?.mimeType, none: await ctx.files.read("nope.txt"), link };
                });
                export const missingLink = callable(async (_d, ctx) => ctx.files.downloadUrl("nope.pdf"));
                export const onReport = files.onUploaded("reports/{uid}/{name}", async (e, ctx) => {
                  await ctx.db.set(`seen/${e.params.uid}`, { name: e.params.name, size: e.file?.sizeBytes ?? 0 });
                });
                """,
        }, "Files", "admin@x");

        var r = await functions.InvokeAsync("make", null, FunctionCaller.Server);
        Assert.True(r.Ok, r.ErrorMessage);
        var o = r.Result!;
        Assert.Equal((70000, "application/pdf", 46, true), (o["size"]!.GetValue<int>(), o["type"]!.GetValue<string>(), o["week"]!.GetValue<int>(), o["same"]!.GetValue<bool>()));
        Assert.Equal(("héllo", "text/plain; charset=utf-8"), (o["text"]!.GetValue<string>(), o["record"]!.GetValue<string>()));
        Assert.Null(o["none"]);
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
                import { callable, documents } from "@winche/functions";
                export const seed = callable(async (_d, ctx) => { await ctx.files.write("limits.txt", "3"); });
                export const check = documents.beforeWrite("orders/{id}", async (_e, ctx) => {
                  if (Number(await ctx.files.readText("limits.txt")) !== 3) throw new Error("couldn't read the limit");
                  return { set: { limit: 3 } };
                });
                export const sneaky = documents.beforeWrite("other/{id}", async (_e, ctx) => { await ctx.files.write("x.txt", "no"); });
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
