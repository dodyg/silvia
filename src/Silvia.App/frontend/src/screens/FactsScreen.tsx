import { useEffect, useState } from "react";
import { describeError, facts, formatWhen, type FactDto } from "../api";
import { MarkdownEditor } from "../MarkdownEditor";
import { renderLines } from "../markdown";
import { Button, Card, Empty, Icon, PageHeader, Section } from "../ui";

interface Props {
  projectId: string;
  onError: (message: string | null) => void;
}

export function FactsScreen({ projectId, onError }: Props) {
  const [rows, setRows] = useState<FactDto[]>([]);
  const [draft, setDraft] = useState("");
  const [editing, setEditing] = useState<{ id: string; text: string } | null>(null);

  useEffect(() => {
    void load();
    // The screen is keyed by project, so this effect runs once per project.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function load() {
    try {
      setRows([...(await facts.list({ projectId }))]);
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  async function add() {
    const text = draft.trim();
    if (!text) {
      return;
    }

    try {
      await facts.add({ projectId, text });
      setDraft("");
      onError(null);
      await load();
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  async function saveEdit() {
    if (!editing) {
      return;
    }

    try {
      await facts.update({ projectId, id: editing.id, text: editing.text });
      setEditing(null);
      onError(null);
      await load();
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  async function remove(fact: FactDto) {
    try {
      await facts.delete({ projectId, id: fact.id });
      await load();
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  return (
    <div>
      <PageHeader
        title="Facts"
        description="Things you know to be true that the documents leave out or only imply. Every answer in this project is given them."
      />

      <Section title="Add a fact">
        <Card className="section-gap">
          <form onSubmit={(event) => { event.preventDefault(); void add(); }}>
            {/* Not a Field: its <label> would name the first toolbar button instead of the text. */}
            <div className="field">
              <span className="field-label">Fact</span>
              <MarkdownEditor
                label="Fact"
                value={draft}
                maxLength={2000}
                onChange={setDraft}
                onSubmit={() => void add()}
                placeholder="For example: every company in the startup profiles was approved for the programme."
              />
              <span className="field-hint">One short statement. Name things the way your questions will. Ctrl+Enter adds it.</span>
            </div>
            <div className="form-actions">
              <Button type="submit" variant="primary" disabled={draft.trim().length === 0}>
                <Icon name="plus" /> Add fact
              </Button>
            </div>
          </form>
        </Card>
      </Section>

      <Section title="Facts" actions={<span className="count">{rows.length} {rows.length === 1 ? "fact" : "facts"}</span>}>
        {rows.length === 0 ? (
          <Empty title="No facts yet" icon="fact">
            Add one here, or press "Correct this" under an answer that got something wrong.
          </Empty>
        ) : (
          <ul className="list" aria-label="Facts">
            {rows.map((fact) => (
              <li key={fact.id}>
                <span className="list-icon"><Icon name="fact" /></span>
                {editing?.id === fact.id ? (
                  <form className="grow" onSubmit={(event) => { event.preventDefault(); void saveEdit(); }}>
                    <MarkdownEditor
                      label="Fact"
                      maxLength={2000}
                      value={editing.text}
                      onChange={(text) => setEditing({ id: fact.id, text })}
                      onSubmit={() => void saveEdit()}
                      onCancel={() => setEditing(null)}
                      autoFocus
                    />
                    <div className="row fact-edit-actions">
                      <Button size="sm" type="submit" variant="primary" disabled={editing.text.trim().length === 0}>Save</Button>
                      <Button size="sm" variant="quiet" onClick={() => setEditing(null)}>Cancel</Button>
                    </div>
                  </form>
                ) : (
                  <>
                    <div className="grow">
                      <div className="fact-text" dangerouslySetInnerHTML={{ __html: renderLines(fact.text) }} />
                      <div className="list-meta">Added {formatWhen(fact.createdUtc)}</div>
                    </div>
                    <div className="list-actions">
                      <Button variant="quiet" size="sm" onClick={() => setEditing({ id: fact.id, text: fact.text })}>Edit</Button>
                      <Button variant="quiet" size="sm" className="btn-danger" onClick={() => void remove(fact)}>
                        <Icon name="trash" /> Remove
                      </Button>
                    </div>
                  </>
                )}
              </li>
            ))}
          </ul>
        )}
      </Section>
    </div>
  );
}
