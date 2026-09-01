import { useCallback, useEffect, useState } from 'react';
import type { JobPlatformApi } from '../api/client';
import type {
  DeclaredSkill, ProfileCertification, ProfileEducation, ProfileExperience,
  ProfileLink, ProfileProject, ProfileRequest, ProfileResponse, SkillLevel,
} from '../api/types';
import { Card, ErrorNote, Field } from '../components/Primitives';

const ARRANGEMENTS = ['Unknown', 'Remote', 'Hybrid', 'OnSite'] as const;
const SENIORITIES = ['Unknown', 'Intern', 'Junior', 'Mid', 'Senior', 'Lead', 'Principal', 'Executive'] as const;
const LEVELS: SkillLevel[] = ['Familiar', 'Proficient', 'Expert'];

const EMPTY: ProfileRequest = {
  fullName: null, headline: null, email: null, phone: null, summary: null,
  locationCity: null, locationCountry: null, willingToRelocate: false,
  preferredArrangement: 'Unknown', maxDaysInOffice: null,
  minimumSalary: null, salaryCurrency: 'GBP', jobTypes: [],
  yearsExperience: null, seniority: 'Unknown',
  experiences: [], education: [], projects: [], certifications: [],
  languages: [], links: [], declaredSkills: [],
};

/**
 * The profile form.
 *
 * **A form, not a CV upload.** Parsing a PDF back into structure is a lossy guess at something
 * the person already knows - which employer, which dates, which bullet point is the one that
 * matters. Asking directly skips the guess and produces a record with fields, which is what
 * makes matching a join and a tailored CV an output rather than a rewrite of an input.
 *
 * The whole form is submitted at once and replaces what was stored. A partial update has no way
 * to express "delete the third job", which is a thing people do.
 */
