using System.Collections.Frozen;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace JobPlatform.Core.Enrichment;

/// <summary>One <c>Broader</c> path, flattened.</summary>
/// <param name="Depth">Hops from descendant to ancestor; 0 is the concept itself.</param>
public readonly record struct ClosureEdge(string AncestorKey, string DescendantKey, int Depth);

/// <summary>One typed edge, for seeding the relation table.</summary>
public readonly record struct ConceptEdge(string FromKey, string ToKey, ConceptRelationType Type);

/// <summary>What a document yielded: what we could resolve, and what we could not.</summary>
/// <remarks>
/// Both halves are returned together on purpose. A caller that only wanted the assertions
/// would have to go out of its way to discard the mentions, which is the right way round —
/// the previous design discarded them by default and nobody could tell.
/// </remarks>
public sealed record ResolutionResult(
    IReadOnlyList<ConceptAssertion> Assertions,
    IReadOnlyList<UnresolvedMention> Mentions)
{
    public static readonly ResolutionResult Empty = new([], []);
}

/// <summary>
/// The curated concept vocabulary, the DAG over it, and the matcher that finds it in text.
/// </summary>
/// <remarks>
/// This is what makes "how has demand for backend skills moved" a <c>GROUP BY</c> rather than a
/// text search. The value is entirely in the keys being stable: <c>k8s</c>, <c>K8S</c> and
/// <c>Kubernetes</c> have to land on one row, or the answer is three different,
/// individually-wrong numbers.
///
/// <b>The key is the identity and the label is an attribute.</b> The vocabulary this replaces
/// used the canonical name as its key, so renaming a skill was a data migration and there was
/// nothing separating the string in the advert from the concept it denoted.
///
/// <b>The vocabulary is a JSON resource, not a table.</b> The resolver runs inside the ingest
/// function; reading labels from SQL would add a round trip and hold a connection open, which
/// the cost model does not tolerate. It is also the file handed to the language model as its
/// allowed output vocabulary — two vocabularies that drifted apart would give the deterministic
/// and model passes different keys for the same concept, which is precisely the failure this
/// class exists to prevent. SQL holds a projection of it, seeded from here.
///
/// <b>Matching is precision-first.</b> Word boundaries are custom because <c>\b</c> is wrong
/// here — it treats <c>#</c>, <c>+</c> and <c>.</c> as breaks, so <c>C</c> would match
/// inside <c>C++</c> and <c>.NET</c> inside <c>ASP.NET</c>. See <see cref="NameChar"/> for
/// why the dot needs more care than the other two. Concepts whose label is an ordinary
/// English word are handled two ways: those normally capitalised (<c>REST</c>, <c>React</c>,
/// <c>SOLID</c>) match only where the text kept a capital, and those no capital can rescue
/// (<c>Go</c>, <c>R</c>, <c>C</c>, <c>Julia</c>) resolve to nothing but are <b>recorded as
/// unresolved mentions</b> rather than dropped. A false spike in demand for Go is worse than
/// undercounting it; undercounting it silently is worse than either.
/// </remarks>
public sealed class ConceptGraph
{
    private const string ResourceName = "JobPlatform.Core.Enrichment.concepts.json";

    /// <summary>
    /// Neither <c>\w</c> nor <c>\b</c>. <c>#</c> and <c>+</c> are part of a name rather than
    /// a break between names, so without them <c>C</c> matches inside <c>C++</c>.
    /// </summary>
    /// <remarks>
    /// <c>.</c> is handled separately and conditionally, which is the whole subtlety here. It
    /// has to break a name - otherwise <c>js</c> matches inside <c>node.js</c> - but treating
    /// it as a boundary character unconditionally means a full stop breaks one too, and then
    /// <c>"we use C#."</c>, <c>"experience with React."</c> and <c>"written in Golang."</c> all
    /// match nothing at all. A skill at the end of a sentence is not a rare shape in an advert;
    /// it is one of the most common, so that quietly costs a large fraction of every match.
    ///
    /// A dot therefore only breaks a name when a name character follows it. Sentence-final
    /// punctuation is then invisible to the matcher, and <c>node.js</c> still refuses to
    /// yield <c>js</c>.
    /// </remarks>
    private const string NameChar = @"\w#+";

