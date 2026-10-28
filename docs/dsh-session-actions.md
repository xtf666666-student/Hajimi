# DSH 会话操作能力

桌宠上的「回复」和「停止」按钮**真的生效**了。做法不是模拟，而是给 DSH 装一个插件，
把 DSH 内部本来就有的会话控制接口开出来。

## 结论

| 按钮 | 行为 | 依据 |
| --- | --- | --- |
| **■ 停止** | 真的取消该会话当前轮 | `agent.cancel({ kind: "user" })` |
| **↩ 回复** | 真的把消息送进该会话 | `createUserMessage(...)` → `agent.followup(...)` |

这两条是 DSH **自己的代码路径**，和用户点停止、在输入框发消息完全一致。

## 怎么做到的

装一个小插件 `dsh-pet-bridge`（见 [packages/dsh-pet-bridge](../packages/dsh-pet-bridge/README.md)），
挂在 DSH 已经在监听的 web 端口上：

```
GET  /pet-bridge/ping
POST /pet-bridge/cancel   { sessionId }
POST /pet-bridge/prompt   { sessionId, text }
```

认证用共享密钥 `~/.dsh/pet-bridge.token`，不碰 DSH 的 launch token。

## 之前为什么做不到

DSH 桌面端不对外提供会话控制面：

| 端口/路径 | 实测 | 性质 |
| --- | --- | --- |
| `8326` | `401 Missing or invalid API key` | OpenAI 兼容**模型网关** |
| web 端口 `/` | `401 dsh web authentication required` | Web UI，要 launch token |
| web 端口 `/acp`、`/ws` | `404` | 没有 ACP 端点 |
| `dsh://` | 只有 `dsh://open` | 仅聚焦窗口 |
| 命令行 | 无 `--session` / `--cancel` | — |
| UI Automation | 只看得到窗口三按钮 | Chromium 树未暴露 |
| 远程调试 | asar 中 0 次出现 | 从未开启 |

协议层（ACP `session/prompt` / `session/cancel`）是存在的，缺的只是一个
**可认证的对外入口**。插件补的就是这一层。

## 关键代码证据

`@deepseek-ai/dsh-agent/lib/index.js`：

```js
ctx.on("workspace/session-stop", ({ sessionId }) => {
  const agent = lookup(sessionId);
  if (agent?.status === "running") agent.cancel({ kind: "user" });
});
```

`@deepseek-ai/dsh-agent-loop/lib/index.js`：

```js
cancel(cause, options = {}) {
  if (!options.keepInbox) {
    this.inbox.clear();
    if (this.phase.kind !== "idle") this.phase.wakeRequested = false;
  }
  if (this.phase.kind !== "idle") this.phase.abort.abort(cause);
}
```

`@deepseek-ai/dsh-api-session-controller/lib/index.js`：

```js
const message = createUserMessage({
  content: await this.ctx.attachments.admitPromptContent(admission.content),
  source
});
...
agent.followup(message);
```

## 实测结果

桥接（`python tools/test_bridge.py`，6/6）：

- `GET /ping` → 200 `{ok:true, bridge:"dsh-pet-bridge"}`
- 无密钥 → 401；错误密钥 → 401
- 取消不存在的会话 → 409（不崩）
- 缺 `sessionId` → 400

桌宠端到端（`python tools/test_pet_bridge.py`，4/4）：

- `token-ok ping-ok cancel-409`
- 说明桌宠能读密钥、连上桥接、并拿到可识别的结果

运行时自省（`GET /pet-bridge/inspect`）确认该版本 agent 上确实有
`cancel`、`send`、`followup`、`steer`、`inject`、`whenIdle`。

## 依赖与限制

- 插件随 DSH 启动加载，**改动插件代码要重启 DSH**（`dsh-client-hmr` 只管客户端热更新）。
- web 端口每次启动会变（实测 61922 → 60061 → 55710），桌宠优先读 `DSH_WEB_URL`，
  否则探测 DSH 进程实际监听端口。
- 只操作内存里的 agent：会话不在本进程运行（已关闭/归档）时返回 409，
  不会去改会话文件。

## 复现

```bash
python tools/test_bridge.py
python tools/test_pet_bridge.py
```

插件安装方式见 [packages/dsh-pet-bridge/README.md](../packages/dsh-pet-bridge/README.md)。
