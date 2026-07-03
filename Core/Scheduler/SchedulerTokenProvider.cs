using System.Net.Http.Json;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seebot.WorkerAgent.Core.Configuration;

namespace Seebot.WorkerAgent.Core.Scheduler;

public sealed class SchedulerTokenProvider : ISchedulerTokenProvider
{
    private readonly HttpClient _httpClient;
    private readonly SchedulerOptions _options;
    private readonly ILogger<SchedulerTokenProvider> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _cachedToken;

    public SchedulerTokenProvider(HttpClient httpClient, SchedulerOptions options, ILogger<SchedulerTokenProvider>? logger = null)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger ?? NullLogger<SchedulerTokenProvider>.Instance;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var token = _cachedToken;
        if (!string.IsNullOrEmpty(token))
        {
            _logger.LogDebug("Scheduler access token cache hit.");
            return token;
        }

        return await RefreshAccessTokenAsync(cancellationToken);
    }

    public async Task<string> RefreshAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            // 另一个并发请求可能已经在等待锁期间完成了刷新，直接复用其结果
            if (!string.IsNullOrEmpty(_cachedToken))
            {
                _logger.LogDebug("Scheduler access token refreshed by concurrent caller, reusing cached token.");
                return _cachedToken;
            }

            _logger.LogInformation("Scheduler access token refresh started.");
            var token = await FetchAccessTokenAsync(cancellationToken);
            _cachedToken = token;
            _logger.LogInformation("Scheduler access token refresh completed.");
            return token;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string> FetchAccessTokenAsync(CancellationToken cancellationToken)
    {
        var query = _options.BaseUrl + "/oauth/token"
            + $"?client_id={Uri.EscapeDataString(_options.ClientId)}"
            + $"&client_secret={Uri.EscapeDataString(_options.ClientSecret)}"
            + "&grant_type=password"
            + $"&username={Uri.EscapeDataString(_options.Username)}"
            + $"&password={Uri.EscapeDataString(_options.Password)}";

        var started = Stopwatch.GetTimestamp();
        _logger.LogInformation(
            "Scheduler login request started. Url={Url}, Username={Username}, ClientId={ClientId}",
            _options.BaseUrl + "/oauth/token",
            _options.Username,
            _options.ClientId);

        using var response = await _httpClient.PostAsync(query, content: null, cancellationToken);
        _logger.LogInformation(
            "Scheduler login request completed. StatusCode={StatusCode}, ElapsedMs={ElapsedMs}",
            (int)response.StatusCode,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Scheduler login request failed. StatusCode={StatusCode}, ResponseBody={ResponseBody}",
                (int)response.StatusCode,
                responseBody);
            throw new SchedulerClientException("Login", response.StatusCode, responseBody);
        }

        var result = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>(cancellationToken: cancellationToken);
        if (result is null || string.IsNullOrWhiteSpace(result.AccessToken))
        {
            throw new SchedulerClientException("Login", response.StatusCode, "Login response did not contain an access_token.");
        }

        return result.AccessToken;
    }

    private sealed class OAuthTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }
    }
}
