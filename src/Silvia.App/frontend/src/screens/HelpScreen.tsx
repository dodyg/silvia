import { useEffect, useMemo, useRef, useState } from "react";
import { filterTopics, HELP_TOPICS, type HelpTopicId } from "../help";
import { renderMarkdown } from "../markdown";
import { Empty, PageHeader } from "../ui";

/**
 * What each screen and setting does, and how to get good answers. A topic list with a search box beside the
 * chosen topic. Another screen can open it at a topic, as Settings does for its own.
 */
export function HelpScreen({ topic, onTopic }: { topic: HelpTopicId; onTopic: (topic: HelpTopicId) => void }) {
  const [query, setQuery] = useState("");
  const topics = useMemo(() => filterTopics(query), [query]);
  const article = useRef<HTMLElement>(null);

  // A search that leaves the chosen topic out of the list moves to the first topic that matches.
  const shown = topics.some((t) => t.id === topic) ? topic : topics[0]?.id ?? null;
  const current = HELP_TOPICS.find((t) => t.id === shown) ?? null;
  const html = useMemo(() => (current ? renderMarkdown(current.body) : ""), [current]);

  useEffect(() => {
    article.current?.scrollTo?.({ top: 0 });
  }, [shown]);

  return (
    <div>
      <PageHeader title="Help" description="What each screen and setting does, and how to get good answers from your documents." />
      <div className="help">
        <nav className="help-topics" aria-label="Help topics">
          <input
            type="search"
            className="help-search"
            placeholder="Search help"
            aria-label="Search help"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
          />
          {topics.length === 0 ? <p className="muted small">No topic mentions “{query.trim()}”.</p> : null}
          <ul>
            {topics.map((t) => (
              <li key={t.id}>
                <button type="button" aria-current={t.id === shown ? "page" : undefined} onClick={() => onTopic(t.id)}>
                  <span className="help-topic-title">{t.title}</span>
                  <span className="help-topic-summary">{t.summary}</span>
                </button>
              </li>
            ))}
          </ul>
        </nav>
        {current ? (
          <article ref={article} className="help-body" aria-labelledby="help-title">
            <h2 id="help-title">{current.title}</h2>
            <div dangerouslySetInnerHTML={{ __html: html }} />
          </article>
        ) : (
          <Empty title="Nothing matches" icon="search">
            Try other words, or clear the search to see every topic.
          </Empty>
        )}
      </div>
    </div>
  );
}
