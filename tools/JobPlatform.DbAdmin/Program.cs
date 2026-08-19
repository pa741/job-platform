using JobPlatform.Data.Sql;
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

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
