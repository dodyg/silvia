import { useState, type FormEvent } from "react";
import { describeError, projects, type ProjectDto } from "../api";
import { Banner, Button, Card, Empty, Field, Icon, PageHeader, Section } from "../ui";

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
      <PageHeader title="Projects" description="Each project is a separate knowledge base with its own folders, index and conversations." />

      {error ? <Banner tone="error">{error}</Banner> : null}
      {notice ? <Banner tone="info">{notice}</Banner> : null}

      <Section title="Your projects" actions={<span className="count">{list.length}</span>}>
        {list.length === 0 ? (
          <Empty title="No projects yet" icon="projects">Give your first knowledge base a name below.</Empty>
        ) : (
          <ul className="list">
            {list.map((project) => {
              const current = project.id === selectedId;
              return (
                <li key={project.id} aria-current={current ? "true" : undefined}>
                  <span className="list-icon"><Icon name="projects" /></span>
                  <div className="grow">
                    <div className="list-title">
                      {project.name}
                      {current ? <span className="badge badge-accent">Open</span> : null}
                      {project.status !== "ready" ? <span className="badge badge-danger">{project.status}</span> : null}
                    </div>
                    {project.description ? <div className="list-meta">{project.description}</div> : null}
                  </div>
                  <div className="list-actions">
                    {current ? null : <Button size="sm" onClick={() => onOpen(project.id)}>Open</Button>}
                    <Button variant="quiet" size="sm" onClick={() => setEditing({ id: project.id, name: project.name, mode: "rename" })}>Rename</Button>
                    <Button variant="quiet" size="sm" onClick={() => setEditing({ id: project.id, name: `${project.name} copy`, mode: "duplicate" })}>Duplicate</Button>
                    <Button variant="quiet" size="sm" onClick={() => exportProject(project)}>Export</Button>
                    <Button variant="quiet" size="sm" className="btn-danger icon-btn" onClick={() => setConfirming(project)} aria-label={`Delete ${project.name}`} title="Delete">
                      <Icon name="trash" />
                    </Button>
                  </div>
                </li>
              );
            })}
          </ul>
        )}

        {editing ? (
          <Card className="edit-card">
            <form className="inline-form" onSubmit={saveEdit}>
              <Field label={editing.mode === "rename" ? "New name" : "Name of the copy"}>
                <input type="text" value={editing.name} onChange={(event) => setEditing({ ...editing, name: event.target.value })} autoFocus />
              </Field>
              <Button type="submit" variant="primary" disabled={editing.name.trim().length === 0}>
                {editing.mode === "rename" ? "Save name" : "Make copy"}
              </Button>
              <Button variant="quiet" onClick={() => setEditing(null)}>Cancel</Button>
            </form>
          </Card>
        ) : null}

        {confirming ? (
          <div className="confirm" role="alertdialog" aria-label="Confirm delete">
            <p>
              Deleting <strong>{confirming.name}</strong> removes its index, conversations and folder list. Your documents stay where they are.
              Type the project name to confirm.
            </p>
            <div className="inline-form">
              <Field label="Project name">
                <input type="text" value={confirmText} onChange={(event) => setConfirmText(event.target.value)} />
              </Field>
              <Button variant="primary" className="btn-destructive" disabled={confirmText !== confirming.name} onClick={() => remove(confirming)}>Delete project</Button>
              <Button onClick={() => { setConfirming(null); setConfirmText(""); }}>Keep it</Button>
            </div>
          </div>
        ) : null}
      </Section>

      <Section title="New project">
        <Card>
          <form onSubmit={create}>
            <div className="form-grid">
              <Field label="Name">
                <input type="text" value={name} onChange={(event) => setName(event.target.value)} placeholder="Field notes" />
              </Field>
              <Field label="Description (optional)">
                <input type="text" value={description} onChange={(event) => setDescription(event.target.value)} />
              </Field>
            </div>
            <div className="form-actions">
              <Button type="submit" variant="primary" disabled={name.trim().length === 0}>
                <Icon name="plus" /> Create project
              </Button>
            </div>
          </form>
        </Card>
      </Section>

      <Section title="Import a project file" description="Bring in a .silvia file exported from Silvia. Its folders are kept as paths, not copied.">
        <Card>
          <form onSubmit={importFile}>
            <div className="form-grid">
              <Field label="Path to a .silvia file">
                <input type="text" value={importPath} onChange={(event) => setImportPath(event.target.value)} placeholder="C:\Exports\field-notes.silvia" />
              </Field>
              <Field label="Name for the imported project">
                <input type="text" value={importName} onChange={(event) => setImportName(event.target.value)} />
              </Field>
            </div>
            <div className="form-actions">
              <Button type="submit" disabled={importPath.trim().length === 0 || importName.trim().length === 0}>Import</Button>
            </div>
          </form>
        </Card>
      </Section>
    </div>
  );
}
