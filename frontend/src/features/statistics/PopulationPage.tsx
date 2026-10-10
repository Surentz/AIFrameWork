import { useId, useState } from 'react';
import { usePopulation, usePopulationAreas } from './queries';
import type { PopulationAreas } from './types';
import { ErrorPanel } from '../../components/ErrorPanel';
import './statistics.css';

/** All of Denmark: StatBank's code for the national total, and the figure shown first. */
const AllDenmark = '000';

/**
 * Official population figures from Statistics Denmark: the external systems pilot (ADR 0031).
 * The source is credited under every figure, as its CC BY 4.0 licence requires.
 */
export function PopulationPage(): React.JSX.Element {
  const areas = usePopulationAreas();

  return (
    <section className="statistics">
      <h1>Population</h1>
      <p className="statistics__note">
        The latest quarter&apos;s population for all of Denmark, a region or a municipality.
      </p>

      {areas.error && <ErrorPanel error={areas.error} />}
      {areas.isPending && <p role="status">Loading areas…</p>}
      {areas.isSuccess && <AreaPopulation areas={areas.data.areas} />}
    </section>
  );
}

interface AreaPopulationProps {
  readonly areas: PopulationAreas['areas'];
}

function AreaPopulation({ areas }: AreaPopulationProps): React.JSX.Element {
  // All of Denmark when the source lists it, else its first area, so the picker and the figure
  // can never show two different areas.
  const [chosen, setChosen] = useState<string | undefined>(undefined);
  const area =
    chosen ?? (areas.some((option) => option.code === AllDenmark) ? AllDenmark : areas[0]?.code);

  return (
    <>
      <label className="statistics__picker">
        Area
        <select
          value={area}
          onChange={(event) => {
            setChosen(event.target.value);
          }}
        >
          {areas.map((option) => (
            <option key={option.code} value={option.code}>
              {option.name}
            </option>
          ))}
        </select>
      </label>
      {area !== undefined && <PopulationFigure area={area} />}
    </>
  );
}

interface PopulationFigureProps {
  readonly area: string;
}

function PopulationFigure({ area }: PopulationFigureProps): React.JSX.Element {
  const population = usePopulation(area);
  const captionId = useId();

  if (population.isPending) {
    return <p role="status">Loading population…</p>;
  }

  if (population.error) {
    return <ErrorPanel error={population.error} />;
  }

  // The caption names the figure (explicitly: not every accessibility tree derives it from a
  // figcaption); the number is its content, read as it is shown.
  return (
    <figure className="card statistics__figure" aria-labelledby={captionId}>
      <p className="statistics__value">{formatPopulation(population.data.population)}</p>
      <figcaption id={captionId}>
        {population.data.areaName}, first day of {population.data.period}.{' '}
        <span className="statistics__note">Source: {population.data.source}</span>
      </figcaption>
    </figure>
  );
}

/**
 * `population` is `number | string` from the generator, correctly: the API accepts either for an
 * int64 (AllowReadingFromString). Danish grouping, so the figure reads the same for every user.
 */
function formatPopulation(population: number | string): string {
  const value = typeof population === 'number' ? population : Number(population);
  return Number.isFinite(value) ? value.toLocaleString('da-DK') : String(population);
}
