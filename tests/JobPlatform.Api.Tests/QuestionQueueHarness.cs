using System.Security.Claims;
using System.Text.Encodings.Web;
using JobPlatform.Core.Matching;
using JobPlatform.Core.Profiles;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql;
using JobPlatform.Data.Sql.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobPlatform.Api.Tests;

/// <summary>
/// Two candidates, a parked advert and a queue of questions, reachable over HTTP as either of them.
/// </summary>
/// <remarks>
/// <b>Over HTTP rather than against the handlers, unlike <c>McpToolHarness</c>, and the reason is
/// the defect this suite was written for.</b> The endpoints existed nowhere: the dashboard called
/// <c>/api/v1/questions</c> and got a 404. A test that invoked a handler directly would have
/// passed against a route nothing mapped, which is the one thing that has to fail here - so the
/// request goes through routing, the authorization policy, model binding and the serialiser, and
/// the assertions are on the JSON a browser actually receives.
///
/// <b>A test authentication scheme, because a test host cannot mint an Entra token.</b> That is
/// why <c>McpEndpointTests</c> says the tool behaviour is not tested over the transport. What can
/// be built is the principal, so a scheme is added on top of <see cref="ApiFactory"/>'s host that
/// reads a subject id off a header and issues the <c>oid</c> claim the API keys everything on.
/// <see cref="ApiFactory"/> is sealed, so it is composed through
/// <see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder"/> rather than derived from -
/// which also means the SQLite database, the stubbed Cosmos readers and the container's own
/// validation are the ones every other suite here runs against.
///
/// <b>A request with no header authenticates as nobody</b>, rather than as a default candidate.
/// The two facts worth pinning about this group are that it is per-principal and that
/// <c>Api:AllowAnonymousReads</c> does not open it, and neither is assertable against a harness
/// that signs everybody in.
/// </remarks>
internal sealed class QuestionQueueHarness : IDisposable
{
    /// <summary>The candidate whose queue this is. Their token carries this as <c>oid</c>.</summary>
    public const string Ada = "11111111-1111-1111-1111-111111111111";

    /// <summary>Another candidate, whose questions must be invisible rather than forbidden.</summary>
    public const string Grace = "22222222-2222-2222-2222-222222222222";

    /// <summary>Ada's advert: matched, parked on a missing answer, and asking two questions.</summary>
    public const long Parked = 10;

    /// <summary>An advert whose employer was never folded into a company row.</summary>
    public const long WithoutEmployer = 11;

    /// <summary>Grace's advert. Nothing of Ada's touches it.</summary>
    public const long Elsewhere = 12;

    public static readonly DateTimeOffset Asked = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly ApiFactory _factory = new() { AllowAnonymousReads = true };
    private readonly List<HttpClient> _clients = [];
    private WebApplicationFactory<Program> _host = null!;

    /// <summary>Ada's profile id, which nothing over the wire may ever carry.</summary>
    public long AdaProfile { get; private set; }

    public long GraceProfile { get; private set; }

    /// <summary>The salary question on the parked advert. Sensitive by its wording alone.</summary>
    public long SalaryQuestion { get; private set; }

    /// <summary>A second question on the same advert, so one answer does not release it.</summary>
    public long NoticeQuestion { get; private set; }

    /// <summary>A question raised with no advert behind it. It holds nothing back.</summary>
    public long LooseQuestion { get; private set; }

    /// <summary>A question on an advert with no employer row, so no company scope is available.</summary>
    public long UnfoldedQuestion { get; private set; }

    /// <summary>Grace's own question, and the id Ada must not be able to see or answer.</summary>
    public long GraceQuestion { get; private set; }

    /// <summary>The submission parked on the missing answer.</summary>
    public long ParkedSubmission { get; private set; }

    /// <summary>Contoso's row, which a company-scoped answer is filed against.</summary>
    public int Contoso { get; private set; }

    public static async Task<QuestionQueueHarness> CreateAsync()
    {
        var harness = new QuestionQueueHarness();

        harness._host = harness._factory.WithWebHostBuilder(builder => builder.ConfigureServices(
            services => services
                .AddAuthentication(HeaderPrincipalHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, HeaderPrincipalHandler>(
                    HeaderPrincipalHandler.SchemeName, _ => { })));

        await harness.SeedAsync();

        return harness;
    }

    /// <summary>A client whose every request carries that subject, or nobody's.</summary>
    public HttpClient As(string? subject)
    {
        var client = _host.CreateClient();

        if (subject is not null)
        {
            client.DefaultRequestHeaders.Add(HeaderPrincipalHandler.SubjectHeader, subject);
        }

        _clients.Add(client);

        return client;
    }

    /// <summary>
    /// A context for reading back what a write actually stored.
    /// </summary>
    /// <remarks>
    /// Round the API deliberately. What the answer route <i>returns</i> is its own account of the
    /// write, and the claim that matters most here is about a column it never projects: whether
    /// the answer was stored as the candidate's own assertion or as a client's. That has to be
    /// read from the row or not at all.
    /// </remarks>
    public JobsDbContext Database()
        => _host.Services.CreateScope().ServiceProvider.GetRequiredService<JobsDbContext>();

    /// <summary>What the apply queue would offer Ada right now.</summary>
    /// <remarks>
    /// The real predicate rather than a restatement of it. "The parked advert can come back" is
    /// the claim the whole queue rests on, and asserting it against anything but
    /// <c>ListApplyableAsync</c> would be asserting that this test knows the rule.
    /// </remarks>
    public async Task<IReadOnlyList<long>> ApplyableAsync()
    {
        using var scope = _host.Services.CreateScope();
        var matches = scope.ServiceProvider.GetRequiredService<JobMatchRepository>();

        var rows = await matches.ListApplyableAsync(AdaProfile, new ApplyableQuery());

        return [.. rows.Select(row => row.PostingId)];
    }

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        _host?.Dispose();
        _factory.Dispose();
    }

