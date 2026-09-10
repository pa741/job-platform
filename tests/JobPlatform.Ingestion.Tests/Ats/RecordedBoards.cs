using System.Net;
using System.Text;

namespace JobPlatform.Ingestion.Tests.Ats;

/// <summary>
/// The four board shapes, recorded rather than reached for.
/// </summary>
/// <remarks>
/// <b>Nothing in this test project touches the network, and that is not only about determinism.</b>
/// These endpoints are a service to their vendors' paying customers and their applicants; a test
/// suite that hit them would put a request on somebody else's rate limiter every time anybody typed
/// <c>dotnet test</c>, which is the opposite of the courtesy the whole feature is written around.
/// It is also what <c>ATS-ENDPOINTS.md</c> asks for in as many words: the shapes are settled, and
/// re-deriving them by probing is a cost to somebody else.
///
/// <b>The payloads are the verified shapes, trimmed to what a reader reads plus enough of what it
/// ignores to prove it ignores it.</b> The employers and tokens are the ones the 2026-09-07
/// verification actually used - <c>cloudflare</c>, <c>sampura</c>, <c>apolloresearch</c>,
/// <c>BlueOptima</c> - so a shape here is traceable to a real check rather than to somebody's idea
/// of what a board looks like. There is no personal data in any of them: a public job advert's
/// title, place and apply URL is the whole content of a board listing.
/// </remarks>
internal static class RecordedBoards
{
    /// <summary>
    /// Greenhouse: <c>boards-api.greenhouse.io/v1/boards/cloudflare/jobs</c>.
    /// </summary>
    /// <remarks>
    /// Five entries, three of which survive. The second is the shape that matters most in this
    /// corpus - Greenhouse embedded under the employer's own domain, where <c>absolute_url</c> is
    /// not a greenhouse.io address at all and a reader checking the host against the vendor's would
    /// throw away 1,259 of 2,006 direct apply URLs. The fourth carries no <c>absolute_url</c> and
    /// the fifth carries something that is not a web address; both are listings nobody can apply
    /// through.
    /// </remarks>
    public const string GreenhouseBoard = """
        {
          "jobs": [
            {
              "id": 6304903,
              "internal_job_id": 4012345,
              "title": "VoidZero Engineer",
              "updated_at": "2026-09-06T11:04:12-04:00",
              "requisition_id": "JR-2026-1188",
              "location": { "name": "London, United Kingdom" },
              "absolute_url": "https://boards.greenhouse.io/cloudflare/jobs/6304903",
              "metadata": []
            },
            {
              "id": 6304904,
              "title": "Senior Software Engineer, Platform",
              "location": { "name": "Remote (UK)" },
              "absolute_url": "https://careers.example-employer.com/jobs?gh_jid=6304904"
            },
            {
              "id": 6304905,
              "title": "Systems Engineer",
              "location": null,
              "absolute_url": "https://boards.greenhouse.io/cloudflare/jobs/6304905"
            },
            {
              "id": 6304906,
              "title": "Engineering Manager",
              "location": { "name": "Austin, TX" }
            },
            {
              "id": 6304907,
              "title": "Support Engineer",
              "location": { "name": "Lisbon, Portugal" },
              "absolute_url": "javascript:void(0)"
            }
          ],
          "meta": { "total": 5 }
        }
        """;

    /// <summary>A Greenhouse board belonging to an employer with nothing open today.</summary>
    public const string GreenhouseEmptyBoard = """
        { "jobs": [], "meta": { "total": 0 } }
        """;

    /// <summary>
    /// Well-formed JSON that is not a board. Distinct from a body that is not JSON at all.
    /// </summary>
    public const string NotABoardShape = """
        { "message": "Service temporarily unavailable", "status": 503 }
        """;

    /// <summary>A body that will not parse. An HTML error page under a 200 reads like this.</summary>
    public const string MalformedBody = "<html><head><title>502 Bad Gateway</title></head></html>";

