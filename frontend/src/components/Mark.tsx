interface MarkProps {
  readonly size: number;
}

export function Mark({ size }: MarkProps): React.JSX.Element {
  return (
    <svg width={size} height={size} viewBox="0 0 32 32" fill="none" aria-hidden="true">
      <rect width="32" height="32" rx="9" fill="currentColor" />
      {/* The glyph is knocked out of the tile, so its colour has to be whatever the tile is
          sitting on. --mark-ink lets a caller override it: on the login brand panel the tile is
          white and the default (--color-surface) would be white on white. */}
      <path
        d="M16 8.5 22.5 23h-3.4l-1.2-2.9h-3.8L12.9 23H9.5L16 8.5Zm0 5.9-1.1 2.8h2.2L16 14.4Z"
        fill="var(--mark-ink, var(--color-surface))"
      />
    </svg>
  );
}
