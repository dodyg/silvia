import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type MouseEvent } from "react";
import { chat, consume, describeError, formatWhen, library, type CitationDto, type ConversationDto, type MessageDto } from "../api";
import { renderMarkdown } from "../markdown";
import { Button, Empty, Icon, Spinner } from "../ui";

interface Turn {
  role: "user" | "assistant";
  content: string;
  sources: CitationDto[];
  /** Set when the person pressed Stop; such a turn is not saved by the host. */
  stopped?: boolean;
}

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

  async function send() {
    const question = draft.trim();
    if (!question || busy) {
      return;
    }

    setDraft("");
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
            <Empty title="Ask about your documents" icon="chat">
              Answers come from this project only, and every answer cites the files it used.
            </Empty>
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
                      <div dangerouslySetInnerHTML={{ __html: renderMarkdown(withCitations(turn.content)) }} />
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
                              <span className="source-name">{source.fileName}</span>
                            </button>
                          ))}
                        </div>
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
              {evidence.source.fileName}
            </h2>
            <Button variant="quiet" className="icon-btn" onClick={() => setPicked(null)} aria-label="Close the cited passage" title="Close">
              <Icon name="close" />
            </Button>
          </div>
          <p className="path">{evidence.source.documentId}</p>
          <blockquote>{evidence.source.text}</blockquote>
          <div className="row">
            <Button size="sm" onClick={() => void openFile(projectId, evidence.source.documentId, onError)}>
              <Icon name="external" /> Open file
            </Button>
            <Button size="sm" onClick={() => void revealFile(projectId, evidence.source.documentId, onError)}>
              <Icon name="folder" /> Show in folder
            </Button>
          </div>
        </aside>
      ) : null}
    </div>
  );
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

async function revealFile(projectId: string, documentId: string, onError: (message: string | null) => void) {
  try {
    await library.revealDocument({ projectId, documentId, page: null });
  } catch (failure) {
    onError(describeError(failure));
  }
}
