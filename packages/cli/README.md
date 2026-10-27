# hajimi-pet-cli

哈基米桌宠的命令行安装器：一条命令安装 Windows 桌面宠物。

```bash
npx hajimi-pet-cli install hajimi-desktop-pet
npx hajimi-pet-cli list
npx hajimi-pet-cli update hajimi-desktop-pet
npx hajimi-pet-cli uninstall hajimi-desktop-pet
npx hajimi-pet-cli --help
```

## 特性

- **只依赖 Node 内置模块**，零第三方依赖
- 桌宠本体是约 **671 KB 的原生 Windows exe**，随本包分发，安装过程**不联网**
- 安装到 `%LOCALAPPDATA%\HajimiPet\<id>\<版本>`，创建开始菜单快捷方式（可选桌面 / 开机自启）
- 默认只做「复制文件 + 创建快捷方式」，**不执行任何来源不明的脚本**
- 桌宠本体不需要 Node.js，只有本安装器需要

## 命令与选项

| 命令 | 说明 |
| --- | --- |
| `install [id]` | 安装，默认 `hajimi-desktop-pet` |
| `update [id]` | 用当前包携带的版本覆盖安装，并清理旧版本目录 |
| `list` | 列出已安装桌宠、版本、路径、快捷方式 |
| `uninstall [id]` | 删除安装目录与快捷方式 |

选项：`--desktop` `--autostart` `--no-shortcut` `--verbose` `-v/--version` `-h/--help`。

环境变量：

| 变量 | 作用 |
| --- | --- |
| `HAJIMI_HOME` | 覆盖安装根目录（测试用） |
| `NO_COLOR` | 关闭颜色输出 |

## 开发

```bash
node --test test/cli.test.mjs    # 12 个端到端用例
npm pack                         # 打 tarball
npx --yes ./hajimi-pet-cli-1.0.0.tgz install   # 本地验证安装
```

测试覆盖：help/version、未知命令与选项、安装落地、PE 头校验、体积上限（< 5MB）、list、update、旧版本清理、卸载清理、重复卸载、未知 ID、包内二进制与 `pets.json` 的 sha256 一致。
