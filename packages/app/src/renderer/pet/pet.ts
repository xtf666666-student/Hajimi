import { CLICK_LINES, FEED_LINES, PET_LINES, pick } from '../../shared/phrases';
import type { Emotion, PetSnapshot } from '../../shared/types';

const api = window.hajimi;

const stage = document.getElementById('stage') as HTMLDivElement;
const pet3d = document.getElementById('pet3d') as HTMLDivElement;
const wrap = document.getElementById('pet3d') as HTMLDivElement;
const bodyEl = document.getElementById('body') as HTMLImageElement;
const headEl = document.getElementById('head') as HTMLImageElement;
const faceEl = document.getElementById('face') as HTMLImageElement;
const bubble = document.getElementById('bubble') as HTMLDivElement;
const bubbleText = document.getElementById('bubble-text') as HTMLSpanElement;
const fxLayer = document.getElementById('fx') as HTMLDivElement;

/** 情绪 -> 特写表情图（同一只猫的真实照片） */
const FACE_FOR: Partial<Record<Emotion, string>> = {
  happy: 'face-smirk.png',
  confused: 'face-grin.png',
  surprised: 'face-shock.png',
};

const ASSETS = ['body.png', 'head-idle.png', 'face-smirk.png', 'face-shock.png', 'face-grin.png'];

let snapshot: PetSnapshot | null = null;
let dragging = false;
let downAt = 0;
let downX = 0;
let downY = 0;
let pointerDown = false;
let mood: Emotion = 'idle';

let bubbleTimer: number | null = null;
let moodTimer: number | null = null;
let blinkTimer: number | null = null;
let petTimer: number | null = null;
let sleepTimer: number | null = null;
let resizeObserver: ResizeObserver | null = null;
let tiltFrame = 0;
let pendingTilt: { x: number; y: number } | null = null;

const recentClicks: number[] = [];

// ---------------------------------------------------------------- 资源

function assetUrl(file: string): string {
  return new URL(`../${file}`, document.baseURI).href;
}

function preloadAssets(): void {
  for (const file of ASSETS) {
    const img = new Image();
    img.src = assetUrl(file);
  }
}

// ---------------------------------------------------------------- 基础状态

function applySnapshot(next: PetSnapshot): void {
  snapshot = next;
  document.documentElement.style.setProperty('--size', `${next.settings.size}px`);
  document.documentElement.classList.toggle('no-anim', !next.settings.animations);
  if (next.settings.animations) scheduleBlink();
  else cancelBlink();
}

function setMood(next: Emotion, holdMs = 900): void {
  mood = next;
  wrap.classList.remove(
    'mood-happy',
    'mood-confused',
    'mood-angry',
    'mood-sleepy',
    'mood-surprised',
    'mood-idle',
  );
  wrap.classList.add(`mood-${next}`);

  const face = FACE_FOR[next];
  if (face) {
    faceEl.src = assetUrl(face);
    pet3d.classList.add('has-face');
  } else {
    pet3d.classList.remove('has-face');
  }

  if (moodTimer !== null) window.clearTimeout(moodTimer);
  if (next !== 'idle') {
    moodTimer = window.setTimeout(() => {
      moodTimer = null;
      setMood('idle');
    }, holdMs);
  }
}

function showBubble(text: string, duration = 2600): void {
  if (!text) return;
  bubbleText.textContent = text;
  bubble.classList.add('show');
  reportGeometry();
  if (bubbleTimer !== null) window.clearTimeout(bubbleTimer);
  bubbleTimer = window.setTimeout(() => {
    bubble.classList.remove('show');
    bubbleTimer = null;
    window.setTimeout(reportGeometry, 240);
  }, duration);
}

function reportGeometry(): void {
  const cat = pet3d.getBoundingClientRect();
  const bubbleRect = bubble.classList.contains('show') ? bubble.getBoundingClientRect() : null;
  api.reportGeometry({
    cat: { x: cat.x, y: cat.y, width: cat.width, height: cat.height },
    bubble: bubbleRect
      ? { x: bubbleRect.x, y: bubbleRect.y, width: bubbleRect.width, height: bubbleRect.height }
      : null,
  });
}

// ---------------------------------------------------------------- 依赖光标方向的 3D 倾斜

function setTilt(nx: number, ny: number): void {
  const root = document.documentElement.style;
  root.setProperty('--ry', `${(nx * 11).toFixed(2)}deg`);
  root.setProperty('--rx', `${(-ny * 7).toFixed(2)}deg`);
  const shadow = document.getElementById('shadow');
  if (shadow) {
    shadow.style.transform = `translateX(calc(-50% + ${(nx * 6).toFixed(1)}px)) translateZ(calc(var(--size) * -0.05)) scaleX(${(
      1 - Math.abs(nx) * 0.08
    ).toFixed(3)})`;
  }
}

