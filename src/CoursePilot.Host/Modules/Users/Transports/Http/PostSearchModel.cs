namespace CoursePilot.Host.Modules.Users.Transports.Http;

public sealed class PostSearchModel
{
    public int Offset { get; set; }
    public int Limit { get; set; } = 25;
    public string? Role { get; set; }
    public string? Approval { get; set; }
}
