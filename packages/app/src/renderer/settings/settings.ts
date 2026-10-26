import type { PetSnapshot, Settings } from '../../shared/types';

const api = window.hajimi;

const $ = <T extends HTMLElement>(id: string): T => document.getElementById(id) as T;

const sizeInput = $<HTMLInputElement>('size');
const sizeValue = $<HTMLSpanElement>('size-value');
const opacityInput = $<HTMLInputElement>('opacity');
const opacityValue = $<HTMLSpanElement>('opacity-value');
const autoSpeak = $<HTMLInputElement>('autoSpeak');
const animations = $<HTMLInputElement>('animations');
const alwaysOnTop = $<HTMLInputElement>('alwaysOnTop');
const hungerValue = $<HTMLSpanElement>('hunger-value');
const hungerFill = $<HTMLDivElement>('hunger-fill');
const statClicks = $<HTMLElement>('stat-clicks');
const statPets = $<HTMLElement>('stat-pets');
const statFeeds = $<HTMLElement>('stat-feeds');
const statDays = $<HTMLElement>('stat-days');
const version = $<HTMLSpanElement>('version');
const presetButtons = Array.from(document.querySelectorAll<HTMLButtonElement>('.chip'));

let current: PetSnapshot | null = null;
let suppressEvents = false;
let pendingPatch: Partial<Settings> | null = null;
let patchTimer: number | null = null;

function hungerLabel(hunger: number): string {
  if (hunger >= 85) return '撑到了';
  if (hunger >= 60) return '很满足';
  if (hunger >= 35) return '还算饱';
  if (hunger >= 15) return '有点饿';
  return '饿扁了';
}

function render(snapshot: PetSnapshot): void {
  current = snapshot;
  suppressEvents = true;
  const { settings, stats, hunger } = snapshot;
  sizeInput.value = String(settings.size);
  sizeValue.textContent = `${settings.size} px`;
  opacityInput.value = String(Math.round(settings.opacity * 100));
  opacityValue.textContent = `${Math.round(settings.opacity * 100)}%`;
  autoSpeak.checked = settings.autoSpeak;
  animations.checked = settings.animations;
  alwaysOnTop.checked = settings.alwaysOnTop;
  for (const button of presetButtons) {
    button.classList.toggle('active', Number(button.dataset.size) === settings.size);
  }
  hungerValue.textContent = `${Math.round(hunger)} · ${hungerLabel(hunger)}`;
  hungerFill.style.width = `${Math.max(2, Math.round(hunger))}%`;
  statClicks.textContent = String(stats.clicks);
  statPets.textContent = String(stats.pets);
  statFeeds.textContent = String(stats.feeds);
  const days = Math.max(1, Math.ceil((Date.now() - stats.firstSeenAt) / 86_400_000));
  statDays.textContent = String(days);
  version.textContent = `v${snapshot.version}`;
  suppressEvents = false;
}

function queuePatch(patch: Partial<Settings>): void {
  pendingPatch = { ...(pendingPatch ?? {}), ...patch };
  if (patchTimer !== null) return;
  patchTimer = window.setTimeout(async () => {
    patchTimer = null;
    const nextPatch = pendingPatch;
    pendingPatch = null;
    if (!nextPatch) return;
    const snapshot = await api.updateSettings(nextPatch);
    render(snapshot);
  }, 70);
}

sizeInput.addEventListener('input', () => {
  const value = Number(sizeInput.value);
  sizeValue.textContent = `${value} px`;
  for (const button of presetButtons) {
    button.classList.toggle('active', Number(button.dataset.size) === value);
  }
  if (!suppressEvents) queuePatch({ size: value });
});

opacityInput.addEventListener('input', () => {
  const value = Number(opacityInput.value) / 100;
  opacityValue.textContent = `${opacityInput.value}%`;
  if (!suppressEvents) queuePatch({ opacity: value });
});

for (const button of presetButtons) {
  button.addEventListener('click', () => {
    const size = Number(button.dataset.size);
    sizeInput.value = String(size);
    sizeValue.textContent = `${size} px`;
    queuePatch({ size });
  });
}

autoSpeak.addEventListener('change', () => queuePatch({ autoSpeak: autoSpeak.checked }));
animations.addEventListener('change', () => queuePatch({ animations: animations.checked }));
alwaysOnTop.addEventListener('change', () => queuePatch({ alwaysOnTop: alwaysOnTop.checked }));

$('btn-close').addEventListener('click', () => api.closeSettings());

$('btn-reset').addEventListener('click', async () => {
  render(await api.resetSettings());
});

$('btn-hide').addEventListener('click', () => api.hidePet());

$('btn-quit').addEventListener('click', () => {
  api.quit();
});

window.addEventListener('keydown', (event) => {
  if (event.key === 'Escape') api.closeSettings();
});

api.onState((snapshot) => {
  if (!current || pendingPatch === null) render(snapshot);
});

void api.getSnapshot().then(render);
void api.getAppInfo().then((info) => {
  version.textContent = `v${info.version}`;
});