export function Profile({ api, onSaved }: { api: JobPlatformApi; onSaved?: () => void }) {
  const [form, setForm] = useState<ProfileRequest>();
  const [stored, setStored] = useState<ProfileResponse | null>();
  const [error, setError] = useState<unknown>();
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);

  const load = useCallback(() => {
    setError(undefined);
    api.profile()
      .then((result) => {
        setStored(result);
        setForm(result ? { ...result } : { ...EMPTY });
      })
      .catch(setError);
  }, [api]);

  useEffect(load, [load]);

  const set = <K extends keyof ProfileRequest>(key: K, value: ProfileRequest[K]) => {
    setSaved(false);
    setForm((current) => (current ? { ...current, [key]: value } : current));
  };

  const save = () => {
    if (!form) return;

    setSaving(true);
    setError(undefined);

    api.saveProfile(form)
      .then((result) => {
        setStored(result);
        setForm({ ...result });
        setSaved(true);
        onSaved?.();
      })
      .catch(setError)
      .finally(() => setSaving(false));
  };

  if (error && !form) return <ErrorNote error={error} onRetry={load} />;
  if (!form) return <div className="empty">Loading…</div>;

  return (
    <div className="grid">
      {error ? <ErrorNote error={error} /> : null}

      <Card
        title="About you"
        subtitle={
          stored?.updatedUtc
            ? `Last saved ${new Date(stored.updatedUtc).toLocaleString()}`
            : 'Not saved yet. Nothing is matched until you save.'
        }
        actions={
          <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
            {saved && <span className="pill">Saved</span>}
            <button className="btn" onClick={save} disabled={saving}>
              {saving ? 'Saving…' : 'Save profile'}
            </button>
          </div>
        }
      >
        <div className="form-grid">
          <Field label="Full name">
            <input value={form.fullName ?? ''} onChange={(e) => set('fullName', e.target.value || null)} />
          </Field>
          <Field label="Headline" hint="What you call yourself. Appears on the generated CV.">
            <input value={form.headline ?? ''} onChange={(e) => set('headline', e.target.value || null)} />
          </Field>
          <Field label="Email">
            <input type="email" value={form.email ?? ''} onChange={(e) => set('email', e.target.value || null)} />
          </Field>
          <Field label="Phone">
            <input value={form.phone ?? ''} onChange={(e) => set('phone', e.target.value || null)} />
          </Field>
          <Field label="City">
            <input value={form.locationCity ?? ''} onChange={(e) => set('locationCity', e.target.value || null)} />
          </Field>
          <Field label="Country">
            <input value={form.locationCountry ?? ''} onChange={(e) => set('locationCountry', e.target.value || null)} />
          </Field>
        </div>

        <Field
          label="Summary"
          hint="In your own words. This is read for skills as well as reproduced on the CV, so specifics beat adjectives."
        >
          <textarea
            rows={4}
            value={form.summary ?? ''}
            onChange={(e) => set('summary', e.target.value || null)}
          />
        </Field>
      </Card>

      <Card title="What you are looking for" subtitle="Used to score every posting against you.">
        <div className="form-grid">
          <Field label="Working arrangement" hint="Unknown means no preference, and scores neutrally.">
            <select
              value={form.preferredArrangement ?? 'Unknown'}
              onChange={(e) => set('preferredArrangement', e.target.value as ProfileRequest['preferredArrangement'])}
            >
              {ARRANGEMENTS.map((value) => (
                <option key={value} value={value}>
                  {value === 'OnSite' ? 'On-site' : value === 'Unknown' ? 'No preference' : value}
                </option>
              ))}
            </select>
          </Field>

          <Field label="Most days a week in the office">
            <input
              type="number" min={0} max={5}
              value={form.maxDaysInOffice ?? ''}
              onChange={(e) => set('maxDaysInOffice', numberOrNull(e.target.value))}
            />
          </Field>

          <Field label="Minimum salary" hint="Annualised. The floor, not the target.">
            <input
              type="number" min={0}
              value={form.minimumSalary ?? ''}
              onChange={(e) => set('minimumSalary', numberOrNull(e.target.value))}
            />
          </Field>

          <Field label="Currency">
            <input
              value={form.salaryCurrency ?? ''}
              onChange={(e) => set('salaryCurrency', e.target.value || null)}
            />
          </Field>

          <Field label="Years of experience" hint="Asked for rather than summed: overlaps and breaks make the sum wrong.">
            <input
              type="number" min={0} max={70}
              value={form.yearsExperience ?? ''}
              onChange={(e) => set('yearsExperience', numberOrNull(e.target.value))}
            />
          </Field>

          <Field label="Seniority">
            <select value={form.seniority ?? 'Unknown'} onChange={(e) => set('seniority', e.target.value)}>
              {SENIORITIES.map((value) => (
                <option key={value} value={value}>{value === 'Unknown' ? 'Not stated' : value}</option>
              ))}
            </select>
          </Field>
        </div>

        <label className="check">
          <input
            type="checkbox"
            checked={form.willingToRelocate}
            onChange={(e) => set('willingToRelocate', e.target.checked)}
          />
          I would relocate for the right role
        </label>
      </Card>

      <Repeater<ProfileExperience>
        title="Experience"
        subtitle="The richest input there is. What you did, in your own words - it is read for skills and rewritten into tailored bullet points."
        items={form.experiences}
        onChange={(items) => set('experiences', items)}
        blank={{
          company: '', title: '', startDate: null, endDate: null,
          locationCity: null, locationCountry: null, description: null,
        }}
        addLabel="Add a role"
        render={(item, update) => (
          <>
            <div className="form-grid">
              <Field label="Job title">
                <input value={item.title} onChange={(e) => update({ ...item, title: e.target.value })} />
              </Field>
              <Field label="Company">
                <input value={item.company} onChange={(e) => update({ ...item, company: e.target.value })} />
              </Field>
              <Field label="From">
                <input
                  type="month"
                  value={month(item.startDate)}
                  onChange={(e) => update({ ...item, startDate: fromMonth(e.target.value) })}
                />
              </Field>
              <Field label="To" hint="Leave empty if this is your current role.">
                <input
                  type="month"
                  value={month(item.endDate)}
                  onChange={(e) => update({ ...item, endDate: fromMonth(e.target.value) })}
                />
              </Field>
            </div>
            <Field label="What you did">
              <textarea
                rows={4}
                value={item.description ?? ''}
                onChange={(e) => update({ ...item, description: e.target.value || null })}
              />
            </Field>
          </>
        )}
      />

      <Repeater<ProfileEducation>
        title="Education"
        items={form.education}
        onChange={(items) => set('education', items)}
        blank={{
          institution: '', qualification: '', fieldOfStudy: null,
          startDate: null, endDate: null, grade: null, description: null,
        }}
        addLabel="Add a qualification"
        render={(item, update) => (
          <div className="form-grid">
            <Field label="Qualification">
              <input value={item.qualification} onChange={(e) => update({ ...item, qualification: e.target.value })} />
            </Field>
            <Field label="Institution">
              <input value={item.institution} onChange={(e) => update({ ...item, institution: e.target.value })} />
            </Field>
            <Field label="Subject">
              <input
                value={item.fieldOfStudy ?? ''}
                onChange={(e) => update({ ...item, fieldOfStudy: e.target.value || null })}
              />
            </Field>
            <Field label="Grade">
              <input value={item.grade ?? ''} onChange={(e) => update({ ...item, grade: e.target.value || null })} />
            </Field>
          </div>
        )}
      />

      <Repeater<ProfileProject>
        title="Projects"
        subtitle="Kept separate from employment on purpose: a posting's requirements match against these just as well, and for some people this is the stronger half."
        items={form.projects}
        onChange={(items) => set('projects', items)}
        blank={{ name: '', description: null, url: null, completedOn: null }}
        addLabel="Add a project"
        render={(item, update) => (
          <>
            <div className="form-grid">
              <Field label="Name">
                <input value={item.name} onChange={(e) => update({ ...item, name: e.target.value })} />
              </Field>
              <Field label="Link">
                <input value={item.url ?? ''} onChange={(e) => update({ ...item, url: e.target.value || null })} />
              </Field>
            </div>
            <Field label="What it is">
              <textarea
                rows={3}
                value={item.description ?? ''}
                onChange={(e) => update({ ...item, description: e.target.value || null })}
              />
            </Field>
          </>
        )}
      />

      <Repeater<ProfileCertification>
        title="Certifications"
        items={form.certifications}
        onChange={(items) => set('certifications', items)}
        blank={{ name: '', issuer: null, year: null }}
        addLabel="Add a certification"
        render={(item, update) => (
          <div className="form-grid">
            <Field label="Name">
              <input value={item.name} onChange={(e) => update({ ...item, name: e.target.value })} />
            </Field>
            <Field label="Issuer">
              <input value={item.issuer ?? ''} onChange={(e) => update({ ...item, issuer: e.target.value || null })} />
            </Field>
            <Field label="Year">
              <input
                type="number" min={1950} max={2100}
                value={item.year ?? ''}
                onChange={(e) => update({ ...item, year: numberOrNull(e.target.value) })}
              />
            </Field>
          </div>
        )}
      />

      <Repeater<ProfileLink>
        title="Links"
        items={form.links}
        onChange={(items) => set('links', items)}
        blank={{ label: '', url: '' }}
        addLabel="Add a link"
        render={(item, update) => (
          <div className="form-grid">
            <Field label="Label" hint="GitHub, LinkedIn, portfolio…">
              <input value={item.label} onChange={(e) => update({ ...item, label: e.target.value })} />
            </Field>
            <Field label="URL">
              <input value={item.url} onChange={(e) => update({ ...item, url: e.target.value })} />
            </Field>
          </div>
        )}
      />

      <Skills
        declared={form.declaredSkills}
        onChange={(items) => set('declaredSkills', items)}
        extracted={stored?.extractedSkills ?? []}
        extractedAtUtc={stored?.extractedAtUtc ?? null}
      />

      <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 10 }}>
        <button className="btn" onClick={save} disabled={saving}>
          {saving ? 'Saving…' : 'Save profile'}
        </button>
      </div>
    </div>
  );
}

