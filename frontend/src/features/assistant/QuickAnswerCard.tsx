import { Link } from 'react-router-dom';
import { isAllowedLink } from './quickLinks';
import type { AssistantAnswer } from './types';

interface QuickAnswerCardProps {
  readonly answer: AssistantAnswer;
}

/**
 * An instant or help answer, from structured data only: every string is rendered as text, and the
 * link only if it is one of the app's own screens. Never Markdown, never HTML. ADR 0036.
 */
export function QuickAnswerCard({ answer }: QuickAnswerCardProps): React.JSX.Element {
  const help = answer.kind === 'Help';

  return (
    <article className="quick-answer" aria-label={help ? 'Help answer' : 'Instant answer'}>
      <p className="assistant-badge">{help ? `Help · ${answer.title ?? ''}` : 'Instant answer'}</p>
      <p className="quick-answer__text">{answer.text}</p>
      {answer.answeredAs && <p className="muted">Answered as: {answer.answeredAs}</p>}
      {answer.facts.length > 0 && (
        <dl className="quick-answer__facts">
          {answer.facts.map((fact) => (
            <div key={fact.label}>
              <dt>{fact.label}</dt>
              <dd>{fact.value}</dd>
            </div>
          ))}
        </dl>
      )}
      {answer.table && (
        <table className="quick-answer__table">
          <thead>
            <tr>
              {answer.table.columns.map((column) => (
                <th key={column} scope="col">
                  {column}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {answer.table.rows.map((row, index) => (
              // Rows never reorder: the answer is fixed once written.
              <tr key={String(index)}>
                {row.map((cell, column) => (
                  <td key={String(column)}>{cell}</td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {answer.link && isAllowedLink(answer.link.to) && (
        <Link className="quick-answer__link" to={answer.link.to}>
          {answer.link.label} →
        </Link>
      )}
    </article>
  );
}
