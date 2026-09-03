import { execFileSync } from 'node:child_process';

export default function globalTeardown(): void {
  execFileSync('docker', ['compose', '-f', '../docker-compose.e2e.yml', 'down', '-v'], {
    stdio: 'inherit',
  });
}
