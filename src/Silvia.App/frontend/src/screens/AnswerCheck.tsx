import type { ClaimDto, VerdictDto } from "../api";
import { Button, Icon, Spinner } from "../ui";

export type CheckState = { status: "checking" } | { status: "done"; verdict: VerdictDto } | { status: "failed"; message: string };

interface Props {
  check: CheckState;
  /** Opens a cited passage by its 1-based number. */
  onCite: (number: number) => void;
  onRetry: () => void;
}

/** The judge's reading of one answer: how many statements the cited passages back, and which ones they do not. */
export function AnswerCheck({ check, onCite, onRetry }: Props) {
  if (check.status === "checking") {
    return (
      <div className="answer-check" role="status">
        <p className="row muted small"><Spinner label="Checking" /> Reading the answer against the passages it cited…</p>
      </div>
    );
  }

  if (check.status === "failed") {
    return (
      <div className="answer-check">
        <p className="small">{check.message}</p>
        <Button size="sm" variant="quiet" onClick={onRetry}><Icon name="refresh" /> Try again</Button>
      </div>
    );
  }

  const { verdict } = check;
  const unsupported = verdict.claims.filter((c) => !c.supported);
  return (
    <div className="answer-check" aria-label="Answer check">
      <div className="row answer-check-head">
        {verdict.claims.length === 0 ? (
          <span className="badge">No statements to check</span>
        ) : unsupported.length === 0 ? (
          <span className="badge badge-ready">All {verdict.claims.length} {plural(verdict.claims.length, "statement")} backed by the passages</span>
        ) : (
          <span className="badge badge-danger">
            {unsupported.length} of {verdict.claims.length} {plural(verdict.claims.length, "statement")} not backed
          </span>
        )}
        {verdict.declines ? <span className="badge">Says the documents do not answer it</span> : verdict.answersQuestion ? null : <span className="badge badge-warn">Does not answer the question</span>}
      </div>
      {verdict.summary ? <p className="small">{verdict.summary}</p> : null}
      {unsupported.length > 0 ? (
        <ul className="claims">
          {unsupported.map((claim, i) => (
            <ClaimRow key={i} claim={claim} onCite={onCite} />
          ))}
        </ul>
      ) : null}
      {verdict.claims.length > unsupported.length ? (
        <details className="claims-all">
          <summary>Every statement</summary>
          <ul className="claims">
            {verdict.claims.map((claim, i) => (
              <ClaimRow key={i} claim={claim} onCite={onCite} />
            ))}
          </ul>
        </details>
      ) : null}
      <p className="field-hint">Graded by the judge model. It can be wrong, so open the passages when it matters.</p>
    </div>
  );
}

export function ClaimRow({ claim, onCite }: { claim: ClaimDto; onCite?: (number: number) => void }) {
  return (
    <li className={claim.supported ? "claim" : "claim claim-unsupported"}>
      <span className="claim-mark" aria-label={claim.supported ? "Backed" : "Not backed"}>{claim.supported ? "✓" : "✗"}</span>
      <span className="grow">
        <q>{claim.text}</q>
        {claim.sources.length > 0 && onCite ? (
          <span className="claim-sources">
            {claim.sources.map((n) => (
              <button key={n} type="button" className="source-number" onClick={() => onCite(n)} aria-label={`Open passage ${n}`}>{n}</button>
            ))}
          </span>
        ) : null}
        {claim.reason ? <span className="claim-reason">{claim.reason}</span> : null}
      </span>
    </li>
  );
}

function plural(count: number, word: string): string {
  return count === 1 ? word : `${word}s`;
}

/**
 * Marks each unsupported statement in an answer's rendered HTML. The judge quotes statements as the
 * answer writes them, so most are found as they are; one that spans markup is left unmarked and still
 * shows in the list under the answer. Only text between tags is searched, never attributes.
 */
export function markUnsupported(html: string, claims: ReadonlyArray<ClaimDto>): string {
  let marked = html;
  for (const claim of claims) {
    if (claim.supported || claim.text.length < 8) {
      continue;
    }

    const needle = escapeHtml(claim.text);
    let from = 0;
    while (from < marked.length) {
      const at = marked.indexOf(needle, from);
      if (at < 0) {
        break;
      }

      // Inside a tag when the last "<" before the match is not yet closed.
      if (marked.lastIndexOf("<", at) > marked.lastIndexOf(">", at)) {
        from = at + needle.length;
        continue;
      }

      marked = `${marked.slice(0, at)}<mark class="unsupported" title="Not backed by the cited passages">${needle}</mark>${marked.slice(at + needle.length)}`;
      break;
    }
  }

  return marked;
}

function escapeHtml(text: string): string {
  // The sanitizer serializes text nodes with only these three escaped.
  return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}