    private const string LeadingBoundary = $"(?<![{NameChar}]|[{NameChar}]\\.)";

    private const string TrailingBoundary = $"(?![{NameChar}]|\\.[{NameChar}])";

    /// <summary>
    /// How far either side of an ambiguous form to look for evidence that it is the language.
    /// </summary>
    /// <remarks>
    /// Narrow on purpose. The signal being looked for is a stack list - "Python, Go, Rust" -
    /// or an immediate qualifier - "Go developer". Widening the window starts catching the
    /// other language names in the same paragraph, which are there for their own reasons and
    /// say nothing about whether this particular "go" was the verb.
    /// </remarks>
    private const int AmbiguityWindow = 30;

    /// <summary>
    /// Words that immediately after an ambiguous name make it the language.
    /// </summary>
    /// <remarks>
    /// "Go developer" and "R programming" are not sentences anyone writes by accident. The
    /// list is deliberately short: every entry here is a phrase that only makes sense if the
    /// preceding token was a language.
    /// </remarks>
    private static readonly Regex FollowedByLanguageWord = new(
        @"^\s*(?:developer|engineer|programmer|programming|developers|engineers|"
        + @"lang|language|codebase|code\s?base|microservices?|services?|backend|dev)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Phrases that immediately before an ambiguous name make it the language.
    /// </summary>
    private static readonly Regex PrecededByLanguageWord = new(
        @"\b(?:written\s+in|coded\s+in|built\s+(?:in|with)|experience\s+(?:in|with)|"
        + @"proficient\s+in|fluent\s+in|languages?\s*[:\-]?|programming\s+in|"
        + @"such\s+as|primarily|stack\s*[:\-]?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// A separator that makes a list: "Python, Go, Rust" or "Java / Go".
    /// </summary>
    private static readonly Regex ListNeighbour = new(
        @"^\s*[,/|]|[,/|]\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Lazy<ConceptGraph> Lazy = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly FrozenDictionary<string, Concept> _byKey;
    private readonly FrozenDictionary<string, Entry> _entryByKey;
    private readonly FrozenDictionary<string, Entry> _resolvable;
    private readonly FrozenDictionary<string, Entry> _tagResolvable;
    private readonly FrozenDictionary<string, string> _ambiguous;
    private readonly FrozenDictionary<string, FrozenDictionary<string, int>> _ancestors;
    private readonly Regex _matcher;
    private readonly int _order;

    private ConceptGraph(int version, IReadOnlyList<Entry> entries)
    {
        Version = version;

        _byKey = entries.ToFrozenDictionary(
            e => e.Key,
            e => new Concept(e.Key, e.ParseKind(), e.Label, e.Broader, e.Implies, e.Related, e.SucceededBy),
            StringComparer.Ordinal);

        _entryByKey = entries.ToFrozenDictionary(e => e.Key, StringComparer.Ordinal);

        Concepts = [.. entries.Select(e => _byKey[e.Key])];
        _order = Concepts.Count;

        var resolvable = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var tagResolvable = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var isDomain = entry.ParseKind() == ConceptKind.Domain;

            foreach (var form in entry.MatchableForms(includeLabel: !isDomain))
            {
                // A collision means the vocabulary is ambiguous, which is a bug in the
                // resource rather than something to resolve at runtime by picking a winner.
                tagResolvable[Normalize(form)] = entry;

                // Domains are structural - nothing writes "Backend Development" in an advert,
                // and matching the phrase would count the few that do as though they were the
                // field. tagOnly concepts are the same problem in miniature: "api" and "cloud"
                // appear in almost every description and mean nothing there.
                if (!isDomain && !entry.TagOnly)
                {
                    resolvable[Normalize(form)] = entry;
                }
            }

            foreach (var form in entry.Ambiguous)
            {
                ambiguous[Normalize(form)] = entry.Key;
            }
        }

        _resolvable = resolvable.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _tagResolvable = tagResolvable.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _ambiguous = ambiguous.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _ancestors = BuildClosure(_byKey);

        // Longest first: .NET's alternation is leftmost-first at each position, so listing
        // "spring boot" before "spring" is what makes the longer name win where both could
        // match. Ordinal tie-break so the pattern is byte-identical run to run.
        var forms = resolvable.Keys
            .Concat(ambiguous.Keys)
            .OrderByDescending(a => a.Length)
            .ThenBy(a => a, StringComparer.Ordinal)
            .Select(Regex.Escape);

        _matcher = new Regex(
            $"{LeadingBoundary}(?:{string.Join('|', forms)}){TrailingBoundary}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    public static ConceptGraph Default => Lazy.Value;

    /// <summary>Bumped when the vocabulary changes in a way that makes stored rows stale.</summary>
    public int Version { get; }

    /// <summary>
    /// The whole vocabulary in resource order, for seeding the concept table and for
    /// constraining the model's output to keys the deterministic pass also uses.
    /// </summary>
    public IReadOnlyList<Concept> Concepts { get; }

    public bool TryGet(string key, out Concept concept) => _byKey.TryGetValue(key, out concept!);

    /// <summary>
    /// Every surface form registered for one concept, with what it is good for.
    /// </summary>
    /// <remarks>
    /// The preferred label is not included - it is already on the concept. This is the
    /// alternates and the ambiguous forms, which is what the label table needs beyond it.
    /// </remarks>
    public IEnumerable<(string Label, ConceptLabelKind Kind)> LabelsOf(string conceptKey)
    {
        if (!_entryByKey.TryGetValue(conceptKey, out var entry))
        {
            yield break;
        }

        foreach (var alias in entry.Aliases)
        {
            yield return (alias, ConceptLabelKind.Alternate);
        }

        foreach (var form in entry.Ambiguous)
        {
            yield return (form, ConceptLabelKind.Ambiguous);
        }
    }

    /// <summary>
    /// The same folding the matcher uses, exposed so a stored label agrees with it.
    /// </summary>
    /// <remarks>
    /// Public because the seeder writes normalised labels into SQL and a lookup there has to
    /// find what the matcher would have matched. Two normalisers would be two definitions of
    /// equality, and the difference would only show up as a lookup that quietly returns
    /// nothing.
    /// </remarks>
    public static string NormalizeLabel(string value) => Normalize(value);

    /// <summary>Every typed edge, for seeding the relation table.</summary>
    public IEnumerable<ConceptEdge> Relations()
    {
        foreach (var concept in Concepts)
        {
            foreach (var to in concept.Broader)
            {
                yield return new ConceptEdge(concept.Key, to, ConceptRelationType.Broader);
            }

            foreach (var to in concept.Implies)
            {
                yield return new ConceptEdge(concept.Key, to, ConceptRelationType.Implies);
            }

            foreach (var to in concept.Related)
            {
                yield return new ConceptEdge(concept.Key, to, ConceptRelationType.Related);
            }

            if (concept.SucceededBy is { } successor)
            {
                yield return new ConceptEdge(concept.Key, successor, ConceptRelationType.SucceededBy);
            }
        }
    }

    /// <summary>
    /// The transitive <c>Broader</c> closure, including a depth-0 row for every concept.
    /// </summary>
    /// <remarks>
    /// The self rows are what let a rollup query be uniform: joining a posting's concepts to
    /// this table and grouping by ancestor counts both "postings wanting C#" and "postings
    /// wanting a backend skill" with the same SQL, instead of a union of two shapes.
    ///
    /// Depth is the <i>shortest</i> path where several exist. In a DAG that is the only
    /// answer that does not depend on which parent happened to be listed first.
    /// </remarks>
    public IEnumerable<ClosureEdge> Closure()
    {
        foreach (var (descendant, ancestors) in _ancestors)
        {
            foreach (var (ancestor, depth) in ancestors)
            {
                yield return new ClosureEdge(ancestor, descendant, depth);
            }
        }
    }

    /// <summary>Ancestor keys of one concept, itself included at depth 0.</summary>
    public IReadOnlyDictionary<string, int> Ancestors(string key)
        => _ancestors.TryGetValue(key, out var found)
            ? found
            : FrozenDictionary<string, int>.Empty;

    /// <summary>
    /// Folds a skill a board published, or the model returned, onto a concept key.
    /// </summary>
    /// <remarks>
    /// Returns false rather than inventing a key for something the vocabulary does not know.
    /// The caller records an <see cref="MentionReason.UnknownBoardSkill"/> mention in that
    /// case, which is right: a board-supplied skill is real evidence even when we have no
    /// concept for it, and dropping it would both understate what the employer asked for and
    /// destroy the only signal that says the vocabulary has a gap.
    /// </remarks>
    /// <param name="fromStructuredField">
    /// True when the form came from a board's own skills field rather than from prose.
    /// </param>
    /// <remarks>
    /// A structured field is a stronger signal than a description, and the vocabulary is
    /// deliberately wider for it: domains and <c>tagOnly</c> concepts resolve here and
    /// nowhere else. The evidence for that is direct - the most frequent forms the resolver
    /// could not place were <c>ai</c>, <c>cloud</c>, <c>machine-learning</c> and
    /// <c>observability</c>, all of them domains the vocabulary already had and was refusing
    /// because the rule written for prose was being applied to a curated tag.
    /// </remarks>
    public bool TryResolve(string? surfaceForm, out Concept concept, bool fromStructuredField = false)
    {
        if (!string.IsNullOrWhiteSpace(surfaceForm))
        {
            var normalized = Normalize(surfaceForm);

            var found = _resolvable.TryGetValue(normalized, out var entry)
                || (fromStructuredField && _tagResolvable.TryGetValue(normalized, out entry));

            if (found)
            {
                concept = _byKey[entry!.Key];
                return true;
            }
        }

        concept = null!;
        return false;
    }

    /// <summary>
    /// Everything the supplied text says, resolved where it safely can be.
    /// </summary>
    /// <remarks>
    /// Assertions come back in vocabulary order rather than order of appearance, so two
    /// postings naming the same concepts produce the same list however each was written. That
    /// keeps a row stable across re-ingests — an unstable list would look like a changed
    /// posting on every run and defeat the change detection entirely.
    ///
    /// Polarity is always <see cref="AssertionPolarity.Unspecified"/> here. A regex cannot
    /// tell "must have" from "would be nice", and claiming otherwise would put a number on
    /// something nobody measured.
    /// </remarks>
    public ResolutionResult Resolve(AssertionSource source, params string?[] texts)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var resolved = new Dictionary<string, string>(_order, StringComparer.Ordinal);
        var mentions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var pending = new List<(Match Match, string Key)>();

            foreach (Match match in _matcher.Matches(text))
            {
                var normalized = Normalize(match.Value);

                if (_ambiguous.TryGetValue(normalized, out var ambiguousKey))
                {
                    // Held back rather than decided here: whether this one is the language
                    // depends on what is around it, and the test is worth doing once per
                    // match rather than inline.
                    pending.Add((match, ambiguousKey));
                    continue;
                }

                if (!_resolvable.TryGetValue(normalized, out var entry))
                {
                    continue;
                }

                // "the rest of the team" is not REST; "react to an incident" is not React.
                if (entry.RequiresCapital && !match.Value.Any(char.IsUpper))
                {
                    continue;
                }

                // First spelling wins, so the evidence is what the advert led with.
                resolved.TryAdd(entry.Key, match.Value);
            }

            foreach (var (match, key) in pending)
            {
                if (LooksLikeTheLanguage(text, match))
                {
                    resolved.TryAdd(key, match.Value);
                }
                else
                {
                    mentions[match.Value] = mentions.GetValueOrDefault(match.Value) + 1;
                }
            }
        }

        if (resolved.Count == 0 && mentions.Count == 0)
        {
            return ResolutionResult.Empty;
        }

        var assertions = Concepts
            .Where(c => resolved.ContainsKey(c.Key))
            .Select(c => new ConceptAssertion(c.Key, source, EvidenceText: resolved[c.Key]))
            .ToArray();

        var unresolved = mentions
            .OrderByDescending(m => m.Value)
            .ThenBy(m => m.Key, StringComparer.Ordinal)
            .Select(m => new UnresolvedMention(m.Key, MentionReason.Ambiguous, m.Value))
            .ToArray();

        return new ResolutionResult(assertions, unresolved);
    }

    /// <summary>
    /// Whether an ambiguous name is being used as the language rather than as the word.
    /// </summary>
    /// <remarks>
    /// "Go", "R", "C" and "Julia" are ordinary English words and a name, and matching them on
    /// sight would invent demand that does not exist - against the live corpus they account
    /// for 833 unresolved mentions, the single largest gap in the data. Refusing them outright
    /// is the safe answer and the one this class shipped with; it is also wrong for the
    /// fraction that really are the language.
    ///
    /// Three signals, all local and all narrow, because a false positive here is worse than
    /// a miss: the form is part of a list of other things ("Python, Go, Rust"), it is
    /// immediately followed by a word that only follows a language ("Go developer"), or it is
    /// immediately preceded by a phrase that introduces one ("written in Go"). Anything else
    /// stays a mention, which is a recorded uncertainty rather than a silent loss.
    ///
    /// A list only counts when the neighbouring item is itself a resolved concept. "Fast,
    /// go, and win" is a list too, and the difference between it and "Python, Go, Rust" is
    /// entirely in what the neighbours are.
    /// </remarks>
    private bool LooksLikeTheLanguage(string text, Match match)
    {
        var beforeStart = Math.Max(0, match.Index - AmbiguityWindow);
        var before = text[beforeStart..match.Index];

        var afterEnd = Math.Min(text.Length, match.Index + match.Length + AmbiguityWindow);
        var after = text[(match.Index + match.Length)..afterEnd];

        if (FollowedByLanguageWord.IsMatch(after) || PrecededByLanguageWord.IsMatch(before))
        {
            return true;
        }

        // Either side: "Python, Go and Rust" has the separator before, "Go, Rust and Python"
        // has it after. Checking only one direction misses half of every list.
        var inList = ListNeighbour.IsMatch(after) || ListNeighbour.IsMatch(before);

        return inList && HasResolvableNeighbour(before, after);
    }

    /// <summary>Whether either side of the form names something the vocabulary knows.</summary>
    private bool HasResolvableNeighbour(string before, string after)
    {
        foreach (var side in (ReadOnlySpan<string>)[before, after])
        {
            foreach (Match neighbour in _matcher.Matches(side))
            {
                var normalized = Normalize(neighbour.Value);

                if (_resolvable.ContainsKey(normalized))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Case and punctuation folded, so <c>Node.js</c>, <c>node js</c> and <c>NODEJS</c> agree.
    /// </summary>
    /// <remarks>
    /// <c>#</c>, <c>+</c> and <c>.</c> survive, because they carry meaning in a name: folding
    /// them would make <c>C#</c> and <c>C</c> the same string, and <c>C++</c> too.
    /// </remarks>
    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = true;

        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '#' or '+' or '.')
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Breadth-first from each concept, so a node reachable by two paths gets the shorter
    /// depth. The graph is a few hundred nodes; this runs once, at first use.
    /// </summary>
    private static FrozenDictionary<string, FrozenDictionary<string, int>> BuildClosure(
        FrozenDictionary<string, Concept> byKey)
    {
        var result = new Dictionary<string, FrozenDictionary<string, int>>(byKey.Count, StringComparer.Ordinal);

        foreach (var key in byKey.Keys)
        {
            var depths = new Dictionary<string, int>(StringComparer.Ordinal) { [key] = 0 };
            var queue = new Queue<string>();
            queue.Enqueue(key);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var depth = depths[current] + 1;

                foreach (var parent in byKey[current].Broader)
                {
                    // A cycle would make this loop forever; the resource is validated for
                    // acyclicity when it is generated, and TryAdd makes it terminate anyway.
                    if (byKey.ContainsKey(parent) && depths.TryAdd(parent, depth))
                    {
                        queue.Enqueue(parent);
                    }
                }
            }

            result[key] = depths.ToFrozenDictionary(StringComparer.Ordinal);
        }

        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static ConceptGraph Load()
    {
        using var stream = typeof(ConceptGraph).GetTypeInfo().Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded concept vocabulary '{ResourceName}' is missing from the assembly. "
                + "It needs an <EmbeddedResource> entry in JobPlatform.Core.csproj.");

        var document = JsonSerializer.Deserialize<Document>(stream, JsonOptions)
            ?? throw new InvalidOperationException("Concept vocabulary resource is empty.");

        return new ConceptGraph(document.Version, document.Concepts);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private sealed record Document
    {
        public int Version { get; init; }

        public IReadOnlyList<Entry> Concepts { get; init; } = [];
    }

    private sealed record Entry
    {
        public required string Key { get; init; }

        public required string Kind { get; init; }

        public required string Label { get; init; }

        public IReadOnlyList<string> Aliases { get; init; } = [];

        /// <summary>Forms that name this concept but cannot be trusted to mean it.</summary>
        public IReadOnlyList<string> Ambiguous { get; init; } = [];

        public IReadOnlyList<string> Broader { get; init; } = [];

        public IReadOnlyList<string> Implies { get; init; } = [];

        public IReadOnlyList<string> Related { get; init; } = [];

        public string? SucceededBy { get; init; }

        /// <summary>
        /// Set for concepts whose label is also an ordinary English word but which are
        /// normally capitalised, so a capital is enough to tell the two apart.
        /// </summary>
        [JsonPropertyName("requiresCapital")]
        public bool RequiresCapital { get; init; }

        /// <summary>
        /// Whether the label is itself safe to look for. False for the handful whose bare form
        /// no capital can rescue — <c>R</c> and <c>C</c> are capitals already, and <c>Go</c>
        /// and <c>Julia</c> start sentences and name people. Those reach a concept only
        /// through their aliases, and their bare form is listed in
        /// <see cref="Ambiguous"/> instead.
        /// </summary>
        /// <remarks>
        /// An explicit flag rather than a rule about label length, which would have quietly
        /// excluded <c>Qt</c> — a real framework whose only spelling is its two-letter name,
        /// and which would then have matched nothing at all.
        /// </remarks>
        [JsonPropertyName("matchLabel")]
        public bool MatchLabel { get; init; } = true;

        /// <summary>
        /// Resolvable from a board's structured skills field, never matched in a description.
        /// </summary>
        /// <remarks>
        /// Every domain is tagOnly, and so are words like <c>api</c>, <c>cloud</c> and
        /// <c>automation</c>. In prose those carry no information - almost every advert
        /// contains them - but in a skills field the employer picked them from a list, which
        /// is a deliberate act. The same string means different things depending on where it
        /// appears, and that is exactly the distinction <see cref="AssertionSource"/> exists
        /// to record.
        /// </remarks>
        [JsonPropertyName("tagOnly")]
        public bool TagOnly { get; init; }

        public ConceptKind ParseKind() => Kind switch
        {
            "domain" => ConceptKind.Domain,
            "skill" => ConceptKind.Skill,
            "qualification" => ConceptKind.Qualification,
            _ => throw new InvalidOperationException($"Concept '{Key}' has unknown kind '{Kind}'."),
        };

        public IEnumerable<string> MatchableForms(bool includeLabel = true)
        {
            if (MatchLabel && includeLabel)
            {
                yield return Label;
            }

            foreach (var alias in Aliases)
            {
                yield return alias;
            }
        }
    }
}
