import { access, mkdtemp, readdir, rm } from 'node:fs/promises';
import { constants } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';

const appDirValue = process.env.POSEIDON_PACKAGE_APP_DIR;
if (!appDirValue) throw new Error('Defina POSEIDON_PACKAGE_APP_DIR para o app self-contained instalado.');
const appDir = resolve(appDirValue);
const executable = join(appDir, 'poseidon');
await access(executable, constants.X_OK);
const port = Number(process.env.POSEIDON_PACKAGE_PORT ?? '5099');
if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error('POSEIDON_PACKAGE_PORT inválida.');
const dataDir = await mkdtemp(join(tmpdir(), 'poseidon-package-clean-'));
if ((await readdir(dataDir)).length !== 0) throw new Error('O data dir do gate deve nascer vazio.');
const runtimeEnv = { ...process.env, POSEIDON_DATA_DIR: dataDir };

function run(command, args, env = runtimeEnv, cwd = appDir) {
  const result = spawnSync(command, args, { cwd, env, stdio: 'inherit' });
  if (result.status !== 0) throw new Error(`${command} terminou com status ${result.status ?? 'desconhecido'}.`);
}

try {
  run(executable, ['start', '--no-browser', '--port', String(port)]);
  const playwright = resolve('node_modules/.bin/playwright');
  run(playwright, ['test', '--config', 'playwright.package.config.ts'], {
    ...runtimeEnv,
    POSEIDON_PACKAGE_URL: `http://127.0.0.1:${port}`,
    POSEIDON_PACKAGE_DATA_DIR: dataDir,
  }, process.cwd());
} finally {
  spawnSync(executable, ['stop'], { cwd: appDir, env: runtimeEnv, stdio: 'inherit' });
  await rm(dataDir, { recursive: true, force: true });
}
