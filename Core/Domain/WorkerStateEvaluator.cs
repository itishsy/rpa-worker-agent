namespace Seebot.WorkerAgent.Core.Domain;

public static class WorkerStateEvaluator
{
    public static bool IsRunnerReady(RunnerStatusCode status)
    {
        return status is RunnerStatusCode.Runnable or RunnerStatusCode.Running;
    }

    public static bool IsRunnerBusy(RunnerStatusCode status)
    {
        return status == RunnerStatusCode.Running;
    }

    public static bool IsRunnerUpgradeLocked(RunnerStatusCode status)
    {
        return status == RunnerStatusCode.Upgrading;
    }

    public static bool CanSwitchBeforeStop(RunnerStatusCode status)
    {
        return status == RunnerStatusCode.Runnable;
    }

    public static WorkerReadyEvaluation EvaluateReadyAfterVmStart(RunnerStatusCode status)
    {
        return status switch
        {
            RunnerStatusCode.Runnable or RunnerStatusCode.Running => new WorkerReadyEvaluation(WorkerReadyEvaluationKind.Ready),
            RunnerStatusCode.New or RunnerStatusCode.Upgrading => new WorkerReadyEvaluation(WorkerReadyEvaluationKind.Wait),
            RunnerStatusCode.Closed => new WorkerReadyEvaluation(WorkerReadyEvaluationKind.Error, ErrorCodes.RunnerClosed),
            RunnerStatusCode.RobotError => new WorkerReadyEvaluation(WorkerReadyEvaluationKind.Error, ErrorCodes.RobotError),
            RunnerStatusCode.ClientError => new WorkerReadyEvaluation(WorkerReadyEvaluationKind.Error, ErrorCodes.ClientError),
            RunnerStatusCode.UpgradeFailed => new WorkerReadyEvaluation(WorkerReadyEvaluationKind.Error, ErrorCodes.UpgradeFailed),
            RunnerStatusCode.Offline => new WorkerReadyEvaluation(WorkerReadyEvaluationKind.Error, ErrorCodes.WorkerOffline),
            _ => new WorkerReadyEvaluation(WorkerReadyEvaluationKind.Error)
        };
    }

    public static SwitchCandidateEvaluation EvaluateSwitchCandidate(
        VmCurrentState vmState,
        bool currentProfilePending,
        int idleStableSeconds,
        DateTimeOffset now)
    {
        if (vmState.IsQuarantined)
        {
            return SwitchCandidateEvaluation.Rejected(ErrorCodes.WorkerQuarantined, "VM is quarantined.");
        }

        if (vmState.HasActiveSwitchTransaction)
        {
            return SwitchCandidateEvaluation.Rejected(ErrorCodes.VmNotIdle, "VM has an active switch transaction.");
        }

        if (vmState.RunnerStatusCode is not RunnerStatusCode.Runnable)
        {
            return SwitchCandidateEvaluation.Rejected(ErrorCodes.VmNotIdle, "Runner is not Runnable.");
        }

        if (currentProfilePending)
        {
            return SwitchCandidateEvaluation.Rejected(ErrorCodes.VmNotIdle, "Current profile still has pending work.");
        }

        if (!HasReachedIdleThreshold(vmState.IdleSince, idleStableSeconds, now))
        {
            return SwitchCandidateEvaluation.Rejected(ErrorCodes.VmNotIdle, "VM idle duration has not reached the threshold.");
        }

        return SwitchCandidateEvaluation.Allowed();
    }

    private static bool HasReachedIdleThreshold(DateTimeOffset? idleSince, int idleStableSeconds, DateTimeOffset now)
    {
        if (idleSince is null)
        {
            return false;
        }

        return now - idleSince.Value >= TimeSpan.FromSeconds(idleStableSeconds);
    }
}
