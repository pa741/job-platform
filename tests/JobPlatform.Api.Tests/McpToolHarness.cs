using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using JobPlatform.Ai;
using JobPlatform.Ai.Applications;
using JobPlatform.Api.Configuration;
using JobPlatform.Api.Features.Mcp;
using JobPlatform.Core.Applications;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Matching;
using JobPlatform.Core.Profiles;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace JobPlatform.Api.Tests;

/// <summary>
/// One candidate, three postings and a way to call a tool as them.
/// </summary>
/// <remarks>
/// <b>The tool class over SQLite rather than the HTTP surface, and that is forced rather than
/// chosen.</b> Every tool resolves its caller from the principal the transport attached to the
/// message, and a test host has no way to mint an Entra token - which is why <c>McpEndpointTests</c>
/// says the behaviour is not tested there. What can be built is the principal itself, so the
/// repositories are constructed over an in-memory database exactly as
/// <c>SubmissionPersistenceTests</c> builds them, and the request is assembled by hand with a
/// claims principal on it.
///
/// <b>So the repositories here are built by hand and the composition is somebody else's claim.</b>
/// That the container can supply the same eight services is asserted separately, in
/// <c>McpEndpointTests</c>, precisely because this file would pass with none of them registered -
/// and the symptom of that would be every tool answering 500 on a surface whose tests were green.
///
/// <b>SQLite rather than fakes, for the reason the rest of this repository gives.</b> The refusals
/// under test are mostly "this posting is not yours" and "this answer is out of bounds", and both
/// are decided by a query that has to translate and a column that has a width. A mocked repository
/// would let every one of them pass while the real predicate said something else.
///
/// <b>The results are read as JSON, not as objects.</b> A tool returns an anonymous type, and what
/// leaves this system is that type serialised - so asserting on the serialised form is asserting on
/// what a client actually receives, including the property names, which is the whole point of
/// <c>McpToolPayloadTests</c>. Reading it back through reflection would assert on something no
/// caller ever sees.
/// </remarks>
internal sealed class McpToolHarness : IDisposable
{
    /// <summary>The person every test acts as. Their token carries this as <c>oid</c>.</summary>
    public const string Subject = "11111111-1111-1111-1111-111111111111";

    /// <summary>A service principal that <c>Mcp:AppPrincipals</c> maps to that person.</summary>
    public const string MappedApplication = "22222222-2222-2222-2222-222222222222";

    /// <summary>A service principal nobody mapped. Its own refusal, and it must stay its own.</summary>
    public const string UnmappedApplication = "33333333-3333-3333-3333-333333333333";

    /// <summary>Matched, judged strong, and the only posting in the database with documents.</summary>
    public const long WithDocuments = 10;

    /// <summary>Matched and judged strong, with nothing generated for it yet.</summary>
    public const long WithoutDocuments = 11;

    /// <summary>A real posting nobody scored against this candidate.</summary>
    public const long Unmatched = 99;

    public static readonly DateTimeOffset Now = new(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobsDbContext> _options;
    private readonly List<JobsDbContext> _contexts = [];

    private McpToolHarness(SqliteConnection connection, DbContextOptions<JobsDbContext> options)
    {
        _connection = connection;
        _options = options;
    }

    /// <summary>Every disclosure the tools wrote, in order.</summary>
    public RecordingDisclosureLog Disclosures { get; } = new();

    /// <summary>The candidate's profile id, which no tool argument may ever carry.</summary>
    public long ProfileId { get; private set; }

    public static async Task<McpToolHarness> CreateAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<JobsDbContext>().UseSqlite(connection).Options;

        var harness = new McpToolHarness(connection, options);

        await using (var db = new JobsDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();

            // The catalogue's 'skills' field reads labels through the concept graph, so the
            // projection has to exist before a profile can answer it.
            await ConceptSeeder.SeedAsync(db);
        }

        // The profile before the matches, because a match names a profile by foreign key and the
        // id is issued by the insert. Nothing in this file may assume it is 1: a tool that started
        // trusting a well-known profile id would pass here and be wrong everywhere else.
        await using (var db = new JobsDbContext(options))
        {
            var (view, _) = await new CandidateProfileRepository(db)
                .SaveAsync(CandidateProfile(), new FixedTime(Now));

            harness.ProfileId = view.Id;
        }

        await using (var db = new JobsDbContext(options))
        {
            Post(db, WithDocuments, "Platform Engineer", "Cloudflare",
                "We are hiring a platform engineer. Kubernetes, Go, and a lot of traffic.",
                "https://jobs.lever.co/cloudflare/platform");

            Post(db, WithoutDocuments, "Data Engineer", "Acme",
                "Pipelines, warehouses, and the people who read them.",
                "https://boards.greenhouse.io/acme/jobs/11");

            // Real, and deliberately never scored against this candidate. Every tool that takes
            // a posting id has to refuse it, because these ids are named by a model.
            Post(db, Unmatched, "Somebody Else's Job", "Elsewhere",
                "Not this candidate's business.", "https://example.invalid/99");

            Match(db, harness.ProfileId, WithDocuments, score: 60, assessment: 85, rank: 9);
            Match(db, harness.ProfileId, WithoutDocuments, score: 72, assessment: 88, rank: 8);

            await db.SaveChangesAsync();
        }

        await using (var db = new JobsDbContext(options))
        {
            await new ApplicationDocumentRepository(db).AddAsync(
                harness.ProfileId,
                WithDocuments,
                new ApplicationDraft
                {
                    CurriculumVitaeMarkdown = "# Ada Lovelace\n\nSenior Backend Engineer.",
                    CoverLetterMarkdown = "Dear Cloudflare,\n\nI would like to apply.",
                    Emphasised = ["Kubernetes at scale"],
                    Model = "test",
                    Version = ApplicationDraft.CurrentVersion,
                },
                instructions: null,
                [new DraftedAnswer("Why do you want to work here?", "Because of the traffic.", FreeTextCategory.PostingSpecific)],
                Now);
        }

        return harness;
    }

