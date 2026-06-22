using Seebot.WorkerAgent.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Seebot.WorkerAgent.Service;

public static class Program
{
    public static Task Main(string[] args)
    {
        return CreateHostBuilder(args).Build().RunAsync();
    }

    public static IHostBuilder CreateHostBuilder(string[] args)
    {
        return Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                services.AddWorkerAgentConfiguration(context.Configuration);
                services.AddWorkerAgentCore();
                services.AddHostedService<WorkerAgent>();
            });
    }
}
