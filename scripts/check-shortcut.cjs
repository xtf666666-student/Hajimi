const path = require('node:path');
const fs = require('node:fs');
const { execFileSync } = require('node:child_process');
const { createShortcut, removeShortcut } = require('../packages/cli/dist/shortcuts.js');

const lnk = path.join(process.env.TEMP, 'hajimi-link-test.lnk');
createShortcut(lnk, 'C:\\Windows\\System32\\notepad.exe', '', '哈基米快捷方式测试');
console.log('created:', fs.existsSync(lnk), fs.statSync(lnk).size, 'bytes');

const script = `(New-Object -ComObject WScript.Shell).CreateShortcut('${lnk}').TargetPath`;
const target = execFileSync('powershell.exe', ['-NoProfile', '-Command', script], { encoding: 'utf8' });
console.log('target:', target.trim());

removeShortcut(lnk);
console.log('removed:', !fs.existsSync(lnk));
