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
        /// <summary>卡片第二行：当前在做什么（"正在执行命令 / 正在生成回复 / 等你确认"）。</summary>
        public string Status;
        /// <summary>
        /// 卡片正文要展示的一段文字。
        ///
        /// 已完成的会话：模型输出的最终结果（turnOutline 末轮的 response）；
        /// 运行中的会话：缓存里没有流式内容，退回上一轮结果的摘要，
        /// 都没有就留空（卡片只显示标题）。
        /// </summary>
        public string Body;
        /// <summary>这一轮是否还在跑。</summary>
        public bool Active;
        /// <summary>
        /// 已完成的卡片是否还要显示。
        ///
        /// 用户要求"任务完成后卡片绝不自动消失"，所以只要用户没主动关掉
        /// （没点 × / 没点进会话 / 没在 DSH 里切过去），就一直保留。
        /// 由 Read() 根据 _dismissed 决定。
        /// </summary>
        public bool ShowCompleted;
        /// <summary>刚跑完、还没被用户看过。注意：这不再影响角标显示。</summary>
        public bool Unread;
        public int Turn;
        public DateTime Updated;
        public string Ago;

        /// <summary>
        /// 会话缓存文件的修改时间。
        ///
        /// 用途：判断"用户是否刚刚在 DSH 里打开过这个会话"。
        /// DSH 打开一个会话时会刷新它的缓存文件，于是 mtime 变新；
        /// 已完成的卡片如果 mtime 在最近 FileTouchWindow 内更新过，
        /// 就认为用户已经从 DSH 侧进入过它 —— 对应需求里的条件 3。
        /// </summary>
        public DateTime FileMtime;

        public override string ToString()
        {
            return (Active ? "* " : (Unread ? "+ " : "  ")) + Title
                 + " [" + Status + "] " + Body + " (" + Ago + ")";
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

        // sessionListMetadata 里的最后提问时间（毫秒 epoch），会话自带的完成时间
        private static readonly Regex LastPromptRe = new Regex(
            "\"lastPromptAt\"\\s*:\\s*(\\d+)",
            RegexOptions.Compiled);

        // pendingCalls 里是否有内容（正在执行工具）
        private static readonly Regex PendingRe = new Regex(
            "\"pendingCalls\"\\s*:\\s*\\{([^}]*)\\}",
            RegexOptions.Compiled);

        // inbox 队列（等用户回应）
        private static readonly Regex InboxRe = new Regex(
            "\"inbox\"\\s*:\\s*\\{([^{}]*)\\}",
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

        /// <summary>
        /// 会话缓存文件在这么多秒内被写过，就认为"用户刚从 DSH 打开了它"。
        /// 用于条件 3 的判定。
        /// </summary>
        private const int FileTouchWindowSeconds = 60;

        /// <summary>用户主动叉掉的会话，永久不再提示（直到它再次开始运行）。</summary>
        private static HashSet<string> _dismissed = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// sessionId -> 桌宠**亲眼见过它处于运行中**。
        ///
        /// 这是"任务完成卡片"的唯一合法来源：
        /// 只有桌宠自己看着某个会话从 running 变成 completed，才出一张卡。
        ///
        /// 之前用"完成时间在 30 分钟内"作为判据，结果刚启动就冒出 5 张 ——
        /// 那 30 分钟里在 DSH 里跑过的会话全被算进来了，桌宠根本没见过它们跑。
        /// 现在这个集合持久化保存，重启后依然知道哪些是自己跟踪过的。
        /// </summary>
        private static HashSet<string> _seenRunning = new HashSet<string>(StringComparer.Ordinal);

        private static bool _loaded;

        /// <summary>sessionId -> 用户已看到的那一轮序号（比时间戳可靠）。</summary>
        private static Dictionary<string, int> _seenTurn =
            new Dictionary<string, int>(StringComparer.Ordinal);

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
                    // 记录里既有本地时间（+08:00）也有 UTC（Z），
                    // 必须统一转成本地时间再比较，否则会差 8 小时。
                    DateTimeOffset dto;
                    if (DateTimeOffset.TryParse(m.Groups[2].Value, out dto))
                    {
                        _seen[m.Groups[1].Value] = dto.LocalDateTime;
                    }
                }
                // 轮次序号（新格式才有）
                foreach (Match m in Regex.Matches(json,
                    "\"id\"\\s*:\\s*\"([^\"]+)\"[^}]*?\"turn\"\\s*:\\s*(\\d+)"))
                {
                    int turn;
                    if (int.TryParse(m.Groups[2].Value, out turn))
                    {
                        _seenTurn[m.Groups[1].Value] = turn;
                    }
                }
                // "桌宠见过它在跑"的标记
                foreach (Match m in Regex.Matches(json,
                    "\"id\"\\s*:\\s*\"([^\"]+)\"[^}]*?\"ran\"\\s*:\\s*true"))
                {
                    _seenRunning.Add(m.Groups[1].Value);
                }
            }
            catch
            {
                /* 记录坏了就当作没记录 */
            }
        }

        /// <summary>
        /// 清空"桌宠见过它在跑"的记录。
        ///
        /// 启动时调用：用户要求桌宠 / DSH 一重启，初始状态不显示任何任务卡片。
        /// 清掉之后从当前时刻重新观察，只有重启后真跑完的会话才出卡片。
        ///
        /// 注意同时清掉 _dismissed：否则"重启前被 × 掉"的旧状态会残留，
        /// 语义上和"全新开始"不一致。
        /// </summary>
        public static void ForgetWatched()
        {
            Load();
            _seenRunning.Clear();
            _dismissed.Clear();
            Save();
        }

        public static void Save()
        {
            try
            {
                var sb = new StringBuilder("{\"items\":[");
                var ids = new List<string>(_seen.Keys);
                foreach (string id in _seenTurn.Keys)
                {
                    if (!ids.Contains(id)) ids.Add(id);
                }
                foreach (string id in _seenRunning)
                {
                    if (!ids.Contains(id)) ids.Add(id);
                }

                bool first = true;
                foreach (string id in ids)
                {
                    if (!first) sb.Append(',');
                    first = false;

                    DateTime when = _seen.ContainsKey(id) ? _seen[id] : DateTime.UtcNow;
                    int turn = _seenTurn.ContainsKey(id) ? _seenTurn[id] : 0;
                    bool ran = _seenRunning.Contains(id);

                    sb.Append("{\"id\":\"").Append(Escape(id))
                      .Append("\",\"seen\":\"").Append(when.ToString("o"))
                      .Append("\",\"turn\":").Append(turn)
                      .Append(",\"ran\":").Append(ran ? "true" : "false")
                      .Append('}');
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
        public static void MarkSeen(string sessionId, int turn)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            _seen[sessionId] = DateTime.Now;
            // 同时记下看到的那一轮，用它比时间戳可靠
            _seenTurn[sessionId] = turn;
            _dismissed.Remove(sessionId);
            _finishedAt.Remove(sessionId);
            Save();
        }

        /// <summary>用户点叉：直接移出列表，直到它再次开始运行。</summary>
        public static void Dismiss(string sessionId, int turn)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            _dismissed.Add(sessionId);
            _seen[sessionId] = DateTime.Now;
            _seenTurn[sessionId] = turn;
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

            DateTime now = DateTime.Now;

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
                        item.ShowCompleted = false;   // 运行中走 Active 分支

                        // **亲眼见过它在跑**：这是唯一能产生"完成卡片"的资格。
                        // 记进集合并持久化，重启后依然认账。
                        if (!_seenRunning.Contains(id))
                        {
                            _seenRunning.Add(id);
                            Save();
                        }
                    }
                    else if (_dismissed.Contains(id))
                    {
                        // 用户已经主动关掉（×、点卡片、或在 DSH 里切进去过）
                        item.Unread = false;
                        item.ShowCompleted = false;
                    }
                    else
                    {
                        // 条件 3：用户在 DSH 里主动切进了这个会话。
                        //
                        // DSH 打开会话时会刷新它的缓存文件，所以"已完成 + 文件刚被写过"
                        // 就等价于"用户已经进去看过了"。这时把卡片关掉。
                        bool justOpened = item.FileMtime != DateTime.MinValue &&
                                          (DateTime.Now - item.FileMtime).TotalSeconds
                                              <= FileTouchWindowSeconds;

                        // 出卡片的**唯一条件**：桌宠亲眼看着它从 running 变成 completed。
                        //
                        // 之前用"完成时间在 30 分钟内"，结果一启动就冒出 5 张：
                        // 那段时间里在 DSH 跑过的会话全被算进来，桌宠从没见过它们跑。
                        // 现在只认 _seenRunning 里的会话 —— 也就是桌宠自己跟踪过、
                        // 看着它跑完的。用户重启桌宠后不会凭空冒出卡片。
                        bool watched = _seenRunning.Contains(id);

                        if (justOpened)
                        {
                            _dismissed.Add(id);
                            _seenTurn[id] = item.Turn;
                            Save();
                            item.ShowCompleted = false;
                        }
                        else if (watched)
                        {
                            // 桌宠亲眼看着它跑完的：卡片**保留**，等用户主动关闭
                            item.ShowCompleted = true;
                        }
                        else
                        {
                            // 桌宠没见过它在跑（比如启动前就已经跑完的历史会话）：
                            // 不显示，也不占 _dismissed
                            item.ShowCompleted = false;
                        }
                        // 未读判定优先用「轮次序号」——会话内嵌、单调递增，最可靠。
                        // 旧记录里没有 turn（只有 seen 时间戳），此时退回时间比较，
                        // 但用的是 lastPromptAt（会话自带），不是文件 mtime
                        // （mtime 会被缓存整理刷新，是误报的根源）。
                        int seenTurn;
                        DateTime seenAt;
                        if (_seenTurn.TryGetValue(id, out seenTurn))
                        {
                            item.Unread = item.Turn > seenTurn;
                        }
                        else if (_seen.TryGetValue(id, out seenAt))
                        {
                            // 老记录：用内嵌完成时间比，且留 2 分钟容差防抖动
                            item.Unread = item.Updated != DateTime.MinValue &&
                                          item.Updated > seenAt.AddMinutes(-2);
                            if (!item.Unread)
                            {
                                // 顺手补齐轮次，之后就走上面那条更可靠的路径
                                _seenTurn[id] = item.Turn;
                            }
                        }
                        else
                        {
                            // 第一次见到这个会话：只有真的是刚结束的（10 分钟内）
                            // 才提示，避免一启动就把全部历史会话翻出来。
                            bool fresh = item.Updated != DateTime.MinValue &&
                                         (DateTime.Now - item.Updated).TotalMinutes <= 10;
                            item.Unread = fresh;
                            if (!fresh)
                            {
                                _seen[id] = now;
                                _seenTurn[id] = item.Turn;
                            }
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

        /// <summary>
        /// 要显示的列表：**只含正在运行的会话**。
        ///
        /// 用户明确要求：没有任务在跑时什么都不要显示。
        /// 所以"跑完未看"不再计入角标，也不会出现在列表里 ——
        /// 它只在会话真正 running 时才有意义。
        /// </summary>
        /// <summary>
        /// 卡片列表：**运行中** 的会话 + **已完成但还没被用户关闭** 的会话。
        ///
        /// 用户要求：任务完成后卡片不能自动消失，必须保留到用户
        ///   × 关掉它、或点卡片进入会话、或在 DSH 里切到那个会话。
        /// 后两种都会调 Dismiss 把 ID 记进 _dismissed，于是这里就不再返回。
        ///
        /// 运行中重新开始的会话会自动从 _dismissed 移除（见 Read 里那段），
        /// 所以同一会话再次跑起来时卡片会重新出现。
        /// </summary>
        public static List<SessionItem> ReadActive()
        {
            List<SessionItem> all = Read();
            List<SessionItem> show = new List<SessionItem>();
            foreach (SessionItem item in all)
            {
                if (item.Active || item.ShowCompleted) show.Add(item);
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

                // kind == "start" 表示这一轮还没收到结束标记 = 正在生成。
                // 但这标记可能是**残留**：DSH 被强杀、崩溃、或那一轮没正常收尾时，
                // 缓存会永远停在 start，于是一个几天前就结束的会话看起来一直在跑。
                // （用户遇到的"没任务却显示运行中 1 个"就是这个）
                //
                // 所以再加一道活性检查：真正在跑的会话会不停写缓存文件，
                // 残留标记的文件则长时间不动。
                bool marked = kind == "start" && openTurn > 0;
                bool active = marked && IsFresh(file, text);

                string status = ReadStatus(text, active);
                string body = ReadBody(text, active);

                // 完成时间必须用会话文件里内嵌的时间戳，不能用文件 mtime。
                // mtime 会被 DSH 自己的缓存整理、迁移、甚至读取刷新，
                // 导致早已结束的会话被误判成"刚完成、未读"。
                DateTime updated = ReadLastPromptAt(text);

                // 文件 mtime 单独留一份：用来感知"用户刚在 DSH 打开过它"
                DateTime fileMtime = DateTime.MinValue;
                try
                {
                    fileMtime = File.GetLastWriteTime(file);
                }
                catch
                {
                    /* 读不到就算了，不影响其它字段 */
                }

                // 读不到内嵌时间戳时才退回文件时间，并把它"钝化"到分钟级，
                // 避免几秒的抖动又被当成新消息。
                if (updated == DateTime.MinValue)
                {
                    try
                    {
                        DateTime mtime = File.GetLastWriteTime(file);
                        updated = new DateTime(mtime.Year, mtime.Month, mtime.Day,
                            mtime.Hour, mtime.Minute, 0);
                    }
                    catch
                    {
                        updated = DateTime.MinValue;
                    }
                }

                return new SessionItem
                {
                    Id = Path.GetFileNameWithoutExtension(file),
                    Title = Decode(title),
                    Status = status,
                    Body = body,
                    Active = active,
                    Unread = false,
                    Turn = turn,
                    Updated = updated,
                    Ago = Relative(updated),
                    FileMtime = fileMtime,
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 推断卡片第二行该写什么。
        ///
        /// DSH 的缓存里没有"当前工具名"这种现成字段，但有：
        ///   sessionStats.pendingCalls —— 正在执行、还没返回的工具调用
        ///   sessionStats.openStep     —— 已开始但未结束的步骤
        ///   inbox                     —— 排队等你的输入（approval / 回答）
        /// 用这几条推出一个人能看懂的状态，不猜具体工具名。
        /// </summary>
        /// <summary>
        /// 取卡片要显示的正文。
        ///
        /// 数据来源是 turnOutline 里最后一轮的 response —— 那是模型这一轮的
        /// 输出正文。缓存里**没有**流式（打字机）内容，所以运行中也只能
        /// 拿上一轮的完整结果，或者干脆留空。
        ///
        /// 清理：去掉 Markdown 标记、代码块、换行，压成一行，好让卡片
        /// 单行截断时语义还连贯。
        /// </summary>
        private static string ReadBody(string text, bool active)
        {
            string resp = ReadLastResponse(text);
            if (string.IsNullOrEmpty(resp)) return string.Empty;

            return FlattenText(resp);
        }

        /// <summary>
        /// 从 turnOutline 里取最后一个非空的 response。
        /// turnOutline 是 {"turns":[{"turn":N,"prompt":"...","response":"..."}]}，
        /// 直接找 '"response":"' 出现的位置，比解析整棵对象快得多，
        /// 而且不怕缓存文件尾部被截断。
        /// </summary>
        private static string ReadLastResponse(string text)
        {
            string last = null;
            int from = 0;
            while (true)
            {
                Match m = ResponseRe.Match(text, from);
                if (!m.Success) break;
                string raw = m.Groups[1].Value;
                if (raw.Length > 0) last = raw;
                from = m.Index + m.Length;
                if (from >= text.Length) break;
            }
            return last == null ? null : Decode(last);
        }

        /// <summary>
        /// 把多行 Markdown 正文压成一行给卡片用。
        /// 去掉 ``` 代码块、** 加粗标记、行首 #/- 等，换行变空格，压缩多余空白。
        /// </summary>
        private static string FlattenText(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                // 跳过整个 ``` ... ``` 代码块
                if (s.Length - i >= 3 && s[i] == '`' && s[i + 1] == '`' && s[i + 2] == '`')
                {
                    int close = s.IndexOf("```", i + 3, StringComparison.Ordinal);
                    i = close < 0 ? s.Length : close + 3;
                    sb.Append(' ');
                    continue;
                }
                char c = s[i];
                if (c == '*' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    i += 2;          // 去掉 ** 加粗标记
                    continue;
                }
                if (c == '`' || c == '#')
                {
                    i++;             // 去掉单个反引号和 # 标题符
                    continue;
                }
                if (c == '\r' || c == '\n' || c == '\t')
                {
                    sb.Append(' ');
                    i++;
                    continue;
                }
                sb.Append(c);
                i++;
            }

            // 压缩连续空白
            string flat = sb.ToString();
            var outSb = new StringBuilder(flat.Length);
            bool space = false;
            foreach (char c in flat)
            {
                if (c == ' ')
                {
                    if (!space) outSb.Append(' ');
                    space = true;
                }
                else
                {
                    outSb.Append(c);
                    space = false;
                }
            }
            return outSb.ToString().Trim();
        }

        private static readonly Regex ResponseRe = new Regex(
            "\"response\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"",
            RegexOptions.Compiled);

        private static string ReadStatus(string text, bool active)
        {
            if (!active) return "已结束";

            // 有排队等你处理的输入 -> 优先提示这个
            Match inbox = InboxRe.Match(text);
            if (inbox.Success &&
                (ContainsNonEmpty(inbox.Groups[1].Value, "next-turn") ||
                 ContainsNonEmpty(inbox.Groups[1].Value, "next-step")))
            {
                return "等你回应";
            }

            // 有未返回的工具调用 -> 正在执行
            Match pending = PendingRe.Match(text);
            if (pending.Success && pending.Groups[1].Value.Trim().Length > 0)
            {
                return "正在执行工具";
            }

            // 打开着的步骤 -> 生成中
            if (text.IndexOf("\"openStep\":{", StringComparison.Ordinal) >= 0)
            {
                return "正在生成回复";
            }

            return "正在响应";
        }

        /// <summary>粗判 inbox 的某个队列里是否有元素。</summary>
        private static bool ContainsNonEmpty(string inboxJson, string key)
        {
            int i = inboxJson.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return false;
            int lb = inboxJson.IndexOf('[', i);
            if (lb < 0) return false;
            int rb = inboxJson.IndexOf(']', lb);
            if (rb < 0) return false;
            return inboxJson.Substring(lb, rb - lb + 1).Trim('[', ']', ' ', '\n', '\r').Length > 0;
        }

        /// <summary>
        /// 判定一个"start"标记是否还活着。
        ///
        /// 真正在跑的会话，DSH 每步都会重写缓存文件；而残留标记的文件
        /// 会一停就是几小时甚至几天。任一"最近活动"信号够新就算活：
        ///   - 文件修改时间（最直接：正在写）
        ///   - lastPromptAt（用户最后一次提问）
        ///
        /// 用"或"而不是"与"：长工具调用期间缓存可能几分钟不落盘，
        /// 但提问时间也不会更新，两者任一过期太久的才是真僵尸。
        /// </summary>
        private static bool IsFresh(string file, string text)
        {
            DateTime fresh = DateTime.Now.AddMinutes(-IdleGraceMinutes);

            try
            {
                if (File.GetLastWriteTime(file) >= fresh) return true;
            }
            catch
            {
                /* 读不到就当不新鲜，继续看下一个信号 */
            }

            DateTime prompt = ReadLastPromptAt(text);
            if (prompt != DateTime.MinValue && prompt >= fresh) return true;

            return false;
        }

        /// <summary>
        /// 多久没动静就不再认为在跑。
        ///
        /// 30 分钟：正常的一步是秒级，长工具调用也就几分钟；
        /// 而实测的僵尸是 2821 分钟（47 小时），差了两个数量级，不会误杀。
        /// </summary>
        private const int IdleGraceMinutes = 30;

        private static string Match1(Regex re, string text)
        {
            Match m = re.Match(text);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// 读会话内嵌的最后提问时间（毫秒 epoch）。
        ///
        /// 用这个而不是文件 mtime：mtime 会被缓存整理、迁移、读取刷新，
        /// 用它判断"是否新完成"会误报。
        /// </summary>
        private static DateTime ReadLastPromptAt(string text)
        {
            Match m = LastPromptRe.Match(text);
            if (!m.Success) return DateTime.MinValue;

            long ms;
            if (!long.TryParse(m.Groups[1].Value, out ms) || ms <= 0)
            {
                return DateTime.MinValue;
            }

            try
            {
                // 显式按 UTC 还原，再转本地时间
                return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
            }
            catch
            {
                return DateTime.MinValue;
            }
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
