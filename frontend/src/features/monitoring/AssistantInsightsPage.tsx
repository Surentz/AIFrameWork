import { useState } from 'react';
import { Link } from 'react-router-dom';
import { ErrorPanel } from '../../components/ErrorPanel';
import { useAssistantInsights } from './queries';
import './monitoring.css';

const Windows = [7, 30] as const;

const PathLabels: Readonly<Record<string, string>> = {
  Instant: 'Instant answer',
  Help: 'Help',
  Suggested: 'Suggestions',
  Ai: 'AI',
};

function ratingLabel(rating: string | null | undefined): string {
  if (rating === 'Down') return '👎';
  if (rating === 'Up') return '👍';
  return '—';
}

/**
 * How the assistant is doing, for administrators: how many questions it answered at once, what
 * people ask most, and the questions it missed — scrubbed, and gone when their conversation goes
 * (ADR 0037).
 */
export function AssistantInsightsPage(): React.JSX.Element {
  const [days, setDays] = useState<number>(7);
  const insights = useAssistantInsights(days);

  return (
    <section>
      <p>
        <Link to="/monitoring">← Monitoring</Link>
      </p>
      <h1>Assistant insights</h1>

      <div className="filters">
        <label htmlFor="window">
          Window
          <select
            id="window"
            value={days}
            onChange={(event) => {
              setDays(Number(event.target.value));
            }}
          >
            {Windows.map((value) => (
              <option key={value} value={value}>
                {`Last ${String(value)} days`}
              </option>
            ))}
          </select>
        </label>
      </div>

      {insights.error && <ErrorPanel error={insights.error} />}
      {insights.isPending && <p role="status">Loading the assistant's insights…</p>}

      {insights.isSuccess &&
        (insights.data.days.length === 0 ? (
          <p>No questions in this window.</p>
        ) : (
          <>
            <table className="runs">
              <caption>Answers by day</caption>
              <thead>
                <tr>
                  <th scope="col">Day</th>
                  <th scope="col">Instant</th>
                  <th scope="col">Help</th>
                  <th scope="col">Suggestions</th>
                  <th scope="col">AI</th>
                  <th scope="col">Answered at once</th>
                </tr>
              </thead>
              <tbody>
                {insights.data.days.map((day) => {
                  const instant = Number(day.instant);
                  const help = Number(day.help);
                  const total = instant + help + Number(day.suggested) + Number(day.ai);
                  const atOnce = total === 0 ? 0 : Math.round(((instant + help) * 100) / total);
                  return (
                    <tr key={day.day}>
                      <td>{day.day}</td>
                      <td>{instant}</td>
                      <td>{help}</td>
                      <td>{Number(day.suggested)}</td>
                      <td>{Number(day.ai)}</td>
                      <td>{`${String(atOnce)}%`}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>

            <table className="runs">
              <caption>Most asked</caption>
              <thead>
                <tr>
                  <th scope="col">Answered by</th>
                  <th scope="col">Intent or article</th>
                  <th scope="col">Questions</th>
                  <th scope="col">👍</th>
                  <th scope="col">👎</th>
                </tr>
              </thead>
              <tbody>
                {insights.data.targets.map((target) => (
                  <tr key={`${target.path}-${target.targetId}`}>
                    <td>{PathLabels[target.path] ?? target.path}</td>
                    <td>{target.targetId}</td>
                    <td>{Number(target.questions)}</td>
                    <td>{Number(target.up)}</td>
                    <td>{Number(target.down)}</td>
                  </tr>
                ))}
              </tbody>
            </table>

            <table className="runs">
              <caption>Missed questions</caption>
              <thead>
                <tr>
                  <th scope="col">When</th>
                  <th scope="col">Question</th>
                  <th scope="col">Ended in</th>
                  <th scope="col">Rated</th>
                  <th scope="col">Best match</th>
                </tr>
              </thead>
              <tbody>
                {insights.data.misses.map((miss, index) => (
                  // No id: a miss names no conversation. Position within one response is stable.
                  <tr key={`${miss.at}-${String(index)}`}>
                    <td>{new Date(miss.at).toLocaleString()}</td>
                    <td>{miss.question}</td>
                    <td>{PathLabels[miss.path] ?? miss.path}</td>
                    <td>{ratingLabel(miss.rating)}</td>
                    <td>
                      {miss.bestScore === null || miss.bestScore === undefined
                        ? '—'
                        : Number(miss.bestScore).toFixed(2)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </>
        ))}
    </section>
  );
}
