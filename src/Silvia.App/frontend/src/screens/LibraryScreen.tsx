import { useEffect, useState } from "react";
import { desktop, describeError, library, type DocumentDto, type SourceDto } from "../api";
import { Banner, Button, Empty, Field, Section } from "../ui";

interface Props {
  projectId: string;
  onError: (message: string | null) => void;
}

export function LibraryScreen({ projectId, onError }: Props) {
  const [sources, setSources] = useState<SourceDto[]>([]);
  const [documents, setDocuments] = useState<DocumentDto[]>([]);
  const [folder, setFolder] = useState("");
  const [manualEntry, setManualEntry] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  useEffect(() => {
    void load();
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
      setFolder("");
      setMessage(`Added ${trimmed}. It is indexed in the background.`);
      await load();
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  async function pickFolder() {
    try {
      const result = await desktop.dialogs.openFolder({
        initialLocation: "",
        extensions: [],
        allowMultiple: false,
        filters: [],
        title: "Choose a folder to index",
      });
      const picked = result.paths?.[0];
      if (result.status === "Success" && picked) {
        await add(picked);
      } else if (result.status !== "Canceled") {
        setManualEntry(true);
      }
    } catch {
      setManualEntry(true);
    }
  }

  async function rescan(sourceId?: string) {
    try {
      await library.rescan({ projectId, sourceId: sourceId ?? null });
      setMessage("Rescan queued. Progress is in Activity.");
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  async function remove(source: SourceDto) {
    try {
      await library.removeSource({ projectId, sourceId: source.id });
      setMessage(`Stopped watching ${source.path}. Its documents were removed from this project.`);
      await load();
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  return (
    <div>
      <Section title="Folders" actions={<Button onClick={() => void rescan()} disabled={sources.length === 0}>Rescan all</Button>}>
        {message ? <Banner tone="info">{message}</Banner> : null}
        <div className="row">
          <Button variant="primary" onClick={() => void pickFolder()}>Add a folder…</Button>
          {manualEntry ? null : (
            <Button variant="quiet" onClick={() => setManualEntry(true)}>Type a path instead</Button>
          )}
        </div>
        {manualEntry ? (
          <form className="row" style={{ marginTop: "0.5rem" }} onSubmit={(event) => { event.preventDefault(); void add(folder); }}>
            <Field label="Folder path">
              <input type="text" value={folder} onChange={(event) => setFolder(event.target.value)} placeholder="C:\Documents\Notes" />
            </Field>
            <Button type="submit" disabled={folder.trim().length === 0}>Add</Button>
          </form>
        ) : null}

        {sources.length === 0 ? (
          <Empty title="No folders yet">
            Add a folder and Silvia indexes the supported files in it, then keeps them in step as they change.
          </Empty>
        ) : (
          <ul className="list" style={{ marginTop: "0.75rem" }}>
            {sources.map((source) => (
              <li key={source.id}>
                <div className="grow">
                  <strong>{source.path}</strong>
                  <div className="muted" style={{ fontSize: "0.85rem" }}>
                    {source.enabled ? "Watching" : "Paused"} · files: {source.includeGlobs}
                  </div>
                </div>
                <Button variant="quiet" onClick={() => void rescan(source.id)}>Rescan</Button>
                <Button variant="danger" onClick={() => void remove(source)}>Remove</Button>
              </li>
            ))}
          </ul>
        )}
      </Section>

      <Section title={`Documents (${documents.length})`}>
        {documents.length === 0 ? (
          <p className="muted">Nothing is indexed yet.</p>
        ) : (
          <ul className="list" aria-label="Indexed documents">
            {documents.slice(0, 300).map((document) => (
              <li key={document.documentId}>
                <div className="grow">
                  <strong>{document.fileName}</strong>
                  <div className="path">{document.folder}</div>
                </div>
                <span className="badge">{document.chunks} passages</span>
              </li>
            ))}
          </ul>
        )}
        {documents.length > 300 ? <p className="muted">Showing the first 300 documents.</p> : null}
      </Section>
    </div>
  );
}
