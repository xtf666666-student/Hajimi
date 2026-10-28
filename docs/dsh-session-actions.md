# DSH 会话操作能力排查（回复 / 停止）

结论：**当前 DSH 桌面端没有向外部程序开放「发送消息到会话」和「取消运行中任务」的能力。**
所以桌宠上真正生效的「回复」「停止」按钮做不出来 —— 不是没写，是没有可用入口。

## 查证过程

### 1. 能力本身是存在的

`resources/app.asar` 里能找到 ACP 协议定义：

```ts
/** Cancels the session's active turn. */
export interface SessionInputParamAgentSessionInputCancel {
  type: 'agent.session.input.cancel';
}
```

RPC 方法名也在：`session/prompt`（发消息并开一轮）、`session/cancel`（取消当前轮）。

**结论：协议层支持，缺的是对外开放的入口。**

### 2. 桌面端只监听两个端口

| 端口 | 路径 | 实测结果 | 性质 |
| --- | --- | --- | --- |
| 8326 | `/` `/acp` `/ws` `/health` | 全部 `401` + `Missing or invalid API key` | OpenAI 兼容**模型网关**，不是会话控制面 |
| 61922 | `/` | `401 dsh web authentication required` | Web UI，需要启动时打印的 launch token |
| 61922 | `/acp` `/ws` | `404` | **没有 ACP 端点** |

### 3. 拿不到 61922 的 token

token 只在 `dsh web` 启动时打印到它自己的 stdout，并写入浏览器 cookie。
桌面应用 fork 的子进程收走了这段输出，没有落到任何文件；
`%USERPROFILE%\.dsh` 下也没有 web token 文件。

**外部程序无法自证身份** —— 即使协议存在也调不到。

### 4. 其它路径也都试过

| 路径 | 结果 |
| --- | --- |
| `dsh://` 深链接 | 只有 `dsh://open`，仅聚焦窗口 |
| Electron 命令行参数 | desktop-host `cli.js` 中 `session`/`resume`/`argv` 出现 0 次 |
| UI Automation | 只能看到窗口三个按钮；Chromium 无障碍树未暴露内部元素，重试 8 次无变化 |
| 远程调试 | asar 中 `remote-debugging-port` 出现 0 次 |
| 直接写会话缓存 | `next-turn`/`next-step` inbox 是运行时内存态，写文件不会被运行中的会话消费 |
| ACP over WebSocket | 端点不存在或需要 token，均被拒 |

## 桌宠实际做了什么

会话行上的两个按钮保留为**真实可用的替代动作**，不是假装：

| 按钮 | 实际行为 |
| --- | --- |
| **回复** | 唤出 DSH，并把该会话标题复制到剪贴板 —— 你按 Ctrl+K 粘贴即可定位并输入 |
| **停止** | 唤出 DSH 并提示：需要在该会话里点停止。桌宠不伪造"已停止" |

这两条都能真实执行，且不会误导你以为任务已经停了。

## 如果以后要真正实现

需要 DSH 端提供以下任一项，桌宠这边可以立刻接上：

1. **本地 RPC 端点**（带握手 token），暴露 `session/prompt` 与 `session/cancel`
2. **命令行入口**：`DeepSeek Harness.exe --session <id> --prompt "..."` / `--cancel <id>`
3. **深链接**：`dsh://session/<id>/prompt?text=...`
4. **把 token 落到文件**（如 `~/.dsh/web-token`）并文档化 `/acp` 的 WS 用法

## 复现命令

```bash
python tools/probe_dsh_ports.py     # 端口与端点探测
python tools/scan_acp_auth.py       # 协议定义与认证实现
python tools/scan_actions.py        # prompt / cancel 事件名
```

结果在 `tools/out/`。
