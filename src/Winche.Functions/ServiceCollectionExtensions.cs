using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Winche.Webhooks;

namespace Winche.Functions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds JavaScript/TypeScript functions, run by Deno in sandboxed workers. Triggers on documents, files, users and
    /// schedules go through Winche.Webhooks (AddWincheHooks and its adapters); <c>ctx.db</c>, <c>ctx.files</c>, <c>ctx.users</c>
    /// come from Winche.Functions.Database/Storage/Auth. Map the app-facing API with <c>app.MapWincheFunctions()</c>.
    /// </summary>
    public static IServiceCollection AddWincheFunctions(this IServiceCollection services, Action<FunctionsOptions> configure)
    {
        var options = new FunctionsOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString)) throw new InvalidOperationException("Winche.Functions: set ConnectionString.");
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddNpgsqlDataSource(options.ConnectionString, serviceKey: FunctionStore.DataSourceKey);
        services.AddSingleton<FunctionStore>();
        services.AddSingleton<FunctionSecrets>();
        Winche.Secrets.SecretsServiceCollectionExtensions.AddWincheSecrets(services);
        if (options.Settings)
            Winche.Core.ServiceCollectionExtensions.AddWincheSettingsArea(services, FunctionsSettings.Area, "Functions",
                new FunctionsSettings { DefaultTimeoutSeconds = (int)options.DefaultTimeout.TotalSeconds, DefaultMemoryMb = options.DefaultMemoryMb, DefaultMaxInstances = options.DefaultMaxInstances },
                s => s.Problems((int)options.MaxTimeout.TotalSeconds, options.MaxMemoryMb, options.MaxConcurrency,
                    options.MemoryBudget ??= FunctionsMemoryBudget.FromContainer(), options.SharedProcessMemoryMb));
        services.AddSingleton<FunctionBundler>();
        services.TryAddSingleton(new PluginRegistryHttp(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }));
        services.AddSingleton<PluginPackages>();
        services.AddHostedService<FunctionsStop>();   // first to hear the app is stopping: no new runs, the ones going on end
        services.AddSingleton<DenoRuntime>();
        services.AddSingleton<WincheFunctions>();
        services.AddSingleton<BeforeFunctions>();
        services.AddScoped<AdminCaller>();
        services.AddSingleton<IFunctionBinding, FunctionsCallBinding>();
        services.AddSingleton<IHookProvider, FunctionHooks>();
        services.AddSingleton<IWincheAction, RunFunctionAction>();
        services.AddSingleton(new WincheChangeSource("schedule", "Schedules", [], HasPaths: false, Variables: [], Internal: true));
        // Background work starts once functions are set up (at once, unless the host sets products up on demand).
        Winche.Core.WincheReadyServiceExtensions.AddWincheProductService<FunctionsSync>(services, Winche.Core.WincheProductNames.Functions);
        services.AddSingleton<FunctionScheduler>();
        Winche.Core.WincheReadyServiceExtensions.AddWincheProductService(services, Winche.Core.WincheProductNames.Functions, sp => sp.GetRequiredService<FunctionScheduler>());
        services.AddSingleton<Winche.Core.IWincheProductSetup, FunctionsProductSetup>();
        return services;
    }
}
