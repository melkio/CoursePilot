using System.ComponentModel.DataAnnotations;
namespace CoursePilot.Host.Modules.Users.Transports.Http;
public sealed record LoginModel([Required] string Email, [Required] string Password);
