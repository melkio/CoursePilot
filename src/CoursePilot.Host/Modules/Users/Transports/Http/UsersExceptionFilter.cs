using CoursePilot.Host.Modules.Users.Mongo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CoursePilot.Host.Modules.Users.Transports.Http;

public sealed class UsersExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var status = context.Exception switch
        {
            ArgumentException => 400,
            UnauthorizedAccessException => 403,
            KeyNotFoundException => 404,
            UsersDuplicateEmailException => 409,
            UsersVersionException => 412,
            UsersConflictException => 409,
            _ => 500
        };
        context.Result = new ObjectResult(new ProblemDetails { Status = status, Title = status switch { 400 => "Invalid request", 403 => "Forbidden", 404 => "Not found", 409 => "Conflict", 412 => "Version mismatch", _ => "Internal error" } }) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}
