namespace Seebot.WorkerAgent.Core.Scheduler;

public interface ISchedulerClient
{
    Task<ProfilePendingTaskResponse> QueryPendingTasksAsync(string profileId, CancellationToken cancellationToken);

    Task ReportHeartbeatAsync(HostAgentHeartbeatRequest request, CancellationToken cancellationToken);

    Task ReportCapabilitiesAsync(HostAgentCapabilitiesRequest request, CancellationToken cancellationToken);

    Task ReportVmStatusAsync(VmStatusReportRequest request, CancellationToken cancellationToken);

    Task ReportSwitchLogAsync(WorkerSwitchLogRequest request, CancellationToken cancellationToken);

    Task ReportDirectoryBackupResultAsync(DirectoryBackupResultRequest request, CancellationToken cancellationToken);
}
