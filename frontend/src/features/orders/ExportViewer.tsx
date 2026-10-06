import { useEffect, useMemo, useRef, useState } from 'react';
import { Document, Page, pdfjs } from 'react-pdf';
import 'react-pdf/dist/Page/TextLayer.css';
import { ErrorPanel } from '../../components/ErrorPanel';
import { orderExportDownloadUrl } from '../../api/orders';
import { useOrderExportDocument } from './queries';

// In this module, not main.tsx: react-pdf's docs warn that a workerSrc set elsewhere can be
// overwritten by its own default, depending on module order. Bundled by Vite and served from this
// origin (nginx.conf maps .mjs to JavaScript - without it the worker never starts in the cluster).
pdfjs.GlobalWorkerOptions.workerSrc = new URL('pdfjs-dist/build/pdf.worker.min.mjs', import.meta.url).toString();

const ZOOM_STEP = 0.2;
const MIN_ZOOM = 0.6;
const MAX_ZOOM = 2.4;

interface ExportViewerProps {
  readonly id: string;
}

/**
 * The export, drawn by pdf.js. Lazy-loaded (ExportViewerDialog), so pdf.js downloads only on the
 * first View. Fit-to-width by default; zoom multiplies the fitted width.
 */
export default function ExportViewer({ id }: ExportViewerProps): React.JSX.Element {
  const { data, error, isPending } = useOrderExportDocument(id);
  const [pages, setPages] = useState(0);
  const [current, setCurrent] = useState(1);
  const [zoom, setZoom] = useState(1);
  const [width, setWidth] = useState(0);
  const body = useRef<HTMLDivElement>(null);

  // pdf.js transfers (detaches) the buffer it is given, so it gets a copy: handing it the cached
  // one would leave nothing to draw the next time the viewer opens.
  const file = useMemo(() => (data === undefined ? undefined : { data: new Uint8Array(data.slice(0)) }), [data]);

  useEffect(() => {
    const element = body.current;
    if (element === null) {
      return undefined;
    }
    const observer = new ResizeObserver(([entry]) => {
      setWidth(entry?.contentRect.width ?? 0);
    });
    observer.observe(element);
    return () => {
      observer.disconnect();
    };
  }, []);

  function onScroll(): void {
    const element = body.current;
    if (element === null || pages === 0) {
      return;
    }
    const pageHeight = element.scrollHeight / pages;
    setCurrent(Math.min(pages, Math.floor(element.scrollTop / pageHeight) + 1));
  }

  return (
    <>
      <div className="export-viewer__toolbar" role="toolbar" aria-label="Viewer controls">
        <span className="export-viewer__pages">{pages > 0 ? `Page ${String(current)} of ${String(pages)}` : ''}</span>
        <button className="btn btn--secondary" type="button" aria-label="Zoom out"
          onClick={() => { setZoom((z) => Math.max(MIN_ZOOM, z - ZOOM_STEP)); }}>−</button>
        <button className="btn btn--secondary" type="button" aria-label="Zoom in"
          onClick={() => { setZoom((z) => Math.min(MAX_ZOOM, z + ZOOM_STEP)); }}>+</button>
        <button className="btn btn--secondary" type="button"
          onClick={() => { setZoom(1); }}>Fit to width</button>
        <a className="btn btn--secondary" href={orderExportDownloadUrl(id)} download>Download</a>
      </div>
      <div className="export-viewer__body" ref={body} onScroll={onScroll}>
        {isPending && (
          <div className="orders__state">
            <span className="spinner" aria-hidden="true" />
            <p role="status">Loading export…</p>
          </div>
        )}
        {error && <ErrorPanel error={error} />}
        {file && (
          <Document
            file={file}
            suspense={false}
            loading={<p role="status">Loading export…</p>}
            error={<p role="alert">This export couldn&apos;t be displayed. You can still download it.</p>}
            onLoadSuccess={(pdf) => { setPages(pdf.numPages); }}
          >
            {Array.from({ length: pages }, (_, i) => (
              <Page
                key={i + 1}
                className="export-viewer__page"
                pageNumber={i + 1}
                {...(width > 0 ? { width: width * zoom } : {})}
                renderAnnotationLayer={false}
                renderTextLayer
              />
            ))}
          </Document>
        )}
      </div>
    </>
  );
}
