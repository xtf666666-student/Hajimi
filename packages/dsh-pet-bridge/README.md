# dsh-pet-bridge

给桌面宠物用的**最小会话控制桥**。装在 DeepSeek Harness 里，让外部程序（桌宠）能真正
**取消** 和 **回复** 某个正在运行的会话，而不是只能"帮你把窗口叫出来"。

## 为什么需要它

DSH 桌面端默认不向外部程序开放会话控制：

- `8326` 是 OpenAI 兼容模型网关（要 API key），不是会话控制面；
- `61922` / 随机端口是 Web UI，`/acp` 与 `/ws` 都是 404，且 launch token 只发到浏览器 cookie；
- `dsh://` 只有 `dsh://open`（聚焦窗口）；命令行没有 `--session` / `--cancel`；
- UI Automation 看不到内部元素；远程调试端口从未开启。

协议层其实**有** `session/prompt` 和 `session/cancel`，缺的只是一个对外的、可认证的入口。
这个插件就是把那个入口补上——它不是模拟，走的是 DSH 自己的代码路径。

## 它做什么

挂在 DSH 已经监听的 web 端口上，提供三个端点：

| 方法 | 路径 | 作用 |
| --- | --- | --- |
| `GET` | `/pet-bridge/ping` | 探活 |
| `POST` | `/pet-bridge/cancel` | 取消该会话当前轮 |
| `POST` | `/pet-bridge/prompt` | 往该会话发一条用户消息 |

全部需要 `Authorization: Bearer <key>`。

## 认证

首次加载时生成 32 字节随机密钥，写到：

```
$DSH_HOME/pet-bridge.token        # 默认 ~/.dsh/pet-bridge.token
```

（`0600`，尽力而为；Windows 上 chmod 语义有限。）比较用定长比较，避免时序侧信道。

桌宠读同一个文件，所以**不需要** DSH 的 launch token，也不依赖浏览器 cookie。

## 它调的是 DSH 自己的接口

取消照的是 DSH 内部的 archive admission（`@deepseek-ai/dsh-agent`）：

```js
if (agent?.status === "running") agent.cancel({ kind: "user" });
```

这和用户点停止按钮是**同一条路径**（`@deepseek-ai/dsh-agent-loop` 的 `cancel(cause, options)`：
清空 inbox 并 abort 当前阶段）。

发送照的是 `@deepseek-ai/dsh-api-session-controller` 的 prompt 路径：

```js
const message = createUserMessage({ content, source });
agent.followup(message);
```

## 响应语义

`/cancel`：

| 情况 | HTTP | 响应 |
| --- | --- | --- |
| 真的调到了 cancel | 200 | `{ok:true, via:"agent.cancel({kind:'user'})"}` |
| 会话没在跑 | 200 | `{ok:true, alreadyIdle:true}` |
| 会话不在本进程 | 409 | `{ok:false, error, tried:[...]}` |

「没在跑」返回 200 是因为对调用方来说目标已达成，避免桌宠误报"停止失败"。

`/prompt`：成功 200 `{ok:true, via}`；会话不在运行 409。

## 安装

插件目录放好后，在 DSH profile 的 `package.json` 里加两处：

```json
{
  "dsh": { "profile": { "bundles": [ "...", "dsh-pet-bridge" ] } },
  "dependencies": { "dsh-pet-bridge": "file:./node_modules/dsh-pet-bridge" }
}
```

然后**重启 DSH**。插件代码改动不会靠 HMR 重载——`dsh-client-hmr` 只管客户端。

## 安全边界

- 不额外开端口，只挂 DSH 已在监听的地址；
- 只接受带正确 Bearer 的请求，其余一律 401；
- 只按 `sessionId` 操作内存里的 agent，不读写文件、不执行命令；
- 密钥泄漏的影响面限于"能取消/发送自己本机会话"，且文件就在用户家目录下。

## 验证

```bash
python tools/test_bridge.py        # 端点、认证、错误分支
python tools/test_pet_bridge.py    # 桌宠进程到桥接的端到端
```

实测（Windows 11 / DSH desktop）：

- `GET /ping` → 200
- 无密钥 / 错密钥 → 401
- 取消不存在的会话 → 409，进程不崩
- 缺 `sessionId` → 400
- 桌宠侧：`token-ok ping-ok cancel-409`
