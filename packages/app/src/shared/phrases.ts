/** 桌宠台词：短、克制，围绕“陪你写代码 / 盯着你的对话”。 */

import type { Mood } from './types';

export const CLICK_LINES: Record<Mood, string[]> = {
  idle: ['在呢。', '嗯？', '叫我干嘛。', '我一直在这儿。'],
  curious: ['哈？', '你点我做什么。', '有事就说。'],
  excited: ['开干了！', '又开一个对话？', '肝起来了。'],
  annoyed: ['别老点我。', '手拿开。', '忙着呢。'],
  sleepy: ['困……', '再睡会儿。'],
  alert: ['跑着呢。', '别急，在想了。', '正在写代码。'],
};

export const PET_LINES = ['嗯。', '舒服。', '就这一下。', '继续。'];

/** 有对话进行中时，偶尔冒的句子 */
export const BUSY_LINES = [
  '有一个还在跑。',
  '它在想事情，别催。',
  '正在敲代码。',
  '对话没结束，我盯着呢。',
];

/** 全空闲时的静默台词 */
export const IDLE_LINES = ['都在休息。', '没活儿了。', '等你开工。', '桌面挺安静。'];

export function pick<T>(list: readonly T[], random: () => number = Math.random): T {
  return list[Math.floor(random() * list.length) % list.length];
}

/** 相对时间：刚刚 / N 分钟前 / N 小时前 / N 天前 */
export function relativeTime(timestamp: number, now = Date.now()): string {
  const diff = Math.max(0, now - timestamp);
  const minutes = Math.floor(diff / 60_000);
  if (minutes < 1) return '刚刚';
  if (minutes < 60) return `${minutes} 分钟前`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} 小时前`;
  return `${Math.floor(hours / 24)} 天前`;
}

/** 标题太长会撑破气泡，压到指定字符数 */
export function shortTitle(title: string, limit = 18): string {
  const clean = title.replace(/^#+\s*/, '').replace(/\s+/g, ' ').trim();
  if (clean.length <= limit) return clean;
  return `${clean.slice(0, limit)}…`;
}
