/**
 * dsh-pet-bridge — 给桌宠用的最小会话控制桥。
 *
 * 解决一个问题：DSH 桌面端没有向外部程序开放的会话控制接口，
 * 所以桌宠上的「回复 / 停止」按钮调不动真正的东西。
 *
 * 这个插件挂在 DSH 自己的 web 服务上，暴露两个端点：
 *
 *   POST /pet-bridge/cancel       { sessionId }  取消该会话当前轮
 *   POST /pet-bridge/prompt       { sessionId, text }  发一条用户消息
 *   GET  /pet-bridge/ping                        探活
 *
 * 认证：共享密钥。首次启动时随机生成，写到 $DSH_HOME/pet-bridge.token
 * （仅本用户可读的权限尽力而为）。桌宠读同一个文件，用
 * `Authorization: Bearer <key>` 调用。这样不依赖浏览器 cookie，
 * 也不把 DSH 的 launch token 暴露给第三方。
 *
 * 安全边界：
 *   - 只监听 DSH 已经绑定的 localhost 端口，不额外开端口；
 *   - 只接受带正确 Bearer 的请求，密钥比较用定长比较；
 *   - 只操作 sessionId 指向的会话，不做任何文件读写或命令执行。
 */
import { randomBytes, randomUUID, timingSafeEqual } from "node:crypto";
import { readFileSync, writeFileSync, existsSync, chmodSync } from "node:fs";
import { join } from "node:path";
import { createRequire } from "node:module";
import { pathToFileURL } from "node:url";

export const name = "dsh-pet-bridge";
export const inject = ["webServer", "sessions", "agents"];

const PREFIX = "/pet-bridge";

function tokenPath(home) {
  return join(home ?? process.env.DSH_HOME ?? join(process.env.USERPROFILE ?? ".", ".dsh"),
    "pet-bridge.token");
}

/** 读已有密钥；没有就生成一个。只在本机、仅当前用户可读。 */
function loadOrCreateToken(home, logger) {
  const file = tokenPath(home);
  try {
    if (existsSync(file)) {
      const existing = readFileSync(file, "utf8").trim();
      if (existing.length >= 32) return existing;
    }
  } catch {
    /* 读失败就走重新生成 */
  }

  const token = randomBytes(32).toString("hex");
  try {
    writeFileSync(file, token, { encoding: "utf8", mode: 0o600 });
    try {
      chmodSync(file, 0o600);
    } catch {
      /* Windows 上 chmod 语义有限，失败不影响功能 */
    }
    logger?.info?.(`pet-bridge: 已生成访问密钥 ${file}`);
  } catch (err) {
    logger?.warn?.(`pet-bridge: 无法写入密钥文件 ${file}: ${err?.message ?? err}`);
  }
  return token;
}

function safeEqual(a, b) {
  const ba = Buffer.from(String(a));
  const bb = Buffer.from(String(b));
  if (ba.length !== bb.length) return false;
  try {
    return timingSafeEqual(ba, bb);
  } catch {
    return false;
  }
}

function readBody(req, limit = 64 * 1024) {
  return new Promise((resolve, reject) => {
    let size = 0;
    const chunks = [];
    req.on("data", (c) => {
      size += c.length;
      if (size > limit) {
        reject(new Error("请求体过大"));
        req.destroy();
        return;
      }
      chunks.push(c);
    });
    req.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
    req.on("error", reject);
  });
}

function send(res, status, payload) {
  const body = JSON.stringify(payload);
  res.writeHead(status, {
    "Content-Type": "application/json; charset=utf-8",
    "Cache-Control": "no-store",
    "Content-Length": Buffer.byteLength(body),
  });
  res.end(body);
}

/**
 * 取消某会话当前这一轮。
 *
 * 依据 DSH 自己的实现（@deepseek-ai/dsh-agent 的 archive admission）：
 *   if (agent?.status === "running") agent.cancel({ kind: "user" });
 * 以及 @deepseek-ai/dsh-agent-loop 的 cancel(cause, options)：
 *   不传 keepInbox 时清空 inbox 并 abort 当前阶段。
 * 这里完全照用户的停止按钮来做，只在 running 时调用。
 */
