using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seebot.WorkerAgent.Core.Configuration;

namespace Seebot.WorkerAgent.Core.Scheduler;

public sealed class SchedulerClient : ISchedulerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly HttpClient _httpClient;
    private readonly ISchedulerTokenProvider _tokenProvider;
    private readonly Uri? _baseUri;
    private readonly ILogger<SchedulerClient> _logger;

    public SchedulerClient(
        HttpClient httpClient,
        SchedulerOptions options,
        ISchedulerTokenProvider tokenProvider,
        ILogger<SchedulerClient>? logger = null)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _logger = logger ?? NullLogger<SchedulerClient>.Instance;
        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            _baseUri = new Uri(EnsureTrailingSlash(options.BaseUrl));
        }
    }

    public async Task<IReadOnlyList<ProfilePendingTaskResponse>> QueryPendingTasksAsync(string workerId, string profileId, CancellationToken cancellationToken)
    {
        var url = BuildUri($"robot/client/task/findTaskProfileCode/{Uri.EscapeDataString(workerId)}?profileCode={Uri.EscapeDataString(profileId)}");
        _logger.LogInformation(
            "Scheduler query pending tasks started. WorkerId={WorkerId}, CurrentProfileId={ProfileId}, Url={Url}",
            workerId,
            profileId,
            url);
        using var response = await SendWithRetryAsync(HttpMethod.Post, url, contentFactory: null, "QueryPendingTasks", cancellationToken);

        var result = await response.Content.ReadFromJsonAsync<ApiResult<string[]>>(JsonOptions, cancellationToken);
        var returnedIds = result?.Data;
        var profiles = returnedIds is null ? [] : returnedIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => new ProfilePendingTaskResponse { ProfileId = id, HasTask = true })
            .ToList();
        _logger.LogInformation(
            "Scheduler query pending tasks completed. WorkerId={WorkerId}, CurrentProfileId={ProfileId}, PendingProfileCount={PendingProfileCount}",
            workerId,
            profileId,
            profiles.Count);
        return profiles;
    }

    public Task ReportCapabilitiesAsync(IReadOnlyList<HostProfileCapabilityRequest> request, CancellationToken cancellationToken)
    {
        return PostAsync("robot/vmProfile/reportSave", request, "ReportCapabilities", cancellationToken);
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
        var uri = BuildUri(relativePath);
        var payloadJson = SerializePayload(payload);
        _logger.LogInformation(
            "Scheduler post started. Operation={Operation}, Url={Url}, PayloadType={PayloadType}, Payload={Payload}",
            operationName,
            uri,
            typeof(T).Name,
            payloadJson);
        using var response = await SendWithRetryAsync(
            HttpMethod.Post,
            uri,
            () => JsonContent.Create(payload, options: JsonOptions),
            operationName,
            cancellationToken);
        await EnsureApiSuccessAsync(response, operationName, cancellationToken);
        _logger.LogInformation(
            "Scheduler post completed. Operation={Operation}, StatusCode={StatusCode}",
            operationName,
            (int)response.StatusCode);
    }

    // 发起请求前附加当前缓存的 token；若响应为 401，则刷新 token 并重试一次
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpMethod method,
        Uri uri,
        Func<HttpContent>? contentFactory,
        string operationName,
        CancellationToken cancellationToken)
    {
        var response = await SendOnceAsync(method, uri, contentFactory, refreshToken: false, operationName, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            await EnsureSuccessAsync(response, operationName, cancellationToken);
            return response;
        }

        _logger.LogWarning("Scheduler request returned 401, refreshing token and retrying. Operation={Operation}", operationName);
        response.Dispose();
        response = await SendOnceAsync(method, uri, contentFactory, refreshToken: true, operationName, cancellationToken);
        await EnsureSuccessAsync(response, operationName, cancellationToken);
        return response;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method,
        Uri uri,
        Func<HttpContent>? contentFactory,
        bool refreshToken,
        string operationName,
        CancellationToken cancellationToken)
    {
        var accessToken = refreshToken
            ? await _tokenProvider.RefreshAccessTokenAsync(cancellationToken)
            : await _tokenProvider.GetAccessTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (contentFactory is not null)
        {
            request.Content = contentFactory();
        }

        var started = Stopwatch.GetTimestamp();
        _logger.LogInformation(
            "Scheduler HTTP request started. Operation={Operation}, Method={Method}, Url={Url}, RefreshToken={RefreshToken}",
            operationName,
            method.Method,
            uri,
            refreshToken);

        try
        {
            var response = await _httpClient.SendAsync(request, cancellationToken);
            _logger.LogInformation(
                "Scheduler HTTP request completed. Operation={Operation}, StatusCode={StatusCode}, ElapsedMs={ElapsedMs}",
                operationName,
                (int)response.StatusCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return response;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Scheduler HTTP request failed. Operation={Operation}, ElapsedMs={ElapsedMs}",
                operationName,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw new SchedulerClientException(operationName, HttpStatusCode.ServiceUnavailable, exception.Message);
        }
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

    private static string SerializePayload<T>(T payload)
    {
        try
        {
            return JsonSerializer.Serialize(payload, JsonOptions);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"<failed to serialize payload: {exception.Message}>";
        }
    }
}
