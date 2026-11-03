using Icaro;
using CoursePilot.Host.Modules.Users.Identity;
using CoursePilot.Host.Modules.Users.Mongo;
using CoursePilot.Host.Modules.Users.Transports.Cli;
using CoursePilot.Host.Modules.Users.Transports.Http;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using MongoDB.Driver;

namespace CoursePilot.Host.Modules.Users;

public static class UsersModuleRegistration
{
    public static IServiceCollection AddUsersModule(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        services.AddMongo(options =>
        {
            configuration.GetSection(MongoOptions.DefaultSection).Bind(options);
        });
        services.AddSingleton<IMongoClient>(sp => sp.GetRequiredService<IMongoDatabase>().Client);
        services.AddSingleton<Icaro.IDocumentMapper<User, UserDocument>, UserDocumentMapper>();
        services.AddSingleton<MongoUsersRepository>();
        services.AddSingleton<IUsersRepository, UsersRepositoryErrorTranslation>();
        services.AddSingleton<IUsersPasswordHasher, AspNetUsersPasswordHasher>();
        services.AddSingleton<IUsersNotifier, NullUsersNotifier>();
        services.AddScoped<IUsersService, UsersService>();
        services.AddSingleton<UsersMongoInitializer>();
        services.Configure<BootstrapAdminOptions>(configuration.GetSection("BootstrapAdmin"));
        services.AddScoped<BootstrapAdminCommandHandler>();
        services.AddScoped<UsersCookieEvents>();
        services.AddScoped<IAuthorizationHandler, UsersAuthorizationHandler>();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromHours(configuration.GetValue("Authentication:CookieHours", 8));
            options.SlidingExpiration = false;
            options.EventsType = typeof(UsersCookieEvents);
            options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        services.AddAuthorization(options =>
        {
            options.AddPolicy("Student", p => p.RequireAuthenticatedUser().AddRequirements(new UsersRoleRequirement(UserRole.Student)));
            options.AddPolicy("ApprovedTeacher", p => p.RequireAuthenticatedUser().AddRequirements(new UsersRoleRequirement(UserRole.Teacher, true)));
            options.AddPolicy("Admin", p => p.RequireAuthenticatedUser().AddRequirements(new UsersRoleRequirement(UserRole.Admin)));
        });
        return services;
    }
}
