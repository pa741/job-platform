using JobPlatform.Core.Applications;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// A CV the candidate wrote, and the rules about a library of them.
/// </summary>
/// <remarks>
/// <b>What is asserted here is what a document reaching an employer depends on.</b> Three of these
/// cases are the ones the remarks on <see cref="CvVariant"/> make a claim about and nothing else
/// would catch: an archived variant that keeps the file a past application uploaded, a render
/// stamped before the text it came from, and a library that is out of date but still sendable. The
/// rest pin the boundaries - an empty library, one that is entirely archived, and the cap.
///
/// Pure throughout, which is the point of the type: no database, no clock, no renderer.
/// </remarks>
public sealed class CvVariantTests
{
    private static readonly DateTimeOffset Authored = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_trims_the_label_and_starts_a_variant_unrendered_and_in_use()
    {
        var variant = CvVariant.Create("  Backend .NET  ", "# Pablo\n\nBackend engineer.", Authored);

        Assert.Equal("Backend .NET", variant.Label);
        Assert.Equal(Authored, variant.AuthoredAtUtc);
        Assert.False(variant.IsArchived);
        Assert.False(variant.IsRendered);
        Assert.False(variant.IsSendable);
        Assert.Equal(0, variant.Id);
    }

    /// <summary>
    /// A blank CV is refused rather than stored empty.
    /// </summary>
    /// <remarks>
    /// Not a validation nicety: a blank variant renders to a blank PDF, and a blank PDF is a file
    /// the browser loop will happily upload to an employer. Nothing downstream would notice - the
    /// pack has a URL, the form has a file, and the application is made.
    /// </remarks>
    [Fact]
    public void Create_refuses_a_blank_cv_because_a_blank_pdf_can_still_be_uploaded()
        => Assert.Throws<ArgumentException>(() => CvVariant.Create("Backend .NET", "   ", Authored));

    [Fact]
    public void Create_refuses_a_label_with_nothing_in_it_a_person_could_read()
        => Assert.Throws<ArgumentException>(() => CvVariant.Create(" - ", "# Pablo", Authored));

