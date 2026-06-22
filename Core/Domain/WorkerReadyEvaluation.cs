namespace Seebot.WorkerAgent.Core.Domain;

public sealed class WorkerReadyEvaluation
{
    public WorkerReadyEvaluation(WorkerReadyEvaluationKind kind, string? errorCode = null)
    {
        Kind = kind;
        ErrorCode = errorCode;
    }

    public WorkerReadyEvaluationKind Kind { get; }
    public string? ErrorCode { get; }
}
