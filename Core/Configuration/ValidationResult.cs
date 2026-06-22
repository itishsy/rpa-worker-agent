namespace Seebot.WorkerAgent.Core.Configuration;

public sealed class ValidationResult
{
    public ValidationResult(IReadOnlyList<string> errors)
    {
        Errors = errors;
    }

    public bool IsValid => Errors.Count == 0;
    public IReadOnlyList<string> Errors { get; }
}
