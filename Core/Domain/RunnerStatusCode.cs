namespace Seebot.WorkerAgent.Core.Domain;

public enum RunnerStatusCode
{
    New = 0,
    Runnable = 1,
    Running = 2,
    Closed = 3,
    RobotError = 4,
    ClientError = 5,
    Upgrading = 6,
    UpgradeFailed = 7,
    Offline = 8
}
