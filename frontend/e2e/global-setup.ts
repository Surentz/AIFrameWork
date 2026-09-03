import { execFileSync } from 'node:child_process';

const connectionString =
  'Host=localhost;Port=55432;Database=aiframework_e2e;Username=e2e;Password=e2e';

export default function globalSetup(): void {
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
      '--startup-project',
      '../src/Api',
    ],
    { stdio: 'inherit', env: { ...process.env, ConnectionStrings__Default: connectionString } },
  );
}
