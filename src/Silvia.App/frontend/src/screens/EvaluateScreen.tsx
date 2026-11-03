import { useEffect, useRef, useState } from "react";
import {
  describeError,
  evaluation,
  formatWhen,
  type EvalQuestionDto,
  type EvalResultDto,
  type EvalRunDetailDto,
  type EvalRunDto,
  type RunComparisonDto,
  type RunEstimateDto,
} from "../api";
import { renderMarkdown } from "../markdown";
import { Banner, Button, Card, Empty, Field, Icon, PageHeader, Section, Spinner } from "../ui";
import { ClaimRow, markUnsupported } from "./AnswerCheck";
import { DocumentView } from "./DocumentView";

interface Props {
  projectId: string;
  onError: (message: string | null) => void;
}

type Tab = "results" | "history" | "questions";

const POLL_MS = 1500;

/** A document overview arrives as a citation with this chunk index; real passages count from zero. */
const OVERVIEW_CHUNK = -1;

/**
 * The project's test set and its runs. A run answers every accepted question the way chat does, then the
 * judge model grades each answer: did it state the expected facts, is every statement backed by the
 * passages, and did search find the expected document. Nothing here calls a model until the user says so.
 */
export function EvaluateScreen({ projectId, onError }: Props) {
  const [tab, setTab] = useState<Tab>("results");
  const [questions, setQuestions] = useState<EvalQuestionDto[]>([]);
  const [runs, setRuns] = useState<EvalRunDto[] | null>(null);
  const [selectedRun, setSelectedRun] = useState<string | null>(null);
  const [detail, setDetail] = useState<EvalRunDetailDto | null>(null);
  const [estimate, setEstimate] = useState<RunEstimateDto | null>(null);
  const [comparison, setComparison] = useState<RunComparisonDto | null>(null);
  const [viewing, setViewing] = useState<string | null>(null);
  const loadedTab = useRef(false);

  const running = runs?.find((r) => r.status === "running") ?? null;

  useEffect(() => {
    void refresh();
    // The screen is keyed by project, so this effect runs once per project.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // While a run is going, its totals and results are read again every moment.
  useEffect(() => {
    if (!running) {
      return;
    }

    const timer = window.setInterval(() => void refresh(), POLL_MS);
    return () => window.clearInterval(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [running?.id]);

  useEffect(() => {
    if (!selectedRun) {
      setDetail(null);
      return;
    }

    let active = true;
    evaluation.run({ projectId, runId: selectedRun })
      .then((next) => active && setDetail(next))
      .catch((failure) => active && onError(describeError(failure)));
    return () => {
      active = false;
    };
    // The detail follows the run's progress while it is running.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedRun, runs]);

  async function refresh() {
    try {
      const [nextQuestions, nextRuns] = await Promise.all([evaluation.listQuestions({ projectId }), evaluation.listRuns({ projectId })]);
      setQuestions([...nextQuestions]);
      setRuns([...nextRuns]);
      setSelectedRun((current) => (current && nextRuns.some((r) => r.id === current) ? current : nextRuns[0]?.id ?? null));
      if (!loadedTab.current) {
        loadedTab.current = true;
        setTab(nextRuns.length > 0 ? "results" : "questions");
      }
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  async function prepareRun() {
    try {
      setEstimate(await evaluation.estimate({ projectId }));
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  async function startRun() {
    try {
      const run = await evaluation.startRun({ projectId });
      setEstimate(null);
      setSelectedRun(run.id);
      setComparison(null);
      setTab("results");
      onError(null);
      await refresh();
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  async function stopRun() {
    try {
      await evaluation.cancelRun({ projectId });
      await refresh();
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  async function compare(before: EvalRunDto, after: EvalRunDto) {
    try {
      setComparison(await evaluation.compare({ projectId, beforeId: before.id, afterId: after.id }));
      setTab("history");
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  async function removeRun(run: EvalRunDto) {
    try {
      await evaluation.deleteRun({ projectId, runId: run.id });
      if (comparison && (comparison.before.id === run.id || comparison.after.id === run.id)) {
        setComparison(null);
      }

      await refresh();
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  if (viewing) {
    return <DocumentView projectId={projectId} documentId={viewing} onBack={() => setViewing(null)} onError={onError} />;
  }

  const accepted = questions.filter((q) => !q.draft).length;

  return (
    <div>
      <PageHeader
        title="Evaluate"
        description="A test set of questions you know the answers to. A run asks each one the way chat does, then the judge model grades every answer."
        actions={
          running ? (
            <Button onClick={() => void stopRun()}><Icon name="stop" /> Stop run</Button>
          ) : (
            <Button variant="primary" onClick={() => void prepareRun()} disabled={accepted === 0}>
              <Icon name="refresh" /> Run test set
            </Button>
          )
        }
      />

      {estimate ? <RunConfirm estimate={estimate} onStart={() => void startRun()} onCancel={() => setEstimate(null)} /> : null}
      {running ? <RunProgress run={running} /> : null}

      <div className="tabs" role="tablist" aria-label="Evaluate">
        {(["results", "history", "questions"] as const).map((id) => (
          <button key={id} type="button" role="tab" aria-selected={tab === id} onClick={() => setTab(id)}>
            {id === "results" ? "Results" : id === "history" ? "History" : `Test set (${accepted})`}
          </button>
        ))}
      </div>

      {tab === "questions" ? (
        <TestSet projectId={projectId} questions={questions} onChanged={refresh} onError={onError} />
      ) : tab === "history" ? (
        <History runs={runs ?? []} comparison={comparison} onCompare={(a, b) => void compare(a, b)} onCloseCompare={() => setComparison(null)} onOpen={(run) => { setSelectedRun(run.id); setTab("results"); }} onDelete={(run) => void removeRun(run)} />
      ) : !detail ? (
        runs !== null && runs.length === 0 ? (
          <Empty title="No runs yet" icon="evaluate">
            Add questions in the test set, from chat with "Save as test question", or let the model draft some. Then run the test set.
          </Empty>
        ) : (
          <p className="row muted"><Spinner label="Loading" /> Loading the run…</p>
        )
      ) : (
        <RunResults detail={detail} runs={runs ?? []} onSelect={setSelectedRun} onOpenDocument={setViewing} />
      )}
    </div>
  );
}

function RunConfirm({ estimate, onStart, onCancel }: { estimate: RunEstimateDto; onStart: () => void; onCancel: () => void }) {
  const sameModel = estimate.chatModel === estimate.judgeModel;
  return (
    <Card className="section-gap run-confirm">
      <h2>Run {estimate.questions} {estimate.questions === 1 ? "question" : "questions"}?</h2>
      <p>
        About {estimate.calls} calls to DeepSeek: <strong>{estimate.chatModel}</strong> writes each answer
        {sameModel ? " and grades it" : <> and <strong>{estimate.judgeModel}</strong> grades it</>}.
        Each question's passages, the project's facts and the answer are sent with it.
      </p>
      <p className="muted small">
        A question whose passages and settings have not changed since an earlier run is read from the cache and sends nothing.
        {estimate.drafts > 0 ? ` ${estimate.drafts} drafted ${estimate.drafts === 1 ? "question waits" : "questions wait"} for review and ${estimate.drafts === 1 ? "is" : "are"} left out.` : ""}
        {sameModel ? " The chat model is grading its own answers; a different judge model in the project's settings gives a more independent grade." : ""}
      </p>
      {estimate.blocked ? <Banner tone="warn">{estimate.blocked}</Banner> : null}
      <div className="form-actions">
        <Button variant="primary" onClick={onStart} disabled={estimate.blocked !== null}>Start run</Button>
        <Button variant="quiet" onClick={onCancel}>Cancel</Button>
      </div>
    </Card>
  );
}

function RunProgress({ run }: { run: EvalRunDto }) {
  const percent = run.total === 0 ? 0 : Math.round((run.completed / run.total) * 100);
  return (
    <Card className="section-gap">
      <p className="row"><Spinner label="Running" /> Asking question {Math.min(run.completed + 1, run.total)} of {run.total}…</p>
      <div className="progress" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent}>
        <span style={{ width: `${percent}%` }} />
      </div>
    </Card>
  );
}

function RunResults({ detail, runs, onSelect, onOpenDocument }: {
  detail: EvalRunDetailDto;
  runs: EvalRunDto[];
  onSelect: (id: string) => void;
  onOpenDocument: (documentId: string) => void;
}) {
  const [open, setOpen] = useState<string | null>(null);
  const { run, results } = detail;

  return (
    <div>
      <div className="row run-picker">
        <Field label="Run">
          <select value={run.id} onChange={(event) => onSelect(event.target.value)}>
            {runs.map((r) => (
              <option key={r.id} value={r.id}>{formatWhen(r.startedUtc)} · {statusLabel(r)}</option>
            ))}
          </select>
        </Field>
      </div>

      {run.error ? <Banner tone={run.status === "failed" ? "error" : "warn"}>{run.error}</Banner> : null}

      <dl className="stats section-gap">
        <Ratio label="Questions passed" value={run.passed} total={run.completed} />
        <Ratio label="Expected facts stated" value={run.factsStated} total={run.factsTotal} />
        <Ratio label="Statements backed" value={run.claimsSupported} total={run.claimsTotal} />
        <Ratio label="Expected document found" value={run.documentsFound} total={run.documentsExpected} />
      </dl>

      {results.length === 0 ? (
        <Empty title="No answers yet" icon="evaluate">The first answer appears here as soon as it is graded.</Empty>
      ) : (
        <table className="coverage eval-results">
          <thead>
            <tr>
              <th scope="col">Question</th>
              <th scope="col">Document</th>
              <th scope="col">Facts</th>
              <th scope="col">Grounded</th>
              <th scope="col">Result</th>
            </tr>
          </thead>
          <tbody>
            {results.map((result) => (
              <ResultRows
                key={result.questionId}
                result={result}
                open={open === result.questionId}
                onToggle={() => setOpen(open === result.questionId ? null : result.questionId)}
                onOpenDocument={onOpenDocument}
              />
            ))}
          </tbody>
        </table>
      )}

      <details className="run-settings">
        <summary>Settings of this run</summary>
        <dl className="run-settings-list">
          {run.settings.map((s) => (
            <div key={s.name} className="info-row">
              <dt>{s.name}</dt>
              <dd>{s.value}</dd>
            </div>
          ))}
        </dl>
      </details>
    </div>
  );
}

function ResultRows({ result, open, onToggle, onOpenDocument }: {
  result: EvalResultDto;
  open: boolean;
  onToggle: () => void;
  onOpenDocument: (documentId: string) => void;
}) {
  const verdict = result.verdict;
  const stated = verdict?.facts.filter((f) => f.stated).length ?? 0;
  const unsupported = verdict?.claims.filter((c) => !c.supported).length ?? 0;

  return (
    <>
      <tr className="eval-row">
        <th scope="row">
          <button type="button" className="link-button" aria-expanded={open} onClick={onToggle}>{result.question}</button>
        </th>
        <td>{result.foundDocument === null ? <span className="muted">—</span> : <Mark ok={result.foundDocument} label={result.foundDocument ? "Found" : "Missed"} />}</td>
        <td>
          {result.expectNoAnswer ? (
            <span className="muted small">Should decline</span>
          ) : result.facts.length === 0 ? (
            <span className="muted">—</span>
          ) : verdict ? (
            <Mark ok={stated === result.facts.length} label={`${stated} of ${result.facts.length}`} />
          ) : (
            <span className="muted">—</span>
          )}
        </td>
        <td>{verdict ? <Mark ok={unsupported === 0} label={unsupported === 0 ? "Yes" : `${unsupported} not backed`} /> : <span className="muted">—</span>}</td>
        <td>{result.error ? <span className="badge badge-warn">Not graded</span> : <span className={result.passed ? "badge badge-ready" : "badge badge-danger"}>{result.passed ? "Pass" : "Fail"}</span>}</td>
      </tr>
      {open ? (
        <tr className="eval-detail">
          <td colSpan={5}>
            {result.error ? <Banner tone="warn">{result.error}</Banner> : null}
            {verdict?.summary ? <p>{verdict.summary}</p> : null}
            {result.expectNoAnswer && verdict ? (
              <p className="small">{verdict.declines ? "The answer says the documents do not hold this, as expected." : "The answer did not say the documents lack this, though they do."}</p>
            ) : null}
            {verdict && verdict.facts.length > 0 ? (
              <>
                <h3>Expected facts</h3>
                <ul className="claims">
                  {verdict.facts.map((fact, i) => (
                    <li key={i} className={fact.stated ? "claim" : "claim claim-unsupported"}>
                      <span className="claim-mark" aria-label={fact.stated ? "Stated" : "Not stated"}>{fact.stated ? "✓" : "✗"}</span>
                      <span className="grow">
                        {fact.fact}
                        {fact.reason ? <span className="claim-reason">{fact.reason}</span> : null}
                      </span>
                    </li>
                  ))}
                </ul>
              </>
            ) : null}
            <h3>Answer</h3>
            <div
              className="eval-answer"
              dangerouslySetInnerHTML={{ __html: result.answer ? markUnsupported(renderMarkdown(result.answer), verdict?.claims ?? []) : "<p>(No answer.)</p>" }}
            />
            {verdict && verdict.claims.length > 0 ? (
              <details className="claims-all">
                <summary>Every statement in the answer</summary>
                <ul className="claims">
                  {verdict.claims.map((claim, i) => <ClaimRow key={i} claim={claim} />)}
                </ul>
              </details>
            ) : null}
            {result.sources.length > 0 ? (
              <details className="claims-all">
                <summary>The {result.sources.length} passages it was given</summary>
                <ol className="eval-passages">
                  {result.sources.map((source, i) => (
                    <li key={`${source.documentId}-${source.chunkIndex}-${i}`}>
                      <div className="row">
                        <span className="source-number">{i + 1}</span>
                        <strong className="grow">{source.chunkIndex === OVERVIEW_CHUNK ? `Overview · ${source.fileName}` : source.fileName}</strong>
                        <Button size="sm" variant="quiet" onClick={() => onOpenDocument(source.documentId)}>
                          <Icon name="file" /> Open in library
                        </Button>
                      </div>
                      <blockquote>{source.text}</blockquote>
                    </li>
                  ))}
                </ol>
              </details>
            ) : (
              <p className="muted small">Search found no passage for this question.</p>
            )}
          </td>
        </tr>
      ) : null}
    </>
  );
}

function History({ runs, comparison, onCompare, onCloseCompare, onOpen, onDelete }: {
  runs: EvalRunDto[];
  comparison: RunComparisonDto | null;
  onCompare: (before: EvalRunDto, after: EvalRunDto) => void;
  onCloseCompare: () => void;
  onOpen: (run: EvalRunDto) => void;
  onDelete: (run: EvalRunDto) => void;
}) {
  if (runs.length === 0) {
    return <Empty title="No runs yet" icon="evaluate">Each run is kept here with the settings it used, so you can see what a change did.</Empty>;
  }

  // Oldest first for the trend; the list below stays newest first.
  const finished = runs.filter((r) => r.status !== "running" && r.completed > 0).slice().reverse();

  return (
    <div>
      {comparison ? <Comparison comparison={comparison} onClose={onCloseCompare} /> : null}

      {finished.length > 1 ? (
        <Section title="Trend" description="Share of questions passed in each run, oldest on the left.">
          <Trend runs={finished} />
        </Section>
      ) : null}

      <Section title="Runs">
        <ul className="list" aria-label="Runs">
          {runs.map((run, i) => {
            const previous = runs.slice(i + 1).find((r) => r.status !== "running" && r.completed > 0);
            return (
              <li key={run.id}>
                <span className="list-icon"><Icon name="evaluate" /></span>
                <div className="grow">
                  <div className="list-title">
                    {run.completed === 0 ? "No answers" : `${run.passed} of ${run.completed} passed`}
                    {" "}<span className={run.status === "done" ? "badge" : run.status === "running" ? "badge badge-accent" : "badge badge-warn"}>{statusLabel(run)}</span>
                  </div>
                  <div className="list-meta">{formatWhen(run.startedUtc)} · {run.settings.find((s) => s.name === "Chat model")?.value}</div>
                </div>
                <div className="list-actions">
                  <Button size="sm" variant="quiet" onClick={() => onOpen(run)}>Open</Button>
                  {previous && run.status !== "running" ? (
                    <Button size="sm" variant="quiet" onClick={() => onCompare(previous, run)}>Compare with previous</Button>
                  ) : null}
                  {run.status !== "running" ? (
                    <Button size="sm" variant="quiet" className="btn-danger" onClick={() => onDelete(run)} aria-label="Delete run">
                      <Icon name="trash" />
                    </Button>
                  ) : null}
                </div>
              </li>
            );
          })}
        </ul>
      </Section>
    </div>
  );
}

function Comparison({ comparison, onClose }: { comparison: RunComparisonDto; onClose: () => void }) {
  const { before, after } = comparison;
  const changed = comparison.questions.filter((q) => q.change !== "same");
  const count = (kind: string) => comparison.questions.filter((q) => q.change === kind).length;

  return (
    <Card className="section-gap comparison">
      <div className="row">
        <h2 className="grow">{formatWhen(before.startedUtc)} → {formatWhen(after.startedUtc)}</h2>
        <Button variant="quiet" className="icon-btn" onClick={onClose} aria-label="Close the comparison" title="Close"><Icon name="close" /></Button>
      </div>
      <p>
        Passed {before.passed} of {before.completed}, then {after.passed} of {after.completed}.
        {" "}{count("improved")} improved, {count("regressed")} regressed{count("added") > 0 ? `, ${count("added")} new` : ""}.
      </p>

      <h3>What changed in the settings</h3>
      {comparison.settings.length === 0 ? (
        <p className="muted small">Nothing. A difference comes from the documents, the facts or the model's own variation.</p>
      ) : (
        <table className="coverage">
          <thead><tr><th scope="col">Setting</th><th scope="col">Before</th><th scope="col">After</th></tr></thead>
          <tbody>
            {comparison.settings.map((s) => (
              <tr key={s.name}><th scope="row">{s.name}</th><td>{s.before ?? "—"}</td><td>{s.after ?? "—"}</td></tr>
            ))}
          </tbody>
        </table>
      )}

      <h3>Questions that changed</h3>
      {changed.length === 0 ? (
        <p className="muted small">Every question kept its result.</p>
      ) : (
        <ul className="claims">
          {changed.map((q) => (
            <li key={q.questionId} className={q.change === "regressed" ? "claim claim-unsupported" : "claim"}>
              <span className={`badge ${q.change === "improved" ? "badge-ready" : q.change === "regressed" ? "badge-danger" : ""}`}>{changeLabel(q.change)}</span>
              <span className="grow">{q.question}</span>
            </li>
          ))}
        </ul>
      )}
      <p className="field-hint">With few questions, one or two changes can be the judge's own variation. Look at the answers before you keep a setting.</p>
    </Card>
  );
}

function Trend({ runs }: { runs: EvalRunDto[] }) {
  const width = 600;
  const height = 120;
  const pad = 12;
  const points = runs.map((run, i) => {
    const x = runs.length === 1 ? width / 2 : pad + (i * (width - 2 * pad)) / (runs.length - 1);
    const y = height - pad - (run.passed / run.completed) * (height - 2 * pad);
    return { x, y, run };
  });

  return (
    <Card>
      <svg className="trend" viewBox={`0 0 ${width} ${height}`} role="img" aria-label={`Pass rate over ${runs.length} runs`}>
        <line x1={pad} x2={width - pad} y1={height - pad} y2={height - pad} className="trend-axis" />
        <line x1={pad} x2={width - pad} y1={pad} y2={pad} className="trend-axis" />
        <polyline points={points.map((p) => `${p.x},${p.y}`).join(" ")} className="trend-line" />
        {points.map((p) => (
          <circle key={p.run.id} cx={p.x} cy={p.y} r={4} className="trend-dot">
            <title>{`${formatWhen(p.run.startedUtc)}: ${p.run.passed} of ${p.run.completed} passed`}</title>
          </circle>
        ))}
      </svg>
      <div className="row muted small trend-key"><span className="grow">0 %</span><span>100 % at the top line</span></div>
    </Card>
  );
}

function TestSet({ projectId, questions, onChanged, onError }: {
  projectId: string;
  questions: EvalQuestionDto[];
  onChanged: () => Promise<void>;
  onError: (message: string | null) => void;
}) {
  const empty = { question: "", facts: "", noAnswer: false };
  const [draft, setDraft] = useState(empty);
  const [editing, setEditing] = useState<{ id: string; question: string; facts: string; noAnswer: boolean; documentId: string | null } | null>(null);
  const [drafting, setDrafting] = useState(false);
  const [draftCount, setDraftCount] = useState(5);

  const drafts = questions.filter((q) => q.draft);
  const accepted = questions.filter((q) => !q.draft);

  async function call(action: () => Promise<unknown>) {
    try {
      await action();
      onError(null);
      await onChanged();
      return true;
    } catch (failure) {
      onError(describeError(failure));
      return false;
    }
  }

  async function add() {
    if (await call(() => evaluation.addQuestion({ projectId, question: draft.question, facts: lines(draft.facts, draft.noAnswer), documentId: null, expectNoAnswer: draft.noAnswer, origin: "manual" }))) {
      setDraft(empty);
    }
  }

  async function saveEdit() {
    if (!editing) {
      return;
    }

    const edit = editing;
    if (await call(() => evaluation.updateQuestion({ projectId, id: edit.id, question: edit.question, facts: lines(edit.facts, edit.noAnswer), documentId: edit.noAnswer ? null : edit.documentId, expectNoAnswer: edit.noAnswer }))) {
      setEditing(null);
    }
  }

  async function draftQuestions() {
    setDrafting(true);
    await call(() => evaluation.draftQuestions({ projectId, count: draftCount }));
    setDrafting(false);
  }

  const row = (q: EvalQuestionDto) => (
    <li key={q.id}>
      <span className="list-icon"><Icon name={q.draft ? "plus" : "evaluate"} /></span>
      {editing?.id === q.id ? (
        <form className="grow" onSubmit={(event) => { event.preventDefault(); void saveEdit(); }}>
          <QuestionFields value={editing} onChange={(next) => setEditing({ ...editing, ...next })} />
          <div className="row fact-edit-actions">
            <Button size="sm" type="submit" variant="primary" disabled={editing.question.trim().length === 0}>Save</Button>
            <Button size="sm" variant="quiet" onClick={() => setEditing(null)}>Cancel</Button>
          </div>
        </form>
      ) : (
        <>
          <div className="grow">
            <div className="list-title">{q.question}</div>
            {q.expectNoAnswer ? (
              <div className="list-meta">The documents do not answer this; the right reply says so.</div>
            ) : q.facts.length > 0 ? (
              <ul className="question-facts">{q.facts.map((f, i) => <li key={i}>{f}</li>)}</ul>
            ) : (
              <div className="list-meta">No facts: the answer only has to address the question.</div>
            )}
            <div className="list-meta">
              {originLabel(q.origin)}{q.fileName ? ` · expects ${q.fileName}` : ""} · {formatWhen(q.createdUtc)}
            </div>
          </div>
          <div className="list-actions">
            {q.draft ? <Button size="sm" variant="primary" onClick={() => void call(() => evaluation.acceptQuestion({ projectId, id: q.id }))}>Accept</Button> : null}
            <Button size="sm" variant="quiet" onClick={() => setEditing({ id: q.id, question: q.question, facts: q.facts.join("\n"), noAnswer: q.expectNoAnswer, documentId: q.documentId })}>Edit</Button>
            <Button size="sm" variant="quiet" className="btn-danger" onClick={() => void call(() => evaluation.deleteQuestion({ projectId, id: q.id }))}>
              <Icon name="trash" /> {q.draft ? "Discard" : "Remove"}
            </Button>
          </div>
        </>
      )}
    </li>
  );

  return (
    <div>
      <Section
        title="Draft questions from the documents"
        description="The model reads passages spread across your documents and writes a question and its facts for each. Drafts stay out of runs until you accept them, because a drafted question can be too easy or slightly wrong."
      >
        <Card>
          <div className="row">
            <Field label="How many">
              <select value={draftCount} onChange={(event) => setDraftCount(Number(event.target.value))}>
                {[3, 5, 10].map((n) => <option key={n} value={n}>{n}</option>)}
              </select>
            </Field>
            <Button onClick={() => void draftQuestions()} disabled={drafting}>
              {drafting ? <><Spinner label="Drafting" /> Drafting…</> : <><Icon name="plus" /> Draft questions</>}
            </Button>
          </div>
          <p className="field-hint">One call to the chat model, with the text of up to {draftCount} passages.</p>
        </Card>
      </Section>

      {drafts.length > 0 ? (
        <Section title="Waiting for review" actions={<span className="count">{drafts.length}</span>}>
          <ul className="list" aria-label="Drafted questions">{drafts.map(row)}</ul>
        </Section>
      ) : null}

      <Section title="Add a question">
        <Card>
          <form onSubmit={(event) => { event.preventDefault(); void add(); }}>
            <QuestionFields value={draft} onChange={(next) => setDraft({ ...draft, ...next })} />
            <div className="form-actions">
              <Button type="submit" variant="primary" disabled={draft.question.trim().length === 0}><Icon name="plus" /> Add question</Button>
            </div>
          </form>
        </Card>
      </Section>

      <Section title="Test set" actions={<span className="count">{accepted.length} {accepted.length === 1 ? "question" : "questions"}</span>}>
        {accepted.length === 0 ? (
          <Empty title="No test questions yet" icon="evaluate">
            Add one above, accept a draft, or press "Save as test question" under a chat answer.
          </Empty>
        ) : (
          <ul className="list" aria-label="Test questions">{accepted.map(row)}</ul>
        )}
      </Section>
    </div>
  );
}

function QuestionFields({ value, onChange }: {
  value: { question: string; facts: string; noAnswer: boolean };
  onChange: (next: Partial<{ question: string; facts: string; noAnswer: boolean }>) => void;
}) {
  return (
    <div className="question-fields">
      <Field label="Question">
        <textarea rows={2} maxLength={2000} value={value.question} onChange={(event) => onChange({ question: event.target.value })} placeholder="When does the Lumen pilot launch?" />
      </Field>
      <label className="check">
        <input type="checkbox" checked={value.noAnswer} onChange={(event) => onChange({ noAnswer: event.target.checked })} />
        <span>
          The documents do not answer this
          <span className="field-hint">Catches answers that make something up. The right reply says the documents do not say.</span>
        </span>
      </label>
      {value.noAnswer ? null : (
        <Field label="Facts a right answer states" hint="One per line, in your own words. Each is checked on its own.">
          <textarea rows={3} value={value.facts} onChange={(event) => onChange({ facts: event.target.value })} placeholder="The pilot launches on 14 March 2027." />
        </Field>
      )}
    </div>
  );
}

function Ratio({ label, value, total }: { label: string; value: number; total: number }) {
  return (
    <div className="stat">
      <dt>{label}</dt>
      <dd>{total === 0 ? "—" : <>{value}<span className="stat-of"> of {total}</span></>}</dd>
    </div>
  );
}

function Mark({ ok, label }: { ok: boolean; label: string }) {
  return <span className={ok ? "mark-ok" : "mark-bad"}>{ok ? "✓" : "✗"} {label}</span>;
}

function lines(text: string, none: boolean): string[] {
  return none ? [] : text.split("\n").map((line) => line.trim()).filter((line) => line.length > 0);
}

function statusLabel(run: EvalRunDto): string {
  return run.status === "running" ? "Running" : run.status === "done" ? "Done" : run.status === "canceled" ? "Stopped" : "Failed";
}

function changeLabel(change: string): string {
  return change === "improved" ? "Now passes" : change === "regressed" ? "Now fails" : change === "added" ? "New" : change === "removed" ? "Removed" : "Same";
}

function originLabel(origin: string): string {
  return origin === "chat" ? "Saved from chat" : origin === "drafted" ? "Drafted by the model" : "Added by hand";
}
