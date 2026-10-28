/**
 * 桌宠侧调用 dsh-pet-bridge。
 *
 * 桥接是装在 DSH 里的插件（见 profiles/desktop/node_modules/dsh-pet-bridge），
 * 挂在 DSH 自己的 web 端口上，用共享密钥认证。
 *
 * 端口问题：DSH 每次启动都会换端口（实测 61922 -> 60061 -> 55710 -> 52062），
 * 所以不能硬编码。解析顺序：
 *   1. 插件写下的端点文件 $DSH_HOME/pet-bridge.endpoint
 *   2. 环境变量 DSH_WEB_URL
 *   3. 扫描 DSH 进程实际监听的本地端口，逐个探 /ping
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HajimiPet
{
    internal static class PetBridge
    {
        private static string _cachedBase;
        private static DateTime _cacheTime = DateTime.MinValue;

        private static string DshHome()
        {
            string home = Environment.GetEnvironmentVariable("DSH_HOME");
            if (string.IsNullOrEmpty(home))
            {
                home = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            }
            return home;
        }

        private static string TokenFile() { return Path.Combine(DshHome(), "pet-bridge.token"); }

        private static string EndpointFile() { return Path.Combine(DshHome(), "pet-bridge.endpoint"); }

        /// <summary>桥接是否已就绪（插件装了、密钥在）。</summary>
        public static bool Available(out string reason)
        {
            reason = null;
            if (!File.Exists(TokenFile()))
            {
                reason = "未找到 pet-bridge.token（桥接插件未安装或未启动）";
                return false;
            }
            try
            {
                if (ReadToken().Length < 32)
                {
                    reason = "pet-bridge.token 内容异常";
                    return false;
                }
            }
            catch (Exception ex)
            {
                reason = "读取密钥失败：" + ex.Message;
                return false;
            }
            return true;
        }

        private static string ReadToken()
        {
            return File.ReadAllText(TokenFile(), Encoding.UTF8).Trim();
        }

        /// <summary>
        /// 解析当前 DSH web 地址。结果缓存 5 秒，避免每次点击都重新探测。
        /// </summary>
        private static string ResolveBase()
        {
            if (_cachedBase != null && (DateTime.UtcNow - _cacheTime).TotalSeconds < 5)
            {
                return _cachedBase;
            }

            // 1) 插件写的端点文件
            try
            {
                string file = EndpointFile();
                if (File.Exists(file))
                {
                    string json = File.ReadAllText(file, Encoding.UTF8);
                    string url = ExtractJsonString(json, "url");
                    if (!string.IsNullOrEmpty(url) && PingOk(url))
                    {
                        _cachedBase = url;
                        _cacheTime = DateTime.UtcNow;
                        return url;
                    }
                }
            }
            catch
            {
                /* 落到下一种方式 */
            }

            // 2) 环境变量
            string env = Environment.GetEnvironmentVariable("DSH_WEB_URL");
            if (!string.IsNullOrEmpty(env))
            {
                string url = env.TrimEnd('/');
                if (PingOk(url))
                {
                    _cachedBase = url;
                    _cacheTime = DateTime.UtcNow;
                    return url;
                }
            }

            // 3) 扫 DSH 进程监听的端口
            foreach (int port in DshListeningPorts())
            {
                string url = "http://127.0.0.1:" + port;
                if (PingOk(url))
                {
                    _cachedBase = url;
                    _cacheTime = DateTime.UtcNow;
                    return url;
                }
            }

            return null;
        }

        private static bool PingOk(string baseUrl)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(baseUrl + "/pet-bridge/ping");
                req.Method = "GET";
                req.Timeout = 1200;
                req.ReadWriteTimeout = 1200;
                req.Headers.Add("Authorization", "Bearer " + ReadToken());
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string body = reader.ReadToEnd();
                    return body.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>找 DSH 主进程监听的本机端口。</summary>
        private static IEnumerable<int> DshListeningPorts()
        {
            var ports = new List<int>();
            try
            {
                var procs = Process.GetProcessesByName("DeepSeek Harness");
                if (procs.Length == 0) return ports;

                var sb = new StringBuilder();
                sb.Append("$pids=@(");
                for (int i = 0; i < procs.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(procs[i].Id);
                }
                sb.Append("); ");
                sb.Append("Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | ");
                sb.Append("Where-Object { $pids -contains $_.OwningProcess -and $_.LocalAddress -eq '127.0.0.1' } | ");
                sb.Append("Select-Object -ExpandProperty LocalPort -Unique");

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -Command \"" + sb + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    foreach (string line in output.Split('\n'))
                    {
                        int port;
                        if (int.TryParse(line.Trim(), out port) && port > 0) ports.Add(port);
                    }
                }
            }
            catch
            {
                /* 探测失败就返回空，由调用方报"连不上" */
            }
            return ports;
        }

        private static string ExtractJsonString(string json, string key)
        {
            string needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i + needle.Length);
            if (i < 0) return null;
            i = json.IndexOf('"', i + 1);
            if (i < 0) return null;
            int end = json.IndexOf('"', i + 1);
            return end > i ? json.Substring(i + 1, end - i - 1) : null;
        }

        /// <summary>取消某会话当前这一轮。</summary>
        public static BridgeResult Cancel(string sessionId)
        {
            return Post("/cancel", "{\"sessionId\":" + Json(sessionId) + "}");
        }

        /// <summary>往某会话发一条用户消息。</summary>
        public static BridgeResult Prompt(string sessionId, string text)
        {
            return Post("/prompt",
                "{\"sessionId\":" + Json(sessionId) + ",\"text\":" + Json(text) + "}");
        }

        public struct BridgeResult
        {
            public bool Ok;
            public int Status;
            public string Body;
            public string Error;
        }

        private static BridgeResult Post(string path, string json)
        {
            var result = new BridgeResult();

            string reason;
            if (!Available(out reason))
            {
                result.Error = reason;
                return result;
            }

            string baseUrl = ResolveBase();
            if (baseUrl == null)
            {
                result.Error = "连不上桥接（DSH 未运行或插件未加载）";
                return result;
            }

            string token;
            try
            {
                token = ReadToken();
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }

            try
            {
                var req = (HttpWebRequest)WebRequest.Create(baseUrl + "/pet-bridge" + path);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Headers.Add("Authorization", "Bearer " + token);
                req.Timeout = 5000;
                req.ReadWriteTimeout = 5000;

                byte[] payload = Encoding.UTF8.GetBytes(json);
                req.ContentLength = payload.Length;
                using (Stream s = req.GetRequestStream())
                {
                    s.Write(payload, 0, payload.Length);
                }

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    result.Status = (int)resp.StatusCode;
                    using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        result.Body = reader.ReadToEnd();
                    }
                    result.Ok = result.Status >= 200 && result.Status < 300
                                && result.Body.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0;
                    if (!result.Ok) result.Error = ExtractError(result.Body);
                    return result;
                }
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp != null)
                {
                    result.Status = (int)resp.StatusCode;
                    using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        result.Body = reader.ReadToEnd();
                    }
                    result.Error = ExtractError(result.Body);
                }
                else
                {
                    result.Error = "连不上桥接（" + ex.Message + "）";
                    // 端口可能又变了，下次重新解析
                    _cachedBase = null;
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                _cachedBase = null;
            }
            return result;
        }

        private static string ExtractError(string body)
        {
            if (string.IsNullOrEmpty(body)) return "（空响应）";
            string e = ExtractJsonString(body, "error");
            if (!string.IsNullOrEmpty(e)) return e;
            return body.Length > 200 ? body.Substring(0, 200) : body;
        }

        /// <summary>最小 JSON 字符串转义；只处理会话 ID 与用户文本。</summary>
        private static string Json(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
