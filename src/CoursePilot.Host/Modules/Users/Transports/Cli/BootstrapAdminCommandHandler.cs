using Microsoft.Extensions.Options;

namespace CoursePilot.Host.Modules.Users.Transports.Cli;

public sealed class BootstrapAdminCommandHandler(IUsersService users, IOptions<BootstrapAdminOptions> options)
{
    public Task Run()
    {
        var email = options.Value.Email;
        var password = options.Value.Password;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            throw new InvalidOperationException("BootstrapAdmin:Email and BootstrapAdmin:Password are required in protected configuration.");
        return users.Execute(new BootstrapAdminCommand(email, password));
    }
}
