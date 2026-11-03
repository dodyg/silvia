import { useEffect, useRef, useState } from "react";
import { describeError, formatWhen, library, type JobSummaryDto } from "../api";
import { Button, Empty, Icon, PageHeader, Section, Spinner, Stat } from "../ui";
import { Coverage } from "./Coverage";

interface Props {
  projectId: string;
  onError: (message: string | null) => void;
}

const REFRESH_MS = 3000;

export function ActivityScreen({ projectId, onError }: Props) {
  const [summary, setSummary] = useState<JobSummaryDto | null>(null);
  const [clearing, setClearing] = useState(false);
  const active = useRef(true);

  async function load() {
    try {
      const next = await library.activity({ projectId });
      if (active.current) {
        setSummary(next);
      }
    } catch (failure) {
      if (active.current) {
        onError(describeError(failure));
      }
    }
  }

  /** Removes one failed file from the log, or all of them; the files and the index are untouched. */
  async function clear(id: string | null) {
    setClearing(true);
    try {
      await (id === null ? library.clearFailures({ projectId }) : library.clearFailure({ projectId, id }));
    } catch (failure) {
      onError(describeError(failure));
    } finally {
      setClearing(false);
    }

    await load();
  }

  useEffect(() => {
    active.current = true;
    void load();
    const timer = window.setInterval(() => void load(), REFRESH_MS);
    return () => {
      active.current = false;
      window.clearInterval(timer);
    };
    // The screen is keyed by project, so this effect runs once per project.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <div>
      <PageHeader title="Activity" description="Indexing work for this project. Updates every few seconds while this screen is open." />

      <Section title="Queue">
        {summary ? (
          <dl className="stats" aria-label="Queue counts">
            <Stat label="Waiting" value={summary.pending} />
            <Stat label="Working" value={summary.running} />
            <Stat label="Done" value={summary.done} />
            <Stat label="Failed" value={summary.failed} tone="danger" />
          </dl>
        ) : (
          <p className="row muted">
            <Spinner label="Loading" /> Loading…
          </p>
        )}
      </Section>

      <Section
        title="Failed files"
        description={summary && summary.recentFailures.length > 0
          ? "Clearing removes a file from this log only. A file that still failed to index or reach the knowledge graph is tried again when it changes, on a rescan, or on Resume."
          : undefined}
        actions={summary && summary.recentFailures.length > 0 ? (
          <Button onClick={() => void clear(null)} disabled={clearing}>
            <Icon name="trash" /> Clear all
          </Button>
        ) : null}
      >
        {summary && summary.recentFailures.length > 0 ? (
          <ul className="list" aria-label="Failed files">
            {summary.recentFailures.map((failure) => (
              <li key={failure.id}>
                <span className="list-icon"><Icon name="file" /></span>
                <div className="grow">
                  <div className="path">{failure.path}</div>
                  <div className="failure-text">{failure.error ?? "The file could not be indexed."}</div>
                </div>
                {failure.finishedUtc ? <span className="list-meta">{formatWhen(failure.finishedUtc)}</span> : null}
                <Button
                  variant="quiet"
                  size="sm"
                  aria-label={`Remove ${failure.path} from the log`}
                  title="Remove from the log"
                  disabled={clearing}
                  onClick={() => void clear(failure.id)}
                >
                  <Icon name="close" />
                </Button>
              </li>
            ))}
          </ul>
        ) : (
          <Empty title="Nothing has failed">No file is in the failure log.</Empty>
        )}
      </Section>

      <Coverage projectId={projectId} onError={onError} />
    </div>
  );
}
