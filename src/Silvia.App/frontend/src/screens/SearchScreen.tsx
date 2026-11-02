import { useState, type FormEvent } from "react";
import { chat, describeError, inspect, library, type TraceDto, type TracedHitDto } from "../api";
import { useModelStatus } from "../models";
import { Button, Empty, Icon, PageHeader, Section } from "../ui";

interface Props {
  projectId: string;
  onError: (message: string | null) => void;
}

const TOP_K = 10;

export function SearchScreen({ projectId, onError }: Props) {
  const [query, setQuery] = useState("");
  const [hits, setHits] = useState<TracedHitDto[] | null>(null);
  const [trace, setTrace] = useState<TraceDto | null>(null);
  const [explain, setExplain] = useState(false);
  const [busy, setBusy] = useState(false);
  const { canEmbed } = useModelStatus(projectId);

  async function run(event: FormEvent) {
    event.preventDefault();
    const text = query.trim();
    if (!text) {
      return;
    }

    setBusy(true);
    onError(null);
    try {
      if (explain) {
        const next = await inspect.trace({ projectId, query: text, topK: TOP_K });
        setTrace(next);
        setHits([...next.hits]);
      } else {
        // Plain search keeps its own call, so turning the explanation off costs nothing extra.
        const plain = await chat.search({ projectId, query: text, topK: TOP_K });
        setTrace(null);
        setHits(plain.map((hit, index) => ({ ...hit, score: hit.relevance, position: index + 1, similarity: null, vectorRank: null })));
      }
    } catch (failure) {
      onError(describeError(failure));
    } finally {
      setBusy(false);
    }
  }

  const shell = (call: typeof library.openDocument, documentId: string) =>
    void call({ projectId, documentId, page: null }).catch((failure) => onError(describeError(failure)));

  return (
    <div>
      <PageHeader
        title="Search"
        description={canEmbed
          ? "Find passages in this project by meaning. Search never calls a cloud model, so it works offline."
          : "Find passages in this project by keyword. Search never calls a cloud model, so it works offline."}
      />

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
      <div className="search-options">
        <label className="check">
          <input type="checkbox" checked={explain} onChange={(event) => setExplain(event.target.checked)} />
          Explain how results were chosen
        </label>
        <p className="search-note">{hits === null ? "Type a few words to find passages." : `${hits.length} passage${hits.length === 1 ? "" : "s"}`}</p>
      </div>

      {trace ? <Pipeline trace={trace} /> : null}

      {hits === null ? null : hits.length === 0 ? (
        <Empty title="No passages matched" icon="search">Try other words, or add more folders in Library.</Empty>
      ) : (
        <ol className="hits" aria-label="Results">
          {hits.map((hit) => {
            const percent = Math.round(scaled(hit.score / 1000) * 100);
            return (
              <li key={`${hit.documentId}-${hit.chunkIndex}`} className="card hit">
                <div className="hit-head">
                  <span className="hit-title">
                    {trace ? <span className="badge badge-accent">#{hit.position}</span> : null}
                    <Icon name="file" /> {hit.fileName}
                    <span className="list-meta">passage {hit.chunkIndex + 1}</span>
                  </span>
                  <span className="relevance">
                    {trace?.mode === "keyword" ? null : (
                      <span className="score" aria-hidden="true">
                        <span style={{ width: `${percent}%` }} />
                      </span>
                    )}
                    {trace ? "Score " : ""}{(hit.score / 1000).toFixed(3)}
                  </span>
                </div>
                {trace ? <Why hit={hit} trace={trace} /> : null}
                <p className="path">{hit.documentId}</p>
                <p className="hit-text">{hit.text}</p>
                <div className="hit-actions">
                  <Button variant="quiet" size="sm" onClick={() => shell(library.openDocument, hit.documentId)}>
                    <Icon name="external" /> Open
                  </Button>
                  <Button variant="quiet" size="sm" onClick={() => shell(library.revealDocument, hit.documentId)}>
                    <Icon name="folder" /> Show in folder
                  </Button>
                </div>
              </li>
            );
          })}
        </ol>
      )}

      {trace && trace.closeCalls.length > 0 ? (
        <Section
          title="Close calls"
          description="Passages whose vectors were near the question but did not make the list. Hybrid search, reranking or the duplicate filter set them aside."
        >
          <ul className="list" aria-label="Close calls">
            {trace.closeCalls.map((hit) => (
              <li key={`${hit.documentId}-${hit.chunkIndex}`}>
                <div className="grow">
                  <div className="list-title">
                    {hit.fileName} <span className="list-meta">passage {hit.chunkIndex + 1}</span>
                  </div>
                  <p className="close-call-text">{hit.text}</p>
                </div>
                <span className="list-meta">
                  Vector rank {hit.vectorRank} · similarity {format(hit.similarity)}
                </span>
              </li>
            ))}
          </ul>
        </Section>
      ) : null}
    </div>
  );
}

/** The steps this search went through, in the order they ran. */
function Pipeline({ trace }: { trace: TraceDto }) {
  const steps = trace.mode === "keyword"
    ? [
        "Keyword search (BM25) over every passage.",
        "Chat adds model-suggested search terms to the question first; this view searches your words only.",
      ]
    : [
        "Hybrid search: nearest vectors to the question, fused with a keyword (BM25) search.",
        trace.reranked ? "A local reranker rescored the candidates." : "No reranker is installed, so candidates kept their fused order.",
        "Near-duplicate passages were dropped.",
        "The best passages were moved to the start and end of the list, where a model reads most closely. Positions show that final order.",
      ];
  return (
    <div className="card pipeline" aria-label="How results were chosen">
      <ol>
        {steps.map((step) => <li key={step}>{step}</li>)}
      </ol>
    </div>
  );
}

function Why({ hit, trace }: { hit: TracedHitDto; trace: TraceDto }) {
  if (trace.mode === "keyword") {
    return <p className="why">Matched by keyword. Higher scores share more and rarer words with the search.</p>;
  }

  return (
    <p className="why">
      Similarity {format(hit.similarity)} ·{" "}
      {hit.vectorRank === null
        ? <span className="badge badge-accent">Found by keyword</span>
        : <>vector rank {hit.vectorRank}{hit.vectorRank <= TOP_K ? null : <> · <span className="badge badge-accent">Lifted by keyword or reranker</span></>}</>}
    </p>
  );
}

function format(thousandths: number | null): string {
  return thousandths === null ? "n/a" : (thousandths / 1000).toFixed(3);
}

/** Cosine scores run from about 0 to 1; the bar shows the part above zero. */
function scaled(score: number): number {
  return Math.max(0, Math.min(1, score));
}
