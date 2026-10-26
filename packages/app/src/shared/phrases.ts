/** 点击 / 摸头 / 主动说话用到的中文文案。 */

import type { Emotion } from './types';

export const CLICK_LINES: Record<Emotion, string[]> = {
  happy: ['嘿嘿，被你摸到了。', '今天心情不错喵～', '再来一下也可以哦。', '本哈基米今天格外可爱。'],
  confused: ['哈？', '你干嘛～', '本哈基米正在思考喵。', '刚刚…是不是有人碰我？'],
  angry: ['摸我干什么！', '哼，手收回去。', '再戳我就生气了喵！', '你是不是又在摸鱼？'],
  surprised: ['！！', '吓我一跳！', '你从哪冒出来的？', '别突然出现啊喵！'],
  sleepy: ['困了…别吵。', '再睡五分钟…', '眼皮好重喵。'],
  idle: ['哼，知道你在看我。', '有事吗？没事我继续发呆了。', '你忙你的，我看着。'],
};

export const PET_LINES = [
  '舒服了喵～',
  '再摸一下。',
  '嗯…就这个位置。',
  '摸头技术不错嘛。',
  '呼噜呼噜…',
];

export const FEED_LINES = [
  '好吃！还要！',
  '这波不亏喵。',
  '哈基米正在暴风吸入。',
  '谢谢投喂，记你一个好。',
];

/** 主动说话：默认安静，偶尔冒一句。 */
export const IDLE_TALK_LINES = [
  '你已经连续摸鱼很久了喵。',
  '喝水了吗？没喝就去喝。',
  '这个点还在写代码？',
  '本哈基米在思考猫生。',
  '盯——',
  '桌面好干净，是不是还没开始干活。',
  '困了就早点睡吧。',
  '我在这儿看着呢，别偷懒。',
  '今天的风评被害了吗？',
  '需要我陪你一会儿吗？',
  '有人在吗？没有的话我继续发呆。',
  '喵。就一声，没别的意思。',
];

export function pick<T>(list: readonly T[], random: () => number = Math.random): T {
  return list[Math.floor(random() * list.length) % list.length];
}
