using System.ComponentModel.DataAnnotations;
namespace CoursePilot.Host.Modules.Users.Transports.Http;
public sealed record RegisterUserModel([Required] string Email, [Required] string Password, [Required] string Role);
