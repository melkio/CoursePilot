namespace CoursePilot.Host.Modules.Users.Transports.Cli;

public sealed class BootstrapAdminCommandHandler(IUsersService users, IConfiguration configuration)
{
    public async Task Run()
    {
        var email = configuration["BootstrapAdmin:Email"];
        var password = configuration["BootstrapAdmin:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            throw new InvalidOperationException("BootstrapAdmin:Email and BootstrapAdmin:Password are required in protected configuration.");
        await users.Execute(new BootstrapAdminCommand(email, password));
    }
}
