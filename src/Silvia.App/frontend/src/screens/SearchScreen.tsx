import { useState, type FormEvent } from "react";
import { chat, describeError, library, type HitDto } from "../api";
import { Button, Empty, Icon, PageHeader } from "../ui";

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
      <PageHeader title="Search" description="Find passages in this project. Search never calls a model, so it works offline." />

      <form className="search-bar" role="search" onSubmit={run}>
        <div className="search-field">
          <Icon name="search" />
          <input
            type="search"
            aria-label="Search this project"
            placeholder="Search the passages in this project"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
          />
        </div>
        <Button type="submit" variant="primary" disabled={busy || query.trim().length === 0}>Search</Button>
      </form>
      <p className="search-note">{hits === null ? "Type a few words to find passages." : `${hits.length} passage${hits.length === 1 ? "" : "s"}`}</p>

      {hits === null ? null : hits.length === 0 ? (
        <Empty title="No passages matched" icon="search">Try other words, or add more folders in Library.</Empty>
      ) : (
        <ol className="hits" aria-label="Results">
          {hits.map((hit) => {
            const percent = Math.round(scaled(hit.relevance / 1000) * 100);
            return (
              <li key={`${hit.documentId}-${hit.chunkIndex}`} className="card hit">
                <div className="hit-head">
                  <span className="hit-title">
                    <Icon name="file" /> {hit.fileName}
                  </span>
                  <span className="relevance">
                    <span className="score" aria-label={`Relevance ${percent} percent`}>
                      <span style={{ width: `${percent}%` }} />
                    </span>
                    {(hit.relevance / 1000).toFixed(3)}
                  </span>
                </div>
                <p className="path">{hit.documentId}</p>
                <p className="hit-text">{hit.text}</p>
                <div className="hit-actions">
                  <Button variant="quiet" size="sm" onClick={() => void library.openDocument({ projectId, documentId: hit.documentId, page: null }).catch((failure) => onError(describeError(failure)))}>
                    <Icon name="external" /> Open
                  </Button>
                  <Button variant="quiet" size="sm" onClick={() => void library.revealDocument({ projectId, documentId: hit.documentId, page: null }).catch((failure) => onError(describeError(failure)))}>
                    <Icon name="folder" /> Show in folder
                  </Button>
                </div>
              </li>
            );
          })}
        </ol>
      )}
    </div>
  );
}

/** Cosine scores run from about 0 to 1; the bar shows the part above zero. */
function scaled(score: number): number {
  return Math.max(0, Math.min(1, score));
}
