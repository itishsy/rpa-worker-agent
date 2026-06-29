using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Seebot.WorkerAgent.Core;
using Seebot.WorkerAgent.Core.Operations;

namespace Seebot.WorkerAgent.Service;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = CreateWebApplicationBuilder(args);
        var app = builder.Build();
        app.MapOperationsApi();
        await app.RunAsync();
    }

    public static WebApplicationBuilder CreateWebApplicationBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        var listenUrl = builder.Configuration["OperationsApi:ListenUrl"];
        if (!string.IsNullOrWhiteSpace(listenUrl))
        {
            builder.WebHost.UseUrls([listenUrl]);
        }

        builder.Services.AddWorkerAgentConfiguration(builder.Configuration);
        builder.Services.AddWorkerAgentCore();
        builder.Services.AddHostedService<WorkerAgent>();

        return builder;
    }
}
