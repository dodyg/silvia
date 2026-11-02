import { useEffect, useState } from "react";
import { describeError, formatWhen, library, type JobSummaryDto } from "../api";
import { Empty, Icon, PageHeader, Section, Spinner, Stat } from "../ui";
import { Coverage } from "./Coverage";

interface Props {
  projectId: string;
  onError: (message: string | null) => void;
}

const REFRESH_MS = 3000;

export function ActivityScreen({ projectId, onError }: Props) {
  const [summary, setSummary] = useState<JobSummaryDto | null>(null);

  useEffect(() => {
    let active = true;
    const load = async () => {
      try {
        const next = await library.activity({ projectId });
        if (active) {
          setSummary(next);
        }
      } catch (failure) {
        if (active) {
          onError(describeError(failure));
        }
      }
    };

    void load();
    const timer = window.setInterval(() => void load(), REFRESH_MS);
    return () => {
      active = false;
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

      <Section title="Failed files">
        {summary && summary.recentFailures.length > 0 ? (
          <ul className="list">
            {summary.recentFailures.map((failure) => (
              <li key={`${failure.path}-${failure.finishedUtc ?? ""}`}>
                <span className="list-icon"><Icon name="file" /></span>
                <div className="grow">
                  <div className="path">{failure.path}</div>
                  <div className="failure-text">{failure.error ?? "The file could not be indexed."}</div>
                </div>
                {failure.finishedUtc ? <span className="list-meta">{formatWhen(failure.finishedUtc)}</span> : null}
              </li>
            ))}
          </ul>
        ) : (
          <Empty title="Nothing has failed">Every file in this project was indexed.</Empty>
        )}
      </Section>

      <Coverage projectId={projectId} onError={onError} />
    </div>
  );
}
