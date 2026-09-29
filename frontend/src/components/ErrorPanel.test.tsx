import { render, screen } from '@testing-library/react';
import { ApiError } from '../api/client';
import { ErrorPanel } from './ErrorPanel';

const id = '0af7651916cd43dd8448eb211c80319c';

describe('ErrorPanel', () => {
  it("gives a server failure's trace id as a reference to quote", () => {
    const error = new ApiError(500, {
      detail: 'Something went wrong.',
      traceId: `00-${id}-b7ad6b7169203331-01`,
    });

    render(<ErrorPanel error={error} />);

    const alert = screen.getByRole('alert');
    expect(alert).toHaveTextContent('Something went wrong.');
    expect(alert).toHaveTextContent(`Reference: ${id}`);
  });

  it('gives no reference for a mistake the caller can fix', () => {
    // A wrong password or a validation failure is not something to raise with support.
    const error = new ApiError(400, { detail: 'Quantity must be positive.', traceId: id });

    render(<ErrorPanel error={error} />);

    expect(screen.getByRole('alert')).not.toHaveTextContent('Reference');
  });

  it('gives no reference when the id is not one the log store knows', () => {
    const error = new ApiError(500, {
      detail: 'Something went wrong.',
      traceId: '0HN7GLM2S8B6J:1',
    });

    render(<ErrorPanel error={error} />);

    expect(screen.getByRole('alert')).not.toHaveTextContent('Reference');
  });

  it('says what failed before the message', () => {
    const error = new ApiError(503, { detail: 'Service unavailable.' });

    render(<ErrorPanel error={error} lead="The products could not be loaded." />);

    expect(screen.getByRole('alert')).toHaveTextContent(
      'The products could not be loaded. Service unavailable.',
    );
  });

  it('gives no reference for a server failure that carried no trace id', () => {
    render(<ErrorPanel error={new ApiError(502, {})} />);

    expect(screen.getByRole('alert')).not.toHaveTextContent('Reference');
  });

  it('shows the message of an error that never reached the server', () => {
    render(<ErrorPanel error={new TypeError('Failed to fetch')} />);

    expect(screen.getByRole('alert')).toHaveTextContent('Failed to fetch');
  });
});
