using System.Net.Http.Json;
using System.Text.Json;
using Seebot.WorkerAgent.Core.Configuration;

namespace Seebot.WorkerAgent.Core.Guest;

public sealed class GuestWorkerClient : IGuestWorkerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;

    public GuestWorkerClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<RunnerStatusResponse> GetRunnerStatusAsync(VirtualMachineOptions vm, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(vm.RunnerStatusUrl, cancellationToken);
            await EnsureSuccessAsync(response, "GetRunnerStatus", vm.RunnerStatusUrl, cancellationToken);
            return await ReadJsonAsync<RunnerStatusResponse>(response, "GetRunnerStatus", vm.RunnerStatusUrl, cancellationToken);
        }
        catch (GuestWorkerClientException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new GuestWorkerClientException("GetRunnerStatus", vm.RunnerStatusUrl, exception.Message, exception);
        }
    }

    public async Task<KillRunnerResponse> KillRunnerAsync(
        VirtualMachineOptions vm,
        string txId,
        string reason,
        int deadlineSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = JsonContent.Create(new
            {
                reason,
                txId,
                deadlineSeconds
            }, options: JsonOptions);

            using var response = await _httpClient.PostAsync(vm.RunnerKillUrl, request, cancellationToken);
            await EnsureSuccessAsync(response, "KillRunner", vm.RunnerKillUrl, cancellationToken);
            return await ReadJsonAsync<KillRunnerResponse>(response, "KillRunner", vm.RunnerKillUrl, cancellationToken);
        }
        catch (GuestWorkerClientException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new GuestWorkerClientException("KillRunner", vm.RunnerKillUrl, exception.Message, exception);
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
}
