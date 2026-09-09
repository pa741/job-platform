import { useCallback, useState } from 'react';
import type { JobPlatformApi } from '../api/client';
import { saveFile } from '../api/save';
import type { CvChoice, CvChoiceVariant } from '../api/types';
import { ErrorNote } from './Primitives';
import { useApiResource } from './useApiResource';
import { LoadingRegion, WakingRegion } from './WakingRegion';

/**
 * Which of the candidate's own CVs goes with this posting.
 *
 * <b>This panel exists because the one before it was blank.</b> The pages rendered the draft's
 * `curriculumVitaeMarkdown`, and nothing has written one since the CV stopped being generated per
 * posting: it is chosen from the variants the candidate wrote themselves. So the panel showed an
 * empty box beside a perfectly good cover letter, which reads as "the CV failed" rather than as
 * "the CV is one you already wrote".
 *
 * <b>What is shown is the choice and the reasoning, never a generated document.</b> The point of
 * the library is that no model writes the CV; a page that rendered one here would be the first
 * thing to undo that. The file offered is the variant's own render — the same bytes an employer
 * would receive — from the library's download route rather than from the draft's.
 *
 * <b>A tie offers both files and a way to settle it.</b> The server answers with the arithmetic's
 * outcome and never spends a model call behind a page load, so where two CVs fit equally well
 * this says so and names them. It used to stop there, which was the wrong place to stop: on this
 * candidate's corpus half of all drafted postings tie, so half the time the page said "no CV is
 * pre-selected" and offered nothing to read — on the one screen whose job is to show what is
 * about to go out. Now every candidate is downloadable, and choosing one is a click, because the
 * person is the only party in this decision who is not inferring.
 */
export function ChosenCv({ api, postingId }: { api: JobPlatformApi; postingId: number }) {
  const load = useCallback(() => api.cvChoice(postingId), [api, postingId]);
  const choice = useApiResource<CvChoice>(load);

  if (choice.state.status === 'waking') {
    return <WakingRegion what="Your CV library" onRetry={choice.reload} />;
  }
  if (choice.state.status === 'error') {
    return <ErrorNote error={choice.state.error} onRetry={choice.reload} />;
  }
  if (choice.state.status === 'loading') return <LoadingRegion what="which CV fits" />;

  const cv = choice.state.data;
  const yours = cv.chosenByCandidate;

  return (
    <>
      <h4 className="mini">CV</h4>

      {/* Their own decision leads, whatever the arithmetic made of the field: it is what will
          actually be sent, and the pack reads it before it reads the scores. */}
      {yours && (
        <>
          <p className="note">
            <b>{yours.label}</b> — you chose this one for this posting
            {yours.score !== null ? <>, scoring {yours.score} against the advert</> : null}. It is
            what an agent will upload.
          </p>

          {!yours.isSendable && (
            <p className="err">
              It cannot be sent as it stands — it has been archived, or rewritten without being
              rendered again. Until that is fixed the scores decide this posting instead.
            </p>
          )}

          <Download api={api} variantId={yours.variantId} label={yours.label} />

          <Decide
            api={api}
            postingId={postingId}
            variantId={null}
            label="Let the scores decide again"
            onDone={choice.reload}
          />
        </>
      )}

      {!yours && cv.outcome === 'Chosen' && cv.chosen && (
        <>
          <p className="note">
            <b>{cv.chosen.label}</b> — your own CV, chosen for this posting. It answers{' '}
            {cv.chosen.answered} of what the advert states, scoring {cv.chosen.score}.
          </p>
          <Download api={api} variantId={cv.chosen.variantId} label={cv.chosen.label} />
        </>
      )}

      {/* The tie, with both documents in reach. Nothing here spends a model call: the pack
          settles it at send time, and the buttons below settle it now if you would rather. */}
      {!yours && cv.outcome === 'Ambiguous' && (
        cv.tied.length > 1
          ? (
            <>
              <p className="note">
                Nothing separates {cv.tied.map((v) => v.label).join(' and ')}. Read either below —
                these are the files an employer would receive. Left alone, one is picked when the
                application is assembled; pick it yourself and that stands instead.
              </p>

              {cv.tied.map((tied) => (
                <Tied
                  key={tied.variantId}
                  api={api}
                  postingId={postingId}
                  tied={tied}
                  onChosen={choice.reload}
                />
              ))}
            </>
          )
          : <p className="note">No CV is pre-selected for this posting.</p>
      )}

      {!yours && cv.outcome === 'NoFit' && (
        <p className="note">
          {cv.considered === 0
            ? <>You have not written a CV yet, so there is none to send. Your CVs are on the CV
                library page.</>
            : <>None of your {cv.considered} CV{cv.considered === 1 ? '' : 's'} covers enough of
                what this posting asks for, so none is offered — the nearest CV to a job it does
                not fit is a rejection nobody hears the reason for.</>}
        </p>
      )}

      {/* The selector's own sentence, which names the numbers it decided on. It is what makes
          "why that one" answerable months later, so it is shown rather than summarised. */}
      <p className="quote">{cv.rationale}</p>

      {cv.missing.length > 0 && (
        <>
          <h4 className="mini">
            {cv.outcome === 'NoFit' ? 'What the next CV would need' : 'What no CV of yours covers'}
          </h4>
          <ul className="tight">
            {cv.missing.slice(0, 8).map((gap) => <li key={gap.concept}>{gap.label}</li>)}
          </ul>
        </>
      )}
    </>
  );
}

