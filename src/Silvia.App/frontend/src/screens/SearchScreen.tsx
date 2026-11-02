import { useState, type FormEvent } from "react";
import { chat, describeError, library, type HitDto } from "../api";
import { Banner, Button, Empty } from "../ui";

interface Props {
  projectId: string;
  onError: (message: string | null) => void;
}

export function SearchScreen({ projectId, onError }: Props) {
  const [query, setQuery] = useState("");
  const [hits, setHits] = useState<HitDto[] | null>(null);
  const [busy, setBusy] = useState(false);

  async function run(event: FormEvent) {
    event.preventDefault();
    const text = query.trim();
    if (!text) {
      return;
    }

    setBusy(true);
    onError(null);
    try {
      setHits([...(await chat.search({ projectId, query: text, topK: 10 }))]);
    } catch (failure) {
      onError(describeError(failure));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div>
      <form className="toolbar" role="search" onSubmit={run}>
        <input
          type="search"
          aria-label="Search this project"
          placeholder="Search the passages in this project"
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          style={{ flex: "1 1 20rem" }}
        />
        <Button type="submit" variant="primary" disabled={busy || query.trim().length === 0}>Search</Button>
      </form>
      <p className="muted" style={{ marginTop: 0 }}>Search returns passages without calling a model, so it works offline.</p>

      {hits === null ? null : hits.length === 0 ? (
        <Empty title="No passages matched">Try other words, or add more folders in Library.</Empty>
      ) : (
        <ol className="list" aria-label="Results">
          {hits.map((hit) => (
            <li key={`${hit.documentId}-${hit.chunkIndex}`} className="hit" style={{ display: "block" }}>
              <div className="row" style={{ justifyContent: "space-between" }}>
                <strong>{hit.fileName}</strong>
                <span className="row muted" style={{ gap: "0.4rem" }}>
                  <span className="score" aria-label={`Relevance ${Math.round(scaled(hit.relevance / 1000) * 100)} percent`}>
                    <span style={{ width: `${Math.round(scaled(hit.relevance / 1000) * 100)}%` }} />
                  </span>
                  {(hit.relevance / 1000).toFixed(3)}
                </span>
              </div>
              <p className="path" style={{ margin: "0.2rem 0" }}>{hit.documentId}</p>
              <p className="hit-text">{hit.text}</p>
              <div className="row">
                <Button variant="quiet" onClick={() => void library.openDocument({ projectId, documentId: hit.documentId, page: null }).catch((failure) => onError(describeError(failure)))}>Open</Button>
                <Button variant="quiet" onClick={() => void library.revealDocument({ projectId, documentId: hit.documentId, page: null }).catch((failure) => onError(describeError(failure)))}>Show in folder</Button>
              </div>
            </li>
          ))}
        </ol>
      )}
      {hits === null ? <Banner tone="info">Type a few words to find passages.</Banner> : null}
    </div>
  );
}

/** Cosine scores run from about 0 to 1; the bar shows the part above zero. */
function scaled(score: number): number {
  return Math.max(0, Math.min(1, score));
}
