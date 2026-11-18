using System.Diagnostics;

namespace Winche.Functions.Tests;

/// <summary>The SDK modules on their own, with Deno's test runner: @winche/functions/testing's helpers.</summary>
public class SdkModuleTests
{
    [Fact]
    public async Task The_testing_helpers_give_secrets_resources_and_a_stand_in_server()
    {
        var dir = Directory.CreateTempSubdirectory("winche-sdk-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".winche"));
            foreach (var (file, _) in FunctionsSdk.Modules) File.WriteAllText(Path.Combine(dir, ".winche", file), FunctionsSdk.SourceOf(file));
            File.WriteAllText(Path.Combine(dir, "deno.json"), FunctionsSdk.DenoJson);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Sdk", "testing_test.ts"), Path.Combine(dir, "testing_test.ts"));
            var info = new ProcessStartInfo(DenoRuntime.Locate(new FunctionsOptions())) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "test", "-q", "testing_test.ts" }) info.ArgumentList.Add(a);
            info.Environment["NO_COLOR"] = "1";
            using var p = Process.Start(info)!;
            var output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            Assert.True(p.ExitCode == 0, output);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
