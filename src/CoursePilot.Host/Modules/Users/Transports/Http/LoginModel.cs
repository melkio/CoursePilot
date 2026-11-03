using System.ComponentModel.DataAnnotations;
namespace CoursePilot.Host.Modules.Users.Transports.Http;
public sealed class LoginModel
{
    [Required]
    public string Email { get; set; } = string.Empty;
    [Required]
    public string Password { get; set; } = string.Empty;
}