    /// <summary>
    /// Ashby: <c>api.ashbyhq.com/posting-api/job-board/sampura</c>.
    /// </summary>
    /// <remarks>
    /// The second entry carries <c>jobUrl</c> and no <c>applyUrl</c>, which is what the ordered
    /// fallback exists for: the advert page with the form one click behind it is a weaker answer
    /// than the form, and a far better one than no link.
    /// </remarks>
    public const string AshbyBoard = """
        {
          "apiVersion": "1",
          "jobs": [
            {
              "id": "1f2a4c66-0b1d-4c2a-9f77-6b0f2f9a1c31",
              "title": "Senior Software Engineer",
              "department": "Engineering",
              "team": "Platform",
              "employmentType": "FullTime",
              "location": "London, United Kingdom",
              "isRemote": false,
              "publishedAt": "2026-09-01T09:12:00.000Z",
              "jobUrl": "https://jobs.ashbyhq.com/sampura/1f2a4c66-0b1d-4c2a-9f77-6b0f2f9a1c31",
              "applyUrl": "https://jobs.ashbyhq.com/sampura/1f2a4c66-0b1d-4c2a-9f77-6b0f2f9a1c31/application"
            },
            {
              "id": "8c9d0e11-2233-4455-6677-889900aabbcc",
              "title": "Data Engineer",
              "location": "Remote (UK)",
              "isRemote": true,
              "jobUrl": "https://jobs.ashbyhq.com/sampura/8c9d0e11-2233-4455-6677-889900aabbcc"
            }
          ]
        }
        """;

    /// <summary>
    /// Lever: <c>api.lever.co/v0/postings/apolloresearch?mode=json</c>.
    /// </summary>
    /// <remarks>
    /// A bare array, a title in <c>text</c> and a place in <c>categories.location</c> - three ways
    /// this vendor differs from its neighbours, and the reason the mapping belongs in the Ingestion
    /// layer rather than anywhere a matcher can see it.
    /// </remarks>
    public const string LeverBoard = """
        [
          {
            "id": "4b1c2d3e-5f60-4718-9a2b-3c4d5e6f7081",
            "text": "Research Engineer",
            "categories": {
              "commitment": "Full-time",
              "department": "Research",
              "location": "London, UK",
              "team": "Evaluations"
            },
            "workplaceType": "hybrid",
            "createdAt": 1756900000000,
            "hostedUrl": "https://jobs.lever.co/apolloresearch/4b1c2d3e-5f60-4718-9a2b-3c4d5e6f7081",
            "applyUrl": "https://jobs.lever.co/apolloresearch/4b1c2d3e-5f60-4718-9a2b-3c4d5e6f7081/apply"
          },
          {
            "id": "90a1b2c3-d4e5-4f60-8172-93a4b5c6d7e8",
            "text": "Operations Lead",
            "categories": { "commitment": "Full-time", "location": "Remote" },
            "hostedUrl": "https://jobs.lever.co/apolloresearch/90a1b2c3-d4e5-4f60-8172-93a4b5c6d7e8"
          }
        ]
        """;

    /// <summary>
    /// Lever's own "no such board", which <c>ATS-ENDPOINTS.md</c> records as a well-formed body
    /// rather than an empty list.
    /// </summary>
    public const string LeverDocumentNotFound = """
        { "ok": false, "error": "Document not found" }
        """;

