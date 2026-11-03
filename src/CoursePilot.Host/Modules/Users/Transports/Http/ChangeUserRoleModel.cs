using System.ComponentModel.DataAnnotations;
namespace CoursePilot.Host.Modules.Users.Transports.Http;
public sealed class ChangeUserRoleModel
{
    [Required]
    public string Role { get; set; } = string.Empty;
}
