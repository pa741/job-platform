using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using JobPlatform.Ai;
using JobPlatform.Ai.Applications;
using JobPlatform.Ai.Extraction;
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
/// One candidate, five postings, a two-CV library, and a way to call a tool as them.
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
/// That the container can supply the same eleven services is asserted separately, in
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
    /// <remarks>
    /// Its requirements are answered outright by <see cref="BackendVariant"/> and only partly by
    /// <see cref="DataVariant"/>, so a selection over this posting is decided rather than tied.
    /// </remarks>
    public const long WithDocuments = 10;

    /// <summary>Matched and judged strong, with nothing generated for it yet.</summary>
    /// <remarks>
    /// <b>Deliberately the mirror image of <see cref="WithDocuments"/>:</b> it pulls
    /// <see cref="DataVariant"/> where that one pulls <see cref="BackendVariant"/>. Two postings of
    /// different kinds pulling different CVs is the spec's own "done when", and a fixture where
    /// every posting pulled the same variant would pass every selection test while the scoring did
    /// nothing at all.
    /// </remarks>
    public const long WithoutDocuments = 11;

    /// <summary>Matched, and asks for things no variant in this library mentions.</summary>
    public const long NoCvFit = 12;

    /// <summary>
    /// A second posting no variant fits, sharing one missing concept with <see cref="NoCvFit"/>.
    /// </summary>
    /// <remarks>
    /// <b>Two, because <c>CvGapBrief.MinimumPostings</c> is two.</b> A concept blocking a single
    /// posting is indistinguishable from one recruiter's vocabulary and is deliberately kept out
    /// of the brief, so a fixture with one blocked posting would produce an empty gap list and a
    /// test of the ranking would pass by having nothing to rank.
    /// </remarks>
    public const long NoCvFitEither = 14;

    /// <summary>Matched, and asks for exactly what both variants answer, so nothing separates them.</summary>
    public const long TiedCv = 13;

    /// <summary>A real posting nobody scored against this candidate.</summary>
    public const long Unmatched = 99;

    /// <summary>The candidate's backend CV. Populated by <see cref="CreateAsync"/>.</summary>
    public long BackendVariant { get; private set; }

    /// <summary>Their data CV.</summary>
    public long DataVariant { get; private set; }

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

            Post(db, NoCvFit, "Site Reliability Engineer", "Northwind",
                "Terraform everywhere and a React console nobody wants to own.",
                "https://jobs.lever.co/northwind/sre");

            Post(db, TiedCv, "Container Platform Engineer", "Tailspin",
                "Kubernetes and Docker, and we are honestly not fussy about the rest.",
                "https://jobs.lever.co/tailspin/containers");

            Post(db, NoCvFitEither, "Cloud Infrastructure Engineer", "Litware",
                "Terraform against AWS, all day.",
                "https://jobs.lever.co/litware/cloud");

            // Real, and deliberately never scored against this candidate. Every tool that takes
            // a posting id has to refuse it, because these ids are named by a model.
            Post(db, Unmatched, "Somebody Else's Job", "Elsewhere",
                "Not this candidate's business.", "https://example.invalid/99");

            // The requirements are stored on the match rather than as PostingConcepts rows,
            // because that is where selection reads them from: MatchResult.Matched plus
            // MatchResult.Gaps is every demand the scorer weighed, and the pack rebuilds the
            // posting's requirement set out of exactly those two columns. A fixture that seeded
            // PostingConcepts instead would leave every selection scoring against nothing and
            // answering Ambiguous, which looks like a passing test of a tie.
            Match(db, harness.ProfileId, WithDocuments, score: 60, assessment: 85, rank: 9,
                [
                    ("skill.kubernetes", AssertionPolarity.Required),
                    ("skill.csharp", AssertionPolarity.Required),
                    ("skill.docker", AssertionPolarity.Unspecified),
                ]);

            Match(db, harness.ProfileId, WithoutDocuments, score: 72, assessment: 88, rank: 8,
                [
                    ("skill.python", AssertionPolarity.Required),
                    ("skill.sql", AssertionPolarity.Required),
                    ("skill.kubernetes", AssertionPolarity.Unspecified),
                ]);

            Match(db, harness.ProfileId, NoCvFit, score: 55, assessment: 81, rank: 7,
                [
                    ("skill.terraform", AssertionPolarity.Required),
                    ("skill.react", AssertionPolarity.Required),
                ]);

            Match(db, harness.ProfileId, TiedCv, score: 58, assessment: 82, rank: 6,
                [
                    ("skill.kubernetes", AssertionPolarity.Required),
                    ("skill.docker", AssertionPolarity.Required),
                ]);

            // Terraform in common with NoCvFit, so the two share a gap and the brief has
            // something to rank. AWS is its own, so the cluster rule has a majority test to make.
            Match(db, harness.ProfileId, NoCvFitEither, score: 52, assessment: 80, rank: 5,
                [
                    ("skill.terraform", AssertionPolarity.Required),
                    ("skill.aws", AssertionPolarity.Required),
                ]);

            await db.SaveChangesAsync();
        }

        await using (var db = new JobsDbContext(options))
        {
            var library = new CvVariantRepository(db);

            harness.BackendVariant = await VariantAsync(
                library, harness.ProfileId, "Backend .NET",
                ["skill.csharp", "skill.kubernetes", "skill.docker"]);

            harness.DataVariant = await VariantAsync(
                library, harness.ProfileId, "Data platforms",
                ["skill.python", "skill.sql", "skill.kubernetes", "skill.docker"]);
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
    public SubmissionTools Tools() => Tools(writer: null);

    /// <summary>
    /// The surface with a tie-break available, which is the only thing a writer is used for here.
    /// </summary>
    /// <remarks>
    /// Null by default, because <b>no AI provider is the shape this system ships in</b> and the
    /// abstention path has to be the one every other test runs through. A deployment with no
    /// provider must answer "two CVs fit and nothing could separate them" rather than sending one,
    /// and a harness that always had a chooser would never exercise that.
    /// </remarks>
    public SubmissionTools Tools(IApplicationWriter? writer)
    {
        var db = new JobsDbContext(_options);
        _contexts.Add(db);

        return new SubmissionTools(
            new CandidateProfileRepository(db),
            new JobMatchRepository(db),
            new SubmissionRepository(db),
            new ApplicationDocumentRepository(db),
            new CvVariantRepository(db),
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
            disclosures: Disclosures,
            writer: writer);
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

    /// <summary>
    /// One scored pair, with the posting's requirements split across the two columns that carry
    /// them.
    /// </summary>
    /// <remarks>
    /// <b>The split between <c>MatchedJson</c> and <c>GapsJson</c> is alternating rather than
    /// meaningful, and that is on purpose.</b> Selection reads the union of the two, so a fixture
    /// that put every requirement in one column would leave the other half of that union untested
    /// - and the half most likely to be dropped by a refactor is the matched one, because a
    /// requirement the candidate already holds does not look like something a CV has to mention.
    /// It does.
    /// </remarks>
    private static void Match(
        JobsDbContext db,
        long profileId,
        long postingId,
        int score,
        int assessment,
        double rank,
        IReadOnlyList<(string Key, AssertionPolarity Demand)> demands)
    {
        var matched = demands
            .Where((_, index) => index % 2 == 0)
            .Select(demand => new ConceptMatch(demand.Key, demand.Key, MatchRelation.Exact, 1.0, demand.Demand))
            .ToList();

        var gaps = demands
            .Where((_, index) => index % 2 == 1)
            .Select(demand => new ConceptGap(demand.Key, demand.Demand, null))
            .ToList();

        db.JobMatches.Add(new JobMatchEntity
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
            MatchedJson = JsonSerializer.Serialize(matched, MatchJson),
            GapsJson = JsonSerializer.Serialize(gaps, MatchJson),
        });
    }

    /// <summary>
    /// Camel case, because that is what <c>JobMatchRepository</c> writes and reads these columns
    /// with. A fixture serialising them in Pascal case would store rows the repository silently
    /// reads back as empty, and every selection would answer on no requirements at all.
    /// </summary>
    private static readonly JsonSerializerOptions MatchJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// One CV in the library: written, extracted and rendered, which is what makes it sendable.
    /// </summary>
    /// <remarks>
    /// <b>All three steps, because <c>CvVariant.IsSendable</c> needs all three.</b> A variant with
    /// no render is not selectable at all, so a fixture that stopped after <c>CreateAsync</c>
    /// would produce an empty library and every pack would answer <c>NoFit</c> - a green test of
    /// the abstention path and a blind spot over everything else.
    ///
    /// The concepts go in through <c>ReplaceConceptsAsync</c> rather than as rows, so the
    /// selection-only guard is exercised where it actually lives: these become
    /// <c>CvVariantConcepts</c>, and nothing anywhere copies them into <c>ProfileConcepts</c>.
    /// </remarks>
    private static async Task<long> VariantAsync(
        CvVariantRepository library, long profileId, string label, string[] concepts)
    {
        var written = await library.CreateAsync(
            profileId, label, $"# Ada Lovelace\n\n{label}.", Now.AddDays(-3));

        var id = written.Variant!.Id;

        await library.ReplaceConceptsAsync(
            profileId,
            id,
            [.. concepts.Select(key => new ConceptAssertion(key, AssertionSource.Model, AssertionPolarity.Expert))],
            CvVariantExtraction.CurrentVersion);

        await library.RecordRenderAsync(
            profileId,
            id,
            new RenderedVariant
            {
                PdfBlobPath = ApplicationPackFile.VariantBlobPath(
                    "profile-cvs", profileId, id, "Ada Lovelace", PackFormat.Pdf),
                DocxBlobPath = ApplicationPackFile.VariantBlobPath(
                    "profile-cvs", profileId, id, "Ada Lovelace", PackFormat.Docx),
                Sha256 = new string('a', 64),

                // After the authoring timestamp, or the render reads as stale and the variant
                // leaves selection - which is the pairing CvVariant.IsRenderCurrent enforces.
                RenderedAtUtc = Now.AddDays(-2),
            });

        return id;
    }

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
/// A tie-break with a scripted answer, and no writing at all.
/// </summary>
/// <remarks>
/// <b><see cref="WriteAsync"/> throws rather than returning null.</b> Nothing on the MCP surface
/// may write a document - the pack chooses among finished ones and generation lives on the
/// dashboard and the nightly pass - so a tool that started calling the writer would be a
/// disclosure and a bill arriving through a stub that answered politely. This is the same
/// argument the surface's own <c>submit_application</c> rule makes: the way to be sure something
/// cannot happen is to make it loud rather than to trust that nobody will.
///
/// <b>It records the ballot it was given</b>, because the bound on that ballot is the assertion
/// worth making: a tie can be the whole library, and a question put over six names is a
/// preference rather than a choice.
/// </remarks>
internal sealed class StubCvChooser(long? answer) : IApplicationWriter
{
    /// <summary>Every ballot this was asked to settle, in order.</summary>
    public List<IReadOnlyList<CvVariantScore>> Ballots { get; } = [];

    public Task<ApplicationDraft?> WriteAsync(ApplicationRequest request, CancellationToken ct = default)
        => throw new InvalidOperationException(
            "No tool on this surface may write a document. The pack chooses among CVs the "
            + "candidate wrote; generation is the dashboard's and the nightly pass's.");

    public Task<long?> ChooseCurriculumVitaeAsync(CvChoiceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Ballots.Add(request.Ballot);

        return Task.FromResult(answer);
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
