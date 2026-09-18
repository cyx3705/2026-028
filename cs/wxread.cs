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
        StringBuilder sb = new StringBuilder();
        TextWriter w = mode == "all"
            ? (TextWriter)new StringWriter(sb)
            : Console.Out;
        try
        {
            IntPtr h = WeChatWindow();
            if (h == IntPtr.Zero) { w.WriteLine("找不到微信窗口"); return; }
            uint pid = WxSnapshot.PidOfWeChatWindow(h);
            w.WriteLine("微信窗口 0x{0:x} pid={1}", h.ToInt64(), pid);

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
