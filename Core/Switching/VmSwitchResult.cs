namespace Seebot.WorkerAgent.Core.Switching;

public sealed class VmSwitchResult
{
    public string TxId { get; set; } = "";
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
