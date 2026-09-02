using System.Text.Json;
using JobPlatform.Core.Dedup;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Model;
using JobPlatform.Core.Searches;
using JobPlatform.Data.Cosmos;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
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
                  dbadmin seed-concepts   "<connection-string>"
                  dbadmin status          "<connection-string>"
                  dbadmin coverage        "<connection-string>" [top-mentions]
                  dbadmin apply-links     "<connection-string>" [days]
                  dbadmin delete-submissions "<connection-string>" <id> [<id>...] [--confirm]
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
                "seed-concepts" => await SeedConceptsAsync(connectionString),
                "status" => await StatusAsync(connectionString),
                "coverage" => await CoverageAsync(
                    connectionString,
                    args.Length >= 3 && int.TryParse(args[2], out var top) ? top : 40),
                "backfill-crossboard" => await BackfillCrossBoardAsync(
                    connectionString,
                    args.Contains("--confirm", StringComparer.Ordinal)),
                "apply-links" => await ApplyLinksAsync(
                    connectionString,
                    args.Length >= 3 && int.TryParse(args[2], out var days) ? days : 7),
                // Dry run unless --confirm. The ids are typed by a person from a list, and
                // the cost of a typo here is somebody's real application history.
                "delete-submissions" => await DeleteSubmissionsAsync(
                    connectionString,
                    [.. args.Skip(2)
                        .Where(a => !a.StartsWith("--", StringComparison.Ordinal))
                        .Select(a => long.TryParse(a, out var id) ? id : -1)
                        .Where(id => id > 0)],
                    args.Contains("--confirm", StringComparer.Ordinal)),
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

    /// <summary>
    /// Projects the embedded vocabulary into the concept tables.
    /// </summary>
    /// <remarks>
    /// Run after any migration that touches the vocabulary, and after any change to
    /// <c>concepts.json</c>. Skipping it is quiet rather than loud: the ingest keeps working
    /// and simply stops recording assertions for concepts the database does not know, so
    /// every count involving them is low with nothing to say why. The ingest logs a warning
    /// naming this command when it notices.
    ///
    /// Idempotent. Running it twice changes nothing the second time.
    /// </remarks>
    private static async Task<int> SeedConceptsAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<JobsDbContext>()
            .UseSqlServer(connectionString, sql =>
            {
                sql.EnableRetryOnFailure(maxRetryCount: 5, TimeSpan.FromSeconds(20), null);
                sql.CommandTimeout(300);
            })
            .Options;

        await using var db = new JobsDbContext(options);

        Console.WriteLine("Seeding concept vocabulary...");

        var result = await ConceptSeeder.SeedAsync(db);

        Console.WriteLine($"Vocabulary version : {result.Version}");
        Console.WriteLine($"Concepts added     : {result.ConceptsAdded}");
        Console.WriteLine($"Concepts updated   : {result.ConceptsUpdated}");
        Console.WriteLine($"Concepts retired   : {result.ConceptsDeactivated}");
        Console.WriteLine($"Labels             : {result.Labels}");
        Console.WriteLine($"Relations          : {result.Relations}");
        Console.WriteLine($"Closure rows       : {result.ClosureRows}");

        return 0;
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
    /// <summary>
    /// How much of the corpus the model has actually read, and what it could not name.
    /// </summary>
    /// <remarks>
    /// <b>Two numbers that are easy to confuse and justify very different work.</b> The share of
    /// <i>assertions</i> that are model-sourced is already on the dashboard, through
    /// <c>/concepts/source-composition</c>. This is the share of <i>postings</i> the model has
    /// read at all, which that figure cannot tell you: one advert with forty model assertions and
    /// forty adverts with none look identical in it.
    ///
    /// <b>And the unresolved-mention log, which is the growth mechanism with no reader.</b>
    /// <c>PostingMentions</c> exists because the previous vocabulary handled ambiguous names by
    /// refusing to match them, which meant the data was wrong with no way to find out by how
    /// much. It is where the next concepts come from - the most frequent unresolved forms - and
    /// until this command there was no way to see it except one posting at a time.
    ///
    /// A console command rather than an endpoint, for now, because it is a full-table aggregate
    /// over two large tables and the API's rule is that SQL is for browse, search and detail.
    /// If it earns a place on the Vocabulary page it should follow <c>/concepts/source-composition</c>:
    /// one round trip, cached hard, never on a bootstrap path.
    /// </remarks>
    private static async Task<int> CoverageAsync(string connectionString, int topMentions)
    {
        await using var db = new JobsDbContext(Options(connectionString));

        var postings = await db.JobPostings.CountAsync();
        var withText = await db.JobPostings.CountAsync(p => p.DescriptionLength > 0);

        // Distinct postings rather than rows: a posting with forty assertions is one posting.
        var withAnyConcept = await db.PostingConcepts.Select(c => c.PostingId).Distinct().CountAsync();
        var withModelConcept = await db.PostingConcepts
            .Where(c => c.Source == AssertionSource.Model)
            .Select(c => c.PostingId).Distinct().CountAsync();

        // "Read by the model" is asked of PostingExtractions rather than inferred from whether
        // any assertion came back, because an advert the model read and found nothing in is read.
        var everExtracted = await db.PostingExtractions.Select(e => e.PostingId).Distinct().CountAsync();
        var currentExtraction = await db.PostingExtractions
            .Where(e => e.ExtractorVersion == DocumentExtraction.CurrentVersion)
            .Select(e => e.PostingId).Distinct().CountAsync();

        Console.WriteLine("Postings");
        Console.WriteLine($"  total                        {postings,7}");
        Console.WriteLine($"  with a description           {withText,7}  {Share(withText, postings)}");
        Console.WriteLine();
        Console.WriteLine("Coverage, by posting - not by assertion");
        Console.WriteLine($"  with any concept             {withAnyConcept,7}  {Share(withAnyConcept, postings)}");
        Console.WriteLine($"  with a model concept         {withModelConcept,7}  {Share(withModelConcept, postings)}");
        Console.WriteLine($"  read by the model, ever      {everExtracted,7}  {Share(everExtracted, postings)}");
        Console.WriteLine($"  read at the current version  {currentExtraction,7}  {Share(currentExtraction, postings)}");
        Console.WriteLine();
        Console.WriteLine($"  no concepts at all           {postings - withAnyConcept,7}");
        Console.WriteLine($"  never read by the model      {postings - everExtracted,7}");

        // The growth mechanism. Ranked by how many postings say it rather than by total
        // occurrences: one advert repeating a word twenty times is one employer's habit, where
        // twenty adverts saying it once is a gap in the vocabulary.
        var mentions = await db.PostingMentions
            .GroupBy(m => new { m.SurfaceForm, m.Reason })
            .Select(g => new
            {
                g.Key.SurfaceForm,
                g.Key.Reason,
                Postings = g.Select(m => m.PostingId).Distinct().Count(),
                Occurrences = g.Sum(m => m.Occurrences),
            })
            .OrderByDescending(x => x.Postings)
            .Take(topMentions)
            .ToListAsync();

        Console.WriteLine();
        Console.WriteLine($"Unresolved mentions, top {topMentions} by how many postings name them");
        Console.WriteLine($"  {"surface form",-34} {"postings",8} {"occurrences",11}  reason");

        foreach (var mention in mentions)
        {
            Console.WriteLine(
                $"  {Truncate(mention.SurfaceForm, 34),-34} {mention.Postings,8} "
                + $"{mention.Occurrences,11}  {mention.Reason}");
        }

        return 0;
    }

    /// <summary>
    /// How much of the corpus the employer hosts, and how much the board does.
    /// </summary>
    /// <remarks>
    /// <b>The number section 0.3 of <c>mcp_handoff.md</c> says not to guess.</b> There is no
    /// Easy Apply column anywhere to read - <c>easy_apply</c> in JobSpy is a scraper *input*
    /// filter and never an output - so the presence of <c>job_url_direct</c> is the flag:
    /// present means the employer's own applicant tracking system, absent on a board posting
    /// means the board hosts the application.
    ///
    /// <b>freehire is excluded, and that is not tidiness.</b> Its scraper sets
    /// <c>job_url_direct</c> to the hit's own URL unconditionally, so every freehire posting
    /// reads as employer-hosted whatever the truth is. Averaging it in would dilute the ratio
    /// with rows that carry no signal at all.
    ///
    /// <b>Read a jump towards 100% board-hosted as a broken selector, not as a market change.</b>
    /// LinkedIn's half of this is a DOM scrape of one element id; if that id is renamed, every
    /// posting reads as Easy Apply, nothing throws, and this number is the only thing that says
    /// so. That is why it is a command rather than a note in a file - it has to be re-runnable.
    /// </remarks>
    private static async Task<int> ApplyLinksAsync(string connectionString, int days)
    {
        await using var db = new JobsDbContext(Options(connectionString));

        var since = DateTimeOffset.UtcNow.AddDays(-days);

        // Anonymous type, not a positional record: EF cannot project a GroupBy straight into a
        // record's constructor - it compiles and then fails at runtime as untranslatable.
        var rows = await db.JobPostings
            .Where(p => p.LastSeenUtc > since)
            .GroupBy(p => p.Site)
            .Select(g => new
            {
                Site = g.Key,
                Postings = g.Count(),
                // SUM(CASE WHEN ...) rather than Count(predicate), mirroring the query this
                // command replaces so the two can be checked against each other by eye.
                BoardHosted = g.Sum(p => p.JobUrlDirect == null ? 1 : 0),

                // The diagnostic that separates the two causes of a null. On LinkedIn both
                // job_url_direct and the description come from the job detail page, so a
                // posting with a description is one the scraper actually opened - which makes
                // its missing apply link mean "the board hosts it" rather than "nobody looked".
                WithDescription = g.Sum(p => p.DescriptionLength > 0 ? 1 : 0),

                // The three-way split that replaced "has a URL or not". A direct link and an
                // offsite flag are both "the employer takes it"; the flag survives where the
                // URL does not, which is the whole of LinkedIn now.
                Offsite = g.Sum(p => p.OffsiteApply == true ? 1 : 0),
                EasyApply = g.Sum(p => p.OffsiteApply == false ? 1 : 0),
                RouteUnknown = g.Sum(p => p.JobUrlDirect == null && p.OffsiteApply == null ? 1 : 0),

                // A "direct" link pointing back at the board it came from is not an external
                // apply link at all. Counting them is what stops a column that is 100% populated
                // being mistaken for a column that is 100% useful.
                SelfHosted = g.Sum(p =>
                    p.JobUrlDirect != null
                    && (p.JobUrlDirect.Contains("linkedin.com")
                        || p.JobUrlDirect.Contains("indeed.com")
                        || p.JobUrlDirect.Contains("glassdoor."))
                        ? 1
                        : 0),
            })
            .OrderByDescending(x => x.Postings)
            .ToListAsync();

        Console.WriteLine($"Apply links, postings last seen within {days} days");
        Console.WriteLine(
            $"  {"site",-16} {"postings",8} {"direct url",11} {"offsite",8} {"easy apply",11} "
            + $"{"route unknown",14} {"detail read",12}");

        foreach (var row in rows)
        {
            var signal = string.Equals(row.Site, ScraperSites.Freehire, StringComparison.OrdinalIgnoreCase);

            Console.WriteLine(
                $"  {Truncate(row.Site, 16),-16} {row.Postings,8} "
                + $"{row.Postings - row.BoardHosted,11} {row.Offsite,8} {row.EasyApply,11} "
                + $"{row.RouteUnknown,7} {Share(row.RouteUnknown, row.Postings),6} "
                + $"{Share(row.WithDescription, row.Postings),12}"
                + (signal ? "   (always direct)" : string.Empty));
        }

        // The check that says whether the split above means anything.
        //
        // Re-keyed to match JobDigestFunction: a missing apply URL is permanent on LinkedIn and
        // alarming on it would fire forever. What never has a legitimate steady state is a site
        // saying nothing either way - no link and no offsite flag - because every board answers
        // that question one way or the other when it is working.
        foreach (var row in rows.Where(r =>
            !string.Equals(r.Site, ScraperSites.Freehire, StringComparison.OrdinalIgnoreCase)
            && r.Postings >= 20
            && r.RouteUnknown == r.Postings))
        {
            Console.WriteLine();
            Console.WriteLine($"  !! {row.Site}: none of {row.Postings} postings say how to apply.");

            if (row.WithDescription >= row.Postings * 0.9)
            {
                Console.WriteLine(
                    $"     The detail page WAS read on {Share(row.WithDescription, row.Postings)} "
                    + "of them, so the markers were looked for");
                Console.WriteLine(
                    "     and not found. Either the scraper emitting offsite_apply has not");
                Console.WriteLine("     reached the NAS yet, or its selectors have moved again.");
            }
            else
            {
                Console.WriteLine(
                    $"     The detail page was read on only {Share(row.WithDescription, row.Postings)} "
                    + "of them, so nothing looked.");
                Console.WriteLine(
                    "     Check linkedin_fetch_description in the scraper's config first.");
            }
        }

        // How many of the missing links another board already knows.
        //
        // JobFingerprint.ContentHash is normalised title|company|location, so the same job
        // cross-posted to LinkedIn and to Indeed or freehire hashes the same - and those two
        // still publish the employer's apply URL. Every match here is a link recoverable with
        // no extra request, no account and nothing to route around: it is already in the
        // database under a different row.
        var linkless = db.JobPostings
            .Where(p => p.LastSeenUtc > since && p.JobUrlDirect == null);

        var linklessCount = await linkless.CountAsync();

        var recoverable = await linkless
            .Where(p => db.JobPostings.Any(other =>
                other.ContentHash == p.ContentHash
                && other.Site != p.Site
                && other.JobUrlDirect != null))
            .CountAsync();

        Console.WriteLine();
        Console.WriteLine("Recoverable from another board, by content fingerprint");
        Console.WriteLine($"  postings with no direct link  {linklessCount,7}");
        Console.WriteLine(
            $"  the same job found elsewhere  {recoverable,7}  {Share(recoverable, linklessCount)}");
        Console.WriteLine(
            "  A link already in the database under another row costs nothing to use - no");
        Console.WriteLine(
            "  request, no account, no proxy. Whatever is left is what any other approach buys.");

        // A zero above is only meaningful if the fingerprint crosses boards at all. Without
        // this line it is impossible to tell "the boards list different jobs" from "the join
        // never matches", and those call for opposite decisions.
        var anyCrossBoard = await db.JobPostings
            .Where(p => p.LastSeenUtc > since)
            .Where(p => db.JobPostings.Any(other =>
                other.ContentHash == p.ContentHash && other.Site != p.Site))
            .CountAsync();

        Console.WriteLine(
            $"  same job on two boards, at all {anyCrossBoard,6}  "
            + $"{Share(anyCrossBoard, linklessCount)} - a zero here means the fingerprint, not the market");

        // ContentHash folds location in, and boards write locations differently - "London,
        // England, United Kingdom" against "London, UK". So a zero above may only mean the hash
        // is too strict to cross boards. Title and employer alone is the looser test that says
        // whether the inventory actually overlaps.
        var looseOverlap = await db.JobPostings
            .Where(p => p.LastSeenUtc > since && p.JobUrlDirect == null)
            .Where(p => db.JobPostings.Any(other =>
                other.Title == p.Title
                && other.Company == p.Company
                && other.Site != p.Site
                && other.JobUrlDirect != null))
            .CountAsync();

        Console.WriteLine(
            $"  by title and employer alone   {looseOverlap,7}  {Share(looseOverlap, linklessCount)}"
            + " - too loose to act on: one employer, one title, several cities");

        // Title, employer and city. The city is parsed rather than raw, so "London, England,
        // United Kingdom" and "London, UK" both reduce to the same value - which is the whole
        // reason ContentHash fails to cross boards. Requiring it back is what stops a London
        // posting being handed the apply link for the Dublin one.
        var cityMatched = await db.JobPostings
            .Where(p => p.LastSeenUtc > since && p.JobUrlDirect == null && p.LocationCity != null)
            .Where(p => db.JobPostings.Any(other =>
                other.Title == p.Title
                && other.Company == p.Company
                && other.LocationCity == p.LocationCity
                && other.Site != p.Site
                && other.JobUrlDirect != null))
            .CountAsync();

        Console.WriteLine(
            $"  ...and the same city          {cityMatched,7}  {Share(cityMatched, linklessCount)}"
            + " - safe to act on");

        var measurable = rows
            .Where(r => !string.Equals(r.Site, ScraperSites.Freehire, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var total = measurable.Sum(r => r.Postings);
        var boardHosted = measurable.Sum(r => r.BoardHosted);

        Console.WriteLine();
        Console.WriteLine("Excluding freehire, which is always direct and therefore carries no flag");
        Console.WriteLine($"  postings                     {total,7}");
        Console.WriteLine($"  board-hosted                 {boardHosted,7}  {Share(boardHosted, total)}");
        Console.WriteLine();
        Console.WriteLine(
            "  A small share means the ATS path is the whole feature and the board path can stay");
        Console.WriteLine(
            "  a link somebody clicks. A large one reorders section 1 of mcp_handoff.md. A share");
        Console.WriteLine(
            "  near 100% on one site is a broken scraper selector, not a change in the market.");

        return 0;
    }

    /// <summary>
    /// Removes submissions by id, with their event logs. <b>The one eraser in the system.</b>
    /// </summary>
    /// <remarks>
    /// <b>Everything else about this pipeline is append-only, and that is not being relaxed.</b>
    /// There is no delete on the API, none on the agent surface, and none in
    /// <c>SubmissionRepository</c>; withdrawing an application is a <c>Withdrawn</c> event.
    /// A log with an eraser in it is not one anybody can trust afterwards.
    ///
    /// This exists for one thing the append-only rule cannot address: rows that never described
    /// a real application at all - a test of the write path, a client that misfired. Those are
    /// not history to preserve, they are noise that makes the history wrong, and a
    /// <c>Withdrawn</c> event on them asserts something equally untrue.
    ///
    /// <b>It is a console command, deliberately, and must not become anything else.</b> An HTTP
    /// route on the API would be reachable with the same token the MCP client carries, so a
    /// misbehaving agent could erase real applications - which inverts the entire argument for
    /// the tool surface being safe. This needs a connection string, a database user, and a
    /// firewall rule, so only a person at a terminal can run it.
    ///
    /// Dry run unless <c>--confirm</c> is passed. It prints exactly what would go, because the
    /// ids come from a human reading a list and the cost of a typo is somebody's real history.
    /// </remarks>
    private static async Task<int> DeleteSubmissionsAsync(
        string connectionString, IReadOnlyList<long> ids, bool confirm)
    {
        if (ids.Count == 0)
        {
            return Fail("delete-submissions needs at least one submission id.");
        }

        await using var db = new JobsDbContext(Options(connectionString));

        var rows = await db.Submissions
            .Where(s => ids.Contains(s.Id))
            .Select(s => new
            {
                s.Id,
                s.PostingId,
                s.Posting!.Title,
                s.CreatedAtUtc,
                Events = s.Events.Count,
                // Named so the operator can see whose row this is before removing it. The
                // subject id rather than the name: this tool has no business reading a profile.
                s.Profile!.SubjectId,
            })
            .OrderBy(s => s.Id)
            .ToListAsync();

        var missing = ids.Where(id => rows.All(r => r.Id != id)).ToList();

        foreach (var id in missing)
        {
            Console.WriteLine($"  no submission {id}");
        }

        if (rows.Count == 0)
        {
            Console.WriteLine("Nothing to delete.");
            return missing.Count == ids.Count ? 1 : 0;
        }

        Console.WriteLine(confirm ? "Deleting:" : "Would delete (dry run):");
        Console.WriteLine($"  {"id",6} {"posting",8} {"events",7}  {"created",-20} title");

        foreach (var row in rows)
        {
            Console.WriteLine(
                $"  {row.Id,6} {row.PostingId,8} {row.Events,7}  "
                + $"{row.CreatedAtUtc:yyyy-MM-dd HH:mm:ss}  {Truncate(row.Title, 46)}");
        }

        Console.WriteLine();
        Console.WriteLine($"  subjects affected: {rows.Select(r => r.SubjectId).Distinct().Count()}");
        Console.WriteLine($"  events removed with them: {rows.Sum(r => r.Events)}");

        if (!confirm)
        {
            Console.WriteLine();
            Console.WriteLine("  Dry run. Re-run with --confirm to delete. Nothing was changed.");
            return 0;
        }

        // The events go with the submission: SubmissionEvents cascades from Submissions, so
        // this cannot leave a log orphaned from the thing it described.
        db.Submissions.RemoveRange(
            rows.Select(r => new SubmissionEntity { Id = r.Id }).ToList());

        var deleted = await db.SaveChangesAsync();

        Console.WriteLine();
        Console.WriteLine($"  Deleted. {deleted} rows removed, submissions and events together.");

        return 0;
    }

    private static string Share(int part, int total)
        => total == 0 ? "" : $"{(double)part / total:P1}";

    private static DbContextOptions<JobsDbContext> Options(string connectionString)
        => new DbContextOptionsBuilder<JobsDbContext>()
            .UseSqlServer(connectionString, sql =>
            {
                sql.EnableRetryOnFailure(maxRetryCount: 5, TimeSpan.FromSeconds(20), null);
                sql.CommandTimeout(120);
            })
            .Options;

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

            var remote = await db.JobPostings.CountAsync(p => p.IsRemote == true);
            var remoteNotStated = await db.JobPostings.CountAsync(p => p.IsRemote == null);
            var withDescription = await db.JobPostings.CountAsync(p => p.DescriptionLength > 0);
            Console.WriteLine();
            Console.WriteLine($"Remote           : {remote}");
            Console.WriteLine($"Remote not stated: {remoteNotStated}");
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

    /// <summary>
    /// Stamps the cross-board key on the postings that predate the column.
    /// </summary>
    /// <remarks>
    /// <b>A console command rather than a step in the migration, because the key is C#.</b>
    /// <c>JobFingerprint.CrossBoardKey</c> parses a city out of a free-text location and folds
    /// case, punctuation and whitespace; rewriting that in T-SQL would be a second implementation
    /// of the one rule the whole of deduplication rests on, and the two would drift on the first
    /// posting either spelled differently. This calls the same function ingest calls, so the rows
    /// written today and the rows written in August end up in the same clusters.
    ///
    /// <b>It is idempotent and it is not a one-off.</b> Re-running it is how the corpus is
    /// re-keyed after a change to the normalisation, which is a thing that has already happened
    /// once to the other fingerprint. It writes only where the value actually differs, so a
    /// second run touches nothing and says so.
    ///
    /// A null key is a null column, never a hash of the empty string: a posting with no city or
    /// no employer is not the same job as another one missing the same field, and the queue's
    /// clustering reads null as "this row clusters with nothing".
    ///
    /// Dry run unless <c>--confirm</c>, like every other command here that writes.
    /// </remarks>
    private static async Task<int> BackfillCrossBoardAsync(string connectionString, bool confirm)
    {
        await using var db = new JobsDbContext(Options(connectionString));

        const int BatchSize = 500;

        var scanned = 0;
        var changed = 0;
        var keyed = 0;
        var unkeyable = 0;
        var lastId = 0L;

        while (true)
        {
            // Keyset paging on the primary key rather than Skip/Take. The rows are being
            // rewritten as they are read, so an offset would walk over a shifting result set.
            var batch = await db.JobPostings
                .Where(p => p.Id > lastId)
                .OrderBy(p => p.Id)
                .Take(BatchSize)
                .Select(p => new { p.Id, p.Title, p.Company, p.LocationRaw, p.CrossBoardKey })
                .ToListAsync();

            if (batch.Count == 0)
            {
                break;
            }

            lastId = batch[^1].Id;
            scanned += batch.Count;

            foreach (var row in batch)
            {
                var hash = JobFingerprint.CrossBoardKeyHash(new JobPosting
                {
                    ExternalId = string.Empty,
                    Site = string.Empty,
                    Title = row.Title,
                    Company = row.Company,
                    Location = row.LocationRaw,
                });

                if (hash is null)
                {
                    unkeyable++;
                }
                else
                {
                    keyed++;
                }

                if (string.Equals(hash, row.CrossBoardKey, StringComparison.Ordinal))
                {
                    continue;
                }

                changed++;

                if (confirm)
                {
                    // Attached by key alone. Loading the entity would drag an unbounded
                    // description across for every row of a 7,000-row corpus to write 64 bytes.
                    var stub = new JobPostingEntity
                    {
                        Id = row.Id,
                        SourceKey = string.Empty,
                        Site = string.Empty,
                        ExternalId = string.Empty,
                        ContentHash = string.Empty,
                        Title = string.Empty,
                        CrossBoardKey = hash,
                    };

                    db.JobPostings.Attach(stub);
                    db.Entry(stub).Property(e => e.CrossBoardKey).IsModified = true;
                }
            }

            if (confirm)
            {
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
            }
        }

        Console.WriteLine($"Scanned      : {scanned}");
        Console.WriteLine($"Keyable      : {keyed}");
        Console.WriteLine($"No key       : {unkeyable}  (no city or no employer - clusters with nothing)");
        Console.WriteLine($"Out of date  : {changed}");
        Console.WriteLine(confirm
            ? $"Written      : {changed}"
            : "Dry run. Nothing was written - pass --confirm to write.");

        return 0;
    }

}
