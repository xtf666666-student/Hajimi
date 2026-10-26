import { CLICK_LINES, PET_LINES, pick } from '../../shared/phrases';
import type { Mood, PetSnapshot } from '../../shared/types';

const api = window.hajimi;

const stage = document.getElementById('stage') as HTMLDivElement;
const badge = document.getElementById('badge') as HTMLDivElement;
const badgeCount = document.getElementById('badge-count') as HTMLSpanElement;
const bubble = document.getElementById('bubble') as HTMLDivElement;
const bubbleText = document.getElementById('bubble-text') as HTMLSpanElement;
const tilt = document.getElementById('tilt') as HTMLDivElement;
const bodyEl = document.getElementById('body') as HTMLImageElement;
const headEl = document.getElementById('head') as HTMLImageElement;
const listEl = document.getElementById('list') as HTMLDivElement;

let snapshot: PetSnapshot | null = null;
let mood: Mood = 'idle';
let dragging = false;
let pointerDown = false;
let downAt = 0;
let downX = 0;
let downY = 0;

let bubbleTimer: number | null = null;
let moodTimer: number | null = null;
let tiltFrame = 0;
let pendingTilt: { x: number; y: number } | null = null;
let listOpen = false;

const ASSETS = ['pet-body.png', 'pet-head.png'];

function assetUrl(file: string): string {
  return new URL(`../${file}`, document.baseURI).href;
}

// ---------------------------------------------------------------- 鐘舵€?
function applySnapshot(next: PetSnapshot): void {
  const prev = snapshot;
  snapshot = next;

  document.documentElement.style.setProperty('--size', `${next.settings.size}px`);
  document.documentElement.classList.toggle('no-anim', !next.settings.animations);

  const active = next.dsh.activeCount;
  badgeCount.textContent = String(active);
  badge.classList.toggle('busy', active > 0);
  badge.title =
    active > 0
      ? `${active} 涓璇濊繘琛屼腑锛堝叡 ${next.dsh.total} 涓級`
      : `娌℃湁瀵硅瘽鍦ㄨ窇锛堝叡 ${next.dsh.total} 涓級`;

  if (prev && prev.dsh.activeCount !== active) {
    badge.classList.remove('bump');
    void badge.offsetWidth;
    badge.classList.add('bump');
    if (active > prev.dsh.activeCount) {
      setMood('alert', 1600);
      showBubble('新对话开始了。', 1800);
    }
  }

  if (listOpen) renderList();
}

function showBubble(text: string, duration = 2000): void {
  if (!text) return;
  bubbleText.textContent = text;
  bubble.classList.add('show');
  if (bubbleTimer !== null) window.clearTimeout(bubbleTimer);
  bubbleTimer = window.setTimeout(() => {
    bubble.classList.remove('show');
    bubbleTimer = null;
  }, duration);
}

function setMood(next: Mood, holdMs = 1100): void {
  mood = next;
  tilt.classList.remove(
    'mood-idle',
    'mood-curious',
    'mood-excited',
    'mood-sleepy',
    'mood-annoyed',
    'mood-alert',
  );
  tilt.classList.add(`mood-${next}`);
  if (moodTimer !== null) window.clearTimeout(moodTimer);
  if (next !== 'idle') {
    moodTimer = window.setTimeout(() => {
      moodTimer = null;
      setMood('idle');
    }, holdMs);
  }
}

// ---------------------------------------------------------------- 3D 鍊炬枩

function setTilt(nx: number, ny: number): void {
  const root = document.documentElement.style;
  root.setProperty('--ry', `${(nx * 12).toFixed(2)}deg`);
  root.setProperty('--rx', `${(-ny * 8).toFixed(2)}deg`);
}

function resetTilt(): void {
  setTilt(0, 0);
}

