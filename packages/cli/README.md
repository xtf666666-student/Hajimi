# hajimi-pet-cli

哈基米桌宠的命令行安装器：一条命令下载、校验并安装 Windows 桌面宠物。

> 尚未发布到 npm 时，可以直接用 GitHub Release 里的 tarball：
> `npx --yes https://github.com/xtf666666-student/Hajimi/releases/download/v1.0.0/hajimi-pet-cli-1.0.0.tgz install hajimi-desktop-pet`

```bash
npx hajimi-pet-cli install hajimi-desktop-pet
npx hajimi-pet-cli list
npx hajimi-pet-cli update hajimi-desktop-pet
npx hajimi-pet-cli uninstall hajimi-desktop-pet
npx hajimi-pet-cli --help
```

## 特性

- 只依赖 Node.js 内置模块，不需要额外 npm 依赖
- 从 GitHub Releases 下载**便携版**（默认）或官方 NSIS 安装包（`--installer`）
- 下载完成后再做 SHA-256 校验，并检查文件是否为 Windows PE（MZ 头），防止把错误页/限流页当成安装包
- 失败自动重试（默认 3 次，指数退避），带进度条
- 安装到 `%LOCALAPPDATA%\HajimiPet\pets\<id>\<版本>`，创建开始菜单快捷方式（可选桌面快捷方式）
- 默认不执行任何下载得到的脚本：便携版只是复制文件 + 建快捷方式
- 注册表（`pets.json`）远端优先、内置兜底，离线也能 `list` / `uninstall`

## 命令与选项

| 命令 | 说明 |
| --- | --- |
| `install [id]` | 安装，默认 `hajimi-desktop-pet` |
| `update [id]` | 比较 `latest` 后安装新版本，并清理旧版本目录 |
| `check [id]` | 只检查更新 |
| `list` | 列出已安装桌宠、版本、路径、快捷方式 |
| `uninstall [id]` | 删除安装目录与快捷方式 |

选项：`--release <版本>` `--desktop` `--no-shortcut` `--force` `--installer` `-y/--yes` `--verbose` `-v/--version` `-h/--help`。

环境变量：

| 变量 | 作用 |
| --- | --- |
| `HAJIMI_HOME` | 覆盖安装根目录（测试用） |
| `HAJIMI_REGISTRY_URL` | 覆盖注册表地址，默认仓库根目录的 `pets.json` |
| `NO_COLOR` | 关闭颜色输出 |

## 注册表格式

见仓库根目录 `pets.json`。核心字段：

```json
{
  "pets": [
    {
      "id": "hajimi-desktop-pet",
      "latest": "1.0.0",
      "releases": {
        "1.0.0": {
          "assets": {
            "win32-x64": {
              "portable": { "url": "https://.../HajimiDesktopPet-Portable-1.0.0-x64.exe", "sha256": "..." },
              "installer": { "url": "https://.../HajimiDesktopPet-Setup-1.0.0-x64.exe", "sha256": "..." }
            }
          }
        }
      }
    }
  ]
}
```

`scripts/make-manifest.mjs` 会从真实构建产物生成这个文件（含 sha256），CI 在发布前自动执行。

## 本地开发

```bash
npm run build   # tsc -> dist/
npm test        # node:test 端到端用例（本地 HTTP 服务器模拟 Releases）
```

测试覆盖：help/version、未知命令、真实下载安装、重复安装跳过、更新并清理旧版本、SHA-256 不匹配拒绝、非 PE 文件拒绝、卸载清理、注册表回退。
