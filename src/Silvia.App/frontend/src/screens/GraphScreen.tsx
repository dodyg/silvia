import { useEffect, useMemo, useRef, useState, type FormEvent, type MouseEvent } from "react";
import { describeError, inspect, library, type GraphDto, type GraphNodeDto } from "../api";
import { Button, Empty, Icon, PageHeader, Section, Spinner } from "../ui";

interface Props {
  projectId: string;
  onError: (message: string | null) => void;
}

// The drawing's own coordinate space, as on the map; the SVG scales it to the column width.
const WIDTH = 1000;
const HEIGHT = 620;
const PAD = 28;
const GRID = 10_000;

// Three hues stay apart for every reader; the other communities share the neutral color.
const SLOTS = 3;

/** The highest ranked entities are named on the drawing; the rest are named when pointed at. */
const LABELS = 16;

/** Hover reaches the nearest entity within this many plot units. */
const HOVER_RADIUS = 20;

/** Relationships listed for the entity in focus before "and N more". */
const SHOWN_RELATIONSHIPS = 12;

/**
 * The knowledge graph: the entities the chat model extracted from the documents, joined by the
 * relationships between them, and grouped into communities. Unlike the map, position here comes from
 * the links, not from the wording: entities that are related sit together.
 */
export function GraphScreen({ projectId, onError }: Props) {
  const [graph, setGraph] = useState<GraphDto | null>(null);
  const [colored, setColored] = useState<Array<number | null>>([]);
  const [hover, setHover] = useState<number | null>(null);
  const [pinned, setPinned] = useState<number | null>(null);
  const [query, setQuery] = useState("");
  const [missed, setMissed] = useState<string | null>(null);
  const svg = useRef<SVGSVGElement>(null);

  async function load() {
    setGraph(null);
    setHover(null);
    setPinned(null);
    try {
      const next = await inspect.graph({ projectId });
      setGraph(next);
      setColored(largest(next.nodes));
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  useEffect(() => {
    void load();
    // The screen is keyed by project, so this effect runs once per project.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Each community with entities on the drawing, by how many of them it holds, named after its top entity.
  const communities = useMemo(() => {
    const rows = new Map<number, { index: number; count: number; lead: string }>();
    graph?.nodes.forEach((node) => {
      const primary = node.communities[0];
      if (primary === undefined) {
        return;
      }

      // Nodes arrive highest ranked first, so the first one seen leads the community.
      const row = rows.get(primary) ?? { index: primary, count: 0, lead: node.name };
      row.count += 1;
      rows.set(primary, row);
    });

    return [...rows.values()].sort((a, b) => b.count - a.count || a.index - b.index);
  }, [graph]);

  const neighbors = useMemo(() => {
    const lists = (graph?.nodes ?? []).map(() => new Set<number>());
    for (const edge of graph?.edges ?? []) {
      lists[edge.source]?.add(edge.target);
      lists[edge.target]?.add(edge.source);
    }

    return lists;
  }, [graph]);

  if (!graph) {
    return (
      <div>
        <PageHeader title="Graph" description="The people, organisations and systems in the project and how they relate." />
        <p className="row muted"><Spinner label="Loading" /> Laying out the knowledge graph…</p>
      </div>
    );
  }

  if (!graph.available) {
    return (
      <div>
        <PageHeader title="Graph" description="The people, organisations and systems in the project and how they relate." />
        <Empty title="This project has no knowledge graph" icon="graph">
          Turn on the knowledge graph in Settings. The chat model then reads each document and extracts the named things in it and how they relate, which this view draws.
        </Empty>
      </div>
    );
  }

  if (graph.nodes.length === 0) {
    return (
      <div>
        <PageHeader
          title="Graph"
          description="The people, organisations and systems in the project and how they relate."
          actions={<Button onClick={() => void load()}><Icon name="refresh" /> Redraw</Button>}
        />
        <Empty title="The graph is still empty" icon="graph">
          The chat model has not extracted anything yet. Activity shows the documents waiting for it; redraw once it is done.
        </Empty>
      </div>
    );
  }

  const nodes = graph.nodes;
  const slotOf = (node: GraphNodeDto) => (node.communities[0] === undefined ? -1 : colored.indexOf(node.communities[0]));
  const x = (node: GraphNodeDto) => PAD + (node.x / GRID) * (WIDTH - 2 * PAD);
  const y = (node: GraphNodeDto) => HEIGHT - PAD - (node.y / GRID) * (HEIGHT - 2 * PAD);
  const radius = (node: GraphNodeDto) => 3.5 + 7 * Math.sqrt(node.rank / 1000);

  const focus = hover ?? pinned;
  const near = focus === null ? null : neighbors[focus];
  const lit = (index: number) => focus === null || index === focus || (near?.has(index) ?? false);

  // Uncolored entities first, then colored, then the ones in focus, so the most relevant are drawn on top.
  const order = nodes.map((_, i) => i).sort((a, b) => weight(a) - weight(b));
  function weight(index: number) {
    return (lit(index) && focus !== null ? 2 : 0) + (slotOf(nodes[index]) >= 0 ? 1 : 0);
  }

  const labeled = new Set<number>(nodes.slice(0, LABELS).map((_, i) => i));
  if (focus !== null) {
    labeled.clear();
    labeled.add(focus);
    near?.forEach((i) => labeled.add(i));
  }

  function nearest(event: MouseEvent<SVGSVGElement>): number | null {
    const matrix = svg.current?.getScreenCTM()?.inverse();
    if (!matrix) {
      return null;
    }

    const at = new DOMPoint(event.clientX, event.clientY).matrixTransform(matrix);
    let best: number | null = null;
    let bestDistance = HOVER_RADIUS * HOVER_RADIUS;
    nodes.forEach((node, i) => {
      const dx = x(node) - at.x;
      const dy = y(node) - at.y;
      const distance = dx * dx + dy * dy;
      if (distance < bestDistance) {
        best = i;
        bestDistance = distance;
      }
    });

    return best;
  }

  function find(event: FormEvent) {
    event.preventDefault();
    const wanted = query.trim().toLocaleLowerCase();
    if (wanted.length === 0) {
      return;
    }

    const exact = nodes.findIndex((n) => n.name.toLocaleLowerCase() === wanted);
    const index = exact >= 0 ? exact : nodes.findIndex((n) => n.name.toLocaleLowerCase().includes(wanted));
    setMissed(index >= 0 ? null : query.trim());
    if (index >= 0) {
      setHover(null);
      setPinned(index);
    }
  }

  function toggle(community: number) {
    setColored((current) => {
      const slots = [...current];
      const at = slots.indexOf(community);
      if (at >= 0) {
        // The freed slot stays empty, so the other communities keep their colors.
        slots[at] = null;
        return slots;
      }

      const free = slots.indexOf(null);
      if (free >= 0) {
        slots[free] = community;
      } else if (slots.length < SLOTS) {
        slots.push(community);
      }

      return slots;
    });
  }

  function open(documentId: string) {
    void library.openDocument({ projectId, documentId, page: null }).catch((failure) => onError(describeError(failure)));
  }

  const nameOf = (community: number) => {
    const row = communities.find((c) => c.index === community);
    return row ? (row.count > 1 ? `${row.lead} and ${row.count - 1} more` : row.lead) : "";
  };
  const full = colored.filter((c) => c !== null).length >= SLOTS;
  const shown = nodes.length < graph.totalEntities
    ? `${nodes.length.toLocaleString()} of ${graph.totalEntities.toLocaleString()} entities (the highest ranked), ${graph.edges.length.toLocaleString()} relationships between them`
    : `${nodes.length.toLocaleString()} entities, ${graph.edges.length.toLocaleString()} relationships`;

  return (
    <div>
      <PageHeader
        title="Graph"
        description="The knowledge graph the chat model built from the documents: each dot a named thing, each line a relationship between two of them. Related entities sit together, so a cluster is a group that comes up together, often across several documents."
        actions={<Button onClick={() => void load()}><Icon name="refresh" /> Redraw</Button>}
      />

      <form className="search-bar" role="search" onSubmit={find}>
        <div className="search-field">
          <Icon name="search" />
          <input
            type="search"
            aria-label="Find an entity"
            placeholder="Find an entity by name"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
          />
        </div>
        <Button type="submit" disabled={query.trim().length === 0}>Find</Button>
      </form>
      {missed ? <p className="search-note">No entity on the drawing is named “{missed}”.</p> : null}

      <div className="card map-card">
        <div className="map-legend" aria-label="Legend">
          {colored.map((community, slot) => community === null ? null : (
            <span key={community} className="map-key">
              <span className={`map-swatch series-${slot + 1}`} aria-hidden="true" />
              {nameOf(community)}
            </span>
          ))}
          {communities.length > colored.filter((c) => c !== null).length ? (
            <span className="map-key">
              <span className="map-swatch series-other" aria-hidden="true" /> Other communities
            </span>
          ) : null}
        </div>
        <svg
          ref={svg}
          className={focus === null ? "map-plot graph-plot" : "map-plot graph-plot has-focus"}
          viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
          role="img"
          aria-label={`Knowledge graph of ${shown}. The communities list below has the same groups as text, and Find picks an entity by name.`}
          onPointerMove={(event) => setHover(nearest(event))}
          onPointerLeave={() => setHover(null)}
          onClick={(event) => setPinned(nearest(event))}
        >
          <rect className="map-frame" x={0.5} y={0.5} width={WIDTH - 1} height={HEIGHT - 1} rx={8} />
          <g>
            {graph.edges.map((edge, i) => {
              const from = nodes[edge.source];
              const to = nodes[edge.target];
              const active = focus !== null && (edge.source === focus || edge.target === focus);
              return (
                <line
                  key={i}
                  className={active ? "graph-edge is-lit" : "graph-edge"}
                  x1={x(from)}
                  y1={y(from)}
                  x2={x(to)}
                  y2={y(to)}
                />
              );
            })}
          </g>
          {order.map((i) => {
            const node = nodes[i];
            const slot = slotOf(node);
            return (
              <circle
                key={node.name}
                className={`map-dot graph-node ${slot >= 0 ? `series-${slot + 1}` : "series-other"}${lit(i) ? " is-lit" : ""}`}
                cx={x(node)}
                cy={y(node)}
                r={radius(node)}
              />
            );
          })}
          {[...labeled].map((i) => {
            const node = nodes[i];
            // Labels on the right half sit to the left of their dot, so none runs off the edge.
            const left = x(node) > WIDTH * 0.6;
            return (
              <text
                key={node.name}
                className={i === focus ? "graph-label is-focus" : "graph-label"}
                x={x(node) + (left ? -1 : 1) * (radius(node) + 4)}
                y={y(node) + 4}
                textAnchor={left ? "end" : "start"}
              >
                {node.name.length > 32 ? `${node.name.slice(0, 31)}…` : node.name}
              </text>
            );
          })}
          {focus !== null ? <circle className="map-focus" cx={x(nodes[focus])} cy={y(nodes[focus])} r={radius(nodes[focus]) + 5} /> : null}
        </svg>
        <p className="map-note">
          {shown}. Bigger dots rank higher: more of the graph leads to them. Position comes from the relationships alone, so distance means “linked”, not “worded alike”.
        </p>
        {focus !== null ? (
          <Detail
            node={nodes[focus]}
            graph={graph}
            index={focus}
            canOpen={pinned === focus && hover === null}
            onFocus={(i) => { setHover(null); setPinned(i); }}
            onOpen={open}
          />
        ) : (
          <p className="map-detail muted">Point at a dot to see the entity and its relationships; click to keep it here.</p>
        )}
      </div>

      <Section
        title="Communities"
        description={`Groups of entities the graph links closely, each with the report the model wrote about it. Pick up to ${SLOTS} to color.`}
      >
        <ul className="list" aria-label="Communities on the graph">
          {communities.map((row) => {
            const slot = colored.indexOf(row.index);
            const community = graph.communities[row.index];
            return (
              <li key={row.index} className="graph-community">
                <div className="graph-community-head">
                  <label className="check grow">
                    <input
                      type="checkbox"
                      checked={slot >= 0}
                      disabled={slot < 0 && full}
                      onChange={() => toggle(row.index)}
                    />
                    <span className={slot >= 0 ? `map-swatch series-${slot + 1}` : "map-swatch series-other"} aria-hidden="true" />
                    {nameOf(row.index)}
                  </label>
                  <span className="badge">{community.members} {community.members === 1 ? "entity" : "entities"}</span>
                </div>
                {community.report ? <p className="graph-report">{community.report}</p> : null}
              </li>
            );
          })}
        </ul>
      </Section>
    </div>
  );
}

interface DetailProps {
  node: GraphNodeDto;
  graph: GraphDto;
  index: number;
  canOpen: boolean;
  onFocus: (index: number) => void;
  onOpen: (documentId: string) => void;
}

/** The entity in focus: what the model said about it, its relationships, its documents, and its community. */
function Detail({ node, graph, index, canOpen, onFocus, onOpen }: DetailProps) {
  const edges = graph.edges.filter((e) => e.source === index || e.target === index);
  const community = node.communities[0] === undefined ? null : graph.communities[node.communities[0]];
  const hidden = node.degree - edges.length;
  return (
    <div className="map-detail graph-detail" aria-live="polite">
      <div className="list-title">
        {node.name}
        {node.type ? <span className="badge">{node.type}</span> : null}
        <span className="list-meta">{node.degree} {node.degree === 1 ? "relationship" : "relationships"}</span>
      </div>
      {node.description ? <p className="graph-description">{node.description}</p> : null}

      {edges.length > 0 ? (
        <ul className="graph-relations" aria-label={`Relationships of ${node.name}`}>
          {edges.slice(0, SHOWN_RELATIONSHIPS).map((edge, i) => {
            const other = edge.source === index ? edge.target : edge.source;
            const outgoing = edge.source === index;
            return (
              <li key={i}>
                <span className="muted" aria-hidden="true">{outgoing ? "→" : "←"}</span>
                {canOpen ? (
                  <button type="button" className="link-button" onClick={() => onFocus(other)}>{graph.nodes[other].name}</button>
                ) : (
                  <strong>{graph.nodes[other].name}</strong>
                )}
                {edge.description ? <span className="muted">{edge.description}</span> : null}
              </li>
            );
          })}
          {edges.length > SHOWN_RELATIONSHIPS ? <li className="muted">and {edges.length - SHOWN_RELATIONSHIPS} more</li> : null}
          {hidden > 0 ? <li className="muted">{hidden} more with entities not on the drawing</li> : null}
        </ul>
      ) : null}

      {node.documents.length > 0 ? (
        <div className="row graph-documents">
          <span className="list-meta">From</span>
          {node.documents.map((d) => {
            const document = graph.documents[d];
            return canOpen ? (
              <Button key={d} variant="quiet" size="sm" onClick={() => onOpen(document.documentId)}>
                <Icon name="external" /> {document.fileName}
              </Button>
            ) : (
              <span key={d} className="badge">{document.fileName}</span>
            );
          })}
        </div>
      ) : null}

      {community?.report ? <p className="close-call-text">{community.report}</p> : null}
      {!canOpen ? <p className="list-meta">Click to keep it here and follow its relationships.</p> : null}
    </div>
  );
}

/** The communities with the most entities on the drawing, which are the ones a reader most likely wants to tell apart. */
function largest(nodes: readonly GraphNodeDto[]): number[] {
  const counts = new Map<number, number>();
  for (const node of nodes) {
    const primary = node.communities[0];
    if (primary !== undefined) {
      counts.set(primary, (counts.get(primary) ?? 0) + 1);
    }
  }

  return [...counts.entries()].sort((a, b) => b[1] - a[1] || a[0] - b[0]).slice(0, SLOTS).map(([id]) => id);
}