async function cancelSession(ctx, sessionId) {
  const tried = [];
  let agent;
  try {
    agent = ctx.agents?.get?.(sessionId);
  } catch (err) {
    tried.push(`agents.get -> ${err?.message ?? err}`);
  }

  if (!agent) {
    tried.push("该会话不在 agents 里（可能已经结束或从未在本进程运行）");
    return { via: null, tried };
  }

  const status = agent.status;
  if (status !== "running") {
    // 不在跑就没有可取消的轮次——对调用方来说这是"目标已达成"
    return { via: null, alreadyIdle: true, status };
  }

  try {
    if (typeof agent.cancel !== "function") {
      tried.push("agent 上没有 cancel 方法");
      return { via: null, tried };
    }
    await agent.cancel({ kind: "user" });
    return { via: "agent.cancel({kind:'user'})", status };
  } catch (err) {
    tried.push(`agent.cancel -> ${err?.message ?? err}`);
    return { via: null, tried };
  }
}

/**
 * 解析 createUserMessage。
 *
 * 插件装在 profile 目录下，而 DSH 的包在 app 目录下，普通的 import 解析不到。
 * 所以按「已知的 app 根目录 → 运行时给的线索 → 直接 import → 等价兜底」依次尝试。
 */
async function resolveCreateUserMessage(ctx) {
  const req = createRequire(import.meta.url);
  const bases = [];

  // DSH 桌面端把 app 放在 exe 同级的 resources\app\dsh
  for (const guess of [
    process.env.DSH_APP_ROOT,
    process.env.DSH_RESOURCES_APP,
    join(process.env.LOCALAPPDATA ?? "", "..", "Programs", "DeepSeek Harness", "resources", "app", "dsh"),
  ]) {
    if (typeof guess === "string" && guess.length > 4) bases.push(guess);
  }
  // 运行时可执行文件旁边就是 app
  try {
    const exeDir = join(process.execPath, "..");
    bases.push(join(exeDir, "resources", "app", "dsh"));
    bases.push(join(exeDir, "files", "resources", "app", "dsh"));
  } catch {
    /* ignore */
  }
  for (const probe of [ctx?.baseDir, ctx?.get?.("appRoot"), process.cwd()]) {
    if (typeof probe === "string" && probe.length > 0) bases.push(probe);
  }

  for (const base of bases) {
    try {
      const resolved = req.resolve("@deepseek-ai/dsh-llm", { paths: [base] });
      const ns = await import(pathToFileURL(resolved).href);
      if (typeof ns.createUserMessage === "function") return ns.createUserMessage;
    } catch {
      /* 试下一个 */
    }
  }

  try {
    const ns = await import("@deepseek-ai/dsh-llm");
    if (typeof ns.createUserMessage === "function") return ns.createUserMessage;
  } catch {
    /* 继续 */
  }

  // 兜底：DSH 的 createUserMessage 只做 structuredClone + 补 role/id，
  // 纯文本场景下这样构造与它等价（见 @deepseek-ai/dsh-llm 的 createMessage）。
  return (input) =>
    Object.freeze({
      ...structuredClone(input),
      role: "user",
      id: randomUUID(),
    });
}

/**
 * 往会话里发一条用户消息。
 *
 * 照 DSH 自己的 prompt 路径（@deepseek-ai/dsh-api-session-controller）：
 *   const message = createUserMessage({ content, source });
 *   agent.followup(message);
 *
 * 这里不 import 包，而是从运行时的 ctx 上取同一批服务，
 * 这样既能拿到 attachments 的规范化，又不受插件所在目录的模块解析限制。
 */
async function promptSession(ctx, sessionId, text) {
  const agent = ctx.agents?.get?.(sessionId);
  if (!agent) return { ok: false, reason: "会话不在运行，无法发送" };

  // 从运行环境里找 createUserMessage：优先 llm 服务，其次模块解析
  const createUserMessage = await resolveCreateUserMessage(ctx);
  if (!createUserMessage) {
    return { ok: false, reason: "拿不到 createUserMessage（dsh-llm 不可达）" };
  }

  // 文本内容；attachments 可选（纯文本不需要附件引用）
  const content = [{ type: "text", text }];

  let message;
  try {
    message = createUserMessage({ content, source: { kind: "user" } });
  } catch (err) {
    return { ok: false, reason: `createUserMessage 失败: ${err?.message ?? err}` };
  }

  if (typeof agent.followup === "function") {
    await agent.followup(message);
    return { ok: true, via: "agent.followup" };
  }
  if (typeof agent.steer === "function") {
    await agent.steer(message);
    return { ok: true, via: "agent.steer" };
  }
  return { ok: false, reason: "agent 上没有 followup/steer" };
}

