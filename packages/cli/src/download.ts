import { createHash } from 'node:crypto';
import { createWriteStream, existsSync, mkdirSync, readFileSync, renameSync, rmSync, statSync } from 'node:fs';
import { get as httpGet } from 'node:http';
import { get as httpsGet } from 'node:https';
import { dirname } from 'node:path';
import { Progress, error as logError, warn } from './ui';

export interface DownloadOptions {
  retries?: number;
  verbose?: boolean;
  /** 下载完成后的期望 SHA-256（小写十六进制） */
  sha256?: string;
  /** 允许的失败重试间隔基准（毫秒） */
  retryDelayMs?: number;
}

function oneGet(url: string, dest: string, onProgress: (n: number, total: number) => void, redirects = 0): Promise<string> {
  return new Promise((resolve, reject) => {
    if (redirects > 5) {
      reject(new Error('重定向次数过多'));
      return;
    }
    const client = url.startsWith('https:') ? httpsGet : httpGet;
    const request = client(
      url,
      { headers: { 'user-agent': 'hajimi-pet-cli' }, timeout: 30000 },
      (response) => {
        const status = response.statusCode ?? 0;
        if (status >= 300 && status < 400 && response.headers.location) {
          response.resume();
          resolve(oneGet(response.headers.location, dest, onProgress, redirects + 1));
          return;
        }
        if (status !== 200) {
          response.resume();
          reject(new Error(`HTTP ${status}`));
          return;
        }
        const total = Number(response.headers['content-length'] ?? 0);
        let received = 0;
        const out = createWriteStream(dest);
        response.on('data', (chunk: Buffer) => {
          received += chunk.length;
          onProgress(received, total);
        });
        response.pipe(out);
        out.on('finish', () => {
          out.close(() => resolve(dest));
        });
        out.on('error', reject);
        response.on('error', reject);
      },
    );
    request.on('timeout', () => request.destroy(new Error('下载超时')));
    request.on('error', reject);
  });
}

export function sha256File(file: string): string {
  const hash = createHash('sha256');
  hash.update(readFileSync(file));
  return hash.digest('hex');
}

/** 下载到目标路径，带进度、失败重试与 SHA-256 校验。 */
export async function download(
  url: string,
  dest: string,
  options: DownloadOptions = {},
): Promise<{ sha256: string; size: number }> {
  const retries = options.retries ?? 3;
  const retryDelayMs = options.retryDelayMs ?? 800;
  mkdirSync(dirname(dest), { recursive: true });
  let lastError: unknown = null;

  for (let attempt = 1; attempt <= retries; attempt += 1) {
    const tmp = `${dest}.part`;
    const progress = new Progress();
    progress.start(`下载 ${url.split('/').pop()}`);
    try {
      rmSync(tmp, { force: true });
      await oneGet(url, tmp, (received, total) => progress.update(received, total));
      const size = statSync(tmp).size;
      if (size === 0) throw new Error('下载内容为空');
      const actual = sha256File(tmp);
      if (options.sha256 && actual.toLowerCase() !== options.sha256.toLowerCase()) {
        throw new Error(`SHA-256 校验失败：期望 ${options.sha256}，实际 ${actual}`);
      }
      rmSync(dest, { force: true });
      renameSync(tmp, dest);
      progress.finish(`下载完成（${(size / 1024 / 1024).toFixed(1)} MB）`);
      return { sha256: actual, size };
    } catch (error) {
      lastError = error;
      rmSync(tmp, { force: true });
      if (attempt < retries) {
        warn(`第 ${attempt} 次下载失败：${String(error)}，${retryDelayMs}ms 后重试`);
        await new Promise((resolve) => setTimeout(resolve, retryDelayMs * attempt));
      }
    }
  }
  logError(`下载失败：${String(lastError)}`);
  throw lastError instanceof Error ? lastError : new Error(String(lastError));
}

export function isSha256(value: string | undefined): boolean {
  return !!value && /^[a-f0-9]{64}$/i.test(value.trim());
}

/** 便携版 .exe 应该是 Windows PE 文件，避免把错误页当成安装包。 */
export function looksLikeWindowsBinary(file: string): boolean {
  if (!existsSync(file)) return false;
  const head = readFileSync(file).subarray(0, 2).toString('ascii');
  return head === 'MZ';
}
