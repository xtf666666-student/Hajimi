# 公开发布说明

这份文档只描述「从当前仓库到别人能 `npx` 装上」还差哪几步，以及每一步怎么验证。
**下面第 0 节是已经在本机真实完成并验证过的部分，第 1 节开始需要你的 GitHub / npm 凭据。**

---

## 0. 已经完成并验证（本机 Windows 11 x64）

| 项目 | 结果 |
| --- | --- |
| 应用构建（主进程 + preload + 3 个渲染页面） | ✅ `npm run build` 通过 |
| TypeScript 检查（app + CLI） | ✅ 通过 |
| 应用自检（源码运行） | ✅ 10/10（透明 alpha=0、渲染、IPC、无边框、喂食、气泡动画、设置面板、右键菜单、设置持久化、统计） |
| 应用自检（打包后运行） | ✅ 10/10，证据在 `packages/app/selftest-packaged/` |
| Windows 安装包 / 便携版 | ✅ `HajimiDesktopPet-Setup-1.0.0-x64.exe`、`HajimiDesktopPet-Portable-1.0.0-x64.exe` |
| 便携版实际启动 | ✅ 启动后 7 个进程常驻（主进程 + 渲染 + GPU），窗口正常 |
| CLI 单元/端到端测试 | ✅ 9/9 |
| CLI 用**真实 80.7MB 安装包**跑完整链路 | ✅ 下载 → SHA-256 → 安装 → list → 卸载 |
| 开始菜单快捷方式 | ✅ WScript.Shell 创建/删除验证通过 |
| `pets.json` + `SHA256SUMS.txt` | ✅ 由真实产物生成 |

当前校验和（本地构建产物）：

```
06f1fe250180956c6ff9a5502c044eb25be9220bcc985a6530bd73da76e1e472  HajimiDesktopPet-Portable-1.0.0-x64.exe
043201bf0882fbcd2eeb1631af21b328f58603979cf6aaae3d30ad6c669b56d0  HajimiDesktopPet-Setup-1.0.0-x64.exe
```

> 重新打包后哈希会变，执行 `npm run manifest` 会刷新 `pets.json` 与 `SHA256SUMS.txt`。

---

## 1. 需要你提供的凭据（最小集）

| 用途 | 需要什么 | 怎么给 |
| --- | --- | --- |
| 推送代码到 GitHub | GitHub 账号登录（HTTPS PAT 或 SSH key）| 本机执行一次 `gh auth login` 或配置 PAT |
| 创建 Release / 上传安装包 | 上面的 GitHub 凭据即可（workflow 用 `GITHUB_TOKEN`）| 无需额外配置 |
| 发布 npm 包 | npm 账号 + Automation Token | 在 GitHub 仓库 Settings → Secrets 添加 `NPM_TOKEN` |

**不要把这些令牌写进仓库或代码里**，工作流只从 `secrets.NPM_TOKEN` 读取。

---

## 2. 第一次公开发布步骤

```bash
# 1) 推送到你提供的仓库
git remote add origin https://github.com/xtf666666-student/Hajimi.git
git push -u origin main

# 2) 打 tag 触发自动构建 + 发布
git tag v1.0.0
git push origin v1.0.0
```

之后 GitHub Actions 的 `release` 工作流会自动：

1. `npm ci` → typecheck → 构建
2. 跑应用自检（截图作为 artifact）
3. 打 `Setup` 与 `Portable` 两个 exe
4. 跑 CLI 测试
5. 用真实产物生成 `pets.json` + `SHA256SUMS.txt`
6. `verify-release.mjs` 端到端校验（失败则整个发布中止）
7. 创建 GitHub Release 并上传 exe / blockmap / SHA256SUMS.txt / pets.json
8. 如果配置了 `NPM_TOKEN`，发布 `hajimi-pet-cli`

### 手动发布 npm（如果不想用 CI）

```bash
cd packages/cli
npm login
npm publish --access public
```

发布前确认 `packages/cli/pets.json` 里的 URL 与 sha256 已经是**真实 Release 资产**的值：

```bash
node scripts/make-manifest.mjs --version 1.0.0
```

---

## 3. 发布后如何验证「用户真的能装」

在**另一台没装过本项目**的 Windows 机器上：

```bash
npx hajimi-pet-cli install hajimi-desktop-pet
```

预期：

- 终端出现下载进度与 `SHA-256 校验通过`
- 安装到 `%LOCALAPPDATA%\HajimiPet\pets\hajimi-desktop-pet\1.0.0\HajimiDesktopPet.exe`
- 开始菜单出现「哈基米桌宠」，点击即可启动
- `npx hajimi-pet-cli list` 能看到已安装版本
- `npx hajimi-pet-cli update` 提示已是最新
- `npx hajimi-pet-cli uninstall` 能删干净

在本机可以先用本地 HTTP 服务器模拟：

```bash
npm run verify:release
```

---

## 4. 发新版本

```bash
# 1) 改版本号（两处要一致）
#    packages/app/package.json  -> version
#    packages/cli/package.json  -> version
git commit -am "chore: release v1.1.0"
git tag v1.1.0 && git push origin v1.1.0
```

CI 会自动产出新 Release、刷新 `pets.json` 的 `latest` 与 assets，用户执行 `npx hajimi-pet-cli update` 即可升级。旧版本目录会在更新时自动清理。

---

## 5. 已知风险与后续可选项

- **未签名**：SmartScreen 会拦一次「未知发布者」，需要代码签名证书（约 $100+/年）才能消除。
- **SmartScreen / 杀软误报**：桌面宠物常被启发式标记，可考虑提交微软 Defender 误报申诉。
- **npmmirror 镜像**：仓库 `.npmrc` 里配了 Electron / electron-builder 二进制镜像（对国内网络友好）。GitHub Actions 在海外也访问得通；若你的 CI 在海外且想走官方源，删掉 `.npmrc` 里两行镜像配置即可。
- **`signAndEditExecutable: false`**：为了在无管理员权限环境跳过 `winCodeSign` 的 macOS 符号链接解压失败，当前关闭了 exe 元数据编辑与签名。若你有签名证书，可删掉这一行开启完整签名流程。