    /// <summary>
    /// Past the bound the save is refused, never trimmed to fit.
    /// </summary>
    /// <remarks>
    /// A CV cut at the bound ends mid-sentence in front of a recruiter, and the person who pasted
    /// the wrong thing in never finds out they did. Refusing is the only answer that leaves them
    /// able to fix it.
    /// </remarks>
    [Fact]
    public void Create_refuses_markdown_past_the_bound_rather_than_truncating_it()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => CvVariant.Create(
                "Backend .NET",
                new string('x', CvVariantLimits.MaxMarkdownLength + 1),
                Authored));

    [Fact]
    public void Create_refuses_a_label_past_the_bound_rather_than_truncating_it()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => CvVariant.Create(
                new string('x', CvVariantLimits.MaxLabelLength + 1),
                "# Pablo",
                Authored));

    /// <summary>
    /// There is no minimum length, and a two-line CV is accepted.
    /// </summary>
    /// <remarks>
    /// A short CV is a bad CV rather than an invalid one, and this system has no business telling
    /// somebody their own document is too thin. Only blankness is refused, because only blankness
    /// produces a file that cannot be read at all.
    /// </remarks>
    [Fact]
    public void Create_keeps_a_short_cv_because_there_is_no_minimum_length()
    {
        var variant = CvVariant.Create("One pager", "# Pablo\n\nBackend.", Authored);

        Assert.Equal("# Pablo\n\nBackend.", variant.Markdown);
    }

    [Fact]
    public void A_rendered_variant_that_is_not_archived_is_sendable()
    {
        var variant = Rendered();

        Assert.True(variant.IsRendered);
        Assert.True(variant.IsRenderCurrent);
        Assert.True(variant.IsSendable);
    }

    /// <summary>
    /// Archiving takes a variant out of selection and takes nothing else away from it.
    /// </summary>
    /// <remarks>
    /// The claim the design rests on: an application made last year has to stay explicable, and the
    /// submission that records it names this variant. If archiving cleared the paths or the hash,
    /// "what exactly did we send them" would have no answer for every CV the candidate has since
    /// retired - which is most of them, eventually.
    /// </remarks>
    [Fact]
    public void An_archived_variant_is_not_sendable_and_keeps_the_file_it_was_sent_as()
    {
        var archived = Rendered() with { IsArchived = true };

        Assert.False(archived.IsSendable);
        Assert.True(archived.IsRenderCurrent);
        Assert.NotNull(archived.PdfBlobPath);
        Assert.NotNull(archived.DocxBlobPath);
        Assert.NotNull(archived.Sha256);
    }

    /// <summary>
    /// A file rendered before the text it came from is read as not current.
    /// </summary>
    /// <remarks>
    /// The clock problem, and the decision is that an inversion means the files are not known to be
    /// this text. Two hosts disagreeing by seconds and a genuinely reordered write are
    /// indistinguishable from here, so the reading is chosen by what each mistake costs: sending an
    /// employer a paragraph the candidate deleted is not recoverable, and re-rendering is free.
    /// </remarks>
    [Fact]
    public void A_variant_rendered_before_it_was_authored_is_not_sendable_until_it_is_rendered_again()
    {
        var inverted = Rendered(renderedAtUtc: Authored.AddSeconds(-2));

        Assert.True(inverted.IsRendered);
        Assert.False(inverted.IsRenderCurrent);
        Assert.False(inverted.IsSendable);

        var rerendered = inverted with { RenderedAtUtc = Authored.AddMinutes(5) };

        Assert.True(rerendered.IsSendable);
    }

    /// <summary>
    /// The boundary is inclusive, so a render stamped at the authoring instant counts.
    /// </summary>
    /// <remarks>
    /// A save and a render inside one transaction can carry the same timestamp, and a strict
    /// comparison would make that ordinary case permanently unsendable - a variant nothing could
    /// ever fix, because re-rendering would reproduce it.
    /// </remarks>
    [Fact]
    public void A_variant_rendered_at_the_instant_it_was_authored_is_sendable()
        => Assert.True(Rendered(renderedAtUtc: Authored).IsSendable);

    /// <summary>
    /// A timestamp with no file behind it does not make a variant sendable.
    /// </summary>
    /// <remarks>
    /// Choosing one produces a pack with no URL, which the browser loop discovers at the upload box
    /// - after the tab is open, which is the late discovery <c>SubmissionQuota</c> exists to prevent
    /// on the other side of the same run.
    /// </remarks>
    [Fact]
    public void A_variant_with_no_pdf_is_not_sendable_however_recently_it_was_stamped()
    {
        var stamped = Rendered() with { PdfBlobPath = null };

        Assert.False(stamped.IsRendered);
        Assert.False(stamped.IsSendable);
    }

    /// <summary>
    /// A missing DOCX narrows where a variant can apply; it does not stop it applying anywhere.
    /// </summary>
    [Fact]
    public void A_variant_with_no_docx_is_still_sendable()
        => Assert.True((Rendered() with { DocxBlobPath = null }).IsSendable);

    [Fact]
    public void WithMarkdown_moves_the_authored_time_and_leaves_the_render_behind()
    {
        var edited = Rendered().WithMarkdown("# Pablo\n\nNow with Kubernetes.", Authored.AddDays(3));

        Assert.Equal("# Pablo\n\nNow with Kubernetes.", edited.Markdown);
        Assert.Equal(Authored.AddDays(3), edited.AuthoredAtUtc);
        Assert.False(edited.IsRenderCurrent);
        Assert.False(edited.IsSendable);
    }

    /// <summary>
    /// An edit does not blank the pointers to the file a previous application uploaded.
    /// </summary>
    /// <remarks>
    /// The variant leaves selection through the timestamps instead, which is reversible by
    /// re-rendering. Clearing the paths would tidy the row at the cost of making an application
    /// already made unexplainable.
    /// </remarks>
    [Fact]
    public void WithMarkdown_keeps_the_paths_so_the_file_already_sent_stays_addressable()
    {
        var original = Rendered();

        var edited = original.WithMarkdown("# Pablo\n\nRewritten.", Authored.AddDays(1));

        Assert.Equal(original.PdfBlobPath, edited.PdfBlobPath);
        Assert.Equal(original.DocxBlobPath, edited.DocxBlobPath);
        Assert.Equal(original.Sha256, edited.Sha256);
        Assert.Equal(original.RenderedAtUtc, edited.RenderedAtUtc);
    }

    /// <summary>
    /// Renaming a CV does not pretend the words changed.
    /// </summary>
    /// <remarks>
    /// <see cref="CvVariant.AuthoredAtUtc"/> is the whole of the staleness signal, so a rename that
    /// touched it would make an unchanged document look freshly written - and would take it out of
    /// selection for a render that is still perfectly good.
    /// </remarks>
    [Fact]
    public void Renaming_a_variant_leaves_its_render_current()
    {
        var renamed = Rendered() with { Label = "Backend and platform" };

        Assert.Equal(Authored, renamed.AuthoredAtUtc);
        Assert.True(renamed.IsSendable);
    }

    /// <summary>
    /// A profile that has never been updated makes nothing stale.
    /// </summary>
    /// <remarks>
    /// Null is "nothing has written this column", not "changed at the beginning of time". Reading
    /// absence as a change flags every CV in the library on the day the feature ships.
    /// </remarks>
    [Fact]
    public void PredatesProfileUpdate_is_false_where_the_profile_has_never_recorded_an_update()
        => Assert.False(Rendered().PredatesProfileUpdate(null));

    [Fact]
    public void PredatesProfileUpdate_is_false_for_a_variant_authored_at_the_same_instant()
        => Assert.False(Rendered().PredatesProfileUpdate(Authored));

    [Fact]
    public void PredatesProfileUpdate_is_true_for_a_variant_authored_before_the_change()
        => Assert.True(Rendered().PredatesProfileUpdate(Authored.AddMinutes(1)));

    [Fact]
    public void Staleness_counts_the_variants_still_in_use_and_says_what_it_compared_against()
    {
        var changedAt = Authored.AddDays(10);

        CvVariant[] library =
        [
            Rendered(id: 1, label: "Backend .NET"),
            Rendered(id: 2, label: "Data platforms"),
            Rendered(id: 3, label: "AI engineering", authoredAtUtc: changedAt.AddDays(1)),
        ];

        var staleness = CvVariantLibrary.Staleness(library, changedAt);

        Assert.Equal(3, staleness.Considered);
        Assert.Equal(2, staleness.Stale);
        Assert.Equal(1, staleness.Current);
        Assert.True(staleness.AnyStale);
        Assert.Equal(changedAt, staleness.ProfileUpdatedUtc);
    }

    /// <summary>
    /// An empty library is not something to nudge about.
    /// </summary>
    /// <remarks>
    /// Somebody with no CVs is told so by the gap brief, which knows how many postings are waiting
    /// on one. "None of your zero CVs are out of date" is noise on the page of somebody who has not
    /// started.
    /// </remarks>
    [Fact]
    public void Staleness_of_an_empty_library_says_nothing_rather_than_nudging()
    {
        var staleness = CvVariantLibrary.Staleness([], Authored.AddDays(1));

        Assert.Equal(0, staleness.Considered);
        Assert.Equal(0, staleness.Stale);
        Assert.False(staleness.AnyStale);
    }

    /// <summary>
    /// A retired CV falling behind the profile is not a thing to report.
    /// </summary>
    /// <remarks>
    /// Archived variants accumulate for good, so counting them would have the notice grow every
    /// time somebody rewrites a CV - the number rising while the library gets better maintained,
    /// which is exactly backwards.
    /// </remarks>
    [Fact]
    public void Staleness_of_a_library_that_is_all_archived_counts_none_of_it()
    {
        CvVariant[] library =
        [
            Rendered(id: 1, archived: true),
            Rendered(id: 2, archived: true),
        ];

        var staleness = CvVariantLibrary.Staleness(library, Authored.AddYears(1));

        Assert.Equal(0, staleness.Considered);
        Assert.False(staleness.AnyStale);
    }

    /// <summary>
    /// Being out of date does not take a CV out of selection.
    /// </summary>
    /// <remarks>
    /// The failure this pins: a profile edit at lunchtime would otherwise empty the library, park
    /// every posting for want of a CV, and tell somebody with six good CVs that they have none. A
    /// stale CV is a nudge, never a lockout.
    /// </remarks>
    [Fact]
    public void Staleness_never_makes_a_variant_unsendable()
    {
        var variant = Rendered();

        Assert.True(variant.PredatesProfileUpdate(Authored.AddYears(1)));
        Assert.True(variant.IsSendable);
        Assert.Single(CvVariantLibrary.Selectable([variant]));
    }

    [Fact]
    public void Selectable_excludes_archived_variants_and_keeps_the_callers_order()
    {
        CvVariant[] library =
        [
            Rendered(id: 1, label: "Backend .NET"),
            Rendered(id: 2, label: "Retired", archived: true),
            Rendered(id: 3, label: "Data platforms"),
        ];

        var selectable = CvVariantLibrary.Selectable(library);

        Assert.Equal(new long[] { 1, 3 }, selectable.Select(variant => variant.Id).ToArray());
    }

    /// <summary>
    /// A library that is entirely archived offers nothing, rather than offering the last one.
    /// </summary>
    /// <remarks>
    /// The abstention is the feature. Sending the nearest CV to a job it does not fit is the failure
    /// this design replaces, and it is invisible: the application simply never comes back.
    /// </remarks>
    [Fact]
    public void Selectable_of_a_library_that_is_all_archived_is_empty_so_the_pack_abstains()
    {
        CvVariant[] library = [Rendered(id: 1, archived: true), Rendered(id: 2, archived: true)];

        Assert.Empty(CvVariantLibrary.Selectable(library));
    }

    [Fact]
    public void Selectable_of_an_empty_library_is_empty()
        => Assert.Empty(CvVariantLibrary.Selectable([]));

    [Fact]
    public void Selectable_excludes_a_variant_nothing_has_rendered_yet()
        => Assert.Empty(CvVariantLibrary.Selectable([Draft()]));

    /// <summary>
    /// The cap counts what is in use, so a shelf full of archived CVs never blocks a new one.
    /// </summary>
    /// <remarks>
    /// Otherwise the seventh rewrite of a CV is impossible until somebody deletes the file an
    /// employer was sent, which trades an auditable history for a row count.
    /// </remarks>
    [Fact]
    public void HasRoomForAnother_counts_only_the_variants_in_use()
    {
        var library = Library(active: 2, archived: 9);

        Assert.Equal(2, CvVariantLibrary.ActiveCount(library));
        Assert.True(CvVariantLibrary.HasRoomForAnother(library));
    }

    [Fact]
    public void HasRoomForAnother_is_false_at_exactly_the_cap()
    {
        var library = Library(active: CvVariantLimits.MaxPerProfile);

        Assert.False(CvVariantLibrary.HasRoomForAnother(library));
        Assert.True(CvVariantLibrary.HasRoomForAnother(Library(active: CvVariantLimits.MaxPerProfile - 1)));
    }

    /// <summary>
    /// A library already over the cap answers no, rather than throwing on a page being drawn.
    /// </summary>
    /// <remarks>
    /// Reachable without anybody doing anything wrong: lowering the constant leaves every library
    /// above it above it. The same shape as <c>SubmissionQuota.Remaining</c> flooring at zero.
    /// </remarks>
    [Fact]
    public void HasRoomForAnother_is_false_rather_than_throwing_on_a_library_already_over_the_cap()
    {
        var library = Library(active: CvVariantLimits.MaxPerProfile + 2);

        Assert.False(CvVariantLibrary.HasRoomForAnother(library));
        Assert.Equal(CvVariantLimits.MaxPerProfile + 2, CvVariantLibrary.ActiveCount(library));
    }

    /// <summary>
    /// Uniqueness is over the folded label, because that is what a reader sees.
    /// </summary>
    /// <remarks>
    /// "Backend .NET" and "backend  .net" are different strings and the same label in a picker. A
    /// comparison over the raw text would let both exist, and the pack's account of which CV it
    /// chose would then name something the candidate cannot identify.
    /// </remarks>
    [Fact]
    public void IsLabelAvailable_folds_case_and_whitespace_rather_than_comparing_strings()
    {
        CvVariant[] library = [Rendered(id: 1, label: "Backend .NET")];

        Assert.Equal("backend .net", library[0].LabelKey);
        Assert.False(CvVariantLibrary.IsLabelAvailable(library, "  backend   .NET "));
        Assert.True(CvVariantLibrary.IsLabelAvailable(library, "Backend"));
    }

    /// <summary>
    /// An archived variant does not reserve its name for ever.
    /// </summary>
    /// <remarks>
    /// Rewriting a CV and giving the new one the old one's name is the ordinary case. Reserving it
    /// would push people into calling their CVs "Backend .NET v3" to get past a constraint meant to
    /// help them, and history does not need it: a submission records the variant id.
    /// </remarks>
    [Fact]
    public void IsLabelAvailable_lets_an_archived_variants_label_be_used_again()
    {
        CvVariant[] library = [Rendered(id: 1, label: "Backend .NET", archived: true)];

        Assert.True(CvVariantLibrary.IsLabelAvailable(library, "Backend .NET"));
    }

    /// <summary>
    /// A variant keeps its own label through a rename that does not change it.
    /// </summary>
    /// <remarks>
    /// Without the exclusion, saving "Backend .NET" over "Backend .NET" collides with itself - which
    /// reads to the person as the system refusing a change they did not make.
    /// </remarks>
    [Fact]
    public void IsLabelAvailable_lets_a_variant_keep_its_own_label_through_a_rename()
    {
        CvVariant[] library = [Rendered(id: 4, label: "Backend .NET")];

        Assert.False(CvVariantLibrary.IsLabelAvailable(library, "Backend .NET"));
        Assert.True(CvVariantLibrary.IsLabelAvailable(library, "Backend .NET", excludingId: 4));
    }

    [Fact]
    public void IsLabelAvailable_refuses_a_label_nobody_could_pick_a_cv_by()
    {
        Assert.False(CvVariantLibrary.IsLabelAvailable([], "   "));
        Assert.False(CvVariantLibrary.IsLabelAvailable([], "..."));
        Assert.False(CvVariantLibrary.IsLabelAvailable(
            [], new string('x', CvVariantLimits.MaxLabelLength + 1)));
    }

    private static CvVariant Draft(
        long id = 1,
        string label = "Backend .NET",
        DateTimeOffset? authoredAtUtc = null)
        => new()
        {
            Id = id,
            Label = label,
            Markdown = "# Pablo\n\nBackend engineer.",
            AuthoredAtUtc = authoredAtUtc ?? Authored,
        };

    private static CvVariant Rendered(
        long id = 1,
        string label = "Backend .NET",
        DateTimeOffset? authoredAtUtc = null,
        DateTimeOffset? renderedAtUtc = null,
        bool archived = false)
        => Draft(id, label, authoredAtUtc) with
        {
            RenderedAtUtc = renderedAtUtc ?? (authoredAtUtc ?? Authored).AddMinutes(1),
            PdfBlobPath = $"profile-cvs/7/{id}/Pablo_De_Groot_Curriculum_Vitae.pdf",
            DocxBlobPath = $"profile-cvs/7/{id}/Pablo_De_Groot_Curriculum_Vitae.docx",
            Sha256 = new string('a', CvVariantLimits.Sha256Length),
            IsArchived = archived,
        };

    private static IReadOnlyList<CvVariant> Library(int active, int archived = 0)
        =>
        [
            .. Enumerable.Range(1, active)
                .Select(index => Rendered(id: index, label: $"Variant {index}")),
            .. Enumerable.Range(active + 1, archived)
                .Select(index => Rendered(id: index, label: $"Variant {index}", archived: true)),
        ];
}
