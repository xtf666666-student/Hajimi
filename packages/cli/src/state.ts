import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';
import { stateFile } from './paths';
import type { InstalledState } from './types';

const EMPTY: InstalledState = { version: 1, pets: {} };

export function loadState(): InstalledState {
  const file = stateFile();
  if (!existsSync(file)) return { ...EMPTY, pets: {} };
  try {
    const parsed = JSON.parse(readFileSync(file, 'utf8')) as InstalledState;
    if (!parsed || typeof parsed !== 'object' || typeof parsed.pets !== 'object') {
      return { ...EMPTY, pets: {} };
    }
    return { version: 1, pets: parsed.pets ?? {} };
  } catch {
    return { ...EMPTY, pets: {} };
  }
}

export function saveState(state: InstalledState): void {
  const file = stateFile();
  mkdirSync(dirname(file), { recursive: true });
  const tmp = `${file}.tmp`;
  writeFileSync(tmp, JSON.stringify(state, null, 2), 'utf8');
  renameSync(tmp, file);
}
