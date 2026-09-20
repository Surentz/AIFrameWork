import { useEffect, useRef, useState } from 'react';

export interface ChartSeries {
  readonly key: string;
  readonly label: string;
  /** A CSS custom property name, so light and dark swap in one place. */
  readonly colorVar: string;
}

export interface ChartPoint {
  readonly at: string;
  readonly values: Readonly<Record<string, number | null>>;
}

interface TrafficChartProps {
  readonly title: string;
  readonly unit: string;
  readonly points: readonly ChartPoint[];
  readonly series: readonly ChartSeries[];
}

const Height = 200;
const Padding = { top: 12, right: 16, bottom: 28, left: 48 };
const FallbackWidth = 720;

/**
 * A line chart over time, drawn as inline SVG.
 *
 * No chart library, deliberately: this repo carries five runtime dependencies and two line charts
 * do not earn a sixth on a page most users never open. The marks follow the dataviz skill's fixed
 * specs — 2px lines with round joins, hairline recessive gridlines, >=8px end markers with a 2px
 * surface ring, and text in the app's own ink tokens rather than in a series colour.
 *
 * The series colours are validated: the app's accent and danger pass every check of the skill's
 * palette validator in light mode, and dark mode uses its own steps (a lighter indigo and a red
 * stepped down into the dark lightness band) rather than an automatic flip.
 */
