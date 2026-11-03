using System.ComponentModel.DataAnnotations;
namespace CoursePilot.Host.Modules.Users.Transports.Http;
public sealed class ChangeUserEmailModel
{
    [Required]
    public string Email { get; set; } = string.Empty;
}
