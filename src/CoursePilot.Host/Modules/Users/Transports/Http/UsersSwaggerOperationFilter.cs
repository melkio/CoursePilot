using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace CoursePilot.Host.Modules.Users.Transports.Http;

public sealed class UsersSwaggerOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.HttpMethod is not ("POST" or "PUT" or "PATCH" or "DELETE")) return;
        operation.Parameters ??= new List<IOpenApiParameter>();
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "X-CSRF-TOKEN",
            In = ParameterLocation.Header,
            Required = true,
            Description = "Obtain the request token and cookie from GET /api/users/antiforgery. Refresh after login or logout.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
        });
        if (context.ApiDescription.RelativePath?.StartsWith("api/admin/users/{id}/", StringComparison.OrdinalIgnoreCase) == true)
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "If-Match", In = ParameterLocation.Header, Required = true,
                Description = "Quoted version from the target user's ETag.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String }
            });
    }
}
