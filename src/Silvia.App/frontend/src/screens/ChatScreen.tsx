import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type MouseEvent } from "react";
import { chat, consume, describeError, evaluation, facts, formatWhen, library, type CitationDto, type ConversationDto, type MessageDto, type VerdictDto } from "../api";
import { renderMarkdown } from "../markdown";
import { Button, Field, Icon, Spinner } from "../ui";
import { AnswerCheck, markUnsupported } from "./AnswerCheck";

interface Turn {
  role: "user" | "assistant";
  content: string;
  sources: CitationDto[];
  /** Set when the person pressed Stop; such a turn is not saved by the host. */
  stopped?: boolean;
  /** Set once the person saved a correction of this answer as a project fact. */
  corrected?: boolean;
  /** The judge's reading of this answer, once the person asked for it. */
  check?: { status: "checking" } | { status: "done"; verdict: VerdictDto } | { status: "failed"; message: string };
  /** Set once the person saved this question to the project's test set. */
  savedTest?: boolean;
}

/** A question being saved to the test set from an answer, as typed so far. */
interface TestDraft {
  turn: number;
  question: string;
  facts: string;
  documentId: string;
  noAnswer: boolean;
}

/** A document overview arrives as a citation with this chunk index; real passages count from zero. */
const OVERVIEW_CHUNK = -1;

interface Props {
  projectId: string;
  onError: (message: string | null) => void;
}

