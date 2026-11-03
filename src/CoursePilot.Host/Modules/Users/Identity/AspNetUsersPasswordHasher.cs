using Microsoft.AspNetCore.Identity;

namespace CoursePilot.Host.Modules.Users.Identity;

public sealed class AspNetUsersPasswordHasher : IUsersPasswordHasher
{
    private static readonly object Subject = new();
    private readonly PasswordHasher<object> _hasher = new();

    public string Hash(string password) => _hasher.HashPassword(Subject, password);
    public bool Verify(string hash, string password) =>
        _hasher.VerifyHashedPassword(Subject, hash, password) != PasswordVerificationResult.Failed;
}
