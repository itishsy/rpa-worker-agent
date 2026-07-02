namespace Seebot.WorkerAgent.Core.Configuration;

public sealed class SchedulerOptions
{
    public string BaseUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string ClientId { get; set; } = "rpa_console";
    public string ClientSecret { get; set; } = "123";
}
