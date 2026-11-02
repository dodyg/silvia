import { useEffect, useState } from "react";
import { describeError, formatWhen, inspect, type CoverageDto } from "../api";
import { Button, Empty, Icon, Section, Spinner } from "../ui";

interface Props {
  projectId: string;
  onError: (message: string | null) => void;
}

/** Under this much text, a file most likely lost its content in parsing: a scan, an image, a form. */
const THIN_CHARACTERS = 300;

/** How much of each document the index holds, largest first, with the documents worth a second look flagged. */
export function Coverage({ projectId, onError }: Props) {
  const [coverage, setCoverage] = useState<CoverageDto | null>(null);
  const [all, setAll] = useState(false);

  async function load() {
    try {
      setCoverage(await inspect.coverage({ projectId }));
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  useEffect(() => {
    void load();
    // The screen is keyed by project, so this effect runs once per project.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const refresh = <Button variant="quiet" size="sm" onClick={() => void load()}><Icon name="refresh" /> Refresh</Button>;
  if (!coverage) {
    return (
      <Section title="Coverage" actions={refresh}>
        <p className="row muted"><Spinner label="Loading" /> Loading…</p>
      </Section>
    );
  }

  const rows = [...coverage.documents].sort((a, b) => b.characters - a.characters || a.fileName.localeCompare(b.fileName));
  const passages = rows.reduce((sum, r) => sum + r.passages, 0);
  const characters = rows.reduce((sum, r) => sum + r.characters, 0);
  const withOverview = rows.filter((r) => r.hasOverview).length;
  const thin = rows.filter((r) => r.characters < THIN_CHARACTERS).length;
  const widest = rows.length === 0 ? 1 : Math.max(1, rows[0].characters);
  const visible = all ? rows : rows.slice(0, 25);

  const mode = coverage.mode === "vector"
    ? `Searched by meaning and keyword, with ${coverage.embedModel ?? "a local embedding model"}.`
    : "Searched by keyword only. Install the embedding model in Settings to search by meaning.";

  return (
    <Section title="Coverage" description={mode} actions={refresh}>
      <dl className="stats section-gap" aria-label="Index totals">
        <div className="stat"><dt>Documents</dt><dd>{rows.length.toLocaleString()}</dd></div>
        <div className="stat"><dt>Passages</dt><dd>{passages.toLocaleString()}</dd></div>
        <div className="stat"><dt>Average passage</dt><dd>{passages === 0 ? 0 : Math.round(characters / passages).toLocaleString()}</dd></div>
        {coverage.overviewsOn ? <div className="stat"><dt>With overview</dt><dd>{withOverview} / {rows.length}</dd></div> : null}
        {coverage.graph ? <div className="stat"><dt>In the knowledge graph</dt><dd>{coverage.graph.documents} / {rows.length}</dd></div> : null}
        {coverage.graph ? <div className="stat"><dt>Entities</dt><dd>{coverage.graph.entities.toLocaleString()}</dd></div> : null}
        <div className={thin > 0 ? "stat stat-warn" : "stat"}><dt>Nearly empty</dt><dd>{thin}</dd></div>
      </dl>

      {rows.length === 0 ? (
        <Empty title="Nothing is indexed yet" icon="file">Add a folder in Library.</Empty>
      ) : (
        <>
          <p className="muted small">
            Text indexed per document. A document with almost no text likely lost its content in parsing, such as a scanned PDF with no text layer.
          </p>
          <table className="coverage" aria-label="Indexed text per document">
            <thead>
              <tr>
                <th scope="col">Document</th>
                <th scope="col" className="num">Passages</th>
                <th scope="col">Characters</th>
                <th scope="col">Last indexed</th>
              </tr>
            </thead>
            <tbody>
              {visible.map((row) => (
                <tr key={row.documentId}>
                  <th scope="row">
                    <span className="coverage-name" title={row.documentId}>{row.fileName}</span>
                    {row.characters < THIN_CHARACTERS ? <span className="badge badge-warn">⚠ Nearly empty</span> : null}
                    {coverage.overviewsOn && !row.hasOverview ? <span className="badge">No overview</span> : null}
                  </th>
                  <td className="num">{row.passages.toLocaleString()}</td>
                  <td>
                    <span className="coverage-bar">
                      <span className="bar-track" aria-hidden="true">
                        <span style={{ width: `${Math.max(1, (row.characters / widest) * 100)}%` }} />
                      </span>
                      <span className="num">{row.characters.toLocaleString()}</span>
                    </span>
                  </td>
                  <td className="muted">{row.indexedUtc ? formatWhen(row.indexedUtc) : "Not recorded"}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {rows.length > visible.length ? (
            <Button variant="quiet" size="sm" onClick={() => setAll(true)}>Show all {rows.length} documents</Button>
          ) : null}
        </>
      )}
    </Section>
  );
}
