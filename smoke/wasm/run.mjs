// Runs the published browser-wasm host headless under Node and exits with its exit code.
// usage: node run.mjs <path to the wwwroot directory of the build output> [args...]
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const root = resolve(process.argv[2] ?? '.');
const { dotnet } = await import(pathToFileURL(resolve(root, '_framework', 'dotnet.js')).href);
const runtime = await dotnet
  .withApplicationArguments(...process.argv.slice(3))
  .create();
const code = await runtime.runMainAndExit();
process.exit(code);