export function apply(ctx, config = {}) {
  const logger = ctx.logger ?? ctx.get?.("logger");
  const token = config.token ?? loadOrCreateToken(config.home ?? ctx.get?.("dshHome"), logger);

  ctx.effect(() =>
    ctx.webServer.register({
      kind: "prefix",
      path: PREFIX,
      handler: async (req, res) => {
        // ---- 认证：共享密钥（不依赖浏览器 cookie）
        const auth = req.headers.authorization ?? "";
        const m = /^Bearer\s+(.+)$/i.exec(auth);
        if (!m || !safeEqual(m[1].trim(), token)) {
          return send(res, 401, {
            ok: false,
            code: "unauthorized",
            error: `需要 Authorization: Bearer <${tokenPath(config.home)}> 里的密钥`,
          });
        }

        const url = new URL(req.url ?? "/", "http://localhost");
        const path = url.pathname.startsWith(PREFIX)
          ? url.pathname.slice(PREFIX.length) || "/"
          : "/";

        try {
          if (req.method === "GET" && path === "/ping") {
            return send(res, 200, { ok: true, bridge: name, version: "1.0.0" });
          }

          // 诊断：列出某个会话的 agent/session 上可用的方法名。
          // 不做任何修改，只读，方便确认这个 DSH 版本到底叫什么。
          if (req.method === "GET" && path === "/inspect") {
            const sid = url.searchParams.get("sessionId") ?? "";
            const describe = (obj) => {
              if (!obj || typeof obj !== "object") return null;
              const methods = [];
              let o = obj;
              let depth = 0;
              while (o && o !== Object.prototype && depth < 4) {
                for (const k of Object.getOwnPropertyNames(o)) {
                  if (k === "constructor") continue;
                  try {
                    if (typeof obj[k] === "function" && !methods.includes(k)) methods.push(k);
                  } catch {
                    /* getter 抛错就跳过 */
                  }
                }
                o = Object.getPrototypeOf(o);
                depth += 1;
              }
              return methods;
            };
            const agent = ctx.agents?.get?.(sid);
            const sess = ctx.sessions?.get?.(sid);
            return send(res, 200, {
              ok: true,
              sessionId: sid,
              hasAgent: Boolean(agent),
              hasSession: Boolean(sess),
              agentMethods: describe(agent),
              sessionMethods: describe(sess),
              agentKeys: agent && typeof agent === "object" ? Object.keys(agent) : null,
              sessionKeys: sess && typeof sess === "object" ? Object.keys(sess) : null,
            });
          }

          if (req.method !== "POST") {
            return send(res, 405, { ok: false, error: "只接受 POST" });
          }

          const raw = await readBody(req);
          let body;
          try {
            body = raw.trim() === "" ? {} : JSON.parse(raw);
          } catch {
            return send(res, 400, { ok: false, error: "请求体不是合法 JSON" });
          }

          const sessionId = typeof body.sessionId === "string" ? body.sessionId.trim() : "";
          if (!sessionId) {
            return send(res, 400, { ok: false, error: "缺少 sessionId" });
          }

          if (path === "/cancel") {
            const result = await cancelSession(ctx, sessionId);
            if (result.via) {
              return send(res, 200, { ok: true, sessionId, via: result.via, status: result.status });
            }
            if (result.alreadyIdle) {
              // 没在跑 = 没有可停的，对调用方等价于"已停止"
              return send(res, 200, {
                ok: true,
                sessionId,
                alreadyIdle: true,
                status: result.status,
                note: "该会话当前没有在运行的轮次",
              });
            }
            return send(res, 409, {
              ok: false,
              sessionId,
              error: "无法取消：会话不在本进程运行",
              tried: result.tried,
            });
          }

          if (path === "/prompt") {
            const text = typeof body.text === "string" ? body.text : "";
            if (!text.trim()) {
              return send(res, 400, { ok: false, error: "缺少 text" });
            }
            const r = await promptSession(ctx, sessionId, text);
            return send(res, r.ok ? 200 : 409, { ok: r.ok, sessionId, ...r });
          }

          return send(res, 404, { ok: false, error: "未知路径" });
        } catch (err) {
          logger?.warn?.(`pet-bridge: ${err?.stack ?? err}`);
          return send(res, 500, {
            ok: false,
            error: String(err?.message ?? err),
            // 前几行堆栈直接回给调用方，省得到日志里翻
            stack: String(err?.stack ?? "").split("\n").slice(0, 6).join(" | "),
          });
        }
      },
    }),
  );

  logger?.info?.(`pet-bridge: 已挂载 ${PREFIX}，密钥 ${tokenPath(config.home)}`);
}
