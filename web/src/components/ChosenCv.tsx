import { useCallback, useState } from 'react';
import type { JobPlatformApi } from '../api/client';
import type { CvChoice } from '../api/types';
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
 * <b>A tie is shown as a tie.</b> The server answers with the arithmetic's outcome and never spends
 * a model call behind a page load, so where two CVs fit equally well this says so and names them.
 * The choice is made when an application is actually assembled.
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

  return (
    <>
      <h4 className="mini">CV</h4>

      {cv.outcome === 'Chosen' && cv.chosen && (
        <>
          <p className="note">
            <b>{cv.chosen.label}</b> — your own CV, chosen for this posting. It answers{' '}
            {cv.chosen.answered} of what the advert states, scoring {cv.chosen.score}.
          </p>
          <Download api={api} variantId={cv.chosen.variantId} label={cv.chosen.label} />
        </>
      )}

      {cv.outcome === 'Ambiguous' && (
        <p className="note">
          {cv.tied.length > 1
            ? <>Nothing separates {cv.tied.map((v) => v.label).join(' and ')}, so no CV is
                pre-selected. One is chosen when the application is assembled.</>
            : <>No CV is pre-selected for this posting.</>}
        </p>
      )}

      {cv.outcome === 'NoFit' && (
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
 * The variant's own rendered file.
 *
 * Both formats, because an applicant tracking system may take only one and several large vendors
 * parse the DOCX more reliably. Rendered when the candidate wrote it rather than now: this is the
 * document that would be uploaded, not a preview of one.
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
      .then((blob) => {
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = `${label}.${format}`;
        link.click();
        URL.revokeObjectURL(url);
      })
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
