import { useEffect, useState } from "react";
import { describeError, formatWhen, inspect, library, type DocumentDetailDto } from "../api";
import { Button, Empty, Icon, PageHeader, Section, Spinner } from "../ui";

interface Props {
  projectId: string;
  documentId: string;
  onBack: () => void;
  onError: (message: string | null) => void;
}

// The path and file name are already in the header; repeating them on every passage adds nothing.
const HIDDEN_METADATA = new Set(["document_id", "file_name", "provider_id"]);

/** Passages longer than this start folded, so a long document stays scannable. */
const FOLD_CHARACTERS = 600;

/** One document exactly as the index holds it: its overview and every passage retrieval can return. */
export function DocumentView({ projectId, documentId, onBack, onError }: Props) {
  const [detail, setDetail] = useState<DocumentDetailDto | null>(null);
  const [missing, setMissing] = useState(false);
  const [open, setOpen] = useState<Set<number>>(new Set());

  useEffect(() => {
    let active = true;
    inspect.document({ projectId, documentId })
      .then((next) => active && setDetail(next))
      .catch((failure) => {
        if (active) {
          setMissing(true);
          onError(describeError(failure));
        }
      });
    return () => {
      active = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, documentId]);

  const back = (
    <Button variant="quiet" size="sm" onClick={onBack}>
      <Icon name="back" /> All documents
    </Button>
  );

  if (!detail) {
    return (
      <div>
        {back}
        {missing ? (
          <Empty title="This document is not indexed" icon="file">It may have been removed or changed since the list loaded.</Empty>
        ) : (
          <p className="row muted"><Spinner label="Loading" /> Loading…</p>
        )}
      </div>
    );
  }

  const characters = detail.passages.reduce((sum, p) => sum + p.text.length, 0);
  const average = detail.passages.length === 0 ? 0 : Math.round(characters / detail.passages.length);
  const shell = (call: typeof library.openDocument) =>
    void call({ projectId, documentId, page: null }).catch((failure) => onError(describeError(failure)));

  function toggle(index: number) {
    setOpen((current) => {
      const next = new Set(current);
      if (next.has(index)) {
        next.delete(index);
      } else {
        next.add(index);
      }

      return next;
    });
  }

  return (
    <div>
      {back}
      <PageHeader
        title={detail.fileName}
        description={<span className="path">{detail.folder}</span>}
        actions={
          <>
            <Button onClick={() => shell(library.openDocument)}><Icon name="external" /> Open</Button>
            <Button onClick={() => shell(library.revealDocument)}><Icon name="folder" /> Show in folder</Button>
          </>
        }
      />

      <dl className="stats section-gap" aria-label="Document size">
        <div className="stat"><dt>Passages</dt><dd>{detail.passages.length}</dd></div>
        <div className="stat"><dt>Characters</dt><dd>{characters.toLocaleString()}</dd></div>
        <div className="stat"><dt>Average passage</dt><dd>{average.toLocaleString()}</dd></div>
        <div className="stat"><dt>Last indexed</dt><dd className="stat-text">{detail.indexedUtc ? formatWhen(detail.indexedUtc) : "Not recorded"}</dd></div>
      </dl>

      <Section
        title="Overview"
        description="The summary answers see alongside passages from this document."
      >
        {detail.overview ? (
          <div className="card overview-text">{detail.overview}</div>
        ) : (
          <Empty title="No overview yet">Overviews are written in the background when a chat model is set up and overviews are on in Settings.</Empty>
        )}
      </Section>

      <Section
        title="Passages"
        description="The text retrieval can return, in document order. Check here when an answer misses something the file contains: if it is not in a passage, the parser did not extract it."
      >
        <ol className="passages" aria-label="Passages">
          {detail.passages.map((passage) => {
            const long = passage.text.length > FOLD_CHARACTERS;
            const expanded = open.has(passage.chunkIndex);
            const metadata = passage.metadata.filter((m) => !HIDDEN_METADATA.has(m.key));
            return (
              <li key={passage.chunkIndex} className="card passage">
                <div className="passage-head">
                  <span className="badge badge-accent">#{passage.chunkIndex + 1}</span>
                  <span className="list-meta">{passage.text.length.toLocaleString()} characters</span>
                  {metadata.map((m) => (
                    <span key={m.key} className="badge" title={m.key}>{label(m.key)}: {m.value}</span>
                  ))}
                </div>
                <p className={long && !expanded ? "hit-text folded" : "hit-text"}>{passage.text}</p>
                {long ? (
                  <Button variant="quiet" size="sm" onClick={() => toggle(passage.chunkIndex)} aria-expanded={expanded}>
                    {expanded ? "Show less" : "Show all"}
                  </Button>
                ) : null}
              </li>
            );
          })}
        </ol>
      </Section>
    </div>
  );
}

function label(key: string): string {
  const spaced = key.replace(/_/g, " ");
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}
