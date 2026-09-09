import type { DownloadedFile } from './client';

/**
 * Hands a downloaded file to the browser, under the name the server gave it.
 *
 * <b>The server's name wins, and that is the whole reason this exists.</b> Every CV route sets
 * `Content-Disposition` from `ApplicationPackFile.FileName` — one stable name whichever variant
 * was chosen, because a file list that reads `Pablo_De_Groot_AI_Engineer_CV.pdf` tells an
 * employer a different CV is kept for other roles, which is a true fact they have no business
 * being handed. Each download in this app used to name the file itself, from the variant's label,
 * so the one machine where somebody checks what is about to go out was the one place that rule
 * did not hold.
 *
 * <b>The fallback is for a header that cannot be read rather than one that is wrong.</b> The name
 * only reaches JavaScript when the API lists `Content-Disposition` in its exposed headers, and
 * this app is served from a different origin — so an older API, or a misconfigured one, yields
 * nothing here. A file saved under a locally invented name is a small annoyance; a download that
 * silently does nothing is a bug report.
 *
 * The object URL is revoked immediately after the click. It is a handle to a blob the page would
 * otherwise hold until it is reloaded, and the click has already been dispatched by then.
 */
export function saveFile(file: DownloadedFile, fallback: string): void {
  const url = URL.createObjectURL(file.blob);
  const link = document.createElement('a');

  link.href = url;
  link.download = file.filename ?? fallback;
  link.click();

  URL.revokeObjectURL(url);
}
