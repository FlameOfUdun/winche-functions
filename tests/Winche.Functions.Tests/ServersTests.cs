namespace Winche.Functions.Tests;

/// <summary>Each server notes it runs the project's functions, so the console can say how many do.</summary>
[Collection("postgres")]
public class ServersTests(PostgresFixture pg)
{
    [Fact]
    public async Task Servers_see_each_other_and_one_that_stops_is_gone_at_once()
    {
        var db = await pg.NewDatabaseAsync();
        var a = await FunctionsApp.StartAsync(db);
        await using var b = await FunctionsApp.StartAsync(db);
        var fa = a.Service<WincheFunctions>();
        var fb = b.Service<WincheFunctions>();
        Assert.NotEqual(fa.ServerId, fb.ServerId);

        IReadOnlyList<string> others = [];
        for (var i = 0; i < 50 && others.Count == 0; i++) { await Task.Delay(100); others = await fb.OtherServersAsync(); }
        Assert.Equal([fa.ServerName], others);   // b sees a, not itself
        Assert.Single(await fa.OtherServersAsync());

        await a.App.StopAsync();
        await a.DisposeAsync();
        Assert.Empty(await fb.OtherServersAsync());
    }
}
