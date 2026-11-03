namespace CoursePilot.Host.Modules.Users;

public interface IUsersNotifier
{
    Task UserChanged(User user);
}
