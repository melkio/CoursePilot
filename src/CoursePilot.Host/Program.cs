using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using CoursePilot.Host.Modules.Users;
using CoursePilot.Host.Modules.Users.Mongo;
using CoursePilot.Host.Modules.Users.Transports.Cli;
using CoursePilot.Host.Modules.Users.Transports.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    options.Filters.Add<UsersExceptionFilter>();
}).AddJsonOptions(options => options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.AddAntiforgery(options => { options.HeaderName = "X-CSRF-TOKEN"; options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always; });
builder.Services.AddUsersModule(builder.Configuration, builder.Environment);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("identity", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = builder.Configuration.GetValue("RateLimits:IdentityPermitLimit", 20),
        Window = TimeSpan.FromMinutes(builder.Configuration.GetValue("RateLimits:IdentityWindowMinutes", 1)),
        QueueLimit = 0
    }));
});
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (string.IsNullOrWhiteSpace(keysPath) && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("DataProtection:KeysPath must point to persistent shared key storage.");
if (!string.IsNullOrWhiteSpace(keysPath)) builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath)).SetApplicationName("CoursePilot");
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.OperationFilter<UsersSwaggerOperationFilter>());
builder.Services.AddHealthChecks();
var app = builder.Build();
await app.Services.GetRequiredService<UsersMongoInitializer>().Initialize();
if (args.Length == 1 && args[0] == "bootstrap-admin")
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<BootstrapAdminCommandHandler>().Run();
    return;
}
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapHealthChecks("/health");
app.Run();

public partial class Program;
