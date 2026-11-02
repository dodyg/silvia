import { useState, type FormEvent } from "react";
import { describeError, projects, type ProjectDto } from "../api";
import { Banner, Button, Empty, Field, Section } from "../ui";

interface Props {
  projects: ProjectDto[];
  selectedId: string | null;
  onChanged: () => Promise<unknown>;
  onOpen: (id: string) => void;
}

export function ProjectsScreen({ projects: list, selectedId, onChanged, onOpen }: Props) {
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [editing, setEditing] = useState<{ id: string; name: string; mode: "rename" | "duplicate" } | null>(null);
  const [confirming, setConfirming] = useState<ProjectDto | null>(null);
  const [confirmText, setConfirmText] = useState("");
  const [importPath, setImportPath] = useState("");
  const [importName, setImportName] = useState("");
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function run(action: () => Promise<string | null>) {
    setError(null);
    setNotice(null);
    try {
      const message = await action();
      if (message) {
        setNotice(message);
      }

      await onChanged();
    } catch (failure) {
      setError(describeError(failure));
    }
  }

  function create(event: FormEvent) {
    event.preventDefault();
    const trimmed = name.trim();
    if (!trimmed) {
      return;
    }

    void run(async () => {
      const created = await projects.create({ name: trimmed, description: description.trim() || null, embedModel: null });
      setName("");
      setDescription("");
      onOpen(created.id);
      return `Created ${created.name}.`;
    });
  }

  function saveEdit(event: FormEvent) {
    event.preventDefault();
    if (!editing) {
      return;
    }

    const current = editing;
    void run(async () => {
      if (current.mode === "rename") {
        await projects.rename({ id: current.id, name: current.name.trim() });
        setEditing(null);
        return "Renamed.";
      }

      const copy = await projects.duplicate({ id: current.id, name: current.name.trim() });
      setEditing(null);
      return `Made a copy named ${copy.name}. Its settings match the original.`;
    });
  }

  function remove(project: ProjectDto) {
    void run(async () => {
      await projects.delete({ id: project.id });
      setConfirming(null);
      setConfirmText("");
      return `Deleted ${project.name}. Its folder and index were removed.`;
    });
  }

  function exportProject(project: ProjectDto) {
    void run(async () => {
      const file = await projects.export({ id: project.id });
      return `Exported to ${file.path}. The file contains the text of your documents, so keep it as private as the folders it came from.`;
    });
  }

  function importFile(event: FormEvent) {
    event.preventDefault();
    void run(async () => {
      const created = await projects.import({ filePath: importPath.trim(), name: importName.trim() });
      setImportPath("");
      setImportName("");
      onOpen(created.id);
      return `Imported ${created.name}.`;
    });
  }

  return (
    <div>
      <Section title="Projects">
        {error ? <Banner tone="error">{error}</Banner> : null}
        {notice ? <Banner tone="info">{notice}</Banner> : null}

        {list.length === 0 ? (
          <Empty title="No projects yet">Give your first knowledge base a name below.</Empty>
        ) : (
          <ul className="list">
            {list.map((project) => (
              <li key={project.id} aria-current={project.id === selectedId ? "true" : undefined}>
                <div className="grow">
                  <strong>{project.name}</strong>{" "}
                  <span className={`badge ${project.status === "ready" ? "badge-ready" : ""}`}>{project.status}</span>
                  {project.description ? <div className="muted">{project.description}</div> : null}
                </div>
                <Button onClick={() => onOpen(project.id)}>{project.id === selectedId ? "Open (current)" : "Open"}</Button>
                <Button variant="quiet" onClick={() => setEditing({ id: project.id, name: project.name, mode: "rename" })}>Rename</Button>
                <Button variant="quiet" onClick={() => setEditing({ id: project.id, name: `${project.name} copy`, mode: "duplicate" })}>Duplicate</Button>
                <Button variant="quiet" onClick={() => exportProject(project)}>Export</Button>
                <Button variant="danger" onClick={() => setConfirming(project)}>Delete</Button>
              </li>
            ))}
          </ul>
        )}

        {editing ? (
          <form className="row" style={{ marginTop: "0.75rem" }} onSubmit={saveEdit}>
            <Field label={editing.mode === "rename" ? "New name" : "Name of the copy"}>
              <input type="text" value={editing.name} onChange={(event) => setEditing({ ...editing, name: event.target.value })} />
            </Field>
            <Button type="submit" variant="primary" disabled={editing.name.trim().length === 0}>
              {editing.mode === "rename" ? "Save name" : "Make copy"}
            </Button>
            <Button onClick={() => setEditing(null)}>Cancel</Button>
          </form>
        ) : null}

        {confirming ? (
          <div className="banner banner-warn" role="alertdialog" aria-label="Confirm delete" style={{ marginTop: "0.75rem" }}>
            <p style={{ marginTop: 0 }}>
              Deleting <strong>{confirming.name}</strong> removes its index, conversations and folder list. Your documents stay where they are.
              Type the project name to confirm.
            </p>
            <div className="row">
              <input type="text" aria-label="Type the project name" value={confirmText} onChange={(event) => setConfirmText(event.target.value)} />
              <Button variant="danger" disabled={confirmText !== confirming.name} onClick={() => remove(confirming)}>Delete project</Button>
              <Button onClick={() => { setConfirming(null); setConfirmText(""); }}>Keep it</Button>
            </div>
          </div>
        ) : null}
      </Section>

      <Section title="New project">
        <form onSubmit={create} className="row">
          <Field label="Name">
            <input type="text" value={name} onChange={(event) => setName(event.target.value)} placeholder="Field notes" />
          </Field>
          <Field label="Description (optional)">
            <input type="text" value={description} onChange={(event) => setDescription(event.target.value)} />
          </Field>
          <Button type="submit" variant="primary" disabled={name.trim().length === 0}>Create project</Button>
        </form>
      </Section>

      <Section title="Import a project file">
        <form onSubmit={importFile} className="row">
          <Field label="Path to a .silvia file">
            <input type="text" value={importPath} onChange={(event) => setImportPath(event.target.value)} placeholder="C:\Exports\field-notes.silvia" />
          </Field>
          <Field label="Name for the imported project">
            <input type="text" value={importName} onChange={(event) => setImportName(event.target.value)} />
          </Field>
          <Button type="submit" disabled={importPath.trim().length === 0 || importName.trim().length === 0}>Import</Button>
        </form>
      </Section>
    </div>
  );
}
