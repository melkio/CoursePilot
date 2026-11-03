namespace CoursePilot.Host.Modules.Users.Transports.Http;

public sealed class AntiforgeryTokenModel
{
    public string? Token { get; set; }
    public string? Header { get; set; }
}