    public void Dispose()
    {
        foreach (var context in _contexts)
        {
            context.Dispose();
        }

        _connection.Dispose();
    }

    /// <summary>
    /// A fresh instance of the surface, over its own context.
    /// </summary>
    /// <remarks>
    /// Per call rather than per harness, because a tool call is a request and a request gets its
    /// own scoped <c>JobsDbContext</c> in the API. Sharing one across a write and a later read
    /// would let a test pass on a tracked entity that was never queried back, which is the failure
    /// mode a change-tracked context hides best.
    /// </remarks>
    public SubmissionTools Tools()
    {
        var db = new JobsDbContext(_options);
        _contexts.Add(db);

        return new SubmissionTools(
            new CandidateProfileRepository(db),
            new JobMatchRepository(db),
            new SubmissionRepository(db),
            new ApplicationDocumentRepository(db),
            new FormAnswerRepository(db),
            new OpenQuestionRepository(db),
            new RunRepository(db),

            // The real resolver with no Kernel: stages one to three run and the fourth abstains,
            // which is the deployment shape the design calls "no provider configured" and the one
            // that must never start guessing at a sensitive field.
            new FormFieldResolver(Options.Create(new AzureOpenAiOptions())),
            new FixedTime(Now),
            Options.Create(new McpOptions
            {
                AppPrincipals = { [MappedApplication] = Subject },
            }),

            // No pack store, which is the deployment with no document storage configured: the
            // pack still answers, with the markdown and no links. Nothing here mints a URL, so
            // no test can accidentally assert on one that would not exist in that deployment.
            packs: null,
            Disclosures);
    }

    /// <summary>
    /// A context for reading back what a write actually stored.
    /// </summary>
    /// <remarks>
    /// Round the tools deliberately. What a tool <i>returns</i> about a write it just made is the
    /// tool's own account of it, and the claim under test in <c>McpAnswerSourceTests</c> is about a
    /// column the tools never project - so the row has to be read from the database or not at all.
    /// </remarks>
    public JobsDbContext Database()
    {
        var db = new JobsDbContext(_options);
        _contexts.Add(db);

        return db;
    }

    /// <summary>A delegated token: a person's client, carrying a scope.</summary>
    public static RequestContext<CallToolRequestParams> AsCandidate()
        => Context(new Claim("oid", Subject), new Claim("scp", "access_as_user"));

    /// <summary>An app-only token whose principal configuration maps to the candidate.</summary>
    public static RequestContext<CallToolRequestParams> AsMappedApplication()
        => Context(new Claim("oid", MappedApplication), new Claim("roles", "Mcp.Access"));

    /// <summary>An app-only token nobody mapped. A role and no scope is what makes it app-only.</summary>
    public static RequestContext<CallToolRequestParams> AsUnmappedApplication()
        => Context(new Claim("oid", UnmappedApplication), new Claim("roles", "Mcp.Access"));

    /// <summary>A token that authenticated and carries no object id.</summary>
    public static RequestContext<CallToolRequestParams> AsNobody()
        => Context(new Claim("name", "Someone"));

    /// <summary>The tool's answer as a client receives it.</summary>
    public static JsonElement Read(object result)
        => JsonSerializer.SerializeToElement(result, result.GetType());

    /// <summary>Whether the answer is a structured refusal, and what it says.</summary>
    public static (bool Refused, string Reason) Refusal(object result)
    {
        var json = Read(result);

        return json.TryGetProperty("refused", out var refused) && refused.GetBoolean()
            ? (true, json.GetProperty("reason").GetString() ?? string.Empty)
            : (false, string.Empty);
    }