    /// <summary>
    /// SmartRecruiters: <c>api.smartrecruiters.com/v1/companies/BlueOptima/postings</c>.
    /// </summary>
    /// <remarks>
    /// The three rows are the three cases: an apply URL and a posting URL together, a posting URL
    /// alone beside a remote-only place, and a row offering nothing but <c>ref</c> - which is an
    /// <c>api.smartrecruiters.com</c> address and must never be handed to somebody as a form.
    /// </remarks>
    public const string SmartRecruitersBoard = """
        {
          "offset": 0,
          "limit": 100,
          "totalFound": 3,
          "content": [
            {
              "id": "744000037654321",
              "name": "Senior Software Engineer",
              "uuid": "0d3b2b0e-1f44-4a3d-9b77-6c2f0a1b2c3d",
              "refNumber": "REF11223X",
              "company": { "identifier": "BlueOptima", "name": "BlueOptima" },
              "releasedDate": "2026-08-19T00:00:00.000Z",
              "location": { "city": "London", "region": "England", "country": "uk", "remote": false },
              "ref": "https://api.smartrecruiters.com/v1/companies/BlueOptima/postings/744000037654321",
              "postingUrl": "https://jobs.smartrecruiters.com/BlueOptima/744000037654321-senior-software-engineer",
              "applyUrl": "https://jobs.smartrecruiters.com/BlueOptima/744000037654321-senior-software-engineer?oga=true"
            },
            {
              "id": "744000037654322",
              "name": "Data Scientist",
              "company": { "identifier": "BlueOptima", "name": "BlueOptima" },
              "location": { "remote": true },
              "ref": "https://api.smartrecruiters.com/v1/companies/BlueOptima/postings/744000037654322",
              "postingUrl": "https://jobs.smartrecruiters.com/BlueOptima/744000037654322-data-scientist"
            },
            {
              "id": "744000037654323",
              "name": "Delivery Manager",
              "company": { "identifier": "BlueOptima", "name": "BlueOptima" },
              "location": { "city": "Bengaluru", "country": "in", "remote": false },
              "ref": "https://api.smartrecruiters.com/v1/companies/BlueOptima/postings/744000037654323"
            }
          ]
        }
        """;

    /// <summary>
    /// One SmartRecruiters page, built to order, so a two-page board can be asserted without a
    /// two-hundred-entry literal.
    /// </summary>
    /// <remarks>
    /// The titles are numbered from the offset, which is what lets a test show that the second
    /// request's rows arrived rather than the first request's a second time - the failure the
    /// echoed offset check exists to catch.
    /// </remarks>
    public static string SmartRecruitersPage(int offset, int rows, int totalFound)
    {
        var body = new StringBuilder();

        body.Append(
            $$"""{"offset":{{offset}},"limit":100,"totalFound":{{totalFound}},"content":[""");

        for (var index = 0; index < rows; index++)
        {
            var id = offset + index;

            if (index > 0)
            {
                body.Append(',');
            }

            body.Append(
                $$"""
                {"id":"{{id}}","name":"Engineer {{id}}",
                 "company":{"name":"BlueOptima"},
                 "location":{"city":"London","country":"uk"},
                 "applyUrl":"https://jobs.smartrecruiters.com/BlueOptima/{{id}}-engineer"}
                """);
        }

        body.Append("]}");

        return body.ToString();
    }

    /// <summary>A 200 carrying one of the bodies above.</summary>
    public static HttpResponseMessage Ok(string body) => Answer(HttpStatusCode.OK, body);

    /// <summary>A status with no body worth reading. A 404 is the ordinary one.</summary>
    public static HttpResponseMessage Status(HttpStatusCode status) => Answer(status, "");

    private static HttpResponseMessage Answer(HttpStatusCode status, string body)
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
}

/// <summary>
/// The careers-page shapes, written from the embeds the vendors document rather than scraped.
/// </summary>
/// <remarks>
/// <b>Nothing in this test project touches the network, for the reason <see cref="RecordedBoards"/>
/// gives and one more besides.</b> These are not vendors' endpoints, they are employers' own web
/// servers - the hosts this feature is most careful with, because they publish nothing for us and
/// have agreed to nothing. A suite that fetched a real one would be doing on every
/// <c>dotnet test</c> exactly what the pass bounds itself to twenty-five of a night.
///
/// <b>The employers are invented and the markup is not.</b> The shapes are the ones Greenhouse and
/// Ashby document for embedding a board under an employer's own domain: a script tag pointing at
/// the vendor's embed host with the board named in <c>for=</c>, per-vacancy links carrying
/// <c>gh_jid</c> under the employer's domain, and a "see every opening" link to the board itself.
/// The hosts use <c>.example</c>, which is reserved and resolves nowhere, so a payload that
/// escaped into a live pass could not reach anybody.
///
/// <b>The <c>gh_jid</c> links are the point of the first fixture and they carry no token.</b>
/// <c>AtsBoardToken.FromUrl</c> answers null for an embed under the employer's own domain and says
/// why; what those links establish is the vendor, and the board link two lines further down is
/// where the token is. A fixture with only one of the two would pass while asserting half the rule.
/// </remarks>
internal static class RecordedCareersPages
{
    /// <summary>An employer running a Greenhouse embed, with the board named beside it.</summary>
    public const string GreenhouseEmbed = """
        <!doctype html>
        <html lang="en">
          <head>
            <title>Careers at Acme Robotics</title>
            <script src="https://boards.greenhouse.io/embed/job_board/js?for=acmerobotics"></script>
          </head>
          <body>
            <a href="https://www.linkedin.com/company/acme-robotics">Follow us on LinkedIn</a>
            <ul id="grnhse_app">
              <li><a href="/careers/opening?gh_jid=6304904">Senior Software Engineer, Platform</a></li>
              <li><a href="https://careers.acmerobotics.example/jobs?gh_jid=6304905">Systems Engineer</a></li>
            </ul>
            <a href="https://job-boards.greenhouse.io/acmerobotics">See every opening</a>
          </body>
        </html>
        """;

