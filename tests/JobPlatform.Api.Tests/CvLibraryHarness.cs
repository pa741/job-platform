using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using JobPlatform.Ai.Extraction;
using JobPlatform.Core.Applications;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Profiles;
using JobPlatform.Data.Applications;
using JobPlatform.Data.Sql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobPlatform.Api.Tests;

/// <summary>
/// Two candidates, a small CV library and a profile that has moved since it was written.
/// </summary>
/// <remarks>
/// <b>Over HTTP rather than against the handlers</b>, for the reason <c>QuestionQueueHarness</c>
/// gives: a test that invokes a handler directly passes against a route nothing mapped, and "the
/// group is registered" is one of the two things a feature that is a folder plus one line can get
/// wrong. So requests go through routing, the authorization policy, model binding and the
/// serialiser, and the assertions are on the JSON a browser receives.
///
/// <b>The renderer is real and only the store is a stub, which is the arrangement that makes the
/// interesting claims assertable at all.</b> No suite here reaches Azure - deliberately, so a fresh
/// clone runs green with no credential - and what is worth pinning about a save is on the far side
/// of an upload: that a variant becomes sendable in the same request the person saved it in, that a
/// re-authored variant stops being sendable until it is rendered again, and that the filename
/// carries no trace of which CV this is. <see cref="RecordingVariantStore"/> is the same shape the
/// renderer's own suite uses.
///
/// <b>Rendering and extraction can be turned off, because a deployment without them is the shape
/// this system ships in.</b> No <c>ApplicationPacks:serviceUri</c> registers no renderer and no AI
/// provider registers no extractor; a library on such a host is text that saves, is editable, and
/// is never selectable. That is a supported state rather than a degraded one, and a harness that
/// could not produce it would leave it untested.
///
/// <b>A request with no header authenticates as nobody</b>, rather than as a default candidate. The
/// two facts worth pinning about this group are that it is per-principal and that
/// <c>Api:AllowAnonymousReads</c> does not open it, and neither is assertable against a harness that
/// signs everybody in.
/// </remarks>
internal sealed class CvLibraryHarness : IDisposable
{
    /// <summary>The candidate whose library this is. Their token carries this as <c>oid</c>.</summary>
    public const string Ada = "11111111-1111-1111-1111-111111111111";

    /// <summary>Another candidate, whose CVs must be invisible rather than forbidden.</summary>
    public const string Grace = "22222222-2222-2222-2222-222222222222";

    /// <summary>A principal with no profile at all, which is the ordinary first visit.</summary>
    public const string Stranger = "99999999-9999-9999-9999-999999999999";

    /// <summary>When the seeded variants were written. Before the profile last moved.</summary>
    public static readonly DateTimeOffset Written = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>When the seeded render was produced.</summary>
    public static readonly DateTimeOffset Rendered = new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// When the profiles last moved, which is the whole of what staleness is measured against.
    /// </summary>
    /// <remarks>
    /// Stamped through a fixed clock rather than taken from the wall, so "this CV predates the last
    /// profile change" is arithmetic between two known instants. A variant written by a test over
    /// HTTP is authored from the host's own <c>TimeProvider.System</c> and is therefore after this,
    /// which is what makes "a CV you just wrote is not out of date" a real assertion rather than a
    /// restatement of the seed.
    /// </remarks>
    public static readonly DateTimeOffset ProfileChanged = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A short, real CV. Long enough to render, short enough to read in a diff.</summary>
    public const string Cv = """
        # Ada Lovelace

        Platform engineer. Kubernetes, Terraform, .NET.

        ## Experience

        - Built a job-market pipeline on Azure.
        - Ran the platform two teams deployed to.
        """;

    private readonly ApiFactory _factory = new() { AllowAnonymousReads = true };
    private readonly List<HttpClient> _clients = [];
    private WebApplicationFactory<Program> _host = null!;

    /// <summary>Ada's profile id, which nothing over the wire may ever carry.</summary>
    public long AdaProfile { get; private set; }

