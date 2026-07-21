using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Seebot.WorkerAgent.Core.Backup;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Guest;
using Seebot.WorkerAgent.Core.Health;
using Seebot.WorkerAgent.Core.Reporting;
using Seebot.WorkerAgent.Core.Scheduler;
using Seebot.WorkerAgent.Core.Scheduling;
using Seebot.WorkerAgent.Core.Startup;
using Seebot.WorkerAgent.Core.Storage;
using Seebot.WorkerAgent.Core.Snapshot;
using Seebot.WorkerAgent.Core.Switching;
using Seebot.WorkerAgent.Core.Vmware;
using Seebot.WorkerAgent.Core.Coordination;
using Seebot.WorkerAgent.Core.Operations;

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
        services.AddSingleton<IVirtualMachineRegistry>(provider =>
        {
            var agentOptions = provider.GetRequiredService<IOptions<AgentOptions>>().Value;
            var dbPath = GetAgentDatabasePath(agentOptions);
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            return new SqliteVirtualMachineRegistry(dbPath);
        });

        services.AddSingleton(provider =>
        {
            var options = new WorkerAgentOptions();
            configuration.Bind(options);
            options.VirtualMachines = provider
                .GetRequiredService<IVirtualMachineRegistry>()
                .GetAllAsync()
                .GetAwaiter()
                .GetResult()
                .ToList();
            return options;
        });

        services.AddSingleton(provider =>
            WorkerAgentOptionsValidator.Validate(provider.GetRequiredService<WorkerAgentOptions>()));
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IVmrunService>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<VmrunOptions>>().Value;
            var commandTimeout = TimeSpan.FromSeconds(options.StopSoftTimeoutSeconds > 0 ? options.StopSoftTimeoutSeconds : 60);
            var fileOperationTimeout = TimeSpan.FromSeconds(options.FileOperationTimeoutSeconds > 0 ? options.FileOperationTimeoutSeconds : 600);
            return new VmrunService(
                options.VmrunPath,
                string.IsNullOrWhiteSpace(options.HostType) ? "ws" : options.HostType,
                provider.GetRequiredService<IProcessRunner>(),
                commandTimeout,
                fileOperationTimeout,
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<VmrunService>>());
        });
        const string SchedulerAuthHttpClientName = "SchedulerAuth";
        services.AddHttpClient(SchedulerAuthHttpClientName, (provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<SchedulerOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                client.BaseAddress = new Uri(options.BaseUrl);
            }
        });
        // Share one scheduler token cache across SchedulerClient consumers.
        services.AddSingleton<ISchedulerTokenProvider>(provider =>
        {
            var httpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient(SchedulerAuthHttpClientName);
            var options = provider.GetRequiredService<SchedulerOptions>();
            return new SchedulerTokenProvider(
                httpClient,
                options,
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SchedulerTokenProvider>>());
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
        services.AddSingleton<IGuestTokenProvisioningService, GuestTokenProvisioningService>();
        services.AddSingleton<LocalStore>(provider =>
        {
            var agentOptions = provider.GetRequiredService<WorkerAgentOptions>().Agent;
            var dbPath = GetAgentDatabasePath(agentOptions);
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            return new LocalStore(dbPath);
        });
        services.AddSingleton<ILocalStore>(provider => provider.GetRequiredService<LocalStore>());
        services.AddSingleton<IVmOperationStore>(provider => provider.GetRequiredService<LocalStore>());
        services.AddSingleton<IVmOperationCoordinator, VmOperationCoordinator>();
        services.AddHostedService<LocalStoreInitializerService>();
        services.AddSingleton<ILogBackupService, LogBackupService>();
        services.AddSingleton<Startup.IStartupValidator, StartupValidator>();
        services.AddSingleton<IProfileSnapshotResolver, ProfileSnapshotResolver>();
        services.AddSingleton<IVmOperationLock, VmOperationLock>();
        services.AddSingleton<IVmSwitchService, VmSwitchService>();
        services.AddSingleton<IVmStateRefreshService, VmStateRefreshService>();
        services.AddSingleton<IVmDiskCleanupService, NoOpVmDiskCleanupService>();
        services.AddSingleton<IVmHealthCheckService, VmHealthCheckService>();
        services.AddSingleton<IVmPowerRecoveryService, VmPowerRecoveryService>();
        services.AddSingleton<IVmPowerOnService, VmPowerOnService>();
        services.AddSingleton<IPoolSchedulerService, PoolSchedulerService>();
        services.AddSingleton<ISnapshotUpdateService, SnapshotUpdateService>();
        services.AddSingleton<IInitFileUpdateService>(provider =>
            new InitFileUpdateService(
                provider.GetRequiredService<IVmrunService>(),
                provider.GetRequiredService<IVmOperationLock>(),
                provider.GetRequiredService<IProfileSnapshotResolver>(),
                provider.GetRequiredService<WorkerAgentOptions>(),
                provider.GetRequiredService<IVirtualMachineRegistry>(),
                logger: provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<InitFileUpdateService>>(),
                guestTokenProvisioningService: provider.GetRequiredService<IGuestTokenProvisioningService>()));
        services.AddSingleton<CapabilityReportService>();

        return services;
    }

    private static string GetAgentDatabasePath(AgentOptions agentOptions)
    {
        return Path.Combine(agentOptions.HostWorkPath, "db", "agent.db");
    }
}