function onPointerMoveTilt(event: PointerEvent): void {
  if (dragging || !snapshot?.settings.animations) return;
  const rect = pet3d.getBoundingClientRect();
  if (rect.width === 0) return;
  const nx = Math.max(-1, Math.min(1, ((event.clientX - rect.left) / rect.width - 0.5) * 2));
  const ny = Math.max(-1, Math.min(1, ((event.clientY - rect.top) / rect.height - 0.5) * 2));
  pendingTilt = { x: nx, y: ny };
  if (tiltFrame) return;
  tiltFrame = requestAnimationFrame(() => {
    tiltFrame = 0;
    if (pendingTilt) setTilt(pendingTilt.x, pendingTilt.y);
  });
}

function resetTilt(): void {
  setTilt(0, 0);
}

// ---------------------------------------------------------------- 眨眼 / 待机

function scheduleBlink(): void {
  cancelBlink();
  const delay = 2800 + Math.random() * 5200;
  blinkTimer = window.setTimeout(() => {
    if (!dragging && snapshot?.settings.animations) {
      const blinkWrap = document.getElementById('blink-wrap');
      document.documentElement.classList.add('is-blinking');
      blinkWrap?.classList.add('is-blink');
      window.setTimeout(() => {
        document.documentElement.classList.remove('is-blinking');
        blinkWrap?.classList.remove('is-blink');
      }, 220);
    }
    blinkTimer = null;
    scheduleBlink();
  }, delay);
}

function cancelBlink(): void {
  if (blinkTimer !== null) {
    window.clearTimeout(blinkTimer);
    blinkTimer = null;
  }
}

function scheduleSleepy(): void {
  if (sleepTimer !== null) window.clearTimeout(sleepTimer);
  sleepTimer = window.setTimeout(() => {
    if (mood === 'idle' && !dragging) {
      setMood('sleepy', 9000);
      showBubble('有点困了喵…', 2600);
    }
    scheduleSleepy();
  }, 150_000 + Math.random() * 180_000);
}

// ---------------------------------------------------------------- 互动

function clickEmotion(): Emotion {
  const roll = Math.random();
  if (roll < 0.3) return 'happy';
  if (roll < 0.56) return 'confused';
  if (roll < 0.76) return 'angry';
  return 'surprised';
}

async function handleClick(): Promise<void> {
  const now = Date.now();
  recentClicks.push(now);
  while (recentClicks.length && now - recentClicks[0] > 2500) recentClicks.shift();
  if (recentClicks.length >= 3) {
    recentClicks.length = 0;
    await petHeadReaction(true);
    return;
  }
  const emotion = clickEmotion();
  setMood(emotion, 1200);
  showBubble(pick(CLICK_LINES[emotion]));
  try {
    applySnapshot(await api.registerClick());
  } catch (error) {
    console.warn('registerClick failed', error);
  }
}

async function petHeadReaction(fromClick = false): Promise<void> {
  setMood('happy', 1700);
  wrap.classList.add('is-petting');
  window.setTimeout(() => wrap.classList.remove('is-petting'), 1600);
  showBubble(fromClick ? '再摸一下也不是不行喵。' : pick(PET_LINES));
  spawnFx(['💗', '✨'], 3);
  try {
    applySnapshot(await api.petHead());
  } catch (error) {
    console.warn('petHead failed', error);
  }
}

function feedReaction(): void {
  setMood('happy', 1800);
  showBubble(pick(FEED_LINES));
  spawnFx(['🍗', '🍖', '💛'], 5);
}

function spawnFx(icons: string[], count: number): void {
  if (!snapshot?.settings.animations) return;
  for (let i = 0; i < count; i += 1) {
    const el = document.createElement('span');
    el.className = 'fx-item';
    el.textContent = icons[i % icons.length];
    el.style.left = `${18 + Math.random() * 64}%`;
    el.style.top = `${26 + Math.random() * 24}%`;
    el.style.animationDelay = `${i * 90}ms`;
    el.style.setProperty('--spin', `${Math.round(Math.random() * 40 - 20)}deg`);
    fxLayer.appendChild(el);
    window.setTimeout(() => el.remove(), 1600 + i * 90);
  }
}

// ---------------------------------------------------------------- 指针

function onPointerDown(event: PointerEvent): void {
  if (event.button !== 0) return;
  pointerDown = true;
  dragging = false;
  downAt = performance.now();
  downX = event.screenX;
  downY = event.screenY;
  wrap.setPointerCapture(event.pointerId);
  event.preventDefault();
}

function onPointerMove(event: PointerEvent): void {
  onPointerMoveTilt(event);
  if (!pointerDown || dragging) return;
  if (Math.hypot(event.screenX - downX, event.screenY - downY) > 5) {
    dragging = true;
    wrap.classList.add('is-dragging');
    api.dragStart();
  }
}

