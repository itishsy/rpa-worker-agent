using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Snapshot;

namespace Seebot.WorkerAgent.Core.Operations;

public static class OperationsApiExtensions
{
    public static WebApplication MapOperationsApi(this WebApplication app)
    {
        var group = app.MapGroup("/operations").AddEndpointFilter(ApiKeyFilter);

        group.MapPost("/snapshots/{vmName}/{profileId}/update", async (
            string vmName,
            string profileId,
            ISnapshotUpdateService snapshotService,
            CancellationToken cancellationToken) =>
        {
            var result = await snapshotService.UpdateSnapshotAsync(vmName, profileId, cancellationToken);
            return result.Success
                ? Results.Ok(result)
                : Results.UnprocessableEntity(result);
        });

        return app;
    }

    private static async ValueTask<object?> ApiKeyFilter(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var options = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<OperationsApiOptions>>().Value;

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            var apiKey = context.HttpContext.Request.Headers["X-Api-Key"].FirstOrDefault()
                ?? context.HttpContext.Request.Query["apiKey"].FirstOrDefault();

            if (!string.Equals(apiKey, options.ApiKey, StringComparison.Ordinal))
            {
                return Results.Unauthorized();
            }
        }

        return await next(context);
    }
}
