using Microsoft.OpenApi.Models;

namespace JobPlatform.Api.Infrastructure;

public static class OpenApiSetup
{
    public static IServiceCollection AddApiOpenApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "job-platform API",
                    Version = "v1",
                    Description =
                        "Read access to scraped job postings and the market metrics derived " +
                        "from them, plus CV-to-posting matching. Metrics are served from " +
                        "Cosmos DB; postings from Azure SQL.",
                };

                return Task.CompletedTask;
            });
        });
    }
}