export function TrafficChart({ title, unit, points, series }: TrafficChartProps): React.JSX.Element {
  const container = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(FallbackWidth);
  const [hovered, setHovered] = useState<number | null>(null);

  // Measured rather than scaled by a viewBox, so axis labels and the tooltip stay at their true
  // size at any container width. Guarded because jsdom has no ResizeObserver.
  useEffect(() => {
    const element = container.current;
    if (element === null || typeof ResizeObserver === 'undefined') {
      return undefined;
    }

    const observer = new ResizeObserver((entries) => {
      const measured = entries[0]?.contentRect.width ?? FallbackWidth;
      setWidth(Math.max(320, measured));
    });

    observer.observe(element);

    return () => {
      observer.disconnect();
    };
  }, []);

  const plotWidth = Math.max(1, width - Padding.left - Padding.right);
  const plotHeight = Height - Padding.top - Padding.bottom;

  const everyValue = points.flatMap((point) =>
    series.map((s) => point.values[s.key]).filter((value): value is number => value !== null),
  );
  const maxValue = Math.max(1, ...everyValue);

  const xAt = (index: number): number =>
    points.length <= 1
      ? Padding.left + plotWidth / 2
      : Padding.left + (index / (points.length - 1)) * plotWidth;

  const yAt = (value: number): number =>
    Padding.top + plotHeight - (value / maxValue) * plotHeight;

  // Four gridlines is enough to read a magnitude against without the grid competing with the data.
  const ticks = [0, 0.25, 0.5, 0.75, 1].map((fraction) => Math.round(maxValue * fraction));

  function moveTo(index: number): void {
    setHovered(Math.min(points.length - 1, Math.max(0, index)));
  }

  if (points.length === 0) {
    return (
      <figure className="viz">
        <figcaption className="viz__title">{title}</figcaption>
        <p className="muted">Nothing recorded in this window yet.</p>
      </figure>
    );
  }

  const active = hovered === null ? null : points[hovered];

  return (
    <figure className="viz" ref={container}>
      <figcaption className="viz__title">
        {title} <span className="viz__unit">({unit})</span>
      </figcaption>

      {/* A legend only for two or more series: with one, the title already names it. */}
      {series.length > 1 && (
        <ul className="viz__legend">
          {series.map((s) => (
            <li key={s.key}>
              <span
                className="viz__key"
                style={{ backgroundColor: `var(${s.colorVar})` }}
                aria-hidden="true"
              />
              {s.label}
            </li>
          ))}
        </ul>
      )}

      <svg
        className="viz__plot"
        width={width}
        height={Height}
        role="img"
        aria-label={`${title}, ${unit}. The table below this chart carries the same values.`}
        tabIndex={0}
        onKeyDown={(event) => {
          if (event.key === 'ArrowRight') {
            moveTo((hovered ?? -1) + 1);
          } else if (event.key === 'ArrowLeft') {
            moveTo((hovered ?? points.length) - 1);
          } else if (event.key === 'Escape') {
            setHovered(null);
          }
        }}
        onBlur={() => {
          setHovered(null);
        }}
        onPointerLeave={() => {
          setHovered(null);
        }}
        onPointerMove={(event) => {
          const bounds = event.currentTarget.getBoundingClientRect();
          const offset = event.clientX - bounds.left - Padding.left;
          const step = points.length <= 1 ? 1 : plotWidth / (points.length - 1);
          moveTo(Math.round(offset / step));
        }}
      >
        {ticks.map((tick) => (
          <g key={tick}>
            <line
              className="viz__grid"
              x1={Padding.left}
              x2={Padding.left + plotWidth}
              y1={yAt(tick)}
              y2={yAt(tick)}
            />
            <text className="viz__axis" x={Padding.left - 8} y={yAt(tick) + 4} textAnchor="end">
              {tick}
            </text>
          </g>
        ))}

        {series.map((s) => {
          const drawn = points
            .map((point, index) => ({ index, value: point.values[s.key] }))
            .filter((entry): entry is { index: number; value: number } => entry.value !== null);

          if (drawn.length === 0) {
            return null;
          }

          const path = drawn
            .map(
              (entry, position) =>
                `${position === 0 ? 'M' : 'L'} ${String(xAt(entry.index))} ${String(yAt(entry.value))}`,
            )
            .join(' ');

          // drawn is non-empty: the guard above returned when it was.
          const last = drawn[drawn.length - 1] ?? drawn[0];

          if (last === undefined) {
            return null;
          }

          return (
            <g key={s.key}>
              <path className="viz__line" d={path} stroke={`var(${s.colorVar})`} />
              {/* An end marker with a 2px surface ring, so overlapping series stay separable. */}
              <circle
                className="viz__end"
                cx={xAt(last.index)}
                cy={yAt(last.value)}
                r={4}
                fill={`var(${s.colorVar})`}
              />
            </g>
          );
        })}

        {/* Selective x labels: the ends of the range, never one per point. The reader needs to
            know WHICH hour they are looking at; the crosshair and tooltip carry the rest. */}
        <text className="viz__axis" x={Padding.left} y={Height - 8} textAnchor="start">
          {timeOf(points[0])}
        </text>
        {points.length > 1 && (
          <text
            className="viz__axis"
            x={Padding.left + plotWidth}
            y={Height - 8}
            textAnchor="end"
          >
            {timeOf(points[points.length - 1])}
          </text>
        )}

        {hovered !== null && (
          <line
            className="viz__crosshair"
            x1={xAt(hovered)}
            x2={xAt(hovered)}
            y1={Padding.top}
            y2={Padding.top + plotHeight}
          />
        )}
      </svg>

      {/* One tooltip listing every series at that x, so the pointer never has to find a line. */}
      {active !== undefined && active !== null && (
        <div className="viz__tooltip" role="status">
          <strong>{new Date(active.at).toLocaleTimeString()}</strong>
          <ul>
            {series.map((s) => (
              <li key={s.key}>
                <span
                  className="viz__key viz__key--line"
                  style={{ backgroundColor: `var(${s.colorVar})` }}
                  aria-hidden="true"
                />
                <strong>{active.values[s.key] ?? '—'}</strong> {s.label}
              </li>
            ))}
          </ul>
        </div>
      )}

      {/* The accessibility floor: every value the chart draws is reachable without hovering. */}
      <details className="viz__table">
        <summary>Show as a table</summary>
        <table className="runs">
          <caption className="muted">{title}</caption>
          <thead>
            <tr>
              <th scope="col">Time</th>
              {series.map((s) => (
                <th key={s.key} scope="col">
                  {s.label}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {points.map((point) => (
              <tr key={point.at}>
                <td>{new Date(point.at).toLocaleTimeString()}</td>
                {series.map((s) => (
                  <td key={s.key}>{point.values[s.key] ?? '—'}</td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </details>
    </figure>
  );
}

/** The clock time of a point, or an empty string when there is none. */
function timeOf(point: ChartPoint | undefined): string {
  return point === undefined ? '' : new Date(point.at).toLocaleTimeString();
}
