# DSH 会话跳转能力排查结论

排查对象：`D:\soft\deepseek-harness\files\resources\app.asar`（182 MB）与
`resources\app\dsh\node_modules\@deepseek-ai\dsh-desktop-host\lib\cli.js`

## 结论：目前没有可用的「打开指定会话」入口

| 疑似入口 | 实际情况 | 能否用来跳转会话 |
| --- | --- | --- |
| `dsh://` 协议 | 只注册了 `dsh://open` 与 `dsh://open/`，都只做 `focusPrimaryWindow()` | 不能，只是聚焦窗口 |
| `dsh://session/<id>` | asar 全文无此模式 | 不存在 |
| `?sessionId=` query | 只在 Host 的文件上传 HTTP handler 里用（`handleFileUploadHttp`），要求 POST + `application/octet-stream` | 不能，语义是上传 |
| `searchParams.get("sessionId")`（客户端） | 同上，服务于上传通道 | 不能 |
| 客户端 URL 路由 | 无 hash 路由、无 `pushState` 会话路径；`/s/:id`、`/session/:id` 均不存在 | 不能 |
| Electron 命令行参数 | desktop-host `cli.js` 中 `session` / `resume` / `argv` 出现次数均为 0 | 不能 |
| `dsh tui --resume <id>` | 属于 tui profile（终端应用），桌面 GUI 不消费此参数 | 不能（仅终端可用） |
| UI Automation | 只能看到窗口三个按钮（最小化/恢复/关闭），Chromium 无障碍树未暴露内部元素，重试 8 次无变化 | 不能 |
| 远程调试 | asar 中 `remote-debugging-port` 出现 0 次 | 不能 |
| `dsh.sessions.current` | 客户端 store 的持久化键（`persist: { name: ... }`），记录当前选中会话 | 只读数据，不是入口 |
| `Ctrl+K` / `Meta+K` | 命中 DevTools 的 `console.clear`、`sources.*` 等 actionId，与业务会话无关 | 不能 |

## 因此桌宠能做的与不能做的

**能做的（真实可用）：**

1. 单击桌宠 → `dsh://open` 唤出 DSH 主窗口 —— 这是 DSH 自己注册的协议，可靠
2. 显示正在运行的会话数量 —— 读 `%USERPROFILE%\.dsh\storages\session_projcache\sessions\*.json`
3. 点数字展开正在运行的会话标题 —— 同上数据源
4. 点标题时：唤出 DSH + 把标题写入剪贴板，用户可在 DSH 内粘贴定位

**不能做的：**

- 真正的「点标题直接进入那个会话」。DSH 未提供该能力的任何入口。

如果以后 DSH 增加了下列任一能力，桌宠这边可以立刻接上：

- 注册 `dsh://session/<sessionId>` 并路由到对应会话
- 支持 `DeepSeek Harness.exe --session <id>` 之类的启动参数
- 启用 CDP（`--remote-debugging-port`）或暴露 UI Automation 树

## 复现方式

```bash
python tools/scan_session_entry.py    # 会话入口扫描
python tools/scan_entry_final.py      # sessionId / store key 校验
python tools/scan_ctrlk.py            # 快捷键与 sessionId 语义
python tools/scan_deeplink2.py        # dsh:// 协议注册
```

结果文件在 `tools/out/`。
