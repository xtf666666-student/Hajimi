const fs = require('node:fs');

const body = [
  '## 哈基米桌宠 1.0.0',
  '',
  '常驻 Windows 桌面的哈基米猫猫桌宠：无边框真透明窗口、可拖动、会呼吸眨眼、跟着鼠标 3D 转头、摸头喂食、中文吐槽气泡。',
  '',
  '![preview](https://raw.githubusercontent.com/xtf666666-student/Hajimi/main/docs/preview.png)',
  '',
  '### 安装（任选其一）',
  '',
  '```bash',
  '# 方式 A：直接用 GitHub 上的 CLI（无需 npm）',
  'npx --yes https://github.com/xtf666666-student/Hajimi/releases/download/v1.0.0/hajimi-pet-cli-1.0.0.tgz install hajimi-desktop-pet',
  '',
  '# 方式 B：下载便携版直接双击运行',
  'HajimiDesktopPet-Portable-1.0.0-x64.exe',
  '```',
  '',
  '### 校验',
  '',
  '`SHA256SUMS.txt` 提供两个 exe 的 SHA-256，CLI 安装时会强制校验。',
  '',
  '### 说明',
  '',
  '- 应用未做代码签名，SmartScreen 可能提示「未知发布者」',
  '- 安装包与 CLI 均在 Windows 11 x64 上实际构建并验证',
].join('\n');

fs.writeFileSync(
  'release-payload.json',
  JSON.stringify(
    { tag_name: 'v1.0.0', name: '哈基米桌宠 1.0.0', body, draft: false, prerelease: false },
    null,
    2,
  ),
  'utf8',
);
console.log('payload bytes', fs.statSync('release-payload.json').size);
