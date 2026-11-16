using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Winche.Functions.Tests;

[Collection("postgres")]
public class HttpFunctionsTests(PostgresFixture pg)
{
    private const string Code = """
        import { http, HttpsError } from "@winche/functions";

        // Another service's webhook: checks its signature with a secret, then records the event.
        export const providerEvents = http.onRequest(async (req, ctx) => {
          const body = await req.text();
          const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(ctx.secrets.get("HOOK_SECRET")), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
          const mac = new Uint8Array(await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(body)));
          const expected = [...mac].map((b) => b.toString(16).padStart(2, "0")).join("");
          if (req.headers.get("x-signature") !== expected) return new Response("bad signature", { status: 401 });
          const event = JSON.parse(body);
          await ctx.db.set(`events/${event.id}`, { type: event.type, user: event.userId });
          return { ok: true };
        }, { methods: ["POST"], secrets: ["HOOK_SECRET"] });

        // A link in an email: /functions/v1/http/report/<token> sends the browser on.
        export const report = http.onRequest(async (req, ctx) => {
          const token = new URL(req.url).pathname.split("/").pop();
          const doc = await ctx.db.get(`links/${token}`);
          if (!doc) throw new HttpsError("not-found", "This link has ended.");
          return Response.redirect(String(doc.fields.to), 302);
        }, { methods: ["GET"] });

        export const echo = http.onRequest(async (req) => new Response(new Uint8Array(await req.arrayBuffer()).reverse(),
          { headers: { "content-type": "application/octet-stream", "x-method": req.method, "x-query": new URL(req.url).searchParams.get("q") ?? "" } }));
        export const nothing = http.onRequest(() => {});
        """;

    private static string Sign(string body, string secret) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    [Fact]
    public async Task Http_functions_answer_requests_with_their_own_response()
    {
        await using var app = await FunctionsApp.StartAsync(await pg.NewDatabaseAsync());
        await app.Service<FunctionSecrets>().SetAsync("HOOK_SECRET", "hook-secret-1", null);
        var d = await app.Service<WincheFunctions>().DeployAsync(new Dictionary<string, string> { ["index.ts"] = Code }, "Http", "admin@x");
        Assert.Equal("HTTP POST at /functions/v1/http/{name}", d.Functions.Single(f => f.Name == "providerEvents").Trigger.Describe());
        var db = app.Service<Winche.Database.Runtime.DocumentDatabase>();

        // The webhook: signed, it's recorded; unsigned, refused by the function itself; wrong method, refused before it runs.
        var body = """{"id":"e1","type":"USER-DELETE","userId":"kc-ann"}""";
        using (var signed = new HttpRequestMessage(HttpMethod.Post, $"{app.Url}/functions/v1/http/providerEvents") { Content = new StringContent(body, Encoding.UTF8, "application/json") })
        {
            signed.Headers.Add("X-Signature", Sign(body, "hook-secret-1"));
            using var r = await app.Http.SendAsync(signed);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("""{"ok":true}""", await r.Content.ReadAsStringAsync());
        }
        Assert.Equal("USER-DELETE", ((Winche.Values.StringValue)(await db.GetAsync("events/e1"))!.Fields["type"]).Value);
        using (var forged = await app.Http.PostAsync($"{app.Url}/functions/v1/http/providerEvents", new StringContent(body)))
            Assert.Equal((HttpStatusCode.Unauthorized, "bad signature"), (forged.StatusCode, await forged.Content.ReadAsStringAsync()));
        using (var get = await app.Http.GetAsync($"{app.Url}/functions/v1/http/providerEvents"))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
            Assert.Equal("POST", get.Content.Headers.Allow.Single());
        }

        // The emailed link: redirected while it lasts, an error with its message after.
        await db.WriteAsync([new Winche.Database.Runtime.Writes.SetWrite { Path = "links/t1", Fields = new Dictionary<string, Winche.Values.Value> { ["to"] = new Winche.Values.StringValue("https://files.example.com/r.pdf?sig=1") } }]);
        using var noRedirects = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using (var link = await noRedirects.GetAsync($"{app.Url}/functions/v1/http/report/t1"))
            Assert.Equal((HttpStatusCode.Found, "https://files.example.com/r.pdf?sig=1"), (link.StatusCode, link.Headers.Location?.ToString()));
        using (var ended = await noRedirects.GetAsync($"{app.Url}/functions/v1/http/report/t9"))
        {
            Assert.Equal(HttpStatusCode.NotFound, ended.StatusCode);
            Assert.Contains("This link has ended.", await ended.Content.ReadAsStringAsync());
        }

        // Bytes both ways, the method, the query; nothing returned is 204; an unknown name is 404.
        using (var echo = await app.Http.PutAsync($"{app.Url}/functions/v1/http/echo/any/path?q=hi", new ByteArrayContent([1, 2, 3])))
        {
            Assert.Equal(new byte[] { 3, 2, 1 }, await echo.Content.ReadAsByteArrayAsync());
            Assert.Equal(("PUT", "hi"), (echo.Headers.GetValues("x-method").Single(), echo.Headers.GetValues("x-query").Single()));
        }
        Assert.Equal(HttpStatusCode.NoContent, (await app.Http.GetAsync($"{app.Url}/functions/v1/http/nothing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Http.GetAsync($"{app.Url}/functions/v1/http/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Http.PostAsync($"{app.Url}/functions/v1/call/echo", new StringContent("{}"))).StatusCode);   // not a callable

        // Too big a body is refused before the function runs.
        using (var big = await app.Http.PutAsync($"{app.Url}/functions/v1/http/echo", new ByteArrayContent(new byte[(1 << 20) + 1])))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
    }
}