    private async Task SeedAsync()
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JobsDbContext>();

        var profiles = new CandidateProfileRepository(db);

        AdaProfile = (await profiles.SaveAsync(
            new CandidateProfile { SubjectId = Ada, FullName = "Ada Lovelace" }, TimeProvider.System)).View.Id;

        GraceProfile = (await profiles.SaveAsync(
            new CandidateProfile { SubjectId = Grace, FullName = "Grace Hopper" }, TimeProvider.System)).View.Id;

        var contoso = new CompanyEntity
        {
            CompanyKey = "contoso",
            DisplayName = "Contoso",
            FirstSeenUtc = Asked.AddDays(-30),
            LastSeenUtc = Asked,
        };

        db.Companies.Add(contoso);
        await db.SaveChangesAsync();
        Contoso = contoso.Id;

        Post(db, Parked, "Platform Engineer", "Contoso", contoso.Id);

        // No company row, so nothing can fold "Fabrikam" on the advert into an employer - which
        // is what makes the company scope unavailable rather than merely unchosen.
        Post(db, WithoutEmployer, "Data Engineer", "Fabrikam", companyId: null);
        Post(db, Elsewhere, "Somebody Else's Job", "Elsewhere", companyId: null);

        Match(db, AdaProfile, Parked);
        Match(db, GraceProfile, Elsewhere);

        await db.SaveChangesAsync();

        var questions = new OpenQuestionRepository(db);

        // Raised with the flag off on purpose: the wording is what marks it, and a queue that
        // showed this one without a confirmation would be reading the column instead.
        SalaryQuestion = (await questions.OpenAsync(
            AdaProfile, "What are your salary expectations?", ["Under 60k", "60-80k", "80k+"],
            sensitive: false, Parked, runId: null, Asked)).Row.Id;

        NoticeQuestion = (await questions.OpenAsync(
            AdaProfile, "What is your notice period?", options: null,
            sensitive: false, Parked, runId: null, Asked.AddMinutes(1))).Row.Id;

        LooseQuestion = (await questions.OpenAsync(
            AdaProfile, "Which office would you rather be based in?", options: null,
            sensitive: false, postingId: null, runId: null, Asked.AddMinutes(2))).Row.Id;

        UnfoldedQuestion = (await questions.OpenAsync(
            AdaProfile, "How many years of Kafka do you have?", options: null,
            sensitive: false, WithoutEmployer, runId: null, Asked.AddMinutes(3))).Row.Id;

        GraceQuestion = (await questions.OpenAsync(
            GraceProfile, "Do you require sponsorship?", options: null,
            sensitive: true, Elsewhere, runId: null, Asked.AddMinutes(4))).Row.Id;

        var submissions = new SubmissionRepository(db);

        ParkedSubmission = (await submissions.ParkAsync(
            AdaProfile, Parked, ParkReason.MissingAnswer, Asked,
            "https://jobs.contoso.invalid/platform")).Row.Id;
    }

    private static void Post(JobsDbContext db, long id, string title, string company, int? companyId)
        => db.JobPostings.Add(new JobPostingEntity
        {
            Id = id,
            SourceKey = $"linkedin:{id}",
            Site = "linkedin",
            ExternalId = id.ToString(),
            ContentHash = new string((char)('a' + (id % 20)), 64),
            Title = title,
            Company = company,
            CompanyId = companyId,
            Description = "A job, described.",
            LocationCity = "London",
            LocationRaw = "London, UK",
            JobUrl = $"https://www.linkedin.com/jobs/view/{id}",
            JobUrlDirect = $"https://jobs.example.invalid/{id}",
            FirstSeenUtc = Asked.AddDays(-7),
            LastSeenUtc = Asked,
        });

    /// <summary>A judged match, because the apply queue gates on the verdict rather than on a score.</summary>
    private static void Match(JobsDbContext db, long profileId, long postingId)
        => db.JobMatches.Add(new JobMatchEntity
        {
            ProfileId = profileId,
            PostingId = postingId,
            Score = 70,
            RankScore = 8,
            ScoredAtUtc = Asked.AddDays(-1),
            Verdict = CandidacyVerdict.Strong,
            AssessmentScore = 85,
            AssessedAtUtc = Asked.AddDays(-1),
            ScorerVersion = MatchResult.CurrentVersion,
        });

    /// <summary>
    /// The principal a test acts as, taken from a header.
    /// </summary>
    /// <remarks>
    /// It issues <c>oid</c> and nothing else, which is exactly what <c>CallerIdentity</c> reads
    /// and deliberately never falls back from. A handler that also issued
    /// <c>ClaimTypes.NameIdentifier</c> would let a route resolving the caller the wrong way pass
    /// here and fail against a real token.
    ///
    /// <see cref="AuthenticateResult.NoResult"/> for a request with no header, so an anonymous
    /// request is anonymous rather than a caller with an empty subject: the difference is a 401
    /// from the policy against a 401 from the handler, and only one of those pins the policy.
    /// </remarks>
    private sealed class HeaderPrincipalHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TestPrincipal";

        public const string SubjectHeader = "X-Test-Subject";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var subject = Request.Headers[SubjectHeader].ToString();

            if (string.IsNullOrWhiteSpace(subject))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim("oid", subject)], SchemeName);

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
