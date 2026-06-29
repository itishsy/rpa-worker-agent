using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using Seebot.WorkerAgent.Core.Configuration;

namespace Seebot.WorkerAgent.Core.Scheduler;

public sealed class SchedulerClient : ISchedulerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly HttpClient _httpClient;
    private readonly SchedulerOptions _options;
    private readonly Uri? _baseUri;

    public SchedulerClient(HttpClient httpClient, SchedulerOptions options)
    {
        _httpClient = httpClient;
        _options = options;
        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            _baseUri = new Uri(EnsureTrailingSlash(options.BaseUrl));
        }
    }

    public async Task<IReadOnlyList<ProfilePendingTaskResponse>> QueryPendingTasksAsync(string workerId, CancellationToken cancellationToken)
    {
        var url = BuildUri($"/robot/client/task/findTaskProfileCode/{Uri.EscapeDataString(workerId)}");
        using var request = CreateRequest(HttpMethod.Post, url);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "QueryPendingTasks", cancellationToken);

        var result = await response.Content.ReadFromJsonAsync<ApiResult<string[]>>(JsonOptions, cancellationToken);
        var profileIds = result?.Data;
        return profileIds is null ? [] : profileIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => new ProfilePendingTaskResponse { ProfileId = id, HasTask = true })
            .ToList();
    }

    public Task ReportCapabilitiesAsync(IReadOnlyList<HostProfileCapabilityRequest> request, CancellationToken cancellationToken)
    {
        return PostAsync("/robot/vmProfile/reportSave", request, "ReportCapabilities", cancellationToken);
    }

    public Task ReportVmStatusAsync(VmStatusReportRequest request, CancellationToken cancellationToken)
    {
        return PostAsync("vm/status", request, "ReportVmStatus", cancellationToken);
    }

    public Task ReportSwitchLogAsync(WorkerSwitchLogRequest request, CancellationToken cancellationToken)
    {
        return PostAsync("worker/switch-log", request, "ReportSwitchLog", cancellationToken);
    }

    public Task ReportDirectoryBackupResultAsync(DirectoryBackupResultRequest request, CancellationToken cancellationToken)
    {
        return PostAsync("worker/log-backup-result", request, "ReportDirectoryBackupResult", cancellationToken);
    }

    private async Task PostAsync<T>(string relativePath, T payload, string operationName, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, BuildUri(relativePath));
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, operationName, cancellationToken);
        await EnsureApiSuccessAsync(response, operationName, cancellationToken);
    }

    private static async Task EnsureApiSuccessAsync(HttpResponseMessage response, string operationName, CancellationToken cancellationToken)
    {
        ApiResult<object>? result;
        try
        {
            result = await response.Content.ReadFromJsonAsync<ApiResult<object>>(JsonOptions, cancellationToken);
        }
        catch
        {
            return;
        }

        if (result is not null && result.Code != 200)
        {
            throw new SchedulerClientException(operationName, response.StatusCode, result.Message ?? $"code={result.Code}");
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
        }

        return request;
    }

    private Uri BuildUri(string relativePath)
    {
        if (_baseUri is null)
        {
            throw new InvalidOperationException("Scheduler BaseUrl is not configured.");
        }

        return new Uri(_baseUri, relativePath);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operationName, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var responseBody = response.Content is null
            ? ""
            : await response.Content.ReadAsStringAsync(cancellationToken);

        throw new SchedulerClientException(operationName, response.StatusCode, responseBody);
    }

    private static string EnsureTrailingSlash(string value)
    {
        return value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";
    }
}
