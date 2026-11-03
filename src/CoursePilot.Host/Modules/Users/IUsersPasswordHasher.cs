namespace CoursePilot.Host.Modules.Users;

public interface IUsersPasswordHasher
{
    string Hash(string password);
    bool Verify(string hash, string password);
}