function onTiltMove(event: PointerEvent): void {
  if (dragging || !snapshot?.settings.animations) return;
  const rect = tilt.getBoundingClientRect();
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

// ---------------------------------------------------------------- 瀵硅瘽鍒楄〃

function renderList(): void {
  if (!snapshot) return;
  listEl.innerHTML = '';
  const sessions = snapshot.dsh.sessions;
  if (sessions.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'empty';
    empty.textContent = snapshot.dsh.available ? '还没有对话。点我开始。' : '读不到 DSH 会话。';
    listEl.appendChild(empty);
    return;
  }
  for (const session of sessions) {
    const button = document.createElement('button');
    button.className = `item${session.active ? ' active' : ''}`;
    button.type = 'button';
    button.title = `${session.title} · ${session.ago}${session.active ? ' · 进行中' : ''}`;

    const dot = document.createElement('span');
    dot.className = 'dot';
    const name = document.createElement('span');
    name.className = 'name';
    name.textContent = session.title;
    const ago = document.createElement('span');
    ago.className = 'ago';
    ago.textContent = session.ago;

    button.append(dot, name, ago);
    button.addEventListener('click', (event) => {
      event.stopPropagation();
      toggleList(false);
      api.openSession(session.id);
    });
    listEl.appendChild(button);
  }
}

function toggleList(force?: boolean): void {
  listOpen = force ?? !listOpen;
  listEl.hidden = !listOpen;
  api.setListOpen(listOpen);
  if (listOpen) {
    // 列表和气泡会叠在一起，展开列表时先收掉气泡
    deadBubble();
    renderList();
    void api.refresh();
  }
}

// ---------------------------------------------------------------- 浜や簰

function onPointerDown(event: PointerEvent): void {
  if (event.button !== 0) return;
  pointerDown = true;
  dragging = false;
  downAt = performance.now();
  downX = event.screenX;
  downY = event.screenY;
  tilt.setPointerCapture(event.pointerId);
  event.preventDefault();
}

function onPointerMove(event: PointerEvent): void {
  onTiltMove(event);
  if (!pointerDown || dragging) return;
  if (Math.hypot(event.screenX - downX, event.screenY - downY) > 4) {
    dragging = true;
    tilt.classList.add('is-dragging');
    deadBubble();
    api.dragStart();
  }
}

function onPointerUp(event: PointerEvent): void {
  if (!pointerDown) return;
  pointerDown = false;
  const duration = performance.now() - downAt;
  const moved = Math.hypot(event.screenX - downX, event.screenY - downY);
  if (tilt.hasPointerCapture(event.pointerId)) tilt.releasePointerCapture(event.pointerId);
  if (dragging) {
    dragging = false;
    tilt.classList.remove('is-dragging');
    api.dragEnd();
    return;
  }
  if (moved <= 5 && duration < 650) void handleClick();
}

function deadBubble(): void {
  if (bubbleTimer !== null) {
    window.clearTimeout(bubbleTimer);
    bubbleTimer = null;
  }
  bubble.classList.remove('show');
}

/** 鍗曞嚮锛氳涓€鍙?+ 鐩存帴鎵撳紑 DSH */
async function handleClick(): Promise<void> {
  const active = snapshot?.dsh.activeCount ?? 0;
  const key: Mood = active > 0 ? 'excited' : 'curious';
  setMood(key, 1000);
  showBubble(pick(CLICK_LINES[key]), 1200);
  api.openDsh();
  try {
    applySnapshot(await api.click());
  } catch {
    // 涓昏繘绋嬬煭鏆備笉鍙敤鏃朵笉鎵撴柇浜や簰
  }
}

/** 鍙屽嚮 = 鎽稿ご */
function onPet(event: MouseEvent): void {
  event.preventDefault();
  event.stopPropagation();
  setMood('curious', 1400);
  tilt.classList.add('mood-petting');
  window.setTimeout(() => tilt.classList.remove('mood-petting'), 900);
  showBubble(pick(PET_LINES), 1400);
  void api.pet();
}

function onContextMenu(event: MouseEvent): void {
  event.preventDefault();
  if (dragging) return;
  api.showMenu({ x: event.screenX, y: event.screenY });
}

tilt.addEventListener('pointerdown', onPointerDown);
tilt.addEventListener('pointermove', onPointerMove);
tilt.addEventListener('pointerup', onPointerUp);
tilt.addEventListener('pointercancel', onPointerUp);
tilt.addEventListener('contextmenu', onContextMenu);
tilt.addEventListener('dblclick', onPet);
tilt.addEventListener('pointerleave', resetTilt);

badge.addEventListener('click', (event) => {
  event.stopPropagation();
  toggleList();
});

stage.addEventListener('pointerdown', (event) => {
  if (listOpen && !listEl.contains(event.target as Node) && event.target !== badge) {
    toggleList(false);
  }
});

// ---------------------------------------------------------------- 涓昏繘绋嬩簨浠?
api.onState((next) => {
  applySnapshot(next);
  reportGeometry();
});

api.onCommand((payload) => {
  switch (payload.type) {
    case 'say': {
      const text = String(payload.text ?? '');
      const nextMood = (payload.mood as Mood | undefined) ?? 'idle';
      if (text) showBubble(text, 2200);
      setMood(nextMood, 1400);
      break;
    }
    case 'pet':
      setMood('curious', 1400);
      showBubble(pick(PET_LINES), 1500);
      break;
    case 'anim':
      document.documentElement.classList.toggle('no-anim', payload.enabled === false);
      break;
    case 'refresh-pulse':
      badge.classList.remove('bump');
      void badge.offsetWidth;
      badge.classList.add('bump');
      break;
    default:
      break;
  }
});

api.onDropped(() => {
  tilt.classList.remove('is-dragging');
  resetTilt();
});

// ---------------------------------------------------------------- 鍚姩

function reportGeometry(): void {
  const rect = tilt.getBoundingClientRect();
  api.reportGeometry({
    cat: { x: rect.x, y: rect.y, width: rect.width, height: rect.height },
  });
}

async function bootstrap(): Promise<void> {
  for (const file of ASSETS) {
    const img = new Image();
    img.src = assetUrl(file);
  }

  bodyEl.addEventListener('load', () => {
    bodyEl.classList.add('loaded');
    reportGeometry();
  });
  headEl.addEventListener('load', () => headEl.classList.add('loaded'));
  bodyEl.src = assetUrl('pet-body.png');
  headEl.src = assetUrl('pet-head.png');

  applySnapshot(await api.getSnapshot());
  setMood('idle');
  reportGeometry();

  const observer = new ResizeObserver(() => reportGeometry());
  observer.observe(tilt);

  window.addEventListener('beforeunload', () => {
    observer.disconnect();
    if (bubbleTimer !== null) window.clearTimeout(bubbleTimer);
    if (moodTimer !== null) window.clearTimeout(moodTimer);
  });
}

void bootstrap();

declare global {
  interface Window {
    __hajimiTest?: {
      say(text: string): void;
      mood(next: Mood): void;
      toggleList(force?: boolean): void;
      catRect(): DOMRect;
      state(): PetSnapshot | null;
    };
  }
}

window.__hajimiTest = {
  say: (text: string) => showBubble(text, 6000),
  mood: (next: Mood) => setMood(next, 8000),
  toggleList: (force?: boolean) => toggleList(force),
  catRect: () => tilt.getBoundingClientRect(),
  state: () => snapshot,
};

