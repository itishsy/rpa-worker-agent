using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Seebot.WorkerAgent.Core;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Logging;
using Seebot.WorkerAgent.Core.Operations;
using Seebot.WorkerAgent.Core.Startup;
using System.Globalization;

namespace Seebot.WorkerAgent.Service;

public static class Program
{
    public static async Task Main(string[] args)
    {
        try
        {
            var builder = CreateWebApplicationBuilder(args);
            var app = builder.Build();
            await ValidateStartupAsync(app, CancellationToken.None).ConfigureAwait(false);
            app.MapOperationsApi();
            await app.RunAsync();
        }
        catch (Exception exception)
        {
            WriteBootstrapFatalLog(exception);
            throw;
        }
    }

    public static async Task ValidateStartupAsync(WebApplication app, CancellationToken cancellationToken)
    {
        var configurationValidation = app.Services.GetRequiredService<ValidationResult>();
        if (!configurationValidation.IsValid)
        {
            foreach (var error in configurationValidation.Errors)
            {
                app.Logger.LogError("Startup configuration validation failed: {Error}", error);
            }

            throw new InvalidOperationException(
                "Startup configuration validation failed: " + string.Join("; ", configurationValidation.Errors));
        }

        var options = app.Services.GetRequiredService<WorkerAgentOptions>();
        var validator = app.Services.GetRequiredService<IStartupValidator>();
        var reportedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var result = await validator
            .ValidateAndBuildCapabilitiesAsync(options, reportedAt, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsValid)
        {
            foreach (var error in result.Errors)
            {
                app.Logger.LogError("Startup environment validation failed: {Error}", error);
            }

            throw new InvalidOperationException(
                "Startup environment validation failed: " + string.Join("; ", result.Errors));
        }

        app.Logger.LogInformation(
            "Startup validation passed for {VmCount} virtual machines.",
            result.Capabilities.Vms.Count);
    }

    public static WebApplicationBuilder CreateWebApplicationBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = GetConfigurationRootPath()
        });
        builder.Host.UseWindowsService(options =>
        {
            options.ServiceName = "SeebotWorkerAgent";
        });
        builder.Logging.AddWorkerAgentFileLogger(builder.Configuration, AppContext.BaseDirectory);

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

    public static string GetConfigurationRootPath()
    {
        var baseDirectory = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var parentDirectory = Directory.GetParent(baseDirectory)?.FullName;
        if (!string.IsNullOrWhiteSpace(parentDirectory)
            && File.Exists(Path.Combine(parentDirectory, "appsettings.json")))
        {
            return parentDirectory;
        }

        return baseDirectory;
    }

    private static void WriteBootstrapFatalLog(Exception exception)
    {
        try
        {
            var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, $"startup-fatal-{DateTimeOffset.Now:yyyyMMdd}.log");
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [Fatal] Service failed to start.{Environment.NewLine}{exception}{Environment.NewLine}";
            File.AppendAllText(logPath, line);
        }
        catch
        {
            // Last-resort startup logging must never hide the original startup error.
        }
    }
}
