import { buildTraceUrl, normaliseTraceId } from './traceId';

const id = '0af7651916cd43dd8448eb211c80319c';

describe('normaliseTraceId', () => {
  it.each([
    [id, id],
    [`00-${id}-b7ad6b7169203331-01`, id],
    [id.toUpperCase(), id],
  ])('reads %s as the bare trace id', (input, expected) => {
    expect(normaliseTraceId(input)).toBe(expected);
  });

  it.each([
    null,
    undefined,
    '',
    // HttpContext.TraceIdentifier: what a ProblemDetails carries when no Activity is running.
    // It appears in no log record, so it must not be offered as something to search for.
    '0HN7GLM2S8B6J:00000001',
    // The W3C "invalid" id.
    '00000000000000000000000000000000',
    'abc123',
  ])('rejects %s', (input) => {
    expect(normaliseTraceId(input)).toBeUndefined();
  });
});

describe('buildTraceUrl', () => {
  it('substitutes every placeholder', () => {
    expect(buildTraceUrl('https://logs.test/t/{traceId}?q={traceId}', id)).toBe(
      `https://logs.test/t/${id}?q=${id}`,
    );
  });

  it('substitutes the bare id when handed a full traceparent', () => {
    expect(buildTraceUrl('https://logs.test/{traceId}', `00-${id}-b7ad6b7169203331-01`)).toBe(
      `https://logs.test/${id}`,
    );
  });

  it('refuses a template that is not http or https', () => {
    expect(buildTraceUrl('javascript:alert(1)//{traceId}', id)).toBeUndefined();
  });

  it('refuses a template that is not a URL at all', () => {
    expect(buildTraceUrl('/relative/{traceId}', id)).toBeUndefined();
  });

  it('needs a template', () => {
    expect(buildTraceUrl(undefined, id)).toBeUndefined();
  });

  it('needs a usable id', () => {
    expect(buildTraceUrl('https://logs.test/{traceId}', null)).toBeUndefined();
  });
});
