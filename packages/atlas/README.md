# dsh-pet-installer

仿 [`codex-pet-installer`](https://www.npmjs.com/package/codex-pet-installer) 实现的桌宠安装器：
把桌宠装成 **一份 `pet.json` + 一张 sprite atlas**，与 Codex 桌宠格式通用。

```sh
npx dsh-pet-installer add hajimi-desktop-pet
npx dsh-pet-installer list
npx dsh-pet-installer remove hajimi-desktop-pet
```

装到：

```sh
%USERPROFILE%\.dsh\pets\<pet-id>\      # Windows
$DSH_HOME/pets/<pet-id>/               # 设置 DSH_HOME 时
```

## 和 codex-pet-installer 的关系

| | codex-pet-installer | 本包 |
| --- | --- | --- |
| 包结构 | `bin` → `src/cli.js` → `src/installer.js` | 相同 |
| 安装产物 | `pet.json` + `spritesheet.webp` | 相同（另加一份 PNG） |
| 安装路径 | `~/.codex/pets/<id>` | `~/.dsh/pets/<id>` |
| 资源来源 | 从 codex-pet.org 拉取 | 随包分发（站点不可达也能装） |
| atlas 规格 | 1536×1872 / 8×9 / 192×208 | 相同 |

codex 版从远端站点取包：

```js
const url = new URL(`/api/pets/${slug}`, siteUrl);
const [manifestBytes, spritesheetBytes] = await Promise.all([
  fetchBytes(manifestUrl, "pet.json"),
  fetchBytes(spritesheetUrl, "spritesheet.webp"),
]);
```

本包把同样的两步换成读本地 `assets/`，其余校验逻辑与产物形状保持一致。

## atlas 规格（来自 Codex 契约）

- 尺寸 **1536 × 1872**，**8 列 × 9 行**，每格 **192 × 208**，透明背景
- 行语义与帧数：

| 行 | 状态 | 帧数 | 首帧/中间/末帧（ms） |
| --- | --- | --- | --- |
| 0 | idle | 6 | 280 / 110 / 320 |
| 1 | running-right | 8 | 120 / 120 / 220 |
| 2 | running-left | 8 | 120 / 120 / 220 |
| 3 | waving | 4 | 140 / 140 / 280 |
| 4 | jumping | 5 | 140 / 140 / 280 |
| 5 | failed | 8 | 140 / 140 / 240 |
| 6 | waiting | 6 | 150 / 150 / 260 |
| 7 | running | 6 | 120 / 120 / 220 |
| 8 | review | 6 | 150 / 150 / 280 |

未被占用的格子必须完全透明。

## pet.json

```json
{
  "id": "hajimi-desktop-pet",
  "displayName": "哈基米桌宠",
  "description": "常驻 Windows 桌面的哈基米猫猫桌宠……",
  "spriteVersionNumber": 2,
  "spritesheetPath": "spritesheet.webp"
}
```

## 为什么同时装一份 PNG

`spritesheetPath` 按契约指向 `.webp`，但 **.NET Framework 的 GDI+ 解不了 WebP**。
原生桌宠读取顺序是：

1. 同目录的 `spritesheet.png`（能读就优先）
2. 回落到 `pet.json` 里 `spritesheetPath` 指的文件

所以 WebP 保留给其它宿主，PNG 给原生渲染用。缺 PNG 时桌宠会退回内置的分层素材，
不会崩。

## 桌宠如何用 atlas

`SpriteSheet.cs` 按行选当前状态，按累计毫秒算帧号：

```
有任务在跑 + 鼠标在身上   -> row 7 running
有任务跑完未查看          -> row 8 review
有任务在跑（鼠标不在）    -> row 6 waiting / row 3 waving
其余                      -> row 0 idle
```

atlas 缺失或尺寸不符时，自动退回原有的「躯干 + 双脚绕脚踝旋转」渲染，
老素材路径完全保留。

## 生成 atlas

```sh
python tools/build_atlas.py
```

从 `packages/native/Assets/` 的现有图层合成 9 行。**局限**：素材只有一张正面
机位，各行动作靠位移、缩放、轻微旋转模拟，观感差异有限，不是真正的骨骼动画。

## 测试

```sh
node --test test/installer.test.mjs    # 10 项
```

覆盖：help、未收录包、非法 slug、落地两个文件、PNG/WebP 尺寸契约、list、remove、空列表、缺参数。
