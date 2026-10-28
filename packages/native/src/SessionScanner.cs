using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HajimiPet
{
    internal sealed class SessionItem
    {
        public string Id;
        public string Title;
        /// <summary>这一轮是否还在跑。</summary>
        public bool Active;
        /// <summary>刚跑完、还没被用户看过（角标要保留它）。</summary>
        public bool Unread;
        public int Turn;
        public DateTime Updated;
        public string Ago;

        /// <summary>角标要统计的数量：跑着的 + 跑完未看的。</summary>
        public bool Counts { get { return Active || Unread; } }

        public override string ToString()
        {
            return (Active ? "* " : (Unread ? "+ " : "  ")) + Title + " (" + Ago + ")";
        }
    }

    /// <summary>
    /// 读取 DSH 的会话缓存，判断哪些会话正在运行 / 刚跑完未查看。
    /// 只读，不写，不联网；解析失败的单条直接跳过。
    ///
    /// 「跑完未看」的判定：这一轮的完成时刻比我们记录的上次查看时刻更晚。
    /// 状态存在 $DSH_HOME/hajimi-pet-seen.json，桌宠自己维护，不碰 DSH 数据。
    /// </summary>
    internal static class SessionScanner
    {
        private static readonly Regex TitleRe = new Regex(
            "\"title\"\\s*:\\s*\\{[^}]*?\"val\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex KindRe = new Regex(
            "\"lastStepBoundary\"\\s*:\\s*\\{\\s*\"kind\"\\s*:\\s*\"([a-z]+)\"",
            RegexOptions.Compiled);

        private static readonly Regex OpenTurnRe = new Regex(
            "\"openTurnStartSeq\"\\s*:\\s*(\\d+)",
            RegexOptions.Compiled);

        private static readonly Regex TurnRe = new Regex(
            "\"lastTurn\"\\s*:\\s*(\\d+)",
            RegexOptions.Compiled);

        private static readonly Regex CwdRe = new Regex(
            "\"cwd\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"",
            RegexOptions.Singleline | RegexOptions.Compiled);

        public static string DshHome
        {
            get
            {
                string home = Environment.GetEnvironmentVariable("DSH_HOME");
                if (string.IsNullOrEmpty(home))
                {
                    home = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
                }
                return home;
            }
        }

        public static string CacheDir
        {
            get { return Path.Combine(DshHome, "storages", "session_projcache", "sessions"); }
        }

        /// <summary>桌宠自己维护的「已查看 / 已关闭」记录。</summary>
        private static string SeenFile
        {
            get { return Path.Combine(DshHome, "hajimi-pet-seen.json"); }
        }

        /// <summary>sessionId -> 已确认的更新时间（用来判断是否出现过新的完成）。</summary>
        private static Dictionary<string, DateTime> _seen =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);

        /// <summary>sessionId -> 完成时刻（首次发现它「已完成」时记下，避免列表抖动）。</summary>
        private static Dictionary<string, DateTime> _finishedAt =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);

        /// <summary>用户主动叉掉的会话，永久不再提示（直到它再次开始运行）。</summary>
        private static HashSet<string> _dismissed = new HashSet<string>(StringComparer.Ordinal);

        private static bool _loaded;

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(SeenFile)) return;
                string json = File.ReadAllText(SeenFile, Encoding.UTF8);
                foreach (Match m in Regex.Matches(json,
                    "\"id\"\\s*:\\s*\"([^\"]+)\"[^}]*?\"seen\"\\s*:\\s*\"([^\"]*)\""))
                {
                    DateTime t;
                    if (DateTime.TryParse(m.Groups[2].Value, out t))
                    {
                        _seen[m.Groups[1].Value] = t;
                    }
                }
            }
            catch
            {
                /* 记录坏了就当作没记录 */
            }
        }

        public static void Save()
        {
            try
            {
                var sb = new StringBuilder("{\"items\":[");
                bool first = true;
                foreach (KeyValuePair<string, DateTime> kv in _seen)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"id\":\"").Append(Escape(kv.Key))
                      .Append("\",\"seen\":\"").Append(kv.Value.ToString("o")).Append("\"}");
                }
                sb.Append("]}");
                Directory.CreateDirectory(Path.GetDirectoryName(SeenFile));
                File.WriteAllText(SeenFile, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                /* 写不进去不影响功能 */
            }
        }

        private static string Escape(string s)
        {
            return s == null ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>用户点开/关闭某会话：标记已读，角标数字随之下降。</summary>
        public static void MarkSeen(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            _seen[sessionId] = DateTime.UtcNow;
            _dismissed.Remove(sessionId);
            _finishedAt.Remove(sessionId);
            Save();
        }

        /// <summary>用户点叉：直接移出列表，直到它再次开始运行。</summary>
        public static void Dismiss(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            _dismissed.Add(sessionId);
            _seen[sessionId] = DateTime.UtcNow;
            _finishedAt.Remove(sessionId);
            Save();
        }

        public static List<SessionItem> Read()
        {
            Load();
            List<SessionItem> result = new List<SessionItem>();
            string dir = CacheDir;
            if (!Directory.Exists(dir))
            {
                return result;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "session-*.json");
            }
            catch
            {
                return result;
            }

            DateTime now = DateTime.UtcNow;

            foreach (string file in files)
            {
                SessionItem item = ReadOne(file);
                if (item != null)
                {
                    string id = item.Id;

                    if (item.Active)
                    {
                        // 又跑起来了：之前的「已完成」状态作废，叉掉也复活
                        _finishedAt.Remove(id);
                        _dismissed.Remove(id);
                        item.Unread = false;
                    }
                    else
                    {
                        if (_dismissed.Contains(id))
                        {
                            // 用户已经主动关掉，不再提示
                            item.Unread = false;
                        }
                        else
                        {
                            // 记下它第一次被观察到「已完成」的时刻
                            if (!_finishedAt.ContainsKey(id))
                            {
                                _finishedAt[id] = now;
                            }
                            DateTime seenAt;
                            if (!_seen.TryGetValue(id, out seenAt))
                            {
                                // 第一次见到这个会话：如果它很久以前就完成了，不打扰
                                if (item.Updated != DateTime.MinValue &&
                                    (DateTime.Now - item.Updated).TotalMinutes > 10)
                                {
                                    _seen[id] = now;
                                    seenAt = now;
                                }
                                else
                                {
                                    seenAt = DateTime.MinValue;
                                }
                            }
                            item.Unread = item.Updated > seenAt;
                        }
                    }

                    result.Add(item);
                }
            }

            // 跑着的排最前，然后是跑完未看的，最后是其余的；同组按时间倒序
            result.Sort((a, b) =>
            {
                int ga = a.Active ? 0 : (a.Unread ? 1 : 2);
                int gb = b.Active ? 0 : (b.Unread ? 1 : 2);
                if (ga != gb) return ga.CompareTo(gb);
                return b.Updated.CompareTo(a.Updated);
            });
            return result;
        }

        /// <summary>角标要显示的列表：跑着的 + 跑完未看的。</summary>
        public static List<SessionItem> ReadActive()
        {
            List<SessionItem> all = Read();
            List<SessionItem> show = new List<SessionItem>();
            foreach (SessionItem item in all)
            {
                if (item.Counts) show.Add(item);
            }
            return show;
        }

        private static SessionItem ReadOne(string file)
        {
            try
            {
                string text = File.ReadAllText(file, Encoding.UTF8);
                string title = Match1(TitleRe, text);
                if (string.IsNullOrEmpty(title))
                {
                    // 末页才可能带 title；尝试读尾部
                    if (text.Length > 400000)
                    {
                        text = text.Substring(text.Length - 400000);
                        title = Match1(TitleRe, text);
                    }
                }
                if (string.IsNullOrEmpty(title))
                {
                    return null;
                }

                string kind = Match1(KindRe, text);
                int openTurn = MatchInt(OpenTurnRe, text);
                int turn = MatchInt(TurnRe, text);
                // kind == "start" 表示这一轮还没收到结束标记 = 正在生成
                bool active = kind == "start" && openTurn > 0;

                DateTime updated;
                try
                {
                    updated = File.GetLastWriteTime(file);
                }
                catch
                {
                    updated = DateTime.MinValue;
                }

                return new SessionItem
                {
                    Id = Path.GetFileNameWithoutExtension(file),
                    Title = Decode(title),
                    Active = active,
                    Unread = false,
                    Turn = turn,
                    Updated = updated,
                    Ago = Relative(updated),
                };
            }
            catch
            {
                return null;
            }
        }

        private static string Match1(Regex re, string text)
        {
            Match m = re.Match(text);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static int MatchInt(Regex re, string text)
        {
            Match m = re.Match(text);
            if (!m.Success)
            {
                return 0;
            }
            int value;
            return int.TryParse(m.Groups[1].Value, out value) ? value : 0;
        }

        // JSON 里的 \uXXXX / \n 等转义还原
        private static string Decode(string raw)
        {
            StringBuilder sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c != '\\' || i + 1 >= raw.Length)
                {
                    sb.Append(c);
                    continue;
                }
                char n = raw[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'u':
                        if (i + 4 < raw.Length)
                        {
                            int code;
                            if (int.TryParse(raw.Substring(i + 1, 4),
                                    System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                        }
                        break;
                    default: sb.Append(n); break;
                }
            }
            return sb.ToString().Replace("\r", " ").Replace("\n", " ").Trim();
        }

        public static string Relative(DateTime when)
        {
            if (when == DateTime.MinValue)
            {
                return "";
            }
            TimeSpan span = DateTime.Now - when;
            if (span.TotalSeconds < 60)
            {
                return "刚刚";
            }
            if (span.TotalMinutes < 60)
            {
                return (int)span.TotalMinutes + " 分前";
            }
            if (span.TotalHours < 24)
            {
                return (int)span.TotalHours + " 时前";
            }
            return (int)span.TotalDays + " 天前";
        }
    }
}
