using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace CorreosVelsat.Api
{
    /// <summary>
    /// Documento OpenAPI (/openapi/v1.json) y Swagger UI (/swagger).
    /// Activos en Development, o en cualquier entorno con Api:SwaggerEnabled=true.
    /// </summary>
    public static class ApiDocumentation
    {
        private const string ApiKeySchemeId = "ApiKey";

        public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
        {
            services.AddOpenApi(options =>
            {
                options.AddDocumentTransformer((document, _, _) =>
                {
                    document.Info = new OpenApiInfo
                    {
                        Title = "Correos Velsat API",
                        Version = "v1",
                        Description = """
                            API de envío de correos de Velsat sobre **Cloudflare Email Sending** (HTTPS, sin SMTP).

                            **Autenticación:** los endpoints de `/api/email` requieren el header `X-API-Key`.
                            En Swagger UI pulsa **Authorize** y pega la clave (`Api:ApiKey` de appsettings.json).

                            **Rate limit:** `Api:RateLimitPerMinute` por IP y `Api:RateLimitPerDay` en total. Al superarlo se responde 429 con `Retry-After`.
                            """,
                    };

                    document.Components ??= new OpenApiComponents();
                    document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                    document.Components.SecuritySchemes[ApiKeySchemeId] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.ApiKey,
                        In = ParameterLocation.Header,
                        Name = ApiKeyEndpointFilter.HeaderName,
                        Description = "Clave definida en Api:ApiKey (appsettings.json).",
                    };
                    return Task.CompletedTask;
                });

                options.AddSchemaTransformer((schema, context, _) =>
                {
                    if (context.JsonTypeInfo.Type == typeof(SendEmailRequest)
                        && schema.Properties?.TryGetValue("html", out var html) == true
                        && html is OpenApiSchema htmlSchema)
                    {
                        htmlSchema.Examples = [JsonValue.Create("<p>Hola, adjuntamos la información solicitada.</p>")];
                    }
                    return Task.CompletedTask;
                });

                // Solo los endpoints marcados con RequireApiKey() muestran el candado en Swagger.
                options.AddOperationTransformer((operation, context, _) =>
                {
                    if (context.Description.ActionDescriptor.EndpointMetadata.OfType<ApiKeyRequiredMetadata>().Any())
                    {
                        operation.Security ??= [];
                        operation.Security.Add(new OpenApiSecurityRequirement
                        {
                            [new OpenApiSecuritySchemeReference(ApiKeySchemeId, context.Document)] = [],
                        });
                    }
                    return Task.CompletedTask;
                });
            });
            return services;
        }

        public static bool IsEnabled(WebApplication app) =>
            app.Environment.IsDevelopment() || app.Configuration.GetValue("Api:SwaggerEnabled", false);

        public static WebApplication MapApiDocumentation(this WebApplication app)
        {
            if (!IsEnabled(app))
                return app;

            app.MapOpenApi();
            app.UseSwaggerUI(options =>
            {
                options.SwaggerEndpoint("/openapi/v1.json", "Correos Velsat API v1");
                options.RoutePrefix = "swagger";
                options.DocumentTitle = "Correos Velsat API";
                options.DisplayRequestDuration();
                options.EnableTryItOutByDefault();
            });
            app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
            return app;
        }
    }
}