    /// <summary>The top-level property names of an answer, sorted.</summary>
    public static IReadOnlyList<string> Keys(JsonElement element)
        => [.. element.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal)];

    private static RequestContext<CallToolRequestParams> Context(params Claim[] claims)
    {
        var request = new JsonRpcRequest
        {
            Method = "tools/call",
            Context = new JsonRpcMessageContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
            },
        };

        return new RequestContext<CallToolRequestParams>(
            Server, request, new CallToolRequestParams { Name = "test" });
    }

    /// <summary>
    /// A server that exists only because <c>RequestContext</c> refuses a null one.
    /// </summary>
    /// <remarks>
    /// Nothing here sends a message and nothing reads one back. The transport is a channel with
    /// no other end, which is honest: the tools under test never touch the server, they read the
    /// principal off the request the transport delivered.
    /// </remarks>
    private static readonly McpServer Server =
        McpServer.Create(new SilentTransport(), new McpServerOptions(), NullLoggerFactory.Instance, null!);

    private static void Post(
        JobsDbContext db, long id, string title, string company, string description, string direct)
        => db.JobPostings.Add(new JobPostingEntity
        {
            Id = id,
            SourceKey = $"linkedin:{id}",
            Site = "linkedin",
            ExternalId = id.ToString(),
            ContentHash = new string((char)('a' + (id % 20)), 64),
            Title = title,
            Company = company,
            Description = description,
            LocationCity = "London",
            LocationRaw = "London, UK",
            JobUrl = $"https://www.linkedin.com/jobs/view/{id}",
            JobUrlDirect = direct,
            FirstSeenUtc = Now.AddDays(-7),
            LastSeenUtc = Now,
        });

    private static void Match(
        JobsDbContext db, long profileId, long postingId, int score, int assessment, double rank)
        => db.JobMatches.Add(new JobMatchEntity
        {
            ProfileId = profileId,
            PostingId = postingId,
            Score = score,
            RankScore = rank,
            ScoredAtUtc = Now.AddDays(-1),
            Verdict = CandidacyVerdict.Strong,
            AssessmentScore = assessment,
            AssessedAtUtc = Now.AddDays(-1),
            ScorerVersion = MatchResult.CurrentVersion,
        });

    /// <summary>
    /// A profile with something in every allowlisted field.
    /// </summary>
    /// <remarks>
    /// Fully populated on purpose: a payload test asserting "only allowlisted names come back" is
    /// vacuous against a profile whose fields are mostly null, because the pack drops absent
    /// values. Every value here is distinctive enough to be searched for in a disclosure record,
    /// which is what makes "the record names what was asked for and never the value" assertable.
    /// </remarks>
    private static CandidateProfile CandidateProfile() => new()
    {
        SubjectId = Subject,
        FullName = "Ada Lovelace",
        Headline = "Senior Backend Engineer",
        Email = "ada@example.invalid",
        Phone = "+44 20 7946 0958",
        Summary = "Backend engineer, mostly C# and Kubernetes.",
        LocationCity = "London",
        LocationCountry = "United Kingdom",
        PreferredArrangement = WorkArrangement.Hybrid,
        MaxDaysInOffice = 2,
        MinimumSalary = 75_000m,
        SalaryCurrency = "GBP",
        YearsExperience = 8,
        Seniority = Seniority.Senior,
        JobTypes = ["fulltime"],
        Links =
        [
            new ProfileLink("linkedin", "https://www.linkedin.com/in/ada"),
            new ProfileLink("github", "https://github.com/ada"),
            new ProfileLink("portfolio", "https://ada.example.invalid"),
        ],
        Experiences =
        [
            new ProfileExperience("Contoso", "Senior Engineer", new DateOnly(2021, 3, 1), null, "Ran the ingestion pipeline."),
            new ProfileExperience("Fabrikam", "Engineer", new DateOnly(2018, 1, 1), new DateOnly(2021, 2, 1), "Owned billing."),
        ],
        Education = [new ProfileEducation("University of Somewhere", "BSc", "Computer Science")],
        Projects = [new ProfileProject("job-platform", "A job market pipeline.")],
        DeclaredSkills = [new DeclaredSkill("skill.csharp", AssertionPolarity.Expert, 8)],
    };

    /// <summary>A clock that does not move, so a bound and a burn-down are assertable exactly.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SilentTransport : ITransport
    {
        private readonly Channel<JsonRpcMessage> _channel = Channel.CreateUnbounded<JsonRpcMessage>();

        public string? SessionId => null;

        public ChannelReader<JsonRpcMessage> MessageReader => _channel.Reader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken ct = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Keeps every disclosure the tools wrote, so a test can read what left and what was said about it.
/// </summary>
/// <remarks>
/// The production log is Cosmos-backed and the API test host removes it outright, which is the
/// honest shape for a host with no Cosmos - but it also means nothing anywhere asserts that a read
/// of the candidate's own data is recorded, or that the record does not contain the data. This is
/// what makes both assertable.
/// </remarks>
internal sealed class RecordingDisclosureLog : IDisclosureLog
{
    private readonly List<DisclosureRecord> _records = [];

    public IReadOnlyList<DisclosureRecord> Records => _records;

    public Task RecordAsync(DisclosureRecord record, CancellationToken ct = default)
    {
        _records.Add(record);

        return Task.CompletedTask;
    }
}
