using Seebot.WorkerAgent.Core.Configuration;

namespace Seebot.WorkerAgent.Core.Switching;

public sealed class VmSwitchRequest
{
    public string? TxId { get; set; }
    public string HostId { get; set; } = "";
    public VirtualMachineOptions Vm { get; set; } = new();
    public string? FromProfileId { get; set; }
    public string? FromSnapshotName { get; set; }
    public string TargetProfileId { get; set; } = "";
    public string TargetSnapshotName { get; set; } = "";
    public long? FirstTaskId { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