    /// <summary>The same embed with no board link: the vendor is proved and the token is not.</summary>
    public const string GreenhouseEmbedWithoutTheBoard = """
        <!doctype html>
        <html lang="en">
          <head><title>Careers at Acme Robotics</title></head>
          <body>
            <ul>
              <li><a href="https://careers.acmerobotics.example/jobs?gh_jid=6304904">Senior Software Engineer</a></li>
              <li><a href="https://careers.acmerobotics.example/jobs?gh_jid=6304905">Systems Engineer</a></li>
            </ul>
          </body>
        </html>
        """;

    /// <summary>A page with no applicant tracking system anywhere on it.</summary>
    public const string NoBoardAtAll = """
        <!doctype html>
        <html lang="en">
          <head><title>Work with us</title></head>
          <body>
            <p>Send a CV to <a href="mailto:jobs@acmerobotics.example">jobs@acmerobotics.example</a>.</p>
            <a href="https://www.linkedin.com/company/acme-robotics">LinkedIn</a>
          </body>
        </html>
        """;

    /// <summary>An agency's page: their own embed, and a client's board beside it.</summary>
    /// <remarks>
    /// The failure this whole phase has to refuse, and the reason two vendors on one page is an
    /// abstention. The employers holding the most link-less applyable postings in this corpus are
    /// recruitment agencies advertising a client's vacancy under their own name, so a page that
    /// links to somebody else's board is the shape to expect rather than an edge case.
    /// </remarks>
    public const string TwoVendorsOnOnePage = """
        <!doctype html>
        <html lang="en">
          <body>
            <a href="https://careers.acmerobotics.example/jobs?gh_jid=6304904">Our own vacancies</a>
            <a href="https://jobs.lever.co/anotherco/2f1c9d7e">A role with one of our clients</a>
          </body>
        </html>
        """;

    /// <summary>One vendor, two boards: a parent and a subsidiary on one careers page.</summary>
    public const string TwoBoardsOnOnePage = """
        <!doctype html>
        <html lang="en">
          <body>
            <a href="https://boards.greenhouse.io/acmerobotics">Engineering roles</a>
            <a href="https://boards.greenhouse.io/acmelabs">Research roles</a>
          </body>
        </html>
        """;

    /// <summary>A 200 carrying one of the pages above.</summary>
    public static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html"),
        };

    /// <summary>A 200 whose body is not a document to read addresses out of.</summary>
    public static HttpResponseMessage NotADocument()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent("%PDF-1.7", Encoding.UTF8, "application/pdf"),
        };

    /// <summary>A status with no page behind it. Every one of them is <c>Unavailable</c> here.</summary>
    /// <remarks>
    /// Unlike a probed board token, where a 404 <i>is</i> the answer, a 404 from a careers page
    /// means no page was seen at all - which says nothing about whether that employer has a board.
    /// </remarks>
    public static HttpResponseMessage Status(HttpStatusCode status)
        => new(status)
        {
            Content = new StringContent("", Encoding.UTF8, "text/html"),
        };
}
