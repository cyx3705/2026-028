// wxread.cs -- diagnostic harness for wxdb.cs (development tool, not shipped
// behaviour).  Prints what the memory reader can see, so the bridge integration
// can be verified independently of the UI.
//
//   wxread dbs                 list recovered databases
//   wxread sessions            list conversations, newest first
//   wxread msgs <sessionId>    list messages of one conversation
//   wxread all                 everything, written to wxread_out.txt

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using LocalChat;

internal static class WxReadMain
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string cls, string title);

    private static IntPtr WeChatWindow()
    {
        IntPtr h = FindWindow("Qt51514QWindowIcon", "微信");
        if (h == IntPtr.Zero) h = FindWindow("Qt51514QWindowIcon", null);
        return h;
    }

    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string mode = args.Length > 0 ? args[0] : "all";

        // pid <n> [dbs|sessions|tables|...] -- capture some other Weixin process
        // (helper processes own their own page caches; the window process is not
        // necessarily the one that has a given database open).
        int forcedPid = 0;
        if (mode == "pid" && args.Length > 1)
        {
            forcedPid = int.Parse(args[1]);
            mode = args.Length > 2 ? args[2] : "dbs";
            string[] rest = new string[args.Length - 2];
            Array.Copy(args, 2, rest, 0, rest.Length);
            args = rest;
        }

        StringBuilder sb = new StringBuilder();
        TextWriter w = mode == "all"
            ? (TextWriter)new StringWriter(sb)
            : Console.Out;
        try
        {
            uint pid;
            if (forcedPid != 0)
            {
                pid = (uint)forcedPid;
                w.WriteLine("指定 pid={0}", pid);
            }
            else
            {
                IntPtr h = WeChatWindow();
                if (h == IntPtr.Zero) { w.WriteLine("找不到微信窗口"); return; }
                pid = WxSnapshot.PidOfWeChatWindow(h);
                w.WriteLine("微信窗口 0x{0:x} pid={1}", h.ToInt64(), pid);
            }

            DateTime t0 = DateTime.Now;
            WxSnapshot snap = WxSnapshot.Capture((int)pid);
            w.WriteLine(snap.Note);
            w.WriteLine("耗时 {0:F2}s", (DateTime.Now - t0).TotalSeconds);

            if (mode == "dbs" || mode == "all")
            {
                w.WriteLine("\n== 数据库 ==");
                foreach (WxDb db in snap.Dbs)
                {
                    Dictionary<string, int> t = null;
                    string extra = "";
                    try
                    {
                        t = db.Tables();
                        int nmsg = 0, nsess = 0;
                        foreach (string k in t.Keys)
                        {
                            if (k.StartsWith("Msg_") && !k.EndsWith("_SENDERID") &&
                                !k.EndsWith("_SERVERID") &&
                                !k.EndsWith("_SORTSEQ") && !k.EndsWith("_TYPE_SEQ"))
                                nmsg++;
                            if (k == "SessionTable") nsess++;
                        }
                        extra = string.Format("表 {0} (Msg_ {1}, Session {2})",
                                              t.Count, nmsg, nsess);
                    }
                    catch (Exception e) { extra = "解析失败 " + e.Message; }
                    w.WriteLine("  DbPages={0,-6} 缓存页={1,-5} change={2,-12} {3}",
                                db.DbPages, db.PageOff.Count, db.ChangeCounter,
                                extra);
                }
            }

            if (mode == "sessions" || mode == "all")
            {
                List<WxSession> ss = snap.Sessions();
                w.WriteLine("\n== 会话 {0} 个（按最后消息时间，最新 20）==", ss.Count);
                for (int i = 0; i < ss.Count && i < 20; i++)
                {
                    WxSession s = ss[i];
                    w.WriteLine("[last {0} | sort {1}] {2,-34} type={3,-4} unread={4,-5} sender={5} name={6}",
                                Time(s.LastTimestamp), Time(s.SortTimestamp),
                                s.UserName, s.LastMsgType,
                                s.Unread, s.LastSender, s.LastSenderName);
                    w.WriteLine("      {0}", Clip(s.Summary, 110));
                }

                // 按 sort_timestamp 再排一遍：这个字段很可能是"会话被激活
                // （点开）"的时间，而不是"收到消息"的时间。
                List<WxSession> bySort = new List<WxSession>(ss);
                bySort.Sort(delegate (WxSession a, WxSession b)
                {
                    return b.SortTimestamp.CompareTo(a.SortTimestamp);
                });
                w.WriteLine("\n== 同一批会话（按 sort_timestamp，最新 15）==");
                for (int i = 0; i < bySort.Count && i < 15; i++)
                {
                    WxSession s = bySort[i];
                    long d = s.SortTimestamp - s.LastTimestamp;
                    w.WriteLine("[sort {0} | last {1} | 差 {2}s] {3,-34} {4}",
                                Time(s.SortTimestamp), Time(s.LastTimestamp), d,
                                s.UserName, Clip(s.Summary, 60));
                }

                if (mode == "all" && ss.Count > 0)
                {
                    string top = ss[0].UserName;
                    w.WriteLine("\n== 最新会话 {0} 的消息 ==", top);
                    string note;
                    List<WxMessage> ms = snap.Messages(top, out note);
                    w.WriteLine("md5={0} {1} 行={2}", WxSnapshot.Md5Hex(top), note,
                                ms.Count);
                    for (int i = 0; i < ms.Count && i < 20; i++)
                    {
                        WxMessage m = ms[i];
                        w.WriteLine("[{0}] id={1,-7} type={2,-4} sender={3,-5} {4}",
                                    Time(m.CreateTime), m.LocalId, m.LocalType,
                                    m.Sender, Clip(m.Text, 140));
                    }
                }
            }

            if (mode == "pagers")
            {
                string note;
                List<string> ls = WxSnapshot.PagerReport((int)pid, out note);
                w.WriteLine(note + "，pager " + ls.Count + " 个");
                foreach (string s in ls) w.WriteLine("  " + s);
            }

            // fresh -- 每张消息表在各修订版里能读到的最新一行，用来判断
            // "读不到"是页缓存没覆盖，还是我们挑错了修订版。
            if (mode == "fresh")
            {
                Dictionary<string, string> names = new Dictionary<string, string>();
                foreach (WxSession s in snap.Sessions())
                    names[WxSnapshot.Md5Hex(s.UserName)] = s.UserName;

                Dictionary<string, long> bestTime = new Dictionary<string, long>();
                Dictionary<string, string> bestLine = new Dictionary<string, string>();
                int total = 0;
                foreach (WxDb db in snap.Dbs)
                {
                    Dictionary<string, int> t;
                    try { t = db.Tables(); }
                    catch { continue; }
                    foreach (KeyValuePair<string, int> kv in t)
                    {
                        if (!WxSnapshot.IsMsgTable(kv.Key)) continue;
                        total++;
                        long newest = 0;
                        int n = 0;
                        try
                        {
                            foreach (SqlRow r in db.TailRows(kv.Value, 4))
                            {
                                n++;
                                object[] v = r.Values;
                                if (v.Length == 17)
                                {
                                    long ct = L(v[5]);
                                    if (ct > newest) newest = ct;
                                }
                            }
                        }
                        catch { }
                        long prev;
                        if (bestTime.TryGetValue(kv.Key, out prev) && prev >= newest)
                            continue;
                        bestTime[kv.Key] = newest;
                        string u;
                        if (!names.TryGetValue(kv.Key.Substring(4), out u))
                            u = "(会话表里没有)";
                        bestLine[kv.Key] = string.Format(
                            "[{0}] {1} {2,-32} 库 DbPages={3,-6} change={4,-12} 行={5}",
                            Time(newest), kv.Key, u, db.DbPages, db.ChangeCounter, n);
                    }
                }
                List<KeyValuePair<string, long>> order =
                    new List<KeyValuePair<string, long>>(bestTime);
                order.Sort(delegate (KeyValuePair<string, long> a,
                                     KeyValuePair<string, long> b)
                {
                    return b.Value.CompareTo(a.Value);
                });
                w.WriteLine("\n== 消息表 {0} 张（跨修订版取最新），最新 30 ==", total);
                for (int i = 0; i < order.Count && i < 30; i++)
                    w.WriteLine(bestLine[order[i].Key]);
                if (order.Count > 30)
                    w.WriteLine("  … 其余 {0} 张更旧", order.Count - 30);
            }

            // find <text> -- 在整份内存镜像里找一段文字，并指出它落在哪个页
            // 缓存/哪一页；落不进任何页缓存就说明它不是 SQLite 页里的数据。
            if (mode == "find" && args.Length > 1)
            {
                byte[] pat = Encoding.UTF8.GetBytes(args[1]);
                byte[] d = snap.Img.Data;
                Dictionary<int, string> owners = WxSnapshot.PageOwners(snap.Img);
                int[] keys = new int[owners.Count];
                owners.Keys.CopyTo(keys, 0);
                Array.Sort(keys);
                int hits = 0;
                for (int i = 0; i + pat.Length <= d.Length && hits < 30; i++)
                {
                    bool ok = true;
                    for (int j = 0; j < pat.Length; j++)
                        if (d[i + j] != pat[j]) { ok = false; break; }
                    if (!ok) continue;
                    hits++;
                    w.WriteLine("命中#{0} 镜像偏移={1} 页内偏移={2}", hits, i,
                                i % WxDb.PageSize);
                    int s = i - 80; if (s < 0) s = 0;
                    int e = i + pat.Length + 80; if (e > d.Length) e = d.Length;
                    w.WriteLine("   上下文: {0}", Clip(Text(d, s, e), 260));
                    int k = Array.BinarySearch(keys, i);
                    if (k < 0) k = ~k - 1;
                    if (k >= 0 && i < keys[k] + WxDb.PageSize)
                        w.WriteLine("   页: 页数据偏移={0} -> {1}", keys[k], owners[keys[k]]);
                    else
                        w.WriteLine("   页: 不在任何页缓存里（页数据偏移 {0}）",
                                    k >= 0 ? keys[k].ToString() : "无");
                }
                w.WriteLine("共 {0} 处（最多显示 30）", hits);
            }

            // leaf <表名> -- 每个页池里根页号、最右叶子页号、以及那页上的行
            if (mode == "leaf" && args.Length > 1)
            {
                int root;
                List<WxDb> pools = snap.PoolsWithTable(args[1], out root);
                w.WriteLine("表 {0} 根页={1}，页池 {2} 个", args[1], root, pools.Count);
                foreach (WxDb db in pools)
                {
                    int[] lp = db.RightmostLeaf(root);
                    List<SqlRow> rows = db.TailRows(root, 40);
                    w.WriteLine("  池 DbPages={0} cc={1} 缓存页={2} 缺={3} 最右叶={4} 行={5}",
                                db.DbPages, db.ChangeCounter, db.PageOff.Count,
                                db.MissingCount,
                                lp == null ? "无" : lp[0].ToString(), rows.Count);
                    foreach (SqlRow r in rows)
                    {
                        object[] v = r.Values;
                        w.WriteLine("      rowid={0} t={1} type={2} {3}", r.RowId,
                                    v.Length == 17 ? Time(L(v[5])) : "?",
                                    v.Length == 17 ? v[2] : null,
                                    v.Length == 17 ? Clip(Convert.ToString(v[12]), 60) : "");
                    }
                }
            }

            // page <页号> -- 把该页当叶子页解析，看里面是谁的行
            if (mode == "page" && args.Length > 1)
            {
                int pg = int.Parse(args[1]);
                foreach (WxDb db in snap.Dbs)
                {
                    if (!db.PageOff.ContainsKey(pg)) continue;
                    int pt = db.PageType(pg);
                    w.WriteLine("页 {0} 在 DbPages={1} cc={2} 类型={3} 缓存页={4}",
                                pg, db.DbPages, db.ChangeCounter, pt, db.PageOff.Count);
                    if (pt == 5 || pt == 2)
                    {
                        List<int> kids = db.ChildrenOf(pg);
                        StringBuilder kb = new StringBuilder();
                        foreach (int c in kids) kb.Append(c).Append(' ');
                        w.WriteLine("   孩子: {0}", kb.ToString());
                        continue;
                    }
                    List<SqlRow> rows = db.PageRows(pg);
                    w.WriteLine("   行={0}", rows.Count);
                    foreach (SqlRow r in rows)
                    {
                        object[] v = r.Values;
                        StringBuilder line = new StringBuilder();
                        for (int i = 0; i < v.Length; i++)
                        {
                            if (i > 0) line.Append(" | ");
                            line.Append(i).Append('=').Append(Fmt(v[i]));
                        }
                        w.WriteLine("   rowid={0} {1}", r.RowId, Clip(line.ToString(), 300));
                    }
                }
            }

            // findpage <页号> -- 在整份镜像里找"最右孩子是这一页"的内部页，
            // 也就是绕过页缓存枚举，直接看这一页的父亲在不在内存里。
            if (mode == "findpage" && args.Length > 1)
            {
                int want = int.Parse(args[1]);
                byte[] d = snap.Img.Data;
                Dictionary<int, string> owners = WxSnapshot.PageOwners(snap.Img);
                int[] keys = new int[owners.Count];
                owners.Keys.CopyTo(keys, 0);
                Array.Sort(keys);
                byte b0 = (byte)(want >> 24), b1 = (byte)(want >> 16),
                     b2 = (byte)(want >> 8), b3 = (byte)want;
                int hits = 0;
                for (int o = 8; o + 12 < d.Length; o++)
                {
                    if (d[o + 3] != b3 || d[o + 2] != b2 || d[o + 1] != b1 ||
                        d[o] != b0) continue;
                    int st = o - 8;
                    byte t = d[st];
                    if (t != 2 && t != 5 && t != 10 && t != 13) continue;
                    int ncell = (d[st + 3] << 8) | d[st + 4];
                    if (ncell == 0 || ncell > 1500) continue;
                    hits++;
                    int k = Array.BinarySearch(keys, st);
                    if (k < 0) k = ~k - 1;
                    string own = (k >= 0 && st < keys[k] + WxDb.PageSize)
                                 ? owners[keys[k]] : "不在任何页缓存里";
                    w.WriteLine("镜像偏移={0} 类型={1} 单元={2} -> {3}", st, t, ncell, own);
                    if (hits >= 20) break;
                }
                w.WriteLine("共 {0} 处", hits);
            }

            // pagescan [low] [high] -- 全镜像扫"长得像 SQLite 页"的缓冲区，
            // 看看除了页缓存之外，内存里还散落着哪些页（新的内部页就在其中）。
            if (mode == "pagescan")
            {
                int low = args.Length > 1 ? int.Parse(args[1]) : 1;
                int high = args.Length > 2 ? int.Parse(args[2]) : 400000;
                byte[] d = snap.Img.Data;
                Dictionary<int, string> owners = WxSnapshot.PageOwners(snap.Img);
                int[] keys = new int[owners.Count];
                owners.Keys.CopyTo(keys, 0);
                Array.Sort(keys);
                int total = 0, reg = 0;
                for (int o = 0; o + 4096 <= d.Length; o++)
                {
                    byte t = d[o];
                    if (t != 2 && t != 5 && t != 10 && t != 13) continue;
                    int ncell = (d[o + 3] << 8) | d[o + 4];
                    if (ncell == 0 || ncell > 1500) continue;
                    int cs = (d[o + 5] << 8) | d[o + 6];
                    if (cs == 0) cs = 65536;
                    if (cs < 8 + 2 * ncell || cs > 4097) continue;
                    total++;
                    int k = Array.BinarySearch(keys, o);
                    if (k < 0) k = ~k - 1;
                    bool inside = (k >= 0 && o < keys[k] + WxDb.PageSize);
                    if (inside) reg++;
                    if (t != 2 && t != 5) continue;
                    int rm = (d[o + 8] << 24) | (d[o + 9] << 16) |
                             (d[o + 10] << 8) | d[o + 11];
                    if (rm < low || rm > high) continue;
                    w.WriteLine("偏移={0} 类型={1} 单元={2} 最右孩子={3} {4}", o, t, ncell,
                                rm, inside ? "[" + owners[keys[k]] + "]" : "[不在页缓存]");
                }
                w.WriteLine("页形状缓冲 {0} 个，其中落在页缓存里 {1} 个", total, reg);
            }

            // hex <偏移> [字节数] -- 看一段原始内存（找那些"页形状缓冲"的来历）
            if (mode == "hex" && args.Length > 1)
            {
                int o = int.Parse(args[1]);
                int n = args.Length > 2 ? int.Parse(args[2]) : 256;
                byte[] d = snap.Img.Data;
                for (int i = 0; i < n; i += 16)
                {
                    if (o + i + 16 > d.Length) break;
                    StringBuilder hx = new StringBuilder();
                    StringBuilder tx = new StringBuilder();
                    for (int j = 0; j < 16; j++)
                    {
                        byte b = d[o + i + j];
                        hx.Append(b.ToString("x2")).Append(' ');
                        tx.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                    }
                    w.WriteLine("{0:x8}  {1} {2}", o + i, hx.ToString(), tx.ToString());
                }
            }

            // fresh2 <表名> -- 单次抓取内自洽地找"更新的内部页副本"：页缓存里
            // 的根页往往停在旧的孩子列表上，内存里却散落着同一页的新副本
            // （最右孩子更大）。这里按"孩子集合重合度"把它们认出来。
            if (mode == "fresh2" && args.Length > 1)
            {
                int root;
                List<WxDb> pools = snap.PoolsWithTable(args[1], out root);
                w.WriteLine("表 {0} 根页={1}，页池 {2} 个", args[1], root, pools.Count);
                HashSet<int> known = new HashSet<int>();
                foreach (WxDb db in pools)
                    foreach (int c in db.ChildrenOf(root)) known.Add(c);
                int kmax = 0;
                foreach (int c in known) if (c > kmax) kmax = c;
                w.WriteLine("页缓存里的根页孩子 {0} 个，最大 {1}", known.Count, kmax);
                byte[] d = snap.Img.Data;
                int best = -1, bestMax = 0, bestOv = 0, bestCells = 0;
                for (int o = 0; o + 4096 <= d.Length; o++)
                {
                    byte t = d[o];
                    if (t != 5) continue;
                    byte[] pg = new byte[4096];
                    Buffer.BlockCopy(d, o, pg, 0, 4096);
                    List<int> kids = WxDb.ChildrenOfPage(pg, 0);
                    if (kids.Count < 3) continue;
                    int ov = 0, mx = 0;
                    foreach (int c in kids)
                    {
                        if (known.Contains(c)) ov++;
                        if (c > mx) mx = c;
                    }
                    if (ov < 3) continue;
                    if (ov * 2 < known.Count) continue;      // 至少认得出半数
                    if (mx <= kmax) continue;
                    if (mx > bestMax) { best = o; bestMax = mx; bestOv = ov; bestCells = kids.Count; }
                }
                if (best < 0) w.WriteLine("没有找到更新的根页副本");
                else
                    w.WriteLine("更新副本: 偏移={0} 单元={1} 重合={2} 最大孩子={3}",
                                best, bestCells, bestOv, bestMax);
            }

            // probe <表名> -- 用严格页校验在整块内存里找这张表的"最新叶子"：
            // 先找与缓存里根页孩子集合重合最多的合法内部页（同一页的新副本），
            // 再用它最后一个单元的 rowid 去认那张新叶子。
            if (mode == "probe" && args.Length > 1)
            {
                int root;
                List<WxDb> pools = snap.PoolsWithTable(args[1], out root);
                HashSet<int> known = new HashSet<int>();
                foreach (WxDb db in pools)
                    foreach (int c in db.ChildrenOf(root)) known.Add(c);
                w.WriteLine("表 {0} 根页={1} 页池={2} 缓存里根页孩子={3} 个",
                            args[1], root, pools.Count, known.Count);
                byte[] d = snap.Img.Data;
                int bestO = -1, bestOv = 0, bestN = 0;
                for (int o = 0; o + WxDb.PageSize <= d.Length; o++)
                {
                    if (d[o] != 5) continue;
                    int type, ncell, cstart, pc0;
                    if (!WxDb.StrictPage(d, o, out type, out ncell, out cstart, out pc0))
                        continue;
                    List<long[]> cells = WxDb.InteriorCells(d, o);
                    if (cells.Count < 8) continue;
                    int ov = 0;
                    foreach (long[] c in cells)
                        if (c[0] > 0 && c[0] < 400000 && known.Contains((int)c[0])) ov++;
                    if (ov < 8 || ov * 2 < known.Count) continue;
                    if (ov > bestOv) { bestO = o; bestOv = ov; bestN = cells.Count; }
                }
                if (bestO < 0) { w.WriteLine("没找到与缓存根页吻合的合法内部页"); return; }
                List<long[]> bc = WxDb.InteriorCells(d, bestO);
                w.WriteLine("最佳内部页：偏移={0} 单元={1} 重合={2}", bestO, bestN, bestOv);
                w.WriteLine("   最后 6 个单元（孩子, 该子树最大 rowid）:");
                for (int i = Math.Max(0, bc.Count - 6); i < bc.Count; i++)
                    w.WriteLine("      child={0} maxRowid={1}", bc[i][0], bc[i][1]);
                long want = bc[bc.Count - 1][1];
                w.WriteLine("   找 maxRowid={0} 的合法叶子页：", want);
                int found = 0;
                for (int o = 0; o + WxDb.PageSize <= d.Length; o++)
                {
                    if (d[o] != 13) continue;
                    int type, ncell, cstart, pc0;
                    if (!WxDb.StrictPage(d, o, out type, out ncell, out cstart, out pc0))
                        continue;
                    List<SqlRow> rows = pools[0].RowsAt(d, o, 1885);
                    if (rows.Count == 0) continue;
                    long mx = 0;
                    foreach (SqlRow r in rows) if (r.RowId > mx) mx = r.RowId;
                    if (mx != want) continue;
                    found++;
                    w.WriteLine("   命中叶子 偏移={0} 行={1}", o, rows.Count);
                    foreach (SqlRow r in rows)
                    {
                        object[] v = r.Values;
                        if (v.Length != 17) continue;
                        w.WriteLine("      rowid={0} t={1} type={2} {3}", r.RowId,
                                    Time(L(v[5])), v[2], Clip(Convert.ToString(v[12]), 70));
                    }
                }
                w.WriteLine("   共 {0} 张", found);
            }

            if (mode == "msgs" && args.Length > 1)
            {
                string note;
                List<WxMessage> ms = snap.Messages(args[1], out note);
                w.WriteLine("会话 {0} md5={1} {2} 行={3}", args[1],
                            WxSnapshot.Md5Hex(args[1]), note, ms.Count);
                for (int i = 0; i < ms.Count; i++)
                {
                    WxMessage m = ms[i];
                    w.WriteLine("[{0}] id={1,-7} type={2,-4} sender={3,-5} {4}",
                                Time(m.CreateTime), m.LocalId, m.LocalType,
                                m.Sender, Clip(m.Text, 140));
                }
            }
            if (mode == "tables")
            {
                for (int d = 0; d < snap.Dbs.Count; d++)
                {
                    WxDb db = snap.Dbs[d];
                    Dictionary<string, int> t;
                    try { t = db.Tables(); }
                    catch { continue; }
                    if (t.Count == 0) continue;
                    w.WriteLine("\nDB[{0}] DbPages={1} change={2} 缓存页={3} 表={4}",
                                d, db.DbPages, db.ChangeCounter, db.PageOff.Count, t.Count);
                    StringBuilder names = new StringBuilder();
                    foreach (string k in t.Keys)
                    {
                        if (k.StartsWith("Msg_") && k.Length > 40) continue;  // 会话消息表太多
                        names.Append(k).Append(' ');
                    }
                    w.WriteLine("  {0}", Clip(names.ToString(), 900));
                    foreach (object[] mv in db.Master())
                        if (mv.Length >= 5 && (mv[1] as string) == "contact")
                            w.WriteLine("  contact SQL: {0}", mv[4]);
                }
            }

            if (mode == "dump" && args.Length > 1)
            {
                string tbl = args[1];
                int want = args.Length > 2 ? int.Parse(args[2]) : 6;
                foreach (WxDb db in snap.Dbs)
                {
                    Dictionary<string, int> t;
                    try { t = db.Tables(); }
                    catch { continue; }
                    int root;
                    if (!t.TryGetValue(tbl, out root)) continue;
                    w.WriteLine("\nDB DbPages={0} change={1} 缓存页={2}",
                                db.DbPages, db.ChangeCounter, db.PageOff.Count);
                    foreach (object[] mv in db.Master())
                        if (mv.Length >= 5 && (mv[1] as string) == tbl)
                            w.WriteLine("SQL: {0}", mv[4]);
                    int n = 0;
                    foreach (SqlRow r in db.TableRows(root))
                    {
                        StringBuilder line = new StringBuilder();
                        for (int i = 0; i < r.Values.Length; i++)
                        {
                            if (i > 0) line.Append(" | ");
                            line.Append(i).Append('=').Append(Fmt(r.Values[i]));
                        }
                        w.WriteLine("  rowid={0} {1}", r.RowId, Clip(line.ToString(), 400));
                        if (++n >= want) break;
                    }
                    w.WriteLine("  （共列出 {0} 行）", n);
                }
            }
        }
        catch (Exception e)
        {
            w.WriteLine("异常: " + e);
        }
        finally
        {
            if (mode == "all")
            {
                File.WriteAllText("wxread_out.txt", sb.ToString(),
                                  new UTF8Encoding(false));
                Console.WriteLine("已写入 wxread_out.txt ({0} 字节)", sb.Length);
            }
        }
    }

    private static string Text(byte[] d, int s, int e)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = s; i < e; i++)
        {
            byte b = d[i];
            if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
            else if (b >= 0x80) sb.Append((char)b);   // CJK 会看着像乱码，够用了
            else sb.Append(' ');
        }
        return sb.ToString();
    }

    private static long L(object o)
    {
        if (o is long) return (long)o;
        if (o is int) return (int)o;
        return 0;
    }

    private static string Fmt(object v)
    {
        if (v == null) return "NULL";
        if (v is byte[])
        {
            byte[] b = (byte[])v;
            StringBuilder h = new StringBuilder();
            for (int i = 0; i < b.Length && i < 16; i++) h.Append(b[i].ToString("x2"));
            return "<" + b.Length + "B " + h + ">";
        }
        return Convert.ToString(v);
    }

    private static string Time(long t)
    {
        if (t < 1000000000L || t > 4000000000L) return t.ToString();
        return new DateTime(1970, 1, 1).AddSeconds(t).ToLocalTime()
            .ToString("MM-dd HH:mm:ss");
    }

    private static string Clip(string s, int n)
    {
        if (s == null) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
