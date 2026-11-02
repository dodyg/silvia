import { useEffect, useMemo, useRef, useState, type MouseEvent } from "react";
import { describeError, inspect, library, type MapDto, type MapPointDto } from "../api";
import { Button, Empty, Icon, PageHeader, Section, Spinner } from "../ui";

interface Props {
  projectId: string;
  onError: (message: string | null) => void;
}

// The plot's own coordinate space; the SVG scales it to the column width.
const WIDTH = 1000;
const HEIGHT = 620;
const PAD = 16;
const GRID = 10_000;

// A scatter shows every pair of colors side by side, so only three hues stay apart for every reader.
// The rest of the documents share one neutral "Other" color.
const SLOTS = 3;

/** Hover reaches the nearest passage within this many plot units, more than a dot's own size. */
const HOVER_RADIUS = 18;

/**
 * Every passage's embedding, projected to two dimensions. Passages that mean similar things land near each
 * other, so documents on one topic cluster, documents that overlap mix, and a stray dot is a passage
 * unlike the rest.
 */
export function MapScreen({ projectId, onError }: Props) {
  const [map, setMap] = useState<MapDto | null>(null);
  const [colored, setColored] = useState<Array<string | null>>([]);
  const [hover, setHover] = useState<MapPointDto | null>(null);
  const [pinned, setPinned] = useState<MapPointDto | null>(null);
  const svg = useRef<SVGSVGElement>(null);

  async function load() {
    setMap(null);
    try {
      const next = await inspect.map({ projectId });
      setMap(next);
      setColored(largest(next.points));
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  useEffect(() => {
    void load();
    // The screen is keyed by project, so this effect runs once per project.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const documents = useMemo(() => {
    const counts = new Map<string, { documentId: string; fileName: string; count: number }>();
    for (const point of map?.points ?? []) {
      const row = counts.get(point.documentId) ?? { documentId: point.documentId, fileName: point.fileName, count: 0 };
      row.count += 1;
      counts.set(point.documentId, row);
    }

    return [...counts.values()].sort((a, b) => b.count - a.count || a.fileName.localeCompare(b.fileName));
  }, [map]);

  if (!map) {
    return (
      <div>
        <PageHeader title="Map" description="Where each passage sits by meaning." />
        <p className="row muted"><Spinner label="Loading" /> Projecting the passages…</p>
      </div>
    );
  }

  if (!map.available) {
    return (
      <div>
        <PageHeader title="Map" description="Where each passage sits by meaning." />
        <Empty title="The map needs a local embedding model" icon="map">
          This project searches by keyword, so its passages have no vectors to place. Install the embedding model in Settings to see the map.
        </Empty>
      </div>
    );
  }

  const slotOf = (documentId: string) => colored.indexOf(documentId);
  const x = (point: MapPointDto) => PAD + (point.x / GRID) * (WIDTH - 2 * PAD);
  const y = (point: MapPointDto) => HEIGHT - PAD - (point.y / GRID) * (HEIGHT - 2 * PAD);

  // "Other" dots go first so the colored documents are drawn over them.
  const ordered = [...map.points].sort((a, b) => (slotOf(a.documentId) >= 0 ? 1 : 0) - (slotOf(b.documentId) >= 0 ? 1 : 0));
  const focus = hover ?? pinned;

  function nearest(event: MouseEvent<SVGSVGElement>): MapPointDto | null {
    const element = svg.current;
    const matrix = element?.getScreenCTM()?.inverse();
    if (!element || !matrix || !map) {
      return null;
    }

    const at = new DOMPoint(event.clientX, event.clientY).matrixTransform(matrix);
    let best: MapPointDto | null = null;
    let bestDistance = HOVER_RADIUS * HOVER_RADIUS;
    for (const point of map.points) {
      const dx = x(point) - at.x;
      const dy = y(point) - at.y;
      const distance = dx * dx + dy * dy;
      if (distance < bestDistance) {
        best = point;
        bestDistance = distance;
      }
    }

    return best;
  }

  function toggle(documentId: string) {
    setColored((current) => {
      const slots = [...current];
      const at = slots.indexOf(documentId);
      if (at >= 0) {
        // The freed slot stays empty, so the other documents keep their colors.
        slots[at] = null;
        return slots;
      }

      const free = slots.indexOf(null);
      if (free >= 0) {
        slots[free] = documentId;
      } else if (slots.length < SLOTS) {
        slots.push(documentId);
      }

      return slots;
    });
  }

  const full = colored.filter((c) => c !== null).length >= SLOTS;
  const shown = map.points.length < map.total ? `${map.points.length.toLocaleString()} of ${map.total.toLocaleString()} passages (an even sample)` : `${map.total.toLocaleString()} passages`;

  return (
    <div>
      <PageHeader
        title="Map"
        description="Every passage placed by meaning: near dots say similar things. Documents on one topic cluster; a dot far from its document's cluster is a passage unlike the rest of it."
        actions={<Button onClick={() => void load()}><Icon name="refresh" /> Redraw</Button>}
      />

      <div className="card map-card">
        <div className="map-legend" aria-label="Legend">
          {colored.map((documentId, slot) => documentId === null ? null : (
            <span key={documentId} className="map-key">
              <span className={`map-swatch series-${slot + 1}`} aria-hidden="true" />
              {documents.find((d) => d.documentId === documentId)?.fileName}
            </span>
          ))}
          {documents.length > colored.filter((c) => c !== null).length ? (
            <span className="map-key">
              <span className="map-swatch series-other" aria-hidden="true" /> Other documents
            </span>
          ) : null}
        </div>
        <svg
          ref={svg}
          className="map-plot"
          viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
          role="img"
          aria-label={`Map of ${shown}. The document list below has the same information as text.`}
          onPointerMove={(event) => setHover(nearest(event))}
          onPointerLeave={() => setHover(null)}
          onClick={(event) => setPinned(nearest(event))}
        >
          <rect className="map-frame" x={0.5} y={0.5} width={WIDTH - 1} height={HEIGHT - 1} rx={8} />
          {ordered.map((point) => {
            const slot = slotOf(point.documentId);
            return (
              <circle
                key={`${point.documentId}-${point.chunkIndex}`}
                className={slot >= 0 ? `map-dot series-${slot + 1}` : "map-dot series-other"}
                cx={x(point)}
                cy={y(point)}
                r={slot >= 0 ? 4.5 : 3.5}
              />
            );
          })}
          {focus ? <circle className="map-focus" cx={x(focus)} cy={y(focus)} r={9} /> : null}
        </svg>
        <p className="map-note">
          {shown}. The axes are the two directions the passages vary most; together they show {((map.firstShare + map.secondShare) / 10).toFixed(0)}% of the spread, so dots that look close can still differ in ways a flat map cannot show.
        </p>
        {focus ? (
          <div className="map-detail" aria-live="polite">
            <div className="list-title">
              {focus.fileName} <span className="list-meta">passage {focus.chunkIndex + 1}</span>
              {pinned && !hover ? (
                <Button variant="quiet" size="sm" onClick={() => void library.openDocument({ projectId, documentId: focus.documentId, page: null }).catch((failure) => onError(describeError(failure)))}>
                  <Icon name="external" /> Open
                </Button>
              ) : null}
            </div>
            <p className="close-call-text">{focus.preview}{focus.preview.length >= 200 ? "…" : ""}</p>
          </div>
        ) : (
          <p className="map-detail muted">Point at a dot to read its passage; click to keep it here.</p>
        )}
      </div>

      <Section
        title="Documents"
        description={`Pick up to ${SLOTS} documents to color. Fewer colors keep every reader able to tell them apart.`}
      >
        <ul className="list" aria-label="Documents on the map">
          {documents.map((document) => {
            const slot = slotOf(document.documentId);
            return (
              <li key={document.documentId}>
                <label className="check grow">
                  <input
                    type="checkbox"
                    checked={slot >= 0}
                    disabled={slot < 0 && full}
                    onChange={() => toggle(document.documentId)}
                  />
                  <span className={slot >= 0 ? `map-swatch series-${slot + 1}` : "map-swatch series-other"} aria-hidden="true" />
                  {document.fileName}
                </label>
                <span className="badge">{document.count} {document.count === 1 ? "passage" : "passages"}</span>
              </li>
            );
          })}
        </ul>
      </Section>
    </div>
  );
}

/** The documents with the most passages, which are the ones a reader most likely wants to tell apart. */
function largest(points: readonly MapPointDto[]): string[] {
  const counts = new Map<string, number>();
  for (const point of points) {
    counts.set(point.documentId, (counts.get(point.documentId) ?? 0) + 1);
  }

  return [...counts.entries()].sort((a, b) => b[1] - a[1]).slice(0, SLOTS).map(([id]) => id);
}