export function ChatScreen({ projectId, onError }: Props) {
  const [conversations, setConversations] = useState<ConversationDto[]>([]);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [turns, setTurns] = useState<Turn[]>([]);
  const [draft, setDraft] = useState("");
  const [busy, setBusy] = useState(false);
  const [picked, setPicked] = useState<{ turn: number; source: number } | null>(null);
  // Deleting takes a second click on the row itself, so a stray click on the icon never loses a chat.
  const [confirmingId, setConfirmingId] = useState<string | null>(null);
  // The answer being corrected, and the fact typed so far.
  const [correcting, setCorrecting] = useState<{ turn: number; text: string } | null>(null);
  const [testing, setTesting] = useState<TestDraft | null>(null);
  const abort = useRef<AbortController | null>(null);
  const endRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    void loadConversations();
    return () => abort.current?.abort();
    // The screen is keyed by project, so this effect runs once per project.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    endRef.current?.scrollIntoView({ block: "end" });
  }, [turns]);

  async function loadConversations() {
    try {
      setConversations([...(await chat.listConversations({ projectId }))]);
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  async function open(conversation: ConversationDto) {
    abort.current?.abort();
    setBusy(false);
    setActiveId(conversation.id);
    setPicked(null);
    setCorrecting(null);
    setTesting(null);
    try {
      const rows: ReadonlyArray<MessageDto> = await chat.messages({ projectId, conversationId: conversation.id });
      setTurns(rows.map((row) => ({ role: row.role === "user" ? "user" : "assistant", content: row.content, sources: [...row.sources] })));
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  function startNew() {
    abort.current?.abort();
    setBusy(false);
    setActiveId(null);
    setTurns([]);
    setPicked(null);
    setCorrecting(null);
    setTesting(null);
  }

  async function remove(conversation: ConversationDto) {
    setConfirmingId(null);
    try {
      await chat.deleteConversation({ projectId, conversationId: conversation.id });
      if (activeId === conversation.id) {
        startNew();
      }

      await loadConversations();
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  async function send(again?: string) {
    const question = (again ?? draft).trim();
    if (!question || busy) {
      return;
    }

    if (again === undefined) {
      setDraft("");
    }

    setCorrecting(null);
    setTesting(null);
    setBusy(true);
    setPicked(null);
    onError(null);
    let conversationId = activeId;
    const controller = new AbortController();
    abort.current = controller;
    setTurns((rows) => [...rows, { role: "user", content: question, sources: [] }, { role: "assistant", content: "", sources: [] }]);

    try {
      const stream = await chat.ask({ projectId, conversationId, question }, { signal: controller.signal });
      await consume(stream, (update) => {
        if (update.kind === "conversation" && update.conversationId) {
          conversationId = update.conversationId;
          setActiveId(update.conversationId);
        } else if (update.kind === "delta" && update.text) {
          const text = update.text;
          setTurns((rows) => patchLast(rows, (turn) => ({ ...turn, content: turn.content + text })));
        } else if (update.kind === "sources") {
          const sources = [...(update.sources ?? [])];
          setTurns((rows) => patchLast(rows, (turn) => ({ ...turn, sources })));
        }
      });
    } catch (failure) {
      if (controller.signal.aborted) {
        setTurns((rows) => patchLast(rows, (turn) => (turn.role === "assistant" ? { ...turn, stopped: true } : turn)));
      } else {
        onError(describeError(failure));
      }
    } finally {
      setBusy(false);
      abort.current = null;
      if (conversationId) {
        setActiveId(conversationId);
        await loadConversations();
      }
    }
  }

  async function saveCorrection() {
    if (!correcting) {
      return;
    }

    const turn = correcting.turn;
    try {
      await facts.add({ projectId, text: correcting.text });
      setCorrecting(null);
      setTurns((rows) => rows.map((row, index) => (index === turn ? { ...row, corrected: true } : row)));
      onError(null);
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  function patchTurn(index: number, change: (turn: Turn) => Turn) {
    setTurns((rows) => rows.map((row, i) => (i === index ? change(row) : row)));
  }

  /** The question an answer replies to: the user turn just before it. */
  function questionFor(index: number): string {
    return index > 0 && turns[index - 1].role === "user" ? turns[index - 1].content : "";
  }

  async function checkAnswer(index: number) {
    const turn = turns[index];
    patchTurn(index, (row) => ({ ...row, check: { status: "checking" } }));
    try {
      const verdict = await evaluation.checkAnswer({ projectId, question: questionFor(index), answer: turn.content, sources: turn.sources });
      patchTurn(index, (row) => ({ ...row, check: { status: "done", verdict } }));
    } catch (failure) {
      patchTurn(index, (row) => ({ ...row, check: { status: "failed", message: describeError(failure) } }));
    }
  }

  function startTest(index: number) {
    const turn = turns[index];
    // A checked answer offers its backed statements as the facts to keep; the person edits them.
    const backed = turn.check?.status === "done" ? turn.check.verdict.claims.filter((c) => c.supported).map((c) => c.text) : [];
    const cited = turn.sources.find((s) => s.chunkIndex !== OVERVIEW_CHUNK) ?? turn.sources[0];
    setCorrecting(null);
    setTesting({ turn: index, question: questionFor(index), facts: backed.join("\n"), documentId: cited?.documentId ?? "", noAnswer: false });
  }

  async function saveTest() {
    if (!testing) {
      return;
    }

    const draft = testing;
    try {
      await evaluation.addQuestion({
        projectId,
        question: draft.question,
        facts: draft.noAnswer ? [] : draft.facts.split("\n").map((f) => f.trim()).filter((f) => f.length > 0),
        documentId: draft.noAnswer || draft.documentId === "" ? null : draft.documentId,
        expectNoAnswer: draft.noAnswer,
        origin: "chat",
      });
      setTesting(null);
      patchTurn(draft.turn, (row) => ({ ...row, savedTest: true }));
      onError(null);
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  function onKey(event: KeyboardEvent<HTMLTextAreaElement>) {
    if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) {
      event.preventDefault();
      void send();
    }
  }

  function onAnswerClick(event: MouseEvent<HTMLDivElement>, turnIndex: number) {
    const anchor = (event.target as Element).closest("a[href^='#cite-']");
    if (!anchor) {
      return;
    }

    event.preventDefault();
    const number = Number(anchor.getAttribute("href")?.replace("#cite-", ""));
    setPicked({ turn: turnIndex, source: number - 1 });
  }

  function testButtons(index: number, turn: Turn) {
    return (
      <>
        {turn.check ? null : (
          <Button size="sm" variant="quiet" disabled={busy} onClick={() => void checkAnswer(index)}>
            <Icon name="check" /> Check this answer
          </Button>
        )}
        {turn.savedTest ? (
          <span className="message-note">Saved to the test set.</span>
        ) : (
          <Button size="sm" variant="quiet" onClick={() => startTest(index)}>
            <Icon name="plus" /> Save as test question
          </Button>
        )}
      </>
    );
  }

  const evidence = useMemo(() => {
    if (!picked) {
      return null;
    }

    const turn = turns[picked.turn];
    const source = turn?.sources[picked.source];
    return source ? { turn: picked.turn, source, index: picked.source } : null;
  }, [picked, turns]);

  return (
    <div className={evidence ? "chat with-evidence" : "chat"}>
      <aside className="chat-list" aria-label="Conversations">
        <Button variant="primary" onClick={startNew}>
          <Icon name="plus" /> New chat
        </Button>
        <span className="chat-list-head">Conversations</span>
        {conversations.length === 0 ? (
          <p className="chat-list-empty">Your conversations appear here.</p>
        ) : (
          <ul className="conversations">
            {conversations.map((conversation) =>
              conversation.id === confirmingId ? (
                <li key={conversation.id} className="conversation-confirm" role="group" aria-label={`Delete ${conversation.title}?`}>
                  <span className="conversation-confirm-text">Delete this chat?</span>
                  <div className="conversation-confirm-actions">
                    <Button size="sm" variant="primary" className="btn-destructive" onClick={() => void remove(conversation)} autoFocus>
                      Delete
                    </Button>
                    <Button size="sm" variant="quiet" onClick={() => setConfirmingId(null)}>
                      Keep
                    </Button>
                  </div>
                </li>
              ) : (
                <li key={conversation.id}>
                  <button
                    type="button"
                    className="conversation"
                    aria-current={conversation.id === activeId ? "true" : undefined}
                    onClick={() => void open(conversation)}
                  >
                    <span className="conversation-title">{conversation.title}</span>
                    <span className="conversation-date">{formatWhen(conversation.createdUtc)}</span>
                  </button>
                  <button
                    type="button"
                    className="conversation-delete"
                    onClick={() => setConfirmingId(conversation.id)}
                    aria-label={`Delete ${conversation.title}`}
                    title="Delete chat"
                  >
                    <Icon name="trash" size={14} />
                  </button>
                </li>
              ),
            )}
          </ul>
        )}
      </aside>

      <section className="thread" aria-label="Conversation">
        {turns.length === 0 ? (
          <div className="thread-empty">
            <div className="hero">
              <span className="hero-rule" aria-hidden="true" />
              <h2 className="hero-title">
                Ask your <em>documents</em>.
              </h2>
              <p className="hero-lede">Answers come from this project only, and every answer cites the files it used.</p>
            </div>
          </div>
        ) : (
          <div className="messages" aria-live="polite">
            <div className="messages-inner">
              {turns.map((turn, index) => (
                <div key={index} className={`message ${turn.role}`} onClick={(event) => onAnswerClick(event, index)}>
                  {turn.role === "user" ? (
                    <p>{turn.content}</p>
                  ) : (
                    <>
                      {turn.content.length === 0 && busy && index === turns.length - 1 ? <Spinner label="Writing the answer" /> : null}
                      <div dangerouslySetInnerHTML={{ __html: markUnsupported(renderMarkdown(withCitations(turn.content)), turn.check?.status === "done" ? turn.check.verdict.claims : []) }} />
                      {turn.stopped ? (
                        <p className="message-note">{turn.content.length === 0 ? "Stopped before an answer arrived." : "Stopped."}</p>
                      ) : turn.content.length === 0 && !(busy && index === turns.length - 1) ? (
                        <p className="message-note">
                          {turn.sources.length > 0
                            ? "This project answers with passages only. These are the closest ones."
                            : "No passages matched this question."}
                        </p>
                      ) : null}
                      {turn.sources.length > 0 ? (
                        <div className="sources" aria-label="Sources">
                          {turn.sources.map((source, sourceIndex) => (
                            <button
                              key={`${source.documentId}-${source.chunkIndex}`}
                              type="button"
                              className="source"
                              aria-pressed={picked?.turn === index && picked.source === sourceIndex}
                              onClick={() => setPicked({ turn: index, source: sourceIndex })}
                            >
                              <span className="source-number">{sourceIndex + 1}</span>
                              <span className="source-name">{sourceName(source)}</span>
                            </button>
                          ))}
                        </div>
                      ) : null}
                      {turn.check ? (
                        <AnswerCheck
                          check={turn.check}
                          onCite={(number) => setPicked({ turn: index, source: number - 1 })}
                          onRetry={() => void checkAnswer(index)}
                        />
                      ) : null}
                      {testing?.turn === index ? (
                        <form className="correction" onSubmit={(event) => { event.preventDefault(); void saveTest(); }}>
                          <Field label="Test question">
                            <textarea rows={2} maxLength={2000} value={testing.question} onChange={(event) => setTesting({ ...testing, question: event.target.value })} autoFocus />
                          </Field>
                          <label className="check">
                            <input type="checkbox" checked={testing.noAnswer} onChange={(event) => setTesting({ ...testing, noAnswer: event.target.checked })} />
                            <span>
                              The documents do not answer this
                              <span className="field-hint">The right reply then says so instead of answering.</span>
                            </span>
                          </label>
                          {testing.noAnswer ? null : (
                            <>
                              <Field label="Facts a right answer states" hint="One per line. For a wrong answer, write what the right one says.">
                                <textarea rows={4} value={testing.facts} onChange={(event) => setTesting({ ...testing, facts: event.target.value })} placeholder="The pilot launches on 14 March 2027." />
                              </Field>
                              <Field label="Expected document" hint="A run also checks that search finds this file.">
                                <select value={testing.documentId} onChange={(event) => setTesting({ ...testing, documentId: event.target.value })}>
                                  <option value="">Any document</option>
                                  {distinctDocuments(turn.sources).map((source) => (
                                    <option key={source.documentId} value={source.documentId}>{source.fileName}</option>
                                  ))}
                                </select>
                              </Field>
                            </>
                          )}
                          <div className="row">
                            <Button size="sm" type="submit" variant="primary" disabled={testing.question.trim().length === 0}>Save test question</Button>
                            <Button size="sm" variant="quiet" onClick={() => setTesting(null)}>Cancel</Button>
                          </div>
                        </form>
                      ) : null}
                      {turn.content.length > 0 && !(busy && index === turns.length - 1) && testing?.turn !== index ? (
                        correcting?.turn === index ? (
                          <form className="correction" onSubmit={(event) => { event.preventDefault(); void saveCorrection(); }}>
                            <label className="field">
                              <span className="field-label">What is actually true?</span>
                              <textarea
                                rows={3}
                                maxLength={2000}
                                value={correcting.text}
                                onChange={(event) => setCorrecting({ turn: index, text: event.target.value })}
                                placeholder="State the fact the answer should have used."
                                autoFocus
                              />
                              <span className="field-hint">Saved as a project fact. Every later answer in this project is given it.</span>
                            </label>
                            <div className="row">
                              <Button size="sm" type="submit" variant="primary" disabled={correcting.text.trim().length === 0}>Save fact</Button>
                              <Button size="sm" variant="quiet" onClick={() => setCorrecting(null)}>Cancel</Button>
                            </div>
                          </form>
                        ) : turn.corrected ? (
                          <div className="message-actions">
                            <span className="message-note">Saved as a project fact.</span>
                            {testButtons(index, turn)}
                            {index > 0 && turns[index - 1].role === "user" ? (
                              <Button size="sm" variant="quiet" disabled={busy} onClick={() => void send(turns[index - 1].content)}>
                                <Icon name="refresh" /> Ask again
                              </Button>
                            ) : null}
                          </div>
                        ) : (
                          <div className="message-actions">
                            <Button size="sm" variant="quiet" onClick={() => { setTesting(null); setCorrecting({ turn: index, text: "" }); }}>
                              <Icon name="fact" /> Correct this
                            </Button>
                            {testButtons(index, turn)}
                          </div>
                        )
                      ) : null}
                    </>
                  )}
                </div>
              ))}
              <div ref={endRef} />
            </div>
          </div>
        )}

        <form className="composer" onSubmit={(event) => { event.preventDefault(); void send(); }}>
          <div className="composer-inner">
            <div className="composer-box">
              <textarea
                aria-label="Your question"
                value={draft}
                onChange={(event) => setDraft(event.target.value)}
                onKeyDown={onKey}
                placeholder="Ask a question about this project"
                rows={1}
              />
              {busy ? (
                <Button onClick={() => abort.current?.abort()}>
                  <Icon name="stop" /> Stop
                </Button>
              ) : (
                <Button type="submit" variant="primary" disabled={draft.trim().length === 0}>
                  Send <Icon name="send" />
                </Button>
              )}
            </div>
            <p className="composer-hint">{busy ? "Answering…" : "Enter sends · Shift+Enter adds a line"}</p>
          </div>
        </form>
      </section>

      {evidence ? (
        <aside className="evidence" aria-label="Cited passage">
          <div className="evidence-head">
            <h2>
              <span className="source-number">{evidence.index + 1}</span>
              {sourceName(evidence.source)}
            </h2>
            <Button variant="quiet" className="icon-btn" onClick={() => setPicked(null)} aria-label="Close the cited passage" title="Close">
              <Icon name="close" />
            </Button>
          </div>
          <p className="path">{evidence.source.url ?? evidence.source.documentId}</p>
          {evidence.source.chunkIndex === OVERVIEW_CHUNK ? (
            <p className="muted small">Written by the chat model from the whole document when it was indexed.</p>
          ) : null}
          <blockquote className={evidence.source.chunkIndex === OVERVIEW_CHUNK ? "overview" : undefined}>{evidence.source.text}</blockquote>
          <div className="row">
            {evidence.source.url ? (
              <Button size="sm" onClick={() => void openPage(projectId, evidence.source.url!, onError)}>
                <Icon name="globe" /> Open page
              </Button>
            ) : (
              <Button size="sm" onClick={() => void openFile(projectId, evidence.source.documentId, onError)}>
                <Icon name="external" /> Open file
              </Button>
            )}
            <Button size="sm" onClick={() => void revealFile(projectId, evidence.source.documentId, onError)}>
              <Icon name="folder" /> Show in folder
            </Button>
          </div>
        </aside>
      ) : null}
    </div>
  );
}

function sourceName(source: CitationDto): string {
  return source.chunkIndex === OVERVIEW_CHUNK ? `Overview · ${source.fileName}` : source.fileName;
}

function distinctDocuments(sources: CitationDto[]): CitationDto[] {
  const seen = new Set<string>();
  return sources.filter((source) => {
    if (seen.has(source.documentId)) {
      return false;
    }

    seen.add(source.documentId);
    return true;
  });
}

function patchLast(rows: Turn[], change: (turn: Turn) => Turn): Turn[] {
  if (rows.length === 0) {
    return rows;
  }

  return [...rows.slice(0, -1), change(rows[rows.length - 1])];
}

/** Turns the model's [n] markers into links the renderer keeps, so each citation can be opened. */
function withCitations(text: string): string {
  return text.replace(/\[(\d{1,2})\](?!\()/g, "[$1](#cite-$1)");
}

async function openFile(projectId: string, documentId: string, onError: (message: string | null) => void) {
  try {
    await library.openDocument({ projectId, documentId, page: null });
  } catch (failure) {
    onError(describeError(failure));
  }
}

/** Opens a cited web page in the browser. The host opens only addresses that are pages of this project. */
async function openPage(projectId: string, url: string, onError: (message: string | null) => void) {
  try {
    await library.openUrl({ projectId, url });
  } catch (failure) {
    onError(describeError(failure));
  }
}

async function revealFile(projectId: string, documentId: string, onError: (message: string | null) => void) {
  try {
    await library.revealDocument({ projectId, documentId, page: null });
  } catch (failure) {
    onError(describeError(failure));
  }
}
