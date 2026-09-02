const DATA = {
  summary: {
    cumulative: 18742, newInLastRun: 412, prevNew: 366, seenInLastRun: 3918, invalid: 44,
    days: 47, remote: 0.342, salaryColumns: 0.216, salaryKnown: 0.589,
    medianStated: 72000, fromProse: 0.63,
    scrapedHoursAgo: 4, sweepHoursAgo: 5, newSinceSweep: 412
  },
  trend: [
    { d: '20 Aug', n: 298, weekend: false }, { d: '21 Aug', n: 341, weekend: false },
    { d: '22 Aug', n: 302, weekend: false }, { d: '23 Aug', n: 96, weekend: true },
    { d: '24 Aug', n: 74, weekend: true }, { d: '25 Aug', n: 388, weekend: false },
    { d: '26 Aug', n: 412, weekend: false }, { d: '27 Aug', n: 377, weekend: false },
    { d: '28 Aug', n: 349, weekend: false }, { d: '29 Aug', n: 310, weekend: false },
    { d: '30 Aug', n: 88, weekend: true }, { d: '31 Aug', n: 71, weekend: true },
    { d: '1 Sep', n: 366, weekend: false }, { d: '2 Sep', n: 412, weekend: false }
  ],
  sites: [{ name: 'LinkedIn', n: 1614 }, { name: 'Indeed', n: 1102 }, { name: 'Glassdoor', n: 617 },
    { name: 'freehire.me', n: 402 }, { name: 'ZipRecruiter', n: 183 }],
  skills: [{ name: 'Python', n: 1204 }, { name: 'AWS', n: 986 }, { name: 'Kubernetes', n: 743 },
    { name: 'TypeScript', n: 690 }, { name: 'Azure', n: 641 }, { name: 'Terraform', n: 512 },
    { name: 'SQL', n: 498 }, { name: 'React', n: 431 }],
  areas: [{ name: 'Backend', n: 1880 }, { name: 'Cloud and platform', n: 1402 }, { name: 'Data', n: 1120 },
    { name: 'Frontend', n: 764 }, { name: 'Security', n: 318 }],
  seniority: [{ name: 'Mid', n: 1412 }, { name: 'Senior', n: 1206 }, { name: 'Junior', n: 512 },
    { name: 'Lead', n: 288 }, { name: 'Principal', n: 94 }],
  arrangement: [{ name: 'On-site', n: 1764 }, { name: 'Hybrid', n: 1402 }, { name: 'Remote', n: 752 }],
  posters: [{ name: 'Capgemini', n: 84 }, { name: 'Accenture', n: 71 }, { name: 'BT Group', n: 58 },
    { name: 'Sky', n: 44 }, { name: 'Deloitte', n: 39 }],
  health: {
    /* Two different faults with the same symptom, named apart. `lastFilled`
       null means the column never arrived at all, which is not a regression. */
    columns: [
      { f: 'job_level', lastFilled: '28 Aug', wasAt: 0.94 },
      { f: 'company_industry', lastFilled: null, wasAt: null }
    ],
    sparse: [{ f: 'salary_source', r: 0.184 }, { f: 'company_rating', r: 0.221 }]
  },
  /* Ranked by how many postings name a form, not by total occurrences: one
     advert repeating a word twenty times is one employer's habit. */
  unresolved: [{ form: 'ci/cd', reason: 'a genuine gap', n: 212 }, { form: 'go', reason: 'needs context', n: 188 },
    { form: 'agile delivery', reason: 'a genuine gap', n: 141 }, { form: 'r', reason: 'needs context', n: 96 },
    { form: 'full stack', reason: 'a genuine gap', n: 88 }, { form: 'c', reason: 'needs context', n: 61 }],

  postings: [
    { id: 41209, title: 'Senior Platform Engineer', company: 'Sky', where: 'Osterley, London', board: 'LinkedIn',
      firstSeen: '2 Sep', boardSays: '2 Sep', repost: false, low: 85000, high: 95000, interval: 'year',
      salarySource: 'column', work: 'Hybrid', level: 'Senior', concepts: ['skill.kubernetes', 'skill.azure', 'skill.terraform'],
      clearance: false, applicants: 41,
      insight: [
        ['Kubernetes', 'Taxonomy', 'Required', 'You will own our Kubernetes platform end to end, from the control plane up.'],
        ['Terraform', 'Taxonomy', 'Required', 'Essential: strong Terraform, and the judgement to know when not to reach for it.'],
        ['Azure', 'Taxonomy', 'Preferred', 'Our estate runs across Azure and AWS in roughly equal measure.'],
        ['Hybrid', 'Board', 'Unspecified', null],
        ['Seniority: Senior', 'Model', 'Unspecified', 'No level in the columns. Read from the title and the eight-year requirement.'],
        ['Cloud and platform', 'Rollup', 'Required', 'Closure over Kubernetes, Azure and Terraform.']
      ] },
    { id: 41188, title: 'Backend Engineer (Go)', company: 'Monzo', where: 'UK remote', board: 'LinkedIn',
      firstSeen: '2 Sep', boardSays: '2 Sep', repost: false, low: 80000, high: 100000, interval: 'year',
      salarySource: 'column', work: 'Remote', level: 'Senior', concepts: ['skill.go', 'skill.aws'],
      clearance: false, applicants: 128,
      insight: [
        ['Go', 'Taxonomy', 'Required', 'Two years of production Go is essential for this one.'],
        ['gRPC', 'Taxonomy', 'Preferred', 'Services talk to each other over gRPC.'],
        ['AWS', 'Board', 'Unspecified', null],
        ['Remote', 'Taxonomy', 'Unspecified', 'Fully remote within the UK.'],
        ['Backend', 'Rollup', 'Required', 'Closure over Go and gRPC.']
      ] },
    { id: 41154, title: 'Cloud Engineer', company: 'BT Group', where: 'Ipswich', board: 'Indeed',
      firstSeen: '1 Sep', boardSays: '1 Sep', repost: false, low: 70000, high: 78000, interval: 'year',
      salarySource: 'column', work: 'On-site', level: 'Mid', concepts: ['skill.azure', 'skill.terraform'],
      clearance: false, applicants: null,
      insight: [
        ['Azure', 'Taxonomy', 'Required', 'AZ-104 or equivalent demonstrable Azure experience.'],
        ['Terraform', 'Taxonomy', 'Required', 'Infrastructure is defined in Terraform, no exceptions.'],
        ['On-site', 'Taxonomy', 'Unspecified', 'Five days a week at our Adastral Park site.'],
        ['Seniority: Mid', 'Model', 'Unspecified', 'Three to five years asked for, no seniority word in the title.']
      ] },
    { id: 41140, title: 'Data Platform Engineer', company: 'Ocado', where: 'UK remote', board: 'freehire.me',
      firstSeen: '1 Sep', boardSays: '1 Sep', repost: false, low: 75000, high: 88000, interval: 'year',
      salarySource: 'prose', work: 'Remote', level: 'Senior', concepts: ['skill.python', 'skill.sql'],
      clearance: false, applicants: null,
      insight: [
        ['Spark', 'Taxonomy', 'Required', 'Our pipelines are Spark on Databricks.'],
        ['Python', 'Taxonomy', 'Required', 'Python is the language of the platform.'],
        ['Salary £75,000 to £88,000', 'Model', 'Unspecified', 'Read out of the prose: “the band for this role is £75k to £88k”. No salary column was supplied.'],
        ['Remote', 'Board', 'Unspecified', null],
        ['Data', 'Rollup', 'Required', 'Closure over Spark, Python and Parquet.']
      ] },
    { id: 41102, title: 'DevOps Engineer', company: 'Capgemini', where: 'Manchester', board: 'Indeed',
      firstSeen: '12 Aug', boardSays: '31 Aug', repost: true, low: 65000, high: 75000, interval: 'year',
      salarySource: 'column', work: 'Hybrid', level: 'Mid', concepts: ['skill.terraform'],
      clearance: true, applicants: null,
      insight: [
        ['SC clearance', 'Taxonomy', 'Required', 'You must hold, or be eligible for, SC clearance before starting.'],
        ['Ansible', 'Taxonomy', 'Required', 'Configuration management with Ansible.'],
        ['Jenkins', 'Taxonomy', 'Required', 'Our pipelines are Jenkins, and there are a lot of them.'],
        ['Security', 'Rollup', 'Required', 'Closure over SC clearance.']
      ] },
    { id: 41077, title: 'Site Reliability Engineer', company: 'Starling Bank', where: 'Cardiff', board: 'Glassdoor',
      firstSeen: '31 Aug', boardSays: '31 Aug', repost: false, low: null, high: null, interval: null,
      salarySource: null, work: 'Hybrid', level: 'Senior', concepts: ['skill.kubernetes'],
      clearance: false, applicants: null,
      insight: [
        ['Prometheus', 'Taxonomy', 'Required', 'Observability is Prometheus, Grafana and a lot of care.'],
        ['Kubernetes', 'Taxonomy', 'Required', 'Everything is on Kubernetes.'],
        ['Hybrid', 'Board', 'Unspecified', null],
        ['Incident command', 'Model', 'Preferred', 'You will take the pager, and sometimes the incident.']
      ] },
    { id: 41044, title: 'Lead Engineer, Payments', company: 'Wise', where: 'Shoreditch, London', board: 'LinkedIn',
      firstSeen: '30 Aug', boardSays: '30 Aug', repost: false, low: 95000, high: 115000, interval: 'year',
      salarySource: 'column', work: 'On-site', level: 'Lead', concepts: ['skill.java'],
      clearance: false, applicants: 12,
      insight: [
        ['Java', 'Board', 'Unspecified', null],
        ['Leading a team', 'Taxonomy', 'Required', 'You will lead a team of six.'],
        ['On-site', 'Taxonomy', 'Unspecified', 'Four days a week in Shoreditch.'],
        ['Backend', 'Rollup', 'Required', 'Closure over Java.']
      ] },
    { id: 41020, title: 'Platform Engineer', company: 'Deliveroo', where: 'Farringdon, London', board: 'ZipRecruiter',
      firstSeen: '4 Aug', boardSays: '30 Aug', repost: true, low: 90000, high: 110000, interval: 'year',
      salarySource: 'column', work: 'Hybrid', level: 'Senior', concepts: ['skill.go', 'skill.kubernetes'],
      clearance: false, applicants: 203,
      insight: [
        ['Go', 'Taxonomy', 'Required', 'Go, and a willingness to read a lot of it.'],
        ['Kubernetes', 'Taxonomy', 'Required', 'A large multi-tenant Kubernetes estate.'],
        ['Hybrid', 'Taxonomy', 'Unspecified', 'Three days a week in Farringdon.'],
        ['Cloud and platform', 'Rollup', 'Required', 'Closure over Go and Kubernetes.']
      ] },
    { id: 40988, title: 'Infrastructure Engineer', company: 'Deloitte', where: 'Belfast', board: 'Indeed',
      firstSeen: '29 Aug', boardSays: '29 Aug', repost: false, low: null, high: null, interval: null,
      salarySource: null, work: 'On-site', level: 'Mid', concepts: ['skill.terraform'],
      clearance: true, applicants: null,
      insight: [
        ['SC clearance', 'Taxonomy', 'Required', 'Candidates must be SC cleared or clearable.'],
        ['VMware', 'Taxonomy', 'Required', 'A large VMware estate, migrating slowly.'],
        ['Terraform', 'Taxonomy', 'Preferred', 'New build is Terraform.'],
        ['On-site', 'Board', 'Unspecified', null]
      ] },
    { id: 40961, title: 'Senior Data Engineer', company: 'Accenture', where: 'Edinburgh', board: 'Glassdoor',
      firstSeen: '29 Aug', boardSays: '29 Aug', repost: false, low: 68000, high: 82000, interval: 'year',
      salarySource: 'prose', work: 'Hybrid', level: 'Senior', concepts: ['skill.python', 'skill.sql'],
      clearance: false, applicants: null,
      insight: [
        ['Databricks', 'Taxonomy', 'Required', 'Databricks, and the appetite to make it behave.'],
        ['SQL', 'Taxonomy', 'Required', 'Strong SQL is non-negotiable.'],
        ['Salary £68,000 to £82,000', 'Model', 'Unspecified', 'Read out of the prose: “£68,000 – £82,000 depending on experience”.'],
        ['Data', 'Rollup', 'Required', 'Closure over Databricks, SQL and Python.']
      ] },
    { id: 40940, title: 'Cloud Consultant (outside IR35)', company: 'Version 1', where: 'Bristol', board: 'Indeed',
      firstSeen: '28 Aug', boardSays: '28 Aug', repost: false, low: 600, high: 650, interval: 'day',
      salarySource: 'column', work: 'Hybrid', level: 'Senior', concepts: ['skill.azure', 'skill.terraform'],
      clearance: false, applicants: null,
      insight: [
        ['Day rate £600 to £650', 'Board', 'Unspecified', null],
        ['Outside IR35', 'Taxonomy', 'Unspecified', 'This engagement has been determined outside IR35.'],
        ['Azure', 'Taxonomy', 'Required', 'Azure landing zones, at scale.'],
        ['Cloud and platform', 'Rollup', 'Required', 'Closure over Azure and Terraform.']
      ] }
  ],

  /* AssertionSource, in descending order of trust. Board tags carry no phrase,
     which is why evidence is null rather than invented. Rollup is not a source
     at all - it is the closure walk - and is listed apart for that reason. */
  sources: [
    { name: 'Board', share: 0.31, note: 'The employer’s own structured tagging, taken from the board’s fields. Highest trust and no phrase to show: nobody wrote a sentence, somebody ticked a box.' },
    { name: 'Taxonomy', share: 0.52, note: 'A string match against the vocabulary, with the phrase it matched. It refuses to guess: go, c and r stay unresolved without surrounding context, because a wrong key is indistinguishable from a right one once stored.' },
    { name: 'Model', share: 0.17, note: 'A judgement, batched many postings to a call. Skipped entirely when no provider is configured, which is why the two above have to stand on their own.' }
  ],
  gradedShare: 0.68,
  polarities: [
    { name: 'Required', n: 41 }, { name: 'Preferred', n: 27 }, { name: 'Mentioned', n: 12 }, { name: 'Unspecified', n: 20 }
  ],

  /* Demand keyed by concept, with the count restricted to postings that score
     for this candidate. The corpus number says what the market asks for; the
     in-band number says what the market asks *you* for, and only the second one
     is worth acting on. */
  demand: [
    { key: 'skill.python', postings: 1204, inBand: 96 }, { key: 'skill.aws', postings: 986, inBand: 88 },
    { key: 'skill.kubernetes', postings: 743, inBand: 71 }, { key: 'skill.typescript', postings: 690, inBand: 34 },
    { key: 'skill.azure', postings: 641, inBand: 64 }, { key: 'skill.terraform', postings: 512, inBand: 61 },
    { key: 'skill.sql', postings: 498, inBand: 40 }, { key: 'skill.react', postings: 431, inBand: 12 },
    { key: 'skill.docker', postings: 402, inBand: 44 }, { key: 'skill.go', postings: 288, inBand: 29 },
    { key: 'skill.java', postings: 264, inBand: 18 }, { key: 'qual.sc-clearance', postings: 214, inBand: 22 },
    { key: 'skill.helm', postings: 96, inBand: 9 }, { key: 'skill.bicep', postings: 88, inBand: 7 }
  ],

  concepts: [
    { key: 'area.backend', label: 'Backend', kind: 'Area', n: 1880, narrower: ['skill.python', 'skill.go', 'skill.java'], broader: [], related: [],
      note: 'An area, not a skill. Nothing is tagged with it directly; it is what the closure gives you when a posting names Python or Go.' },
    { key: 'area.cloud-platform', label: 'Cloud and platform', kind: 'Area', n: 1402, narrower: ['skill.kubernetes', 'skill.terraform', 'skill.aws', 'skill.azure'], broader: [], related: [], note: '' },
    { key: 'area.data', label: 'Data', kind: 'Area', n: 1120, narrower: ['skill.sql', 'skill.python'], broader: [], related: [], note: '' },
    { key: 'area.frontend', label: 'Frontend', kind: 'Area', n: 764, narrower: ['skill.react', 'skill.typescript'], broader: [], related: [], note: '' },
    { key: 'area.security', label: 'Security', kind: 'Area', n: 318, narrower: ['qual.sc-clearance'], broader: [], related: [], note: '' },
    { key: 'skill.python', label: 'Python', kind: 'Skill', n: 1204, broader: ['area.backend', 'area.data'], narrower: [], related: [],
      note: 'Sits under two areas, which is why the areas do not sum to the corpus.' },
    { key: 'skill.aws', label: 'AWS', kind: 'Skill', n: 986, broader: ['area.cloud-platform'], narrower: [], related: ['skill.azure'], note: '' },
    { key: 'skill.kubernetes', label: 'Kubernetes', kind: 'Skill', n: 743, broader: ['area.cloud-platform'], narrower: ['skill.helm'], related: ['skill.docker'], note: '' },
    { key: 'skill.typescript', label: 'TypeScript', kind: 'Skill', n: 690, broader: ['area.frontend'], narrower: [], related: ['skill.react'], note: '' },
    { key: 'skill.azure', label: 'Azure', kind: 'Skill', n: 641, broader: ['area.cloud-platform'], narrower: [], related: ['skill.aws'], note: '' },
    { key: 'skill.terraform', label: 'Terraform', kind: 'Skill', n: 512, broader: ['area.cloud-platform'], narrower: [], related: ['skill.bicep'], note: '' },
    { key: 'skill.sql', label: 'SQL', kind: 'Skill', n: 498, broader: ['area.data'], narrower: [], related: [], note: '' },
    { key: 'skill.react', label: 'React', kind: 'Skill', n: 431, broader: ['area.frontend'], narrower: [], related: ['skill.typescript'],
      note: 'The resolver will not take the bare word from prose. “React calmly in stressful situations” is an English verb, and it appeared often enough to matter.' },
    { key: 'skill.docker', label: 'Docker', kind: 'Skill', n: 402, broader: ['area.cloud-platform'], narrower: [], related: ['skill.kubernetes'], note: '' },
    { key: 'skill.go', label: 'Go', kind: 'Skill', n: 288, broader: ['area.backend'], narrower: [], related: [],
      note: 'Only resolved with surrounding context. The bare word is refused, which costs recall and buys the vocabulary its credibility.' },
    { key: 'skill.java', label: 'Java', kind: 'Skill', n: 264, broader: ['area.backend'], narrower: [], related: [], note: '' },
    { key: 'qual.sc-clearance', label: 'SC clearance', kind: 'Qualification', n: 214, broader: ['area.security'], narrower: [], related: [],
      note: 'A qualification rather than a skill: it cannot be picked up before an application closes, so a gap here is disqualifying in a way a tool never is.' },
    { key: 'skill.helm', label: 'Helm', kind: 'Skill', n: 96, broader: ['skill.kubernetes'], narrower: [], related: [], note: '' },
    { key: 'skill.bicep', label: 'Bicep', kind: 'Skill', n: 88, broader: ['area.cloud-platform'], narrower: [], related: ['skill.terraform'],
      note: 'Supersedes ARM templates. A profile holding this satisfies an ARM requirement through the Superseded relation, and the match breakdown names that relation rather than claiming an exact hit.' }
  ],

  /* Deliberately NOT in descending score order: the list is ordered by fusion,
     which is the claim the copy makes and the earlier revision quietly failed
     to demonstrate. `axes` is [name, score, weight] - two axes drawn the same
     length said a 34% axis and a 10% axis were equally important.
     `relations` is how a held concept satisfied a required one. */
  matches: [
    { id: 1, score: 91, coverage: 0.82, arrived: null, changed: null, title: 'Senior Platform Engineer', company: 'Sky', work: 'Hybrid',
      salary: '£85k–£95k', verdict: 'Strong', unmet: 1,
      axes: [['Essential skills', 82, .34], ['Other skills', 64, .12], ['Seniority', 100, .10], ['Experience', 88, .14], ['Working arrangement', 100, .12], ['Salary', 71, .18]],
      relations: [['Bicep', 'Terraform', 'Related'], ['Helm', 'Kubernetes', 'Specialisation']],
      rationale: 'Nine of their eleven stated requirements are covered, including the two they lead with. The gap is Terraform at a level they call essential, and you have Bicep, which the graph records as related rather than equivalent.',
      strengths: ['Kubernetes at the depth the advert asks for', 'Azure and AWS both named, they want either', 'Platform team leadership matches the reporting line'],
      gaps: ['Terraform named Required, not in your profile', 'They ask for on-call rotation experience'] },
    { id: 3, score: 84, coverage: 0.79, arrived: 'overnight', changed: null, title: 'Cloud Engineer', company: 'BT Group', work: 'On-site',
      salary: '£70k–£78k', verdict: 'Strong', unmet: 0,
      axes: [['Essential skills', 94, .34], ['Other skills', 70, .12], ['Seniority', 88, .10], ['Experience', 84, .14], ['Working arrangement', 40, .12], ['Salary', 62, .18]],
      relations: [['Azure', 'Azure', 'Exact'], ['Bicep', 'ARM templates', 'Superseded']],
      rationale: 'Everything they call Required is covered. The friction is location: five days on-site in Ipswich against a remote preference, and the band sits below what you asked for.',
      strengths: ['Every Required assertion met', 'Azure certification they name explicitly'],
      gaps: ['Five days on-site', 'Band tops out below your stated floor'] },
    { id: 2, score: 88, coverage: 0.74, arrived: null, changed: null, title: 'Backend Engineer (Go)', company: 'Monzo', work: 'Remote',
      salary: '£80k–£100k', verdict: 'Possible', unmet: 2,
      axes: [['Essential skills', 68, .34], ['Other skills', 72, .12], ['Seniority', 100, .10], ['Experience', 92, .14], ['Working arrangement', 100, .12], ['Salary', 88, .18]],
      relations: [['Python', 'Go', 'Related']],
      rationale: 'Strong on distributed systems and the domain, thin on Go itself. Your profile shows C# and Python at depth; the advert treats Go as learnable but lists two years of it as Required.',
      strengths: ['Event-driven architecture, named three times in the advert', 'Fintech domain experience'],
      gaps: ['Go, two years, Required', 'gRPC not shown anywhere in your profile'] },
    { id: 8, score: 58, coverage: 0.44, arrived: null, changed: 'The model reread this last night and moved it from Weak to Strong.', title: 'Lead Engineer, Payments', company: 'Wise', work: 'On-site',
      salary: '£95k–£115k', verdict: 'Strong', unmet: 2,
      axes: [['Essential skills', 46, .34], ['Other skills', 50, .12], ['Seniority', 96, .10], ['Experience', 94, .14], ['Working arrangement', 40, .12], ['Salary', 96, .18]],
      relations: [['C#', 'Java', 'Related']],
      rationale: 'The lowest score on the page and the entry the ordering exists for. The advert states very little, so the arithmetic had little to score, but what it does say is leadership and payments at scale, and both are the strongest parts of your record.',
      strengths: ['Ten years, five of them leading teams', 'Payments domain, end to end', 'Band is the highest on your shortlist'],
      gaps: ['Advert names Java, you have C#', 'Four days on-site in London'] },
    { id: 6, score: 74, coverage: 0.77, arrived: 'overnight', changed: null, title: 'Data Platform Engineer', company: 'Ocado', work: 'Remote',
      salary: '£75k–£88k', verdict: 'Strong', unmet: 0,
      axes: [['Essential skills', 86, .34], ['Other skills', 66, .12], ['Seniority', 92, .10], ['Experience', 78, .14], ['Working arrangement', 100, .12], ['Salary', 74, .18]],
      relations: [['Python', 'Python', 'Exact'], ['SQL', 'Data modelling', 'Implied']],
      rationale: 'A quieter advert than its neighbours, and everything it does state, you have. Fully remote and the band clears your floor.',
      strengths: ['Spark and Parquet, both named', 'Fully remote'], gaps: [] },
    { id: 9, score: 69, coverage: 0.31, arrived: 'overnight', changed: null, title: 'Engineering Lead', company: 'Trainline', work: 'Hybrid',
      salary: '£88k–£102k', verdict: 'Unknown', unmet: 0,
      axes: [['Essential skills', 60, .34], ['Other skills', 55, .12], ['Seniority', 94, .10], ['Experience', 90, .14], ['Working arrangement', 100, .12], ['Salary', 84, .18]],
      relations: [],
      rationale: 'The model read this one and would not commit. Four paragraphs about culture and one sentence of requirements: there is little here to agree with and little to disagree with. Unknown is a judgement that was made, not a place in the queue.',
      strengths: [], gaps: [] },
    { id: 4, score: 79, coverage: 0.71, arrived: null, changed: null, title: 'Senior Software Engineer', company: 'Deliveroo', work: 'Hybrid',
      salary: '£90k–£110k', verdict: null, unmet: 1,
      axes: [['Essential skills', 76, .34], ['Other skills', 68, .12], ['Seniority', 92, .10], ['Experience', 86, .14], ['Working arrangement', 100, .12], ['Salary', 90, .18]],
      relations: [['Kubernetes', 'Kubernetes', 'Exact']],
      rationale: '', strengths: [], gaps: [] },
    { id: 7, score: 71, coverage: 0.58, arrived: null, changed: null, title: 'Site Reliability Engineer', company: 'Starling Bank', work: 'Hybrid',
      salary: 'not stated', verdict: 'Possible', unmet: 1,
      axes: [['Essential skills', 74, .41], ['Other skills', 62, .15], ['Seniority', 84, .12], ['Experience', 80, .17], ['Working arrangement', 100, .15], ['Salary', null, 0]],
      relations: [['Azure Monitor', 'Prometheus', 'Related']],
      rationale: 'No salary stated, so that axis was dropped and the remaining weights renormalised rather than a zero being scored. On what the advert does say the fit is good, and the one gap is a tool rather than a discipline.',
      strengths: ['Observability stack matches theirs', 'Incident command experience'], gaps: ['Prometheus Required, you show Azure Monitor'] },
    { id: 5, score: 76, coverage: 0.66, arrived: null, changed: null, title: 'DevOps Engineer', company: 'Capgemini', work: 'Hybrid',
      salary: '£65k–£75k', verdict: 'Weak', unmet: 3,
      axes: [['Essential skills', 52, .34], ['Other skills', 58, .12], ['Seniority', 70, .10], ['Experience', 80, .14], ['Working arrangement', 100, .12], ['Salary', 48, .18]],
      relations: [],
      rationale: 'The title matches and little else does. Three Required assertions are unmet and the band is well under your floor, so the score is carried by axes the advert barely states.',
      strengths: ['CI/CD pipeline ownership'], gaps: ['Ansible', 'Jenkins', 'SC clearance required before start'] }
  ],

  submissions: [
    { group: 'Live', company: 'Sky', title: 'Senior Platform Engineer', phase: 'Acknowledged', days: 4, closed: false, stale: false,
      events: [['2 Sep', 'Acknowledged', 'read from email'], ['29 Aug', 'Sent through the employer system', 'you']] },
    { group: 'Live', company: 'Monzo', title: 'Backend Engineer (Go)', phase: 'Screening booked', days: 1, closed: false, stale: false,
      events: [['1 Sep', 'Screening booked for 8 September', 'read from email'], ['30 Aug', 'Acknowledged', 'read from email'], ['28 Aug', 'Sent', 'you']] },
    { group: 'Live', company: 'Ocado', title: 'Data Platform Engineer', phase: 'Sent', days: 15, closed: false, stale: true,
      events: [['18 Aug', 'Sent through the board', 'you']] },
    { group: 'Closed', company: 'Capgemini', title: 'DevOps Engineer', phase: 'Rejected', days: 9, closed: true, stale: false,
      events: [['24 Aug', 'Rejected', 'read from email'], ['16 Aug', 'Sent', 'you']] }
  ],
  drafts: [
    { company: 'Wise', title: 'Lead Engineer, Payments', written: 'written 1 Sep', revision: 2, channel: 'the employer’s own system', urlSource: 'Posting' },
    { company: 'Starling Bank', title: 'Site Reliability Engineer', written: 'written 31 Aug', revision: 1, channel: 'the job board', urlSource: 'MatchedOnAnotherBoard' }
  ],

  searches: [
    { term: 'platform engineer', slug: 'platform-engineer', where: 'United Kingdom',
      boards: ['LinkedIn', 'Indeed', 'Glassdoor', 'freehire.me', 'ZipRecruiter'], rows: 3918,
      last: 'today, 04:12', on: true, published: true, publishedUtc: 'today, 04:02', hoursOld: 72, wanted: 400 },
    { term: 'data engineer', slug: 'data-engineer', where: 'United Kingdom',
      boards: ['LinkedIn', 'Indeed'], rows: 2104,
      last: 'today, 04:12', on: true, published: true, publishedUtc: 'today, 04:02', hoursOld: 72, wanted: 250 },
    { term: 'security engineer', slug: 'security-engineer', where: 'London',
      boards: ['LinkedIn', 'Glassdoor'], rows: 881,
      last: '28 Aug, 04:09', on: false, published: false, publishedUtc: '28 Aug, 04:02', hoursOld: 48, wanted: 150 }
  ],

  profile: {
    name: 'Pablo D.', headline: 'Platform engineer', where: 'Manchester', rtw: 'United Kingdom, no sponsorship needed',
    arrangement: 'Remote or hybrid', maxDaysInOffice: 2, floor: 80000, notice: 'One month', levels: 'Senior or lead',
    experience: [
      { role: 'Platform Engineer', company: 'Auto Trader', when: '2022 – present',
        text: 'Owned the Kubernetes platform four product teams deploy onto. Moved the estate from hand-rolled manifests to a paved road, and took deploy time from forty minutes to six.' },
      { role: 'Senior Software Engineer', company: 'The Hut Group', when: '2019 – 2022',
        text: 'Payments and order capture, .NET and Azure. Led the team that took card processing in-house, which is the piece of work the payments adverts keep matching against.' },
      { role: 'Software Engineer', company: 'Zuto', when: '2016 – 2019',
        text: 'Backend services in C#, and the first person there to put anything in a container.' }
    ],
    education: [{ role: 'BSc Computer Science', company: 'University of Manchester', when: '2012 – 2016', text: 'First class.' }],
    projects: [
      { role: 'job-platform', company: 'Personal', when: '2026',
        text: 'The thing you are looking at. Scraper on a NAS, everything else on Azure, one concept graph shared by postings and profiles so matching is a join.' },
      { role: 'ppdm', company: 'Personal', when: '2024',
        text: 'A parquet-backed store for time series that did not need a database.' }
    ],
    certifications: [
      { role: 'AZ-305 Azure Solutions Architect', company: 'Microsoft', when: '2024' },
      { role: 'CKA Certified Kubernetes Administrator', company: 'CNCF', when: '2023' }
    ],
    links: ['github.com/pa741', 'linkedin.com/in/pablo-d', 'pablo.dev'],
    declared: [['skill.kubernetes', 'Expert, 6 years'], ['skill.azure', 'Expert, 8 years'], ['skill.docker', 'Expert, 7 years'],
      ['skill.python', 'Working, 4 years'], ['skill.sql', 'Working, 9 years'], ['skill.bicep', 'Working, 3 years']],
    /* The half the model read out of the prose, with the phrase it read it
       from. This is what makes an inference checkable by the candidate, and
       it is the side of the profile they most need to see. */
    extracted: [
      ['skill.helm', 'moved the estate from hand-rolled manifests to a paved road'],
      ['skill.typescript', 'the first person there to put anything in a container'],
      ['skill.react', null]
    ]
  },

  /* AiCallOutcome: Succeeded | PartiallyDiscarded | Failed. `discarded` is the
     number the ledger exists for - answers paid for and thrown away. There is
     no money on this page: the API records tokens, and no unit price is stored
     anywhere in the system. */
  calls: [
    { t: '04:12:07', purpose: 'Extraction batch', model: 'gpt-5.6-luna', items: 10, out: 'Succeeded', discarded: 0, tok: 84210, reasoning: 12040, ms: 6240, reason: null },
    { t: '04:11:58', purpose: 'Extraction batch', model: 'gpt-5.6-luna', items: 10, out: 'PartiallyDiscarded', discarded: 3, tok: 81964, reasoning: 11890, ms: 5980, reason: 'Three answers did not align to a posting id and were dropped' },
    { t: '04:10:31', purpose: 'Match assessment', model: 'gpt-5.6-luna', items: 1, out: 'Succeeded', discarded: 0, tok: 31420, reasoning: 9870, ms: 4110, reason: null },
    { t: '04:09:02', purpose: 'Match assessment', model: 'gpt-5.6-luna', items: 1, out: 'Failed', discarded: 1, tok: 30886, reasoning: 9120, ms: 3980, reason: '429 from the deployment, postings 41188 and 41154 unassessed' },
    { t: '23:40:16', purpose: 'Application draft', model: 'gpt-5.6-sol', items: 1, out: 'Succeeded', discarded: 0, tok: 12408, reasoning: 4210, ms: 9120, reason: null }
  ],
  callTotals: { calls: 41, succeeded: 38, partial: 2, failed: 1, tokens: 1240000, reasoning: 380000, discarded: 11, medianMs: 5100 }
};

