namespace Seebot.WorkerAgent.Core.Guest;

public sealed class GuestTokenProvisioningResult
{
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