/**
 * Declared skills, and what the model read out of the prose.
 *
 * The two are shown apart, and that is the point. What somebody said about themselves and what
 * was inferred about them are different claims, and a person is entitled to see which is which -
 * and to see the phrase an inference came from, so they can tell whether it is right.
 */
function Skills({ declared, onChange, extracted, extractedAtUtc }: {
  declared: DeclaredSkill[];
  onChange: (items: DeclaredSkill[]) => void;
  extracted: { conceptKey: string; label: string; level: string; years: number | null; evidence: string | null }[];
  extractedAtUtc: string | null;
}) {
  const [key, setKey] = useState('');
  const [level, setLevel] = useState<SkillLevel>('Proficient');

  const add = () => {
    const trimmed = key.trim();
    if (!trimmed) return;

    onChange([...declared.filter((s) => s.conceptKey !== trimmed), { conceptKey: trimmed, level, years: null }]);
    setKey('');
  };

  return (
    <Card
      title="Skills"
      subtitle="Concept keys from the shared vocabulary, so a claim here and a requirement in an advert are the same thing rather than two spellings of it."
    >
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'flex-end' }}>
        <Field label="Concept key" hint="e.g. skill.kubernetes">
          <input
            value={key}
            onChange={(e) => setKey(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); add(); } }}
          />
        </Field>
        <Field label="Level">
          <select value={level} onChange={(e) => setLevel(e.target.value as SkillLevel)}>
            {LEVELS.map((value) => <option key={value} value={value}>{value}</option>)}
          </select>
        </Field>
        <button className="btn" onClick={add}>Add</button>
      </div>

      {declared.length > 0 && (
        <div className="chips">
          {declared.map((skill) => (
            <span key={skill.conceptKey} className="pill">
              {skill.conceptKey} · {skill.level ?? 'Proficient'}
              <button
                className="chip-x"
                aria-label={`Remove ${skill.conceptKey}`}
                onClick={() => onChange(declared.filter((s) => s.conceptKey !== skill.conceptKey))}
              >
                ×
              </button>
            </span>
          ))}
        </div>
      )}

      <h3 style={{ fontSize: 13, marginTop: 18 }}>Read from what you wrote</h3>
      {extracted.length === 0 ? (
        <p className="muted" style={{ fontSize: 13 }}>
          {extractedAtUtc
            ? 'Nothing was inferred beyond what you declared.'
            : 'Save the form and your prose is read for skills you did not list.'}
        </p>
      ) : (
        <div className="scroll-x">
          <table>
            <thead>
              <tr><th>Skill</th><th>Level</th><th>Read from</th></tr>
            </thead>
            <tbody>
              {extracted.map((skill) => (
                <tr key={skill.conceptKey}>
                  <td>{skill.label}</td>
                  <td>{skill.level}</td>
                  <td className="muted">{skill.evidence ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
}

/** A repeated section of the form: add, remove, reorder by position. */
function Repeater<T>({ title, subtitle, items, onChange, blank, addLabel, render }: {
  title: string;
  subtitle?: string;
  items: T[];
  onChange: (items: T[]) => void;
  blank: T;
  addLabel: string;
  render: (item: T, update: (next: T) => void) => React.ReactNode;
}) {
  return (
    <Card
      title={title}
      subtitle={subtitle}
      actions={<button className="btn" onClick={() => onChange([...items, blank])}>{addLabel}</button>}
    >
      {items.length === 0 && <p className="muted" style={{ fontSize: 13 }}>Nothing added yet.</p>}

      {items.map((item, index) => (
        <div key={index} className="repeat-item">
          <div style={{ flex: 1 }}>
            {render(item, (next) => onChange(items.map((existing, i) => (i === index ? next : existing))))}
          </div>
          <div className="repeat-actions">
            {/* Order is the candidate's choice - leading with a side contract rather than the
                most recent job is sometimes exactly right - so it is theirs to set. */}
            <button
              className="btn" aria-label="Move up" disabled={index === 0}
              onClick={() => onChange(swap(items, index, index - 1))}
            >
              ↑
            </button>
            <button
              className="btn" aria-label="Move down" disabled={index === items.length - 1}
              onClick={() => onChange(swap(items, index, index + 1))}
            >
              ↓
            </button>
            <button
              className="btn" aria-label="Remove"
              onClick={() => onChange(items.filter((_, i) => i !== index))}
            >
              Remove
            </button>
          </div>
        </div>
      ))}
    </Card>
  );
}

/**
 * Reorders two items.
 *
 * An explicit map rather than a destructured swap: `noUncheckedIndexedAccess` types an indexed
 * read as possibly undefined, and going through a temporary would need two non-null assertions
 * to state something the callers already guarantee - both indices come from a bounded loop.
 */
function swap<T>(items: T[], a: number, b: number): T[] {
  return items.map((item, index) => {
    if (index === a) return items[b] as T;
    if (index === b) return items[a] as T;
    return item;
  });
}

function numberOrNull(value: string): number | null {
  if (value.trim() === '') return null;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

/** ISO date to the `yyyy-MM` an `<input type="month">` wants. */
function month(value: string | null): string {
  return value ? value.slice(0, 7) : '';
}

/** `yyyy-MM` back to the first of that month, which is the precision a CV needs. */
function fromMonth(value: string): string | null {
  return value ? `${value}-01` : null;
}