const fmt = (n) => n.toLocaleString('en-GB');
const conceptOf = (k) => DATA.concepts.find(c => c.key === k);
const labelOf = (k) => (conceptOf(k) || {label:k}).label;
const held = new Set(
  DATA.profile.declared.map((d) => d[0]).concat(DATA.profile.extracted.map((e) => e[0]))
);

function nearMiss(key) {
  const c = conceptOf(key);
  if (!c) return null;
  const down = c.narrower.find((k) => held.has(k));
  if (down) return { key: down, rel: 'Specialisation' };
  const side = c.related.find((k) => held.has(k));
  if (side) return { key: side, rel: 'Related' };
  const back = DATA.concepts.find((x) => held.has(x.key) && x.related.includes(key));
  if (back) return { key: back.key, rel: 'Related' };
  const up = c.broader.find((k) => held.has(k));
  if (up) return { key: up, rel: 'Generalisation' };
  return null;
}

const REL_CONSEQUENCE = {
  Specialisation: 'satisfies it outright and scores as a full hit',
  Generalisation: 'is broader than what they ask for, so it earns partial credit',
  Related: 'is recorded as comparable rather than equivalent, so it earns partial credit and never full'
};

function antiJoinRows() {
  return DATA.demand
    .filter((d) => !held.has(d.key))
    .sort((a, b) => b.inBand - a.inBand)
    .map((d) => {
      const c = conceptOf(d.key);
      const near = nearMiss(d.key);
      let why;
      if (c.kind === 'Qualification') {
        why = 'A qualification, not a skill: it cannot be picked up before an application closes, so this is a ' +
          'filter on what you can apply to rather than something to learn. <em>' + d.inBand + ' of your matches ' +
          'are gated behind it.</em>';
      } else if (near) {
        why = 'You hold <em>' + labelOf(near.key) + '</em>, which the graph records as <em>' + near.rel +
          '</em> &mdash; it ' + REL_CONSEQUENCE[near.rel] + '.';
      } else {
        why = 'Nothing in your profile touches it, by any relation in the graph. This is the gap with no ' +
          'partial credit behind it.';
      }
      return '<div class="gap"><span class="nm">' + c.label + '</span>' +
        '<span class="counts"><b>' + d.inBand + '</b> of your 248 &middot; ' + fmt(d.postings) + ' in the corpus</span>' +
        '<p class="why">' + why + '</p></div>';
    }).join('');
}



console.log('held:', [...held].join(', '));
const rows = antiJoinRows();
console.log('
--- anti-join rows ---');
console.log(rows.replace(/<[^>]+>/g, '|').replace(/\|+/g, ' | ').trim());
console.log('
--- shortlist order vs score order ---');
const byScore = DATA.matches.slice().sort((a,b)=>b.score-a.score).map(m=>m.id);
DATA.matches.forEach((m,i) => {
  const sp = byScore.indexOf(m.id)+1;
  console.log(String(i+1).padStart(2), m.score, m.company.padEnd(15), 'scores', sp, sp!==i+1 ? '(moved)' : '');
});
console.log('
arrivals:', DATA.matches.filter(m=>m.arrived).length,
            '| changed:', DATA.matches.filter(m=>m.changed).length,
            '| drafts:', DATA.drafts.length,
            '| quiet:', DATA.submissions.filter(a=>a.stale).map(a=>a.company+' '+a.days+'d').join(','));