    public long GraceProfile { get; private set; }

    /// <summary>Ada's rendered, sendable CV. Written before her profile last moved.</summary>
    public long Backend { get; private set; }

    /// <summary>A CV Ada has retired. Still readable, never selectable, and not counted as stale.</summary>
    public long Retired { get; private set; }

    /// <summary>Grace's CV, and the id Ada must not be able to read, rename or archive.</summary>
    public long Hers { get; private set; }

    /// <summary>Every file the renderer uploaded, in the order it uploaded them.</summary>
    public RecordingVariantStore Files { get; } = new();

    /// <summary>Every markdown the extractor was handed, and what it answered.</summary>
    public StubVariantExtractor Extractor { get; } = new();

    public static async Task<CvLibraryHarness> CreateAsync(bool rendering = true)
    {
        var harness = new CvLibraryHarness();

        harness._host = harness._factory.WithWebHostBuilder(builder => builder.ConfigureServices(
            services =>
            {
                services
                    .AddAuthentication(HeaderPrincipalHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, HeaderPrincipalHandler>(
                        HeaderPrincipalHandler.SchemeName, _ => { });

                if (!rendering)
                {
                    return;
                }

                // Registered here rather than by configuring a storage account, because
                // AddApplicationPacks builds a BlobServiceClient from the URI it is given and the
                // renderer is the only part of that graph these tests are about. The renderer
                // itself is the real one: what is stubbed is the upload.
                services.AddSingleton<ICvVariantFileStore>(harness.Files);
                services.AddSingleton<CvVariantRenderer>();
                services.AddSingleton<ICvVariantExtractor>(harness.Extractor);
            }));

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
    /// Round the API deliberately. The claim that matters most in this feature is about a table the
    /// routes never project: that a variant's concepts landed in <c>CvVariantConcepts</c> and that
    /// <c>ProfileConcepts</c> was not touched. That has to be read from the rows or not at all.
    /// </remarks>
    public JobsDbContext Database()
        => _host.Services.CreateScope().ServiceProvider.GetRequiredService<JobsDbContext>();

    /// <summary>
    /// Writes a variant straight through the repository, at whatever age the test needs.
    /// </summary>
    /// <remarks>
    /// The routes cannot produce an old variant - they author as of now, correctly - so the seeded
    /// half of every staleness and cap assertion comes through here. It is the same repository the
    /// endpoints use, so a seeded row is shaped exactly as a saved one.
    /// </remarks>
    public async Task<long> SeedVariantAsync(
        long profileId,
        string label,
        string markdown = Cv,
        DateTimeOffset? authoredAtUtc = null,
        bool rendered = false,
        bool archived = false)
    {
        using var scope = _host.Services.CreateScope();
        var variants = new CvVariantRepository(scope.ServiceProvider.GetRequiredService<JobsDbContext>());

        var written = await variants.CreateAsync(
            profileId, label, markdown, authoredAtUtc ?? Written);

        var id = written.Variant!.Id;

        if (rendered)
        {
            await variants.RecordRenderAsync(profileId, id, new RenderedVariant
            {
                PdfBlobPath = $"profile-cvs/{profileId}/{id}/Ada_Lovelace_Curriculum_Vitae.pdf",
                DocxBlobPath = $"profile-cvs/{profileId}/{id}/Ada_Lovelace_Curriculum_Vitae.docx",
                Sha256 = new string('a', 64),
                RenderedAtUtc = Rendered,
            });
        }

        if (archived)
        {
            await variants.ArchiveAsync(profileId, id);
        }

        return id;
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
        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JobsDbContext>();
            var profiles = new CandidateProfileRepository(db);
            var clock = new FixedTime(ProfileChanged);

            AdaProfile = (await profiles.SaveAsync(
                new CandidateProfile { SubjectId = Ada, FullName = "Ada Lovelace" }, clock)).View.Id;

            GraceProfile = (await profiles.SaveAsync(
                new CandidateProfile { SubjectId = Grace, FullName = "Grace Hopper" }, clock)).View.Id;
        }

        Backend = await SeedVariantAsync(AdaProfile, "Backend .NET", rendered: true);
        Retired = await SeedVariantAsync(AdaProfile, "Retired platform CV", archived: true);
        Hers = await SeedVariantAsync(GraceProfile, "Compilers");
    }

    /// <summary>
    /// The principal a test acts as, taken from a header.
    /// </summary>
    /// <remarks>
    /// It issues <c>oid</c> and nothing else, which is exactly what <c>CallerIdentity</c> reads and
    /// deliberately never falls back from. A handler that also issued
    /// <c>ClaimTypes.NameIdentifier</c> would let a route resolving the caller the wrong way pass
    /// here and fail against a real token.
    ///
    /// <see cref="AuthenticateResult.NoResult"/> for a request with no header, so an anonymous
    /// request is anonymous rather than a caller with an empty subject: the difference is a 401 from
    /// the policy against a 401 from the handler, and only one of those pins the policy.
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

    /// <summary>A clock that does not move, so a seeded timestamp is a constant a test can name.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// A store that keeps what it was handed and builds the path the real one would.
    /// </summary>
    /// <remarks>
    /// The path comes from <c>ApplicationPackFile.VariantBlobPath</c> rather than from a literal,
    /// because what is worth asserting is that the renderer passed the right profile, variant,
    /// name and format - not that a test can spell a path. The container name is the default one,
    /// which is what the deployment sets.
    /// </remarks>
    internal sealed class RecordingVariantStore : ICvVariantFileStore
    {
        public List<VariantFileRequest> Requests { get; } = [];

        /// <summary>The format this store answers null for, as a storage failure would.</summary>
        public PackFormat? Refuse { get; set; }

        public Task<StoredPackFile?> StoreVariantAsync(
            VariantFileRequest file, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(file);

            Requests.Add(file);

            if (Refuse == file.Format)
            {
                return Task.FromResult<StoredPackFile?>(null);
            }

            var path = ApplicationPackFile.VariantBlobPath(
                "profile-cvs", file.ProfileId, file.VariantId, file.CandidateName, file.Format);

            return Task.FromResult<StoredPackFile?>(new StoredPackFile
            {
                BlobPath = path,
                FileName = path[(path.LastIndexOf('/') + 1)..],
                ContentType = ApplicationPackFile.ContentType(file.Format),
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(file.Content)),
                Length = file.Content.Length,
            });
        }
    }

