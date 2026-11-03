using System.ComponentModel.DataAnnotations;
namespace CoursePilot.Host.Modules.Users.Transports.Http;
public sealed record ChangeUserEmailModel([Required] string Email);
