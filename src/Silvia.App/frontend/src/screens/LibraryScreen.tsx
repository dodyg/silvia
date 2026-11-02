import { useEffect, useRef, useState } from "react";
import { describeError, library, type DocumentDto, type SourceDto } from "../api";
import { Banner, Button, Card, Empty, Field, Icon, PageHeader, Section } from "../ui";
import { DocumentView } from "./DocumentView";

interface Props {
  projectId: string;
  onError: (message: string | null) => void;
}

const REFRESH_MS = 3000;

export function LibraryScreen({ projectId, onError }: Props) {
  const [sources, setSources] = useState<SourceDto[]>([]);
  const [documents, setDocuments] = useState<DocumentDto[]>([]);
  const [folder, setFolder] = useState("");
  const [manualEntry, setManualEntry] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const changed = useRef(false);

  useEffect(() => {
    void load();

    // While the queue has work, the document list is reloaded so files appear and disappear as they are
    // processed. After an add, remove or rescan it reloads at least once more when the queue is idle,
    // because short jobs can finish between two polls.
    let active = true;
    const timer = window.setInterval(async () => {
      try {
        const queue = await library.activity({ projectId });
        const busy = queue.pending + queue.running > 0;
        if (active && (busy || changed.current)) {
          await load();
        }

        if (!busy) {
          changed.current = false;
        }
      } catch {
        // A failed poll is retried on the next tick; load() reports real errors.
      }
    }, REFRESH_MS);
    return () => {
      active = false;
      window.clearInterval(timer);
    };
    // The screen is keyed by project, so this effect runs once per project.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function load() {
    try {
      const [sourceRows, documentRows] = await Promise.all([
        library.listSources({ projectId }),
        library.listDocuments({ projectId }),
      ]);
      setSources([...sourceRows]);
      setDocuments([...documentRows]);
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  async function add(path: string) {
    const trimmed = path.trim();
    if (!trimmed) {
      return;
    }

    onError(null);
    try {
      await library.addSource({ projectId, path: trimmed, includeGlobs: null, excludeGlobs: null });
      changed.current = true;
      setFolder("");
      setManualEntry(false);
      setMessage(`Added ${trimmed}. It is indexed in the background.`);
      await load();
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  // The host shows the native dialog; the page only receives the chosen folder. When no dialog can be
  // shown, the path field opens instead, so a folder can always be added.
  async function pickFolder() {
    try {
      const result = await library.pickFolder({});
      if (result.status === "picked" && result.path) {
        await add(result.path);
      } else if (result.status === "unavailable") {
        setManualEntry(true);
        setMessage("The folder dialog is not available here. Type the folder's path instead.");
      }
    } catch (failure) {
      setManualEntry(true);
      onError(describeError(failure));
    }
  }

  async function rescan(sourceId?: string) {
    try {
      await library.rescan({ projectId, sourceId: sourceId ?? null });
      changed.current = true;
      setMessage("Rescan queued. Progress is in Activity.");
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  async function remove(source: SourceDto) {
    try {
      await library.removeSource({ projectId, sourceId: source.id });
      changed.current = true;
      setMessage(`Stopped watching ${source.path}. Its documents are being removed from this project.`);
      await load();
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  if (selected) {
    return <DocumentView projectId={projectId} documentId={selected} onBack={() => setSelected(null)} onError={onError} />;
  }

  return (
    <div>
      <PageHeader
        title="Library"
        description="The folders this project indexes. Silvia keeps them in step as files change."
        actions={
          <>
            <Button onClick={() => void rescan()} disabled={sources.length === 0}>
              <Icon name="refresh" /> Rescan all
            </Button>
            <Button variant="primary" onClick={() => void pickFolder()}>
              <Icon name="plus" /> Add a folder…
            </Button>
          </>
        }
      />

      {message ? <Banner tone="info">{message}</Banner> : null}

      <Section
        title="Folders"
        actions={manualEntry ? null : <Button variant="quiet" size="sm" onClick={() => setManualEntry(true)}>Type a path instead</Button>}
      >
        {manualEntry ? (
          <Card className="section-gap">
            <form className="inline-form" onSubmit={(event) => { event.preventDefault(); void add(folder); }}>
              <Field label="Folder path">
                <input type="text" value={folder} onChange={(event) => setFolder(event.target.value)} placeholder="C:\Documents\Notes" />
              </Field>
              <Button type="submit" variant="primary" disabled={folder.trim().length === 0}>Add folder</Button>
              <Button variant="quiet" onClick={() => setManualEntry(false)}>Cancel</Button>
            </form>
          </Card>
        ) : null}

        {sources.length === 0 ? (
          <Empty title="No folders yet" icon="folder">
            Add a folder and Silvia indexes the supported files in it, then keeps them in step as they change.
          </Empty>
        ) : (
          <ul className="list">
            {sources.map((source) => (
              <li key={source.id}>
                <span className="list-icon"><Icon name="folder" /></span>
                <div className="grow">
                  <div className="list-title">
                    {source.path}
                    <span className={source.enabled ? "badge badge-ready" : "badge"}>{source.enabled ? "Watching" : "Paused"}</span>
                  </div>
                  <div className="list-meta">Files: {source.includeGlobs}</div>
                </div>
                <div className="list-actions">
                  <Button variant="quiet" size="sm" onClick={() => void rescan(source.id)}>
                    <Icon name="refresh" /> Rescan
                  </Button>
                  <Button variant="quiet" size="sm" className="btn-danger" onClick={() => void remove(source)}>
                    <Icon name="trash" /> Remove
                  </Button>
                </div>
              </li>
            ))}
          </ul>
        )}
      </Section>

      <Section title="Documents" actions={<span className="count">{documents.length} indexed</span>}>
        {documents.length === 0 ? (
          <Empty title="Nothing is indexed yet" icon="file">Documents appear here once a folder has been scanned.</Empty>
        ) : (
          <ul className="list" aria-label="Indexed documents">
            {documents.slice(0, 300).map((document) => (
              <li key={document.documentId}>
                <span className="list-icon"><Icon name="file" /></span>
                <div className="grow">
                  <div className="list-title">
                    <button type="button" className="link-button" onClick={() => setSelected(document.documentId)}>{document.fileName}</button>
                  </div>
                  <div className="path">{document.folder}</div>
                </div>
                <span className="badge">{document.chunks} {document.chunks === 1 ? "passage" : "passages"}</span>
              </li>
            ))}
          </ul>
        )}
        {documents.length > 300 ? <p className="muted small">Showing the first 300 documents.</p> : null}
      </Section>
    </div>
  );
}
