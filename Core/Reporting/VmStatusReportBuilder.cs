using System.Globalization;
using Seebot.WorkerAgent.Core.Configuration;
using Seebot.WorkerAgent.Core.Domain;
using Seebot.WorkerAgent.Core.Scheduler;

namespace Seebot.WorkerAgent.Core.Reporting;

public static class VmStatusReportBuilder
{
    public static VmStatusReportRequest Build(
        WorkerAgentOptions options,
        VmCurrentState state,
        DateTimeOffset reportedAt)
    {
        var configuredVm = options.VirtualMachines.FirstOrDefault(vm =>
            string.Equals(vm.Name, state.VmName, StringComparison.OrdinalIgnoreCase));

        return new VmStatusReportRequest
        {
            HostId = options.Agent.HostId,
            VmName = state.VmName,
            WorkerId = string.IsNullOrWhiteSpace(state.WorkerId) ? configuredVm?.WorkerId ?? "" : state.WorkerId,
            CurrentProfileId = state.CurrentProfileId,
            CurrentSnapshotName = state.CurrentSnapshotName,
            AgentVmStatus = state.VmStatus.ToString(),
            RunnerStatusCode = state.RunnerStatusCode is null ? null : (int)state.RunnerStatusCode.Value,
            RunnerStatusName = state.RunnerStatusCode?.ToString(),
            CurrentTaskId = state.CurrentTaskId,
            IsQuarantined = state.IsQuarantined,
            LastHeartbeatTime = reportedAt.ToString("O", CultureInfo.InvariantCulture),
            ErrorCode = state.ErrorCode,
            ErrorMessage = state.ErrorMessage
        };
    }
}
