/**
 * 桌宠侧调用 dsh-pet-bridge。
 *
 * 桥接是装在 DSH 里的插件（见 profiles/desktop/node_modules/dsh-pet-bridge），
 * 挂在 DSH 自己的 web 端口上，用共享密钥认证。密钥在
 * $DSH_HOME/pet-bridge.token，只同意本机调用。
 */
using System;
using System.IO;
using System.Net;
using System.Text;

namespace HajimiPet
{
    internal static class PetBridge
    {
        /// <summary>DSH web 端口的候选值：环境变量优先，然后是桌面端实际端口。</summary>
        private static string[] CandidateBases()
        {
            string fromEnv = Environment.GetEnvironmentVariable("DSH_WEB_URL");
            if (!string.IsNullOrEmpty(fromEnv))
            {
                return new[] { fromEnv.TrimEnd('/') };
            }
            return new[] { "http://127.0.0.1:61922" };
        }

        private static string TokenFile()
        {
            string home = Environment.GetEnvironmentVariable("DSH_HOME");
            if (string.IsNullOrEmpty(home))
            {
                home = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            }
            return Path.Combine(home, "pet-bridge.token");
        }

        /// <summary>桥接是否已就绪（插件已装载且有密钥）。</summary>
        public static bool Available(out string reason)
        {
            reason = null;
            string file = TokenFile();
            if (!File.Exists(file))
            {
                reason = "未找到 pet-bridge.token（DSH 里的桥接插件未安装或未启动）";
                return false;
            }
            try
            {
                if (ReadToken(file).Length < 32)
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

        private static string ReadToken(string file)
        {
            return File.ReadAllText(file, Encoding.UTF8).Trim();
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

            string token;
            try
            {
                token = ReadToken(TokenFile());
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }

            foreach (string baseUrl in CandidateBases())
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(baseUrl + "/pet-bridge" + path);
                    req.Method = "POST";
                    req.ContentType = "application/json";
                    req.Headers.Add("Authorization", "Bearer " + token);
                    req.Timeout = 4000;
                    req.ReadWriteTimeout = 4000;

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
                        // 401/409 说明桥接在，只是这次请求不成立，不用再换端口
                        if (result.Status == 401 || result.Status == 409) return result;
                    }
                    else
                    {
                        result.Error = ex.Message;
                    }
                }
                catch (Exception ex)
                {
                    result.Error = ex.Message;
                }
            }
            return result;
        }

        private static string ExtractError(string body)
        {
            if (string.IsNullOrEmpty(body)) return "（空响应）";
            int i = body.IndexOf("\"error\":\"", StringComparison.Ordinal);
            if (i < 0) return body.Length > 200 ? body.Substring(0, 200) : body;
            i += 9;
            int end = body.IndexOf('"', i);
            return end > i ? body.Substring(i, end - i) : body.Substring(i);
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
