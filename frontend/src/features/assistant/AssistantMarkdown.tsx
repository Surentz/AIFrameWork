import Markdown from 'react-markdown';
import type { Components } from 'react-markdown';
import remarkGfm from 'remark-gfm';

/**
 * Links leave the app in a new tab and get no handle on it. react-markdown's default URL transform
 * already empties javascript: and other unsafe URLs before this sees them.
 */
const components: Components = {
  a: ({ href, children }) => (
    <a href={href} target="_blank" rel="noopener noreferrer">
      {children}
    </a>
  ),
};

interface AssistantMarkdownProps {
  readonly text: string;
}

/**
 * An answer as Markdown (GFM: tables, strikethrough, task lists). Model output is untrusted: there
 * is no rehype-raw, so HTML in it is shown as text and never becomes an element. The only module
 * that imports react-markdown.
 */
export function AssistantMarkdown({ text }: AssistantMarkdownProps): React.JSX.Element {
  return (
    <div className="assistant-markdown">
      <Markdown remarkPlugins={[remarkGfm]} components={components}>
        {text}
      </Markdown>
    </div>
  );
}
