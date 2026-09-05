// Normalises CRLF inside the generated OpenAPI document's string values.
//
// The document embeds XML doc comments as JSON `description` strings. A multi-line comment
// therefore lands in the file as an ESCAPED "\r\n" on Windows and "\n" on Linux — characters
// inside a string value, not line terminators, so .gitattributes' `text eol=lf` cannot touch
// them. Without this, a document generated on Windows can never match one generated in CI, and
// the drift job fails with a diff no one can fix by regenerating.
//
// Runs at the start of `npm run generate:api`, which is the one step both the documented
// workflow and the CI job share. Idempotent: a no-op on a document generated on Linux.
import { readFileSync, writeFileSync } from 'node:fs';

const path = new URL('../openapi/AiFramework.Api.json', import.meta.url);
const before = readFileSync(path, 'utf8');
const after = before.split(String.raw`\r\n`).join(String.raw`\n`);

if (before !== after) {
  writeFileSync(path, after);
  console.log('normalized CRLF in openapi/AiFramework.Api.json');
} else {
  console.log('openapi/AiFramework.Api.json already normalized');
}
