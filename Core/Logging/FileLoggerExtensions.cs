using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Seebot.WorkerAgent.Core.Logging;

public static class FileLoggerExtensions
{
    public static ILoggingBuilder AddWorkerAgentFileLogger(
        this ILoggingBuilder builder,
        IConfiguration configuration,
        string? fallbackDirectory = null)
    {
        var options = BuildOptions(configuration, fallbackDirectory);
        if (options.Enabled && !string.IsNullOrWhiteSpace(options.DirectoryPath))
        {
            builder.AddProvider(new FileLoggerProvider(options));
        }

        return builder;
    }

    private static FileLoggerOptions BuildOptions(IConfiguration configuration, string? fallbackDirectory)
    {
        var options = new FileLoggerOptions();
        configuration.GetSection("Logging:File").Bind(options);

        if (string.IsNullOrWhiteSpace(options.DirectoryPath))
        {
            var hostWorkPath = configuration["Agent:HostWorkPath"];
            if (!string.IsNullOrWhiteSpace(hostWorkPath))
            {
                options.DirectoryPath = Path.Combine(hostWorkPath, "logs");
            }
        }

        if (string.IsNullOrWhiteSpace(options.DirectoryPath)
            && !string.IsNullOrWhiteSpace(fallbackDirectory))
        {
            options.DirectoryPath = Path.Combine(fallbackDirectory, "logs");
        }

        return options;
    }
}