function onPointerUp(event: PointerEvent): void {
  if (!pointerDown) return;
  pointerDown = false;
  const duration = performance.now() - downAt;
  const moved = Math.hypot(event.screenX - downX, event.screenY - downY);
  if (wrap.hasPointerCapture(event.pointerId)) wrap.releasePointerCapture(event.pointerId);
  if (dragging) {
    dragging = false;
    wrap.classList.remove('is-dragging');
    api.dragEnd();
    return;
  }
  if (moved <= 6 && duration < 700) void handleClick();
}

function onContextMenu(event: MouseEvent): void {
  event.preventDefault();
  if (dragging) return;
  api.showContextMenu({ x: event.screenX, y: event.screenY });
}

function onHeadHover(event: PointerEvent): void {
  const rect = pet3d.getBoundingClientRect();
  const localY = event.clientY - rect.top;
  if (localY > rect.height * 0.45) return;
  if (petTimer !== null) window.clearTimeout(petTimer);
  petTimer = window.setTimeout(() => {
    petTimer = null;
    if (!dragging) void petHeadReaction();
  }, 780);
}

function onHeadLeave(): void {
  if (petTimer !== null) {
    window.clearTimeout(petTimer);
    petTimer = null;
  }
}

wrap.addEventListener('pointerdown', onPointerDown);
wrap.addEventListener('pointermove', onPointerMove);
wrap.addEventListener('pointerup', onPointerUp);
wrap.addEventListener('pointercancel', onPointerUp);
wrap.addEventListener('contextmenu', onContextMenu);
wrap.addEventListener('pointerenter', onHeadHover);
wrap.addEventListener('pointermove', onHeadHover);
wrap.addEventListener('pointerleave', () => {
  onHeadLeave();
  resetTilt();
});

// ---------------------------------------------------------------- 主进程事件

api.onState((next) => {
  const prevSize = snapshot?.settings.size;
  applySnapshot(next);
  if (prevSize !== undefined && prevSize !== next.settings.size) reportGeometry();
});

api.onSay(({ text }) => {
  if (dragging) return;
  setMood(Math.random() < 0.5 ? 'confused' : 'idle', 900);
  showBubble(text, 3000);
});

api.onCommand((payload) => {
  switch (payload.type) {
    case 'feed':
      feedReaction();
      break;
    case 'pet-head':
      void petHeadReaction();
      break;
    case 'resize': {
      const size = Number(payload.size);
      if (Number.isFinite(size)) {
        document.documentElement.style.setProperty('--size', `${size}px`);
        requestAnimationFrame(reportGeometry);
      }
      break;
    }
    case 'size-toast':
      showBubble(`换成 ${payload.size} 的大小啦`, 1600);
      break;
    default:
      break;
  }
});

api.onDropped(() => {
  wrap.classList.remove('is-dragging');
  resetTilt();
});

// ---------------------------------------------------------------- 启动

async function bootstrap(): Promise<void> {
  preloadAssets();

  const loaded = new Set<string>();
  const markLoaded = (el: HTMLImageElement): void => {
    el.classList.add('is-loaded');
    if (loaded.size >= 2) reportGeometry();
  };
  bodyEl.addEventListener('load', () => markLoaded(bodyEl));
  headEl.addEventListener('load', () => markLoaded(headEl));

  bodyEl.src = assetUrl('body.png');
  headEl.src = assetUrl('head-idle.png');
  loaded.add('body');

  const initial = await api.getSnapshot();
  applySnapshot(initial);
  setMood('idle');
  reportGeometry();
  scheduleSleepy();

  resizeObserver?.disconnect();
  resizeObserver = new ResizeObserver(() => reportGeometry());
  resizeObserver.observe(pet3d);

  window.addEventListener('beforeunload', () => {
    cancelBlink();
    if (bubbleTimer !== null) window.clearTimeout(bubbleTimer);
    if (sleepTimer !== null) window.clearTimeout(sleepTimer);
    if (moodTimer !== null) window.clearTimeout(moodTimer);
    resizeObserver?.disconnect();
  });
}

void bootstrap();

// 自检 / 调试钩子（仅渲染进程内部）
declare global {
  interface Window {
    __hajimiTest?: {
      showBubble(text: string): void;
      setMood(emotion: Emotion): void;
      spawnFx(icons: string[], count: number): void;
      geometry(): DOMRect;
      mood(): Emotion;
      tilt(nx: number, ny: number): void;
    };
  }
}

window.__hajimiTest = {
  showBubble: (text: string) => showBubble(text, 6000),
  setMood: (emotion: Emotion) => setMood(emotion, 8000),
  spawnFx,
  geometry: () => pet3d.getBoundingClientRect(),
  mood: () => mood,
  tilt: (nx: number, ny: number) => setTilt(nx, ny),
};
