using JobPlatform.Api.Endpoints;
using JobPlatform.Api.Features.Postings;
using JobPlatform.Api.Infrastructure;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.AspNetCore.Mvc;

namespace JobPlatform.Api.Features.Runs;

/// <summary>
/// Ingestion history: which blobs were processed, when, and what each produced.
/// </summary>
/// <remarks>
/// The operational counterpart to the metrics endpoints. Metrics answer "what does the market
/// look like"; these answer "did the pipeline actually run, and did it get anything". They
/// read SQL because run rows are relational and are written there by ingestion, not
/// duplicated into Cosmos.
/// </remarks>
public sealed class RunEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/runs")
            .WithTags("Runs")
            .RequireAuthorization(AuthSetup.PublicReadPolicy)
            .RequireRateLimiting(RateLimitSetup.ReadPolicy)
            .CacheOutput(CacheSetup.PostingsPolicy);

        group.MapGet("/", ListAsync)
            .WithName("ListRuns")
            .WithSummary("Recent scrape runs, newest first.");

        group.MapGet("/{id:int}", GetAsync)
            .WithName("GetRun")
            .WithSummary("One scrape run.");
    }

    private static async Task<IResult> ListAsync(
        [FromServices] JobPostingQueryRepository repository,
        CancellationToken ct,
        string? searchTerm = null,
        int limit = 25,
        int offset = 0)
    {
        if (offset < 0)
        {
            return TypedResults.Problem(
                detail: "offset must not be negative.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var page = await repository.ListRunsAsync(searchTerm, limit, offset, ct);

        return TypedResults.Ok(new PageResponse<RunResponse>
        {
            Items = page.Items.Select(ToResponse).ToList(),
            HasMore = page.HasMore,
            Total = null,
            Limit = Math.Clamp(limit, 1, JobPostingQueryRepository.MaxLimit),
            Offset = offset,
        });
    }

    private static async Task<IResult> GetAsync(
        int id,
        [FromServices] JobPostingQueryRepository repository,
        CancellationToken ct)
    {
        var run = await repository.GetRunAsync(id, ct);

        return run is null
            ? TypedResults.Problem(
                detail: $"No run with id {id}.",
                statusCode: StatusCodes.Status404NotFound)
            : TypedResults.Ok(ToResponse(run));
    }

    private static RunResponse ToResponse(ScrapeRun run) => new()
    {
        Id = run.Id,
        BlobPath = run.BlobPath,
        BlobSizeBytes = run.BlobSizeBytes,
        SearchTerm = run.SearchTerm,
        ScrapedAtUtc = run.ScrapedAtUtc,
        IngestedAtUtc = run.IngestedAtUtc,
        ScrapeDate = run.ScrapeDate,
        RowCount = run.RowCount,
        ParsedCount = run.ParsedCount,
        InvalidCount = run.InvalidCount,
        NewCount = run.NewCount,
        UpdatedCount = run.UpdatedCount,
        UnchangedCount = run.UnchangedCount,
    };
}

public sealed record RunResponse
{
    public required int Id { get; init; }
    public required string BlobPath { get; init; }
    public long BlobSizeBytes { get; init; }
    public required string SearchTerm { get; init; }
    public DateTimeOffset ScrapedAtUtc { get; init; }
    public DateTimeOffset IngestedAtUtc { get; init; }
    public DateOnly ScrapeDate { get; init; }
    public int RowCount { get; init; }
    public int ParsedCount { get; init; }
    public int InvalidCount { get; init; }
    public int NewCount { get; init; }
    public int UpdatedCount { get; init; }
    public int UnchangedCount { get; init; }
}
