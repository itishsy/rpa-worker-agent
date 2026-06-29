namespace Seebot.WorkerAgent.Core.Scheduler;

public interface ISchedulerClient
{
    Task<IReadOnlyList<ProfilePendingTaskResponse>> QueryPendingTasksAsync(string workerId, CancellationToken cancellationToken);

    Task ReportCapabilitiesAsync(IReadOnlyList<HostProfileCapabilityRequest> request, CancellationToken cancellationToken);

    Task ReportVmStatusAsync(VmStatusReportRequest request, CancellationToken cancellationToken);

    Task ReportSwitchLogAsync(WorkerSwitchLogRequest request, CancellationToken cancellationToken);

    Task ReportDirectoryBackupResultAsync(DirectoryBackupResultRequest request, CancellationToken cancellationToken);
}
