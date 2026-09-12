import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { PREVIEW_PORT } from './env.ts';

export interface Target {
  /** For log lines and failure messages. */
  readonly name: string;
  readonly baseURL: string;
  /** Does Playwright start the servers, migrate the database, and tear it down? */
  readonly managesStack: boolean;
  readonly ignoreHTTPSErrors: boolean;
}

const KindHost = 'aiframework.localtest.me';

/**
 * The ingress https port comes from deploy/kind-cluster.yaml — the same file and the same
 * mapping deploy/deploy.ps1 parses to print its "Ready:" URL. It is 8443 today only because
 * host 443 is held by http.sys on this machine; hard-coding it here would drift silently the
 * moment that changes.
 */
export function kindBaseUrl(): string {
  const configPath = fileURLToPath(new URL('../../../deploy/kind-cluster.yaml', import.meta.url));
  const yaml = readFileSync(configPath, 'utf8');
  const match = /containerPort:\s*443\s*\r?\n\s*hostPort:\s*(\d+)/m.exec(yaml);
  const port = match?.[1] ?? '443';

  return port === '443' ? `https://${KindHost}` : `https://${KindHost}:${port}`;
}

export function resolveTarget(): Target {
  const requested = process.env.E2E_TARGET ?? 'local';

  if (requested === 'local') {
    return {
      name: 'local',
      baseURL: `http://localhost:${PREVIEW_PORT}`,
      managesStack: true,
      ignoreHTTPSErrors: false,
    };
  }

  if (requested === 'kind') {
    return {
      name: 'kind',
      baseURL: kindBaseUrl(),
      managesStack: false,
      // The kind ingress serves a self-signed certificate (k8s/overlays/local/tls.yaml). Scoped
      // to this target rather than set globally, so a genuine TLS fault elsewhere still fails.
      ignoreHTTPSErrors: true,
    };
  }

  if (!/^https?:\/\//.test(requested)) {
    throw new Error(`E2E_TARGET must be 'local', 'kind', or an http(s) URL. Got: '${requested}'.`);
  }

  return {
    name: requested,
    baseURL: requested.replace(/\/$/, ''),
    managesStack: false,
    ignoreHTTPSErrors: false,
  };
}
