import { execFileSync } from 'node:child_process';
import { E2E_CONNECTION_STRING } from './env.ts';

export default function globalSetup(): void {
  // dotnet-ef is a local tool (.config/dotnet-tools.json); without a restore this only works
  // by accident, on a machine that also happens to have it installed globally.
  execFileSync('dotnet', ['tool', 'restore'], { stdio: 'inherit' });

  execFileSync('docker', ['compose', '-f', '../docker-compose.e2e.yml', 'up', '-d', '--wait'], {
    stdio: 'inherit',
  });

  execFileSync(
    'dotnet',
    [
      'ef',
      'database',
      'update',
      '--project',
      '../src/Infrastructure',
      // Infrastructure, not Api: DesignTimeDbContextFactory already builds the DbContext
      // without the Api composition root, and Api need not (and, per the reverted csproj
      // change, must not) reference Microsoft.EntityFrameworkCore.Design just to satisfy the
      // CLI's startup-project requirement. Same fix Task 4 already established.
      '--startup-project',
      '../src/Infrastructure',
    ],
    {
      stdio: 'inherit',
      env: { ...process.env, ConnectionStrings__Default: E2E_CONNECTION_STRING },
    },
  );
}
