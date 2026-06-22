using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Seebot.WorkerAgent.Core.Configuration;

namespace Seebot.WorkerAgent.Core.Scheduler;

public sealed class SchedulerClient : ISchedulerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

    public async Task<ProfilePendingTaskResponse> QueryPendingTasksAsync(string profileId, CancellationToken cancellationToken)
    {
        var url = BuildUri($"profile-task/pending?profileId={Uri.EscapeDataString(profileId)}");
        using var request = CreateRequest(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "QueryPendingTasks", cancellationToken);

        var body = await response.Content.ReadFromJsonAsync<ProfilePendingTaskResponse>(JsonOptions, cancellationToken);
        return body ?? new ProfilePendingTaskResponse();
    }

    public Task ReportHeartbeatAsync(HostAgentHeartbeatRequest request, CancellationToken cancellationToken)
    {
        return PostAsync("host-agent/heartbeat", request, "ReportHeartbeat", cancellationToken);
    }

    public Task ReportCapabilitiesAsync(HostAgentCapabilitiesRequest request, CancellationToken cancellationToken)
    {
        return PostAsync("host-agent/capabilities", request, "ReportCapabilities", cancellationToken);
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