    /// <summary>
    /// An extractor that records what it was asked and answers with keys the vocabulary knows.
    /// </summary>
    /// <remarks>
    /// <b>Real concept keys, because the repository drops the ones the graph does not know.</b> A
    /// stub answering "cv.backend" would store nothing and every assertion about a variant's
    /// concepts would pass vacuously against an empty table - which is the shape of failure this
    /// feature is least able to notice, since a concept missing from a set is nothing anybody sees.
    ///
    /// It also carries what it was handed, so the claim that the model reads the candidate's words and
    /// never writes them can be made against the request rather than inferred from the absence of a
    /// route.
    /// </remarks>
    internal sealed class StubVariantExtractor : ICvVariantExtractor
    {
        public List<CvVariantExtractionRequest> Requests { get; } = [];

        /// <summary>What the next call answers. Null stands for a provider failure.</summary>
        public CvVariantExtraction? Answer { get; set; } = new()
        {
            Concepts =
            [
                new ConceptAssertion("skill.kubernetes", AssertionSource.Model, AssertionPolarity.Required),
                new ConceptAssertion("skill.csharp", AssertionSource.Model, AssertionPolarity.Mentioned),
            ],
        };

        public Task<CvVariantExtraction?> ExtractAsync(
            CvVariantExtractionRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);

            return Task.FromResult(Answer);
        }
    }
}