/**
 * One of the tied CVs: what it scored, its file, and the button that ends the tie.
 *
 * The score and the answered count are shown here as they are for a chosen CV. They were in the
 * payload all along and rendered only on the `Chosen` branch, which left the tie as two bare
 * names — the one place where a person actually has to tell two documents apart.
 */
function Tied({ api, postingId, tied, onChosen }: {
  api: JobPlatformApi; postingId: number; tied: CvChoiceVariant; onChosen: () => void;
}) {
  return (
    <div className="draft">
      <p className="note">
        <b>{tied.label}</b> — answers {tied.answered} of what the advert states, scoring{' '}
        {tied.score}.
      </p>

      <Download api={api} variantId={tied.variantId} label={tied.label} />

      <Decide
        api={api}
        postingId={postingId}
        variantId={tied.variantId}
        label={`Send ${tied.label}`}
        onDone={onChosen}
      />
    </div>
  );
}

/**
 * Records which CV to send, or hands the decision back.
 *
 * <b>A write from a page that is otherwise all reads, and it is the one write worth making
 * here.</b> The alternative to a person deciding is a model deciding, over an advert, between two
 * documents somebody wrote about their own working life. The clear button matters as much as the
 * pick: a choice that can only be replaced and never withdrawn is one people are right to be wary
 * of making.
 */
function Decide({ api, postingId, variantId, label, onDone }: {
  api: JobPlatformApi;
  postingId: number;
  variantId: number | null;
  label: string;
  onDone: () => void;
}) {
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<unknown>();

  const decide = () => {
    setSaving(true);
    setError(undefined);

    api.setCvChoice(postingId, variantId)
      .then(onDone)
      .catch(setError)
      .finally(() => setSaving(false));
  };

  return (
    <>
      {error ? <ErrorNote error={error} /> : null}
      <div className="row-actions">
        <button className="btn" disabled={saving} onClick={decide}>
          {saving ? 'Saving…' : label}
        </button>
      </div>
    </>
  );
}

/**
 * The variant's own rendered file.
 *
 * Both formats, because an applicant tracking system may take only one and several large vendors
 * parse the DOCX more reliably. Rendered when the candidate wrote it rather than now: this is the
 * document that would be uploaded, not a preview of one.
 *
 * The filename comes back with the bytes and is used as sent. Every one of these downloads is
 * `Pablo_De_Groot_Curriculum_Vitae.pdf` whichever variant it is, which is the point: the label is
 * for the candidate and never for an employer's file list.
 */
function Download({ api, variantId, label }: {
  api: JobPlatformApi; variantId: number; label: string;
}) {
  const [downloading, setDownloading] = useState<string>();
  const [error, setError] = useState<unknown>();

  const download = (format: 'pdf' | 'docx') => {
    setDownloading(format);
    setError(undefined);

    api.cvVariantFile(variantId, format)
      .then((file) => saveFile(file, `${label}.${format}`))
      .catch(setError)
      .finally(() => setDownloading(undefined));
  };

  return (
    <>
      {error ? <ErrorNote error={error} /> : null}

      <div className="row-actions">
        <button className="btn" disabled={downloading === 'pdf'} onClick={() => download('pdf')}>
          {downloading === 'pdf' ? 'Preparing…' : 'Download CV (PDF)'}
        </button>
        <button className="btn" disabled={downloading === 'docx'} onClick={() => download('docx')}>
          {downloading === 'docx' ? 'Preparing…' : 'DOCX'}
        </button>
      </div>
    </>
  );
}
