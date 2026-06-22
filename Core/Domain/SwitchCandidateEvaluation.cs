namespace Seebot.WorkerAgent.Core.Domain;

public sealed class SwitchCandidateEvaluation
{
    public SwitchCandidateEvaluation(bool canSwitch, string? errorCode = null, string? reason = null)
    {
        CanSwitch = canSwitch;
        ErrorCode = errorCode;
        Reason = reason;
    }

    public bool CanSwitch { get; }
    public string? ErrorCode { get; }
    public string? Reason { get; }

    public static SwitchCandidateEvaluation Allowed()
    {
        return new SwitchCandidateEvaluation(canSwitch: true);
    }

    public static SwitchCandidateEvaluation Rejected(string errorCode, string reason)
    {
        return new SwitchCandidateEvaluation(canSwitch: false, errorCode, reason);
    }
}
