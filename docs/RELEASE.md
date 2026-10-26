# 公开发布说明

当前状态：**GitHub Release 已发布，npm 包待发布。**

- Release：<https://github.com/xtf666666-student/Hajimi/releases/tag/v1.0.0>
- 已上传资产：`HajimiDesktopPet-Portable-1.0.0-x64.exe`、`HajimiDesktopPet-Setup-1.0.0-x64.exe`、
  对应 `.blockmap`、`SHA256SUMS.txt`、`pets.json`、`hajimi-pet-cli-1.0.0.tgz`
- 已验证可用的安装命令（不依赖 npm）：

```bash
npx --yes https://github.com/xtf666666-student/Hajimi/releases/download/v1.0.0/hajimi-pet-cli-1.0.0.tgz install hajimi-desktop-pet
```

- 还差一步：把 CLI 发布到 npm，命令才能简化成 `npx hajimi-pet-cli install hajimi-desktop-pet`

---

## 0. 已经完成并验证（本机 Windows 11 x64）

| 项目 | 结果 |
| --- | --- |
| 应用构建（主进程 + preload + 3 个渲染页面） | ✅ `npm run build` 通过 |
| TypeScript 检查（app + CLI） | ✅ 通过 |
| 应用自检（源码运行） | ✅ 10/10（透明 alpha=0、渲染、IPC、无边框、喂食、气泡动画、设置面板、右键菜单、设置持久化、统计） |
| 应用自检（打包后运行） | ✅ 10/10，证据在 `packages/app/selftest-packaged/` |
| Windows 安装包 / 便携版 | ✅ `HajimiDesktopPet-Setup-1.0.0-x64.exe`、`HajimiDesktopPet-Portable-1.0.0-x64.exe` |
| 便携版实际启动 | ✅ 启动后常驻进程，窗口正常；真实桌面截图确认透明悬浮 |
| CLI 单元/端到端测试 | ✅ 9/9 |
| CLI 用**真实 80.7MB 安装包**跑完整链路 | ✅ 下载 → SHA-256 → 安装 → list → 卸载 |
| 从 GitHub 用 npx 真实安装 | ✅ 公网 URL → 下载 80.7MB → 校验 → 安装 → 开始菜单快捷方式 → 启动 |
| 开始菜单快捷方式 | ✅ WScript.Shell 创建/删除验证通过 |
| GitHub Release v1.0.0 与资产上传 | ✅ 6 个资产全部 HTTP 201 |
| `pets.json` + `SHA256SUMS.txt` | ✅ 由真实产物生成 |

当前校验和（已发布产物）：

```
06f1fe250180956c6ff9a5502c044eb25be9220bcc985a6530bd73da76e1e472  HajimiDesktopPet-Portable-1.0.0-x64.exe
043201bf0882fbcd2eeb1631af21b328f58603979cf6aaae3d30ad6c669b56d0  HajimiDesktopPet-Setup-1.0.0-x64.exe
```

> 重新打包后哈希会变，执行 `npm run manifest` 会刷新 `pets.json` 与 `SHA256SUMS.txt`；然后重新上传资产即可。

---

## 1. 只剩这一步：发布 npm 包

需要你的 npm 账号（Automation Token），本机没有 npm 登录态，所以这一步没有执行。

```bash
cd packages/cli
npm login                 # 或者 npm config set //registry.npmjs.org/:_authToken <token>
npm publish --access public
```

发布后验证：

```bash
npx hajimi-pet-cli --version
npx hajimi-pet-cli install hajimi-desktop-pet
```

包名 `hajimi-pet-cli` 若已被占用，改 `packages/cli/package.json` 的 `name`（`bin` 名称可保持不变），
再同步更新 README 与 Release 说明中的命令。

配置 CI 自动发布：GitHub 仓库 Settings → Secrets and variables → Actions → 新建 `NPM_TOKEN`，
之后每次打 tag，`release` 工作流会在创建 Release 后自动 `npm publish`（未配置则自动跳过）。
