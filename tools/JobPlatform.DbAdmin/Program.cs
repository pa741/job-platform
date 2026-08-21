using System.Text.Json;
using JobPlatform.Data.Cosmos;
using JobPlatform.Data.Sql;
using Microsoft.Azure.Cosmos;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace JobPlatform.DbAdmin;

/// <summary>
/// Database administration for job-platform, as a self-contained console app.
/// </summary>
/// <remarks>
/// This exists instead of a shell script because neither <c>sqlcmd</c> nor the
/// <c>SqlServer</c> PowerShell module is a safe assumption on a developer machine or a CI
/// runner, and both operations here need Microsoft Entra authentication. Using
/// <c>Microsoft.Data.SqlClient</c> with <c>Active Directory Default</c> means the same
/// command works from a laptop after <c>az login</c> and from a workflow under OIDC,
/// with nothing to install and no password anywhere.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                """
                Usage:
                  dbadmin migrate         "<connection-string>"
                  dbadmin grant-identity  "<connection-string>" <managed-identity-name>
                  dbadmin grant-migrator  "<connection-string>" <principal-name>
                  dbadmin status          "<connection-string>"
                  dbadmin metrics         "<cosmos-account-endpoint>" [search-term]

                The connection string must authenticate as the server's Entra admin, e.g.
                  "Server=tcp:<server>.database.windows.net,1433;Database=jobsdb;Authentication=Active Directory Default;Encrypt=True;Connect Timeout=60;"
                """);
            return 1;
        }

        var command = args[0];
        var connectionString = args[1];

        try
        {
            return command switch
            {
                "migrate" => await MigrateAsync(connectionString),
                "grant-identity" when args.Length >= 3 => await GrantIdentityAsync(connectionString, args[2]),
                "grant-identity" => Fail("grant-identity needs the managed identity name."),
                "grant-migrator" when args.Length >= 3 => await GrantMigratorAsync(connectionString, args[2]),
                "grant-migrator" => Fail("grant-migrator needs the principal name."),
                "status" => await StatusAsync(connectionString),
                // Second positional argument is the Cosmos endpoint, not a SQL connection.
                "metrics" => await MetricsAsync(connectionString, args.Length >= 3 ? args[2] : null),
                _ => Fail($"Unknown command '{command}'."),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> MigrateAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<JobsDbContext>()
            .UseSqlServer(connectionString, sql =>
            {
                // First contact usually has to wake a paused serverless database.
                sql.EnableRetryOnFailure(maxRetryCount: 5, TimeSpan.FromSeconds(20), null);
                sql.CommandTimeout(300);
            })
            .Options;

        await using var db = new JobsDbContext(options);

        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count == 0)
        {
            Console.WriteLine("Schema is already up to date.");
            return 0;
        }

        Console.WriteLine($"Applying {pending.Count} migration(s): {string.Join(", ", pending)}");
        await db.Database.MigrateAsync();
        Console.WriteLine("Migration complete.");
        return 0;
    }

    /// <summary>
    /// Maps a managed identity to a database user. Not expressible in Bicep — the ARM
    /// layer has no reach into the database — so it runs once after the first deploy.
    /// </summary>
    private static async Task<int> GrantIdentityAsync(string connectionString, string identityName)
    {
        // Identity names come from our own deployment, but a bracket in one would still
        // break out of the quoted identifier, so escape it the way T-SQL expects.
        var escaped = identityName.Replace("]", "]]", StringComparison.Ordinal);

        var statements = new[]
        {
            $"""
             IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @name)
                 CREATE USER [{escaped}] FROM EXTERNAL PROVIDER;
             """,
            $"ALTER ROLE db_datareader ADD MEMBER [{escaped}];",
            $"ALTER ROLE db_datawriter ADD MEMBER [{escaped}];",
        };

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        foreach (var sql in statements)
        {
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@name", identityName);
            await command.ExecuteNonQueryAsync();
        }

        // Deliberately not db_ddladmin: the app only reads and writes rows. Schema changes
        // go through `migrate`, run by an administrator.
        Console.WriteLine($"Granted db_datareader and db_datawriter to '{identityName}'.");
        return 0;
    }

    /// <summary>
    /// Gives a principal exactly what running EF migrations needs, and no more.
    /// </summary>
    /// <remarks>
    /// The alternative was making the deploy principal an Entra admin on the server,
    /// which is what the deploy workflow originally assumed. That hands CI the ability to
    /// drop the database in order to let it add a column. This is scoped to one database
    /// and revoked with a DROP USER.
    ///
    /// db_ddladmin covers the schema changes; the reader and writer roles are there
    /// because EF also reads and writes __EFMigrationsHistory, which db_ddladmin alone
    /// does not grant. Deliberately not db_owner: that would include managing users.
    /// </remarks>
    private static async Task<int> GrantMigratorAsync(string connectionString, string principalName)
    {
        // Names come from our own deployment, but a bracket in one would still break out
        // of the quoted identifier, so escape it the way T-SQL expects.
        var escaped = principalName.Replace("]", "]]", StringComparison.Ordinal);

        var statements = new[]
        {
            $"""
             IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @name)
                 CREATE USER [{escaped}] FROM EXTERNAL PROVIDER;
             """,
            $"ALTER ROLE db_ddladmin   ADD MEMBER [{escaped}];",
            $"ALTER ROLE db_datareader ADD MEMBER [{escaped}];",
            $"ALTER ROLE db_datawriter ADD MEMBER [{escaped}];",
        };

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        foreach (var sql in statements)
        {
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@name", principalName);
            await command.ExecuteNonQueryAsync();
        }

        Console.WriteLine(
            $"Granted db_ddladmin, db_datareader and db_datawriter to '{principalName}'.");
        return 0;
    }

    /// <summary>Prints what actually landed in the database, for verifying an ingest.</summary>
    private static async Task<int> StatusAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<JobsDbContext>()
            .UseSqlServer(connectionString, sql =>
            {
                sql.EnableRetryOnFailure(maxRetryCount: 5, TimeSpan.FromSeconds(20), null);
                sql.CommandTimeout(120);
            })
            .Options;

        await using var db = new JobsDbContext(options);

        var runs = await db.ScrapeRuns.OrderByDescending(r => r.IngestedAtUtc).Take(5).ToListAsync();
        var postingCount = await db.JobPostings.CountAsync();

        Console.WriteLine($"JobPostings rows : {postingCount}");
        Console.WriteLine($"ScrapeRuns rows  : {await db.ScrapeRuns.CountAsync()}");
        Console.WriteLine();
        Console.WriteLine("Most recent runs:");
        Console.WriteLine($"  {"blob",-52} {"rows",5} {"parsed",6} {"new",5} {"upd",5} {"same",5} {"bad",4}");

        foreach (var run in runs)
        {
            Console.WriteLine(
                $"  {Truncate(run.BlobPath, 52),-52} {run.RowCount,5} {run.ParsedCount,6} " +
                $"{run.NewCount,5} {run.UpdatedCount,5} {run.UnchangedCount,5} {run.InvalidCount,4}");
        }

        if (postingCount > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Postings by site:");
            var bySite = await db.JobPostings
                .GroupBy(p => p.Site)
                .Select(g => new { Site = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ToListAsync();
            foreach (var site in bySite)
            {
                Console.WriteLine($"  {site.Site,-12} {site.Count}");
            }

            var remote = await db.JobPostings.CountAsync(p => p.IsRemote);
            var withDescription = await db.JobPostings.CountAsync(p => p.DescriptionLength > 0);
            Console.WriteLine();
            Console.WriteLine($"Remote           : {remote}");
            Console.WriteLine($"With description : {withDescription}");
        }

        return 0;
    }

    /// <summary>Dumps the metric documents Cosmos holds, for verifying an ingest.</summary>
    private static async Task<int> MetricsAsync(string accountEndpoint, string? searchTerm)
    {
        using var client = CosmosClientFactory.Create(new CosmosOptions { AccountEndpoint = accountEndpoint });
        var container = client.GetContainer("jobplatform", "metrics");

        var sql = searchTerm is null
            ? "SELECT * FROM c ORDER BY c.type"
            : "SELECT * FROM c WHERE c.searchTerm = @term ORDER BY c.type";

        var query = new QueryDefinition(sql);
        if (searchTerm is not null)
        {
            query = query.WithParameter("@term", searchTerm);
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        var count = 0;

        using var iterator = container.GetItemQueryIterator<JsonElement>(query);
        while (iterator.HasMoreResults)
        {
            foreach (var document in await iterator.ReadNextAsync())
            {
                count++;
                Console.WriteLine(JsonSerializer.Serialize(document, options));
                Console.WriteLine();
            }
        }

        Console.WriteLine($"{count} metric document(s).");
        return 0;
    }

    private static string Truncate(string value, int length)
        => value.Length <= length ? value : string.Concat("...", value.AsSpan(value.Length - length + 3));

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
