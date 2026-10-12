import { render } from '@testing-library/react';
import { NavIcon } from './NavIcon';
import { navGroups } from './navigation';

describe('NavIcon', () => {
  it.each(navGroups.map((group) => group.icon))(
    'draws the %s icon, hidden from assistive tech',
    (name) => {
      const { container } = render(<NavIcon name={name} />);

      const svg = container.querySelector('svg');
      expect(svg).toHaveAttribute('aria-hidden', 'true');
      expect(svg?.children.length).toBeGreaterThan(0);
    },
  );
});
