const supportsColor =
  process.stdout.isTTY && !process.env.NO_COLOR && process.env.TERM !== 'dumb';

const wrap = (code: number) => (text: string): string =>
  supportsColor ? `\u001b[${code}m${text}\u001b[0m` : text;

export const color = {
  bold: wrap(1),
  dim: wrap(2),
  red: wrap(31),
  green: wrap(32),
  yellow: wrap(33),
  blue: wrap(36),
  magenta: wrap(35),
};

export const symbol = {
  ok: color.green('✔'),
  fail: color.red('✖'),
  warn: color.yellow('!'),
  info: color.blue('›'),
};

export function title(text: string): void {
  console.log(`\n${color.bold(color.magenta('哈基米桌宠'))} ${color.dim('·')} ${text}\n`);
}

export function info(message: string): void {
  console.log(`${symbol.info} ${message}`);
}

export function success(message: string): void {
  console.log(`${symbol.ok} ${message}`);
}

export function warn(message: string): void {
  console.log(`${symbol.warn} ${color.yellow(message)}`);
}

export function error(message: string): void {
  console.error(`${symbol.fail} ${color.red(message)}`);
}

export function detail(message: string): void {
  console.log(`  ${color.dim(message)}`);
}

/** 单行刷新进度条，非 TTY 环境退化为整十百分比输出。 */
export class Progress {
  private lastPercent = -1;
  private lastLineLength = 0;

  start(label: string): void {
    if (!process.stdout.isTTY) {
      info(label);
      return;
    }
    this.render(label, 0, 0, 0);
  }

  update(downloaded: number, total: number): void {
    if (!process.stdout.isTTY) {
      const percent = total > 0 ? Math.floor((downloaded / total) * 100) : 0;
      const bucket = Math.floor(percent / 10) * 10;
      if (bucket !== this.lastPercent && bucket % 10 === 0) {
        this.lastPercent = bucket;
        console.log(`  ${color.dim(`已下载 ${bucket}%`)}`);
      }
      return;
    }
    const percent = total > 0 ? downloaded / total : 0;
    this.render(`下载中`, downloaded, total, percent);
  }

  finish(message: string): void {
    if (process.stdout.isTTY && this.lastLineLength > 0) {
      process.stdout.write('\r' + ' '.repeat(this.lastLineLength) + '\r');
      this.lastLineLength = 0;
    }
    success(message);
  }

  private render(label: string, downloaded: number, total: number, percent: number): void {
    const width = 26;
    const filled = Math.min(width, Math.round(percent * width));
    const bar = `${'█'.repeat(filled)}${'░'.repeat(width - filled)}`;
    const mb = (downloaded / 1024 / 1024).toFixed(1);
    const totalMb = total > 0 ? (total / 1024 / 1024).toFixed(1) : '?';
    const line = `  ${label} ${color.green(bar)} ${String(Math.floor(percent * 100)).padStart(3)}% ${color.dim(
      `${mb}/${totalMb} MB`,
    )}`;
    process.stdout.write(`\r${line}${' '.repeat(Math.max(0, this.lastLineLength - line.length))}`);
    this.lastLineLength = line.length;
  }
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}
