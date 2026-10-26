import { readFileSync, readdirSync, statSync } from 'node:fs';
import { homedir } from 'node:os';
import { basename, join } from 'node:path';

/** 一个 DSH 对话的公开摘要（只暴露标题、进度、是否在跑）。 */
export interface DshSession {
  id: string;
  title: string;
  cwd: string;
  /** true = 该对话正在进行中（最后一轮还没结束） */
  active: boolean;
  turn: number;
  updatedAt: number;
}

export interface DshSnapshot {
  /** DSH 会话目录是否可读 */
  available: boolean;
  sessions: DshSession[];
  activeCount: number;
  /** 最近活跃的对话标题，给桌宠气泡用 */
  focusTitle: string | null;
  error: string | null;
  scannedAt: number;
}

const EMPTY: DshSnapshot = {
  available: false,
  sessions: [],
  activeCount: 0,
  focusTitle: null,
  error: null,
  scannedAt: 0,
};

function dshHome(): string {
  return process.env.DSH_HOME ?? join(homedir(), '.dsh');
}

function cacheDir(): string {
  return join(dshHome(), 'storages', 'session_projcache', 'sessions');
}

/** 会话文件名 session-<uuid>.json，id 只取 uuid 部分。 */
function sessionIdOf(file: string): string {
  return basename(file, '.json').replace(/^session-/, '');
}

interface CacheRecord {
  record?: {
    identity?: { cwd?: string; createdAt?: number };
    rows?: {
      title?: { val?: string };
      turnBoundary?: {
        val?: {
          openTurnStartSeq?: number;
          lastStepStartSeq?: number;
          lastTurn?: number;
          lastStepBoundary?: { kind?: string };
        };
      };
    };
  };
}

/** 读取单个会话缓存。任何结构异常都当作“无此会话”，不让桌宠崩。 */
function readSession(file: string): DshSession | null {
  try {
    const raw = JSON.parse(readFileSync(file, 'utf8')) as CacheRecord;
    const record = raw.record;
    if (!record) return null;
    const cwd = record.identity?.cwd ?? '';
    const title = (record.rows?.title?.val ?? '').trim();
    const boundary = record.rows?.turnBoundary?.val;
    const kind = boundary?.lastStepBoundary?.kind ?? '';
    const openTurn = typeof boundary?.openTurnStartSeq === 'number' ? boundary.openTurnStartSeq : 0;
    // kind === 'start' 表示这一步还没收到结束标记，即对话正在生成
    const active = kind === 'start' && openTurn > 0;
    const updatedAt = statSync(file).mtimeMs;
    if (!title && !cwd) return null;
    return {
      id: sessionIdOf(file),
      title: title || '未命名对话',
      cwd,
      active,
      turn: typeof boundary?.lastTurn === 'number' ? boundary.lastTurn : 0,
      updatedAt,
    };
  } catch {
    return null;
  }
}

/** 扫描 DSH 会话缓存，返回对话列表与进行中数量。 */
export function readDshSnapshot(): DshSnapshot {
  const dir = cacheDir();
  let files: string[];
  try {
    files = readdirSync(dir).filter((name) => name.startsWith('session-') && name.endsWith('.json'));
  } catch (error) {
    return { ...EMPTY, error: `读不到 DSH 会话目录：${String(error)}`, scannedAt: Date.now() };
  }

  const sessions: DshSession[] = [];
  for (const name of files) {
    const session = readSession(join(dir, name));
    // 空壳会话（还没有标题也没有 cwd）直接丢掉
    if (session && (session.title !== '未命名对话' || session.turn > 0)) sessions.push(session);
  }

  sessions.sort((a, b) => {
    if (a.active !== b.active) return a.active ? -1 : 1;
    return b.updatedAt - a.updatedAt;
  });

  const active = sessions.filter((session) => session.active);
  const focus = active[0] ?? sessions[0] ?? null;

  return {
    available: true,
    sessions: sessions.slice(0, 12),
    activeCount: active.length,
    focusTitle: focus ? focus.title : null,
    error: null,
    scannedAt: Date.now(),
  };
}

export function dshCachePath(): string {
  return cacheDir();
}
