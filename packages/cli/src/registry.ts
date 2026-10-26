import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { get as httpGet } from 'node:http';
import { get as httpsGet } from 'node:https';
import { platformKey } from './paths';
import type { PetEntry, Registry, ResolvedAsset } from './types';

export const DEFAULT_REGISTRY_URL =
  'https://raw.githubusercontent.com/xtf666666-student/Hajimi/main/pets.json';

const BUNDLED_PATH = join(__dirname, '..', 'pets.json');

function validate(registry: unknown): Registry {
  const data = registry as Registry;
  if (!data || !Array.isArray(data.pets)) throw new Error('注册表格式不正确：缺少 pets 数组');
  return data;
}

export function loadBundledRegistry(): Registry {
  if (!existsSync(BUNDLED_PATH)) throw new Error(`找不到内置注册表：${BUNDLED_PATH}`);
  return validate(JSON.parse(readFileSync(BUNDLED_PATH, 'utf8')));
}

function httpGetJson(url: string, timeoutMs = 8000, redirects = 0): Promise<unknown> {
  return new Promise((resolve, reject) => {
    if (redirects > 5) {
      reject(new Error('重定向次数过多'));
      return;
    }
    const client = url.startsWith('https:') ? httpsGet : httpGet;
    const request = client(
      url,
      { headers: { 'user-agent': 'hajimi-pet-cli', accept: 'application/json' }, timeout: timeoutMs },
      (response) => {
        const status = response.statusCode ?? 0;
        if (status >= 300 && status < 400 && response.headers.location) {
          response.resume();
          resolve(httpGetJson(response.headers.location, timeoutMs, redirects + 1));
          return;
        }
        if (status !== 200) {
          response.resume();
          reject(new Error(`HTTP ${status}`));
          return;
        }
        let body = '';
        response.setEncoding('utf8');
        response.on('data', (chunk) => {
          body += chunk;
        });
        response.on('end', () => {
          try {
            resolve(JSON.parse(body));
          } catch (error) {
            reject(new Error(`注册表不是合法 JSON：${String(error)}`));
          }
        });
      },
    );
    request.on('timeout', () => request.destroy(new Error('请求超时')));
    request.on('error', reject);
  });
}

/** 优先远端注册表（可发新版），失败时回退到包内置副本。 */
export async function loadRegistry(verbose = false): Promise<{ registry: Registry; source: string }> {
  const url = process.env.HAJIMI_REGISTRY_URL ?? DEFAULT_REGISTRY_URL;
  try {
    const remote = validate(await httpGetJson(url));
    if (verbose) console.log(`  registry: ${url}`);
    return { registry: remote, source: url };
  } catch (error) {
    const bundled = loadBundledRegistry();
    if (verbose) console.log(`  registry 远端不可用（${String(error)}），使用内置副本`);
    return { registry: bundled, source: 'bundled' };
  }
}

export function findPet(registry: Registry, id: string): PetEntry {
  const pet = registry.pets.find((item) => item.id === id);
  if (!pet) {
    const ids = registry.pets.map((item) => item.id).join(', ');
    throw new Error(`未找到桌宠「${id}」。可用：${ids || '（空）'}`);
  }
  return pet;
}

export function resolveAsset(
  pet: PetEntry,
  version: string | undefined,
  kind: 'portable' | 'installer',
): ResolvedAsset {
  const target = version ?? pet.latest;
  const release = pet.releases[target];
  if (!release) {
    throw new Error(
      `桌宠「${pet.id}」没有 ${target} 版本。已有：${Object.keys(pet.releases).join(', ') || '无'}`,
    );
  }
  const key = platformKey();
  const platformAssets = release.assets[key];
  if (!platformAssets) {
    throw new Error(`版本 ${target} 不支持当前平台 ${key}`);
  }
  const asset = platformAssets[kind] ?? platformAssets.portable ?? platformAssets.installer;
  if (!asset?.url) {
    throw new Error(`版本 ${target} 在 ${key} 上没有可用的 ${kind} 安装包`);
  }
  const url = asset.url;
  const fileName = decodeURIComponent(url.split('/').pop() ?? `hajimi-${target}.exe`);
  return { version: target, url, sha256: asset.sha256, kind, fileName };
}
