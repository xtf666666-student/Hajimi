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
        public bool Active;
        public int Turn;
        public DateTime Updated;
        public string Ago;

        public override string ToString()
        {
            return (Active ? "* " : "  ") + Title + " (" + Ago + ")";
        }
    }

    /// <summary>
    /// 读取 DSH 的会话缓存，判断哪些会话正在运行。
    /// 只读，不写，不联网；解析失败的单条直接跳过。
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

        public static string CacheDir
        {
            get
            {
                string home = Environment.GetEnvironmentVariable("DSH_HOME");
                if (string.IsNullOrEmpty(home))
                {
                    home = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
                }
                return Path.Combine(home, "storages", "session_projcache", "sessions");
            }
        }

        public static List<SessionItem> Read()
        {
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

            foreach (string file in files)
            {
                SessionItem item = ReadOne(file);
                if (item != null)
                {
                    result.Add(item);
                }
            }

            // 跑着的排前面，然后按更新时间倒序
            result.Sort((a, b) =>
            {
                if (a.Active != b.Active) return a.Active ? -1 : 1;
                return b.Updated.CompareTo(a.Updated);
            });
            return result;
        }

        public static List<SessionItem> ReadActive()
        {
            List<SessionItem> all = Read();
            List<SessionItem> active = new List<SessionItem>();
            foreach (SessionItem item in all)
            {
                if (item.Active)
                {
                    active.Add(item);
                }
            }
            return active;
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
