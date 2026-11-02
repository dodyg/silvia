import { useEffect, useState } from "react";
import { describeError, library, type JobSummaryDto } from "../api";
import { Banner, Section } from "../ui";

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
      <Section title="Queue">
        {summary ? (
          <dl className="row" style={{ gap: "1.5rem" }} aria-label="Queue counts">
            <Count label="Waiting" value={summary.pending} />
            <Count label="Working" value={summary.running} />
            <Count label="Done" value={summary.done} />
            <Count label="Failed" value={summary.failed} />
          </dl>
        ) : (
          <p className="muted">Loading…</p>
        )}
        <p className="muted">Updates every few seconds while this screen is open.</p>
      </Section>

      <Section title="Failed files">
        {summary && summary.recentFailures.length > 0 ? (
          <ul className="list">
            {summary.recentFailures.map((failure) => (
              <li key={`${failure.path}-${failure.finishedUtc ?? ""}`}>
                <div className="grow">
                  <strong>{failure.path}</strong>
                  <div className="banner banner-error" style={{ margin: "0.3rem 0 0", padding: "0.3rem 0.5rem" }}>
                    {failure.error ?? "The file could not be indexed."}
                  </div>
                </div>
              </li>
            ))}
          </ul>
        ) : (
          <Banner tone="info">No file has failed in this project.</Banner>
        )}
      </Section>
    </div>
  );
}

function Count({ label, value }: { label: string; value: number }) {
  return (
    <div>
      <dt className="muted">{label}</dt>
      <dd style={{ margin: 0, fontSize: "1.4rem" }}>{value}</dd>
    </div>
  );
}
