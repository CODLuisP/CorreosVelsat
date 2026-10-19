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
                options.AddDocumentTransformer((document, context, _) =>
                {
                    // Servidor relativo: "Try it out" llama a la misma URL desde donde se abrió Swagger,
                    // con el prefijo de PathBase (p. ej. /correos) si el servicio está detrás de nginx.
                    string pathBase = context.ApplicationServices.GetRequiredService<IConfiguration>()["PathBase"]?.Trim().TrimEnd('/') ?? "";
                    if (pathBase.Length > 0 && !pathBase.StartsWith('/'))
                        pathBase = "/" + pathBase;
                    document.Servers = [new OpenApiServer { Url = pathBase.Length > 0 ? pathBase : "/" }];

                    document.Info = new OpenApiInfo
                    {
                        Title = "Correos Velsat API",
                        Version = "v1",
                        Description = """
                            API de envío de correos de Velsat sobre **Cloudflare Email Sending** (HTTPS, sin SMTP).

                            **Autenticación:** los endpoints de `/api/email` requieren el header `X-API-Key`.
                            En Swagger UI pulsa **Authorize** y pega la clave (`Api__ApiKey` / `Api:ApiKey`).

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
                        Description = "Clave definida en Api__ApiKey (variable de entorno) o Api:ApiKey (appsettings.json).",
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
                // Ruta relativa a /swagger/index.html para que funcione también bajo un prefijo (/correos/swagger/).
                options.SwaggerEndpoint("../openapi/v1.json", "Correos Velsat API v1");
                options.RoutePrefix = "swagger";
                options.DocumentTitle = "Correos Velsat API";
                options.DisplayRequestDuration();
                options.EnableTryItOutByDefault();
            });
            app.MapGet("/", (HttpRequest request) => Results.Redirect($"{request.PathBase}/swagger")).ExcludeFromDescription();
            return app;
        }
    }
}
