using System.Net.Http.Json;
using System.Text.Json;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;
using Seebot.WorkerAgent.Core.Vmware;

namespace Seebot.WorkerAgent.Core.Guest;

public sealed class GuestWorkerClient : IGuestWorkerClient
{
    private const int RunnerControlPort = 9090;
    private const string RunnerStatusPath = "api/robot/start/status";
    private const string RunnerKillPath = "api/robot/start/status"; // TODO: 临时复用状态接口路径，待 Kill 接口路径确定后更新

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IVmrunService _vmrunService;

    public GuestWorkerClient(HttpClient httpClient, IVmrunService vmrunService)
    {
        _httpClient = httpClient;
        _vmrunService = vmrunService;
    }

    public async Task<RunnerStatusResponse> GetRunnerStatusAsync(VirtualMachineOptions vm, CancellationToken cancellationToken)
    {
        var url = await BuildRunnerUrlAsync(vm, RunnerStatusPath, "GetRunnerStatus", cancellationToken);

        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            await EnsureSuccessAsync(response, "GetRunnerStatus", url, cancellationToken);

            var result = await ReadJsonAsync<ApiResult<int>>(response, "GetRunnerStatus", url, cancellationToken);
            // data=0 表示可切换（Runnable），其他值表示不可切换
            var statusCode = result.Data == 0 ? RunnerStatusCode.Runnable : RunnerStatusCode.Running;

            return new RunnerStatusResponse
            {
                Success = true,
                RunnerStatusCode = statusCode
            };
        }
        catch (GuestWorkerClientException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new GuestWorkerClientException("GetRunnerStatus", url, exception.Message, exception);
        }
    }

    public async Task<KillRunnerResponse> KillRunnerAsync(
        VirtualMachineOptions vm,
        string txId,
        string reason,
        int deadlineSeconds,
        CancellationToken cancellationToken)
    {
        var url = await BuildRunnerUrlAsync(vm, RunnerKillPath, "KillRunner", cancellationToken);

        try
        {
            using var request = JsonContent.Create(new
            {
                reason,
                txId,
                deadlineSeconds
            }, options: JsonOptions);

            using var response = await _httpClient.PostAsync(url, request, cancellationToken);
            await EnsureSuccessAsync(response, "KillRunner", url, cancellationToken);

            var result = await ReadJsonAsync<ApiResult<int>>(response, "KillRunner", url, cancellationToken);
            return new KillRunnerResponse
            {
                Success = result.Data == 0,
                ErrorCode = result.Data == 0 ? null : ErrorCodes.ExecutorStopFailed,
                Message = result.Data == 0 ? result.Message : $"Kill runner failed, data={result.Data}"
            };
        }
        catch (GuestWorkerClientException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new GuestWorkerClientException("KillRunner", url, exception.Message, exception);
        }
    }

    private async Task<string> BuildRunnerUrlAsync(
        VirtualMachineOptions vm,
        string path,
        string operationName,
        CancellationToken cancellationToken)
    {
        try
        {
            var ipAddress = await _vmrunService.GetGuestIPAddressAsync(vm.VmxPath, cancellationToken);
            return $"http://{ipAddress}:{RunnerControlPort}/{path}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new GuestWorkerClientException(operationName, vm.VmxPath, exception.Message, exception);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operationName,
        string requestUrl,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var responseBody = response.Content is null
            ? ""
            : await response.Content.ReadAsStringAsync(cancellationToken);

        throw new GuestWorkerClientException(
            operationName,
            requestUrl,
            $"HTTP {(int)response.StatusCode} ({response.StatusCode}): {responseBody}");
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        string operationName,
        string requestUrl,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        if (body is null)
        {
            throw new GuestWorkerClientException(operationName, requestUrl, "Response body was empty.");
        }

        return body;
    }

    private sealed class ApiResult<T>
    {
        public int Code { get; set; }
        public string? Message { get; set; }
        public T Data { get; set; } = default!;
    }
}
