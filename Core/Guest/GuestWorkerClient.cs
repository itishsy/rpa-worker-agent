using System.Net.Http.Json;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ILogger<GuestWorkerClient> _logger;

    public GuestWorkerClient(HttpClient httpClient, IVmrunService vmrunService, ILogger<GuestWorkerClient>? logger = null)
    {
        _httpClient = httpClient;
        _vmrunService = vmrunService;
        _logger = logger ?? NullLogger<GuestWorkerClient>.Instance;
    }

    public async Task<RunnerStatusResponse> GetRunnerStatusAsync(VirtualMachineOptions vm, CancellationToken cancellationToken)
    {
        var url = await BuildRunnerUrlAsync(vm, RunnerStatusPath, "GetRunnerStatus", cancellationToken);
        var started = Stopwatch.GetTimestamp();
        _logger.LogInformation("Runner status request started. VmName={VmName}, WorkerId={WorkerId}, Url={Url}", vm.Name, vm.WorkerId, url);

        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            await EnsureSuccessAsync(response, "GetRunnerStatus", url, cancellationToken);

            var result = await ReadJsonAsync<ApiResult<int>>(response, "GetRunnerStatus", url, cancellationToken);
            // data=0 表示可切换（Runnable），其他值表示不可切换
            var statusCode = result.Data == 0 ? RunnerStatusCode.Runnable : RunnerStatusCode.Running;

            var runnerStatus = new RunnerStatusResponse
            {
                Success = true,
                RunnerStatusCode = statusCode
            };
            _logger.LogInformation(
                "Runner status request completed. VmName={VmName}, StatusCode={StatusCode}, RunnerStatusCode={RunnerStatusCode}, ElapsedMs={ElapsedMs}",
                vm.Name,
                (int)response.StatusCode,
                runnerStatus.RunnerStatusCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return runnerStatus;
        }
        catch (GuestWorkerClientException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Runner status request failed. VmName={VmName}, ElapsedMs={ElapsedMs}",
                vm.Name,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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
        var started = Stopwatch.GetTimestamp();
        _logger.LogInformation(
            "Runner kill request started. VmName={VmName}, WorkerId={WorkerId}, TxId={TxId}, Reason={Reason}, DeadlineSeconds={DeadlineSeconds}, Url={Url}",
            vm.Name,
            vm.WorkerId,
            txId,
            reason,
            deadlineSeconds,
            url);

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
            var killResult = new KillRunnerResponse
            {
                Success = result.Data == 0,
                ErrorCode = result.Data == 0 ? null : ErrorCodes.ExecutorStopFailed,
                Message = result.Data == 0 ? result.Message : $"Kill runner failed, data={result.Data}"
            };
            _logger.LogInformation(
                "Runner kill request completed. VmName={VmName}, TxId={TxId}, Success={Success}, Data={Data}, ErrorCode={ErrorCode}, ElapsedMs={ElapsedMs}",
                vm.Name,
                txId,
                killResult.Success,
                result.Data,
                killResult.ErrorCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            await ForceKillGuestProcessesAsync(vm, cancellationToken);

            return killResult;
        }
        catch (GuestWorkerClientException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Runner kill request failed. VmName={VmName}, TxId={TxId}, ElapsedMs={ElapsedMs}",
                vm.Name,
                txId,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw new GuestWorkerClientException("KillRunner", url, exception.Message, exception);
        }
    }

    // 通过 vmrun taskkill 强杀 rpa-client.exe 及 java.exe，释放所有文件句柄
    // 设计为 best-effort：VMware Tools 异常时仅记录警告，不影响调用方流程
    private async Task ForceKillGuestProcessesAsync(VirtualMachineOptions vm, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(vm.GuestUser) || string.IsNullOrWhiteSpace(vm.VmxPath))
        {
            return;
        }

        var targets = new[] { "rpa-client.exe", "java.exe" };
        foreach (var processName in targets)
        {
            try
            {
                await _vmrunService.RunProgramInGuestAsync(
                    vm.VmxPath,
                    vm.GuestUser,
                    vm.GuestPasswordSecret,
                    @"C:\Windows\System32\taskkill.exe",
                    ["/F", "/IM", processName],
                    cancellationToken);
                _logger.LogInformation(
                    "Force killed guest process. VmName={VmName}, Process={Process}",
                    vm.Name, processName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // taskkill 进程不存在时 exit code 非零，属于正常情况，降级为 Debug
                _logger.LogDebug(ex,
                    "Force kill guest process did not succeed (process may not exist). VmName={VmName}, Process={Process}",
                    vm.Name, processName);
            }
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
            _logger.LogInformation("Resolving guest IP address started. VmName={VmName}, VmxPath={VmxPath}, Operation={Operation}", vm.Name, vm.VmxPath, operationName);
            var ipAddress = await _vmrunService.GetGuestIPAddressAsync(vm.VmxPath, cancellationToken);
            _logger.LogInformation("Resolving guest IP address completed. VmName={VmName}, IpAddress={IpAddress}, Operation={Operation}", vm.Name, ipAddress, operationName);
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
