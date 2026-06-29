using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Seebot.WorkerAgent.Core.Backup;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Guest;
using Seebot.WorkerAgent.Core.Reporting;
using Seebot.WorkerAgent.Core.Scheduler;
using Seebot.WorkerAgent.Core.Scheduling;
using Seebot.WorkerAgent.Core.Startup;
using Seebot.WorkerAgent.Core.Storage;
using Seebot.WorkerAgent.Core.Snapshot;
using Seebot.WorkerAgent.Core.Switching;
using Seebot.WorkerAgent.Core.Vmware;

namespace Seebot.WorkerAgent.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWorkerAgentCore(this IServiceCollection services)
    {
        services.AddSingleton<IAgentCoreMarker, AgentCoreMarker>();
        return services;
    }

    public static IServiceCollection AddWorkerAgentConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AgentOptions>(configuration.GetSection("Agent"));
        services.Configure<OperationsApiOptions>(configuration.GetSection("OperationsApi"));
        services.Configure<SchedulerOptions>(configuration.GetSection("Scheduler"));
        services.Configure<VmrunOptions>(configuration.GetSection("Vmrun"));
        services.Configure<List<VirtualMachineOptions>>(configuration.GetSection("VirtualMachines"));
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<SchedulerOptions>>().Value);

        services.AddSingleton(provider =>
        {
            var options = new WorkerAgentOptions();
            configuration.Bind(options);
            return options;
        });

        services.AddSingleton(provider =>
            WorkerAgentOptionsValidator.Validate(provider.GetRequiredService<WorkerAgentOptions>()));
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IVmrunService>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<VmrunOptions>>().Value;
            var timeoutSeconds = options.StopSoftTimeoutSeconds > 0 ? options.StopSoftTimeoutSeconds : 60;
            return new VmrunService(
                options.VmrunPath,
                provider.GetRequiredService<IProcessRunner>(),
                TimeSpan.FromSeconds(timeoutSeconds));
        });
        services.AddHttpClient<ISchedulerClient, SchedulerClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<SchedulerOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                client.BaseAddress = new Uri(options.BaseUrl);
            }
        });
        services.AddHttpClient<IGuestWorkerClient, GuestWorkerClient>();
        services.AddSingleton<ILocalStore>(provider =>
        {
            var agentOptions = provider.GetRequiredService<WorkerAgentOptions>().Agent;
            string dbPath;
            if (string.IsNullOrWhiteSpace(agentOptions.LocalDbPath))
            {
                dbPath = Path.Combine(AppContext.BaseDirectory, "data", "agent.db");
            }
            else if (string.Equals(Path.GetExtension(agentOptions.LocalDbPath), ".db", StringComparison.OrdinalIgnoreCase))
            {
                dbPath = agentOptions.LocalDbPath;
            }
            else
            {
                dbPath = Path.Combine(agentOptions.LocalDbPath, "agent.db");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            return new LocalStore(dbPath);
        });
        services.AddHostedService<LocalStoreInitializerService>();
        services.AddSingleton<ILogBackupService, LogBackupService>();
        services.AddSingleton<Startup.IStartupValidator, StartupValidator>();
        services.AddSingleton<IVmSwitchService, VmSwitchService>();
        services.AddSingleton<IVmStateRefreshService, VmStateRefreshService>();
        services.AddSingleton<IPoolSchedulerService, PoolSchedulerService>();
        services.AddSingleton<IConfigFileUpdater>(_ =>
            new ConfigFileUpdater(Path.Combine(AppContext.BaseDirectory, "appsettings.json")));
        services.AddSingleton<ISnapshotUpdateService, SnapshotUpdateService>();
        services.AddSingleton<CapabilityReportService>();
        services.AddHostedService(sp => sp.GetRequiredService<CapabilityReportService>());

        return services;
    }
}
