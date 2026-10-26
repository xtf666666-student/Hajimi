import type { MenuAction } from '../../shared/types';

const api = window.hajimi;
const items = Array.from(document.querySelectorAll<HTMLButtonElement>('.item'));

function activate(button: HTMLButtonElement): void {
  const action = button.dataset.action as MenuAction | undefined;
  if (!action) return;
  api.menuAction(action);
}

for (const item of items) {
  item.addEventListener('click', () => activate(item));
}

document.addEventListener('keydown', (event) => {
  const index = items.indexOf(document.activeElement as HTMLButtonElement);
  if (event.key === 'Escape') {
    api.closeMenu();
    return;
  }
  if (event.key === 'ArrowDown') {
    event.preventDefault();
    items[(index + 1 + items.length) % items.length].focus();
  } else if (event.key === 'ArrowUp') {
    event.preventDefault();
    items[(index - 1 + items.length) % items.length].focus();
  } else if (event.key === 'Enter' && index >= 0) {
    event.preventDefault();
    activate(items[index]);
  }
});

window.addEventListener('contextmenu', (event) => event.preventDefault());
items[0]?.focus();
