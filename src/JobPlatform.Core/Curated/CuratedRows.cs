namespace JobPlatform.Core.Curated;

/// <summary>
/// One posting, denormalised. The gold row an analysis reads.
/// </summary>
/// <remarks>
/// Flat and typed on purpose: Parquet is columnar, so a query that wants one column reads one
/// column, and a downstream engine can push a filter down without deserialising the row. The
/// concepts and tags travel as delimited strings rather than list columns because that is the
/// shape every reader agrees on — DuckDB, pandas, Fabric and Synapse serverless all split a
/// string without configuration, whereas nested list support varies by engine and version.
/// Anything that needs the relation properly has <c>curated/pairs</c>.
///
/// Property names are lower_snake_case because that is the convention every one of those
/// engines expects, and a column named <c>AnnualSalaryMin</c> needs quoting in most of them.
/// </remarks>
public sealed class CuratedPosting
{
    public string source_key { get; set; } = string.Empty;
    public string site { get; set; } = string.Empty;
    public string? source_board { get; set; }
    public string title { get; set; } = string.Empty;

    public string? company { get; set; }

    /// <summary>Folded name, so a group-by counts one employer once.</summary>
    public string? company_key { get; set; }

    public string? location_city { get; set; }
    public string? location_region { get; set; }
    public string? location_country { get; set; }

    public DateTime? date_posted { get; set; }
    public DateTime first_seen_utc { get; set; }
    public DateTime last_seen_utc { get; set; }
    public int seen_count { get; set; }

    /// <summary>Ordinal, so it sorts. 0 is unknown.</summary>
    public int seniority { get; set; }
    public string seniority_name { get; set; } = string.Empty;

    public int role_family { get; set; }
    public string role_family_name { get; set; } = string.Empty;

    public int work_arrangement { get; set; }
    public string work_arrangement_name { get; set; } = string.Empty;
    public int? hybrid_days_in_office { get; set; }

    /// <summary>Null where the board said nothing. Not false — see the posting entity.</summary>
    public bool? is_remote { get; set; }

    public int? years_experience_min { get; set; }
    public int? years_experience_max { get; set; }

    public decimal? annual_salary_min { get; set; }
    public decimal? annual_salary_max { get; set; }
    public string? annual_salary_currency { get; set; }

    /// <summary>
    /// True where the figure came from prose. Averaging across this without splitting on it
    /// mixes two different measurements.
    /// </summary>
    public bool salary_from_text { get; set; }

    /// <summary>What the source said before annualisation: a day rate is not a salary.</summary>
    public string? salary_stated_interval { get; set; }

    public bool? visa_sponsorship { get; set; }
    public bool requires_security_clearance { get; set; }
    public bool requires_degree { get; set; }
    public string? ir35 { get; set; }

    public string? job_types { get; set; }

    /// <summary>Pipe-delimited concept keys, deduplicated across sources.</summary>
    public string? concept_keys { get; set; }

    /// <summary>Pipe-delimited domain keys the concepts roll up to, via the closure.</summary>
    public string? domain_keys { get; set; }

    public string? tags { get; set; }

    public int description_length { get; set; }
    public bool has_contact_email { get; set; }

    public int enrichment_version { get; set; }

    /// <summary>Which search surfaced it. Also the partition key.</summary>
    public string search_term { get; set; } = string.Empty;
}

/// <summary>
/// One posting-to-concept edge. The training export.
/// </summary>
/// <remarks>
/// This is the dataset that makes the concept graph worth building. At roughly 200,000
/// postings a year carrying about eight concepts each it produces ~1.6M rows a year in exactly
/// the shape published job-domain encoders are fine-tuned on — TechWolf's JobBERT-v2 used 5.5M
/// job-title-to-skill pairs, CareerBERT derived ~131k from a taxonomy. Hard negatives come
/// free: a concept common to similar titles but absent from this one.
///
/// It is also a plain edge list, which is what every graph-embedding library actually takes.
/// node2vec, PyTorch Geometric and DGL all want an edge list, not a graph database — Neo4j's
/// own documentation recommends exporting to PyTorch Geometric to train. Nothing downstream
/// needs a graph engine to use this.
/// </remarks>
public sealed class CuratedPair
{
    public string source_key { get; set; } = string.Empty;

    /// <summary>The query side of the pair.</summary>
    public string title { get; set; } = string.Empty;

    public int seniority { get; set; }
    public string? role_family_name { get; set; }

    /// <summary>The item side: a stable key, never a label.</summary>
    public string concept_key { get; set; } = string.Empty;
    public string concept_label { get; set; } = string.Empty;
    public string concept_kind { get; set; } = string.Empty;

    /// <summary>board, taxonomy or model. Not equally good evidence; weight accordingly.</summary>
    public string source { get; set; } = string.Empty;

    /// <summary>0 unspecified, 1 mentioned, 2 preferred, 3 required.</summary>
    public int polarity { get; set; }

    public int? years_min { get; set; }
    public int? years_max { get; set; }

    public double? confidence { get; set; }

    /// <summary>The surface form the document used. What makes a match explainable.</summary>
    public string? evidence { get; set; }

    public DateTime last_seen_utc { get; set; }
}
