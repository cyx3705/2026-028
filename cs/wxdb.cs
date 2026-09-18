// wxdb.cs -- read WeChat's message database WITHOUT the database key.
//
// WeChat 4.x (Weixin.exe) stores its databases in SQLCipher-encrypted files,
// but SQLCipher decrypts every page into SQLite's page cache, so the plaintext
// of any recently used page is sitting in the process's heap.  This module
// recovers those pages and reads them as ordinary SQLite databases.
//
// No key, no brute force, no mouse, no clipboard, no focus change.
//
// C# 5 only (compiled by the csc.exe that ships with Windows).

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace LocalChat
{
    #region win32

    internal static class Native
    {
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_VM_READ = 0x0010;
        public const uint MEM_COMMIT = 0x1000;
        public const uint PAGE_GUARD = 0x100;
        public const uint PAGE_NOACCESS = 0x01;
        public const uint MEM_IMAGE = 0x1000000;

        [StructLayout(LayoutKind.Sequential)]
        public struct MBI64
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public uint __a1;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            public uint __a2;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit,
                                                int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int VirtualQueryEx(IntPtr h, IntPtr addr,
                                                out MBI64 mbi, int len);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr,
                                                    byte[] buf, int size,
                                                    out IntPtr read);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd,
                                                          out uint pid);
    }

    #endregion

    #region memory image

    /// <summary>A snapshot of another process's readable committed memory.</summary>
    public sealed class MemImage
    {
        public byte[] Data;
        public long[] Addr;      // sorted region base addresses
        public int[] Off;        // offset of that region inside Data
        public int[] Size;

        public int RegionCount { get { return Addr.Length; } }

        /// <summary>Virtual address -> index in Data, or -1.</summary>
        public int AddrToOff(long a)
        {
            int lo = 0, hi = Addr.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (a < Addr[mid]) hi = mid - 1;
                else if (a >= Addr[mid] + Size[mid]) lo = mid + 1;
                else return Off[mid] + (int)(a - Addr[mid]);
            }
            return -1;
        }

        public long OffToAddr(int o)
        {
            int lo = 0, hi = Off.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (o < Off[mid]) hi = mid - 1;
                else if (o >= Off[mid] + Size[mid]) lo = mid + 1;
                else return Addr[mid] + (o - Off[mid]);
            }
            return 0;
        }
    }

    #endregion

    #region sqlite value model

    public sealed class SqlRow
    {
        public long RowId;
        public object[] Values;
    }

    #endregion

    #region one recovered database

    /// <summary>One database assembled out of decrypted pages found in memory.</summary>
    public sealed class WxDb
    {
        public const int PageSize = 4096;
        public const int Usable = 4016;          // page size - reserve(80)

        public Dictionary<int, int> PageOff = new Dictionary<int, int>();
        public int DbPages;                      // from the page-1 header
        public long ChangeCounter;
        public string Label = "";
        private MemImage _img;
        private List<object[]> _master;
        private HashSet<int> _missing = new HashSet<int>();

        public WxDb(MemImage img) { _img = img; }

        public byte[] Page(int pgno)
        {
            int o;
            if (!PageOff.TryGetValue(pgno, out o)) { _missing.Add(pgno); return null; }
            byte[] d = _img.Data;
            if (o + PageSize > d.Length) { _missing.Add(pgno); return null; }
            byte[] p = new byte[PageSize];
            Buffer.BlockCopy(d, o, p, 0, PageSize);
            return p;
        }

        public int MissingCount { get { return _missing.Count; } }

        // ---- varint / serial types -------------------------------------

        private static int Varint(byte[] b, int p, int end, out long v)
        {
            v = 0;
            for (int i = 0; i < 9; i++)
            {
                if (p + i >= end) { v = 0; return 0; }
                byte c = b[p + i];
                if (i == 8) { v = (v << 8) | (long)c; return 9; }
                v = (v << 7) | (long)(c & 0x7F);
                if ((c & 0x80) == 0) return i + 1;
            }
            return 9;
        }

        private static int SerialSize(long t)
        {
            if (t == 0) return 0;
            if (t <= 4) return (int)t;
            if (t == 5) return 6;
            if (t == 6 || t == 7) return 8;
            if (t >= 8 && t <= 11) return 0;
            if (t % 2 == 0) return (int)((t - 12) / 2);
            return (int)((t - 13) / 2);
        }

        private static object[] ParseRecord(byte[] rec, int len)
        {
            long hlen;
            int n = Varint(rec, 0, len, out hlen);
            if (n == 0 || hlen < n || hlen > len) return null;
            List<long> types = new List<long>();
            int i = n;
            while (i < hlen)
            {
                long t;
                int m = Varint(rec, i, len, out t);
                if (m == 0) return null;
                types.Add(t);
                i += m;
            }
            object[] vals = new object[types.Count];
            int off = (int)hlen;
            for (int k = 0; k < types.Count; k++)
            {
                long t = types[k];
                int sz = SerialSize(t);
                if (off + sz > len) return null;
                if (t == 0) { vals[k] = null; }
                else if (t >= 13 && (t % 2) == 1)
                {
                    vals[k] = Encoding.UTF8.GetString(rec, off, sz);
                }
                else if (t >= 12 && (t % 2) == 0)
                {
                    byte[] b = new byte[sz];
                    Buffer.BlockCopy(rec, off, b, 0, sz);
                    vals[k] = b;
                }
                else if (sz == 0) { vals[k] = (t == 9) ? (object)1L : (object)0L; }
                else
                {
                    long v = 0;
                    for (int j = 0; j < sz; j++)
                        v = (v << 8) | (long)rec[off + j];
                    vals[k] = v;
                }
                off += sz;
            }
            return vals;
        }

        // ---- payload assembly (overflow pages) -------------------------

        private byte[] Assemble(byte[] page, int off, long payload, int pgno,
                                out int localLen)
        {
            localLen = 0;
            if (payload < 0 || payload > 64L * 1024 * 1024) return null;
            int U = Usable;
            int X = U - 35;
            int local, extra;
            if (payload <= X) { local = (int)payload; extra = 0; }
            else
            {
                int M = ((U - 12) * 32 / 255) - 23;
                int K = M + (int)((payload - M) % (U - 4));
                if (K <= X) { local = K; extra = (int)(payload - K); }
                else { local = M; extra = (int)(payload - M); }
            }
            localLen = local;
            if (off + local > PageSize) return null;
            byte[] outb = new byte[payload];
            Buffer.BlockCopy(page, off, outb, 0, local);
            if (extra == 0) return outb;
            if (off + local + 4 > PageSize) return null;
            int next = (page[off + local] << 24) | (page[off + local + 1] << 16) |
                       (page[off + local + 2] << 8) | page[off + local + 3];
            int need = extra, w = local, guard = 0;
            while (next != 0 && need > 0 && guard++ < 4096)
            {
                byte[] op = Page(next);
                if (op == null) return null;
                int take = Math.Min(need, U - 4);
                Buffer.BlockCopy(op, 4, outb, w, take);
                w += take; need -= take;
                next = (op[0] << 24) | (op[1] << 16) | (op[2] << 8) | op[3];
            }
            if (need != 0) return null;
            return outb;
        }

        // ---- b-tree walking -------------------------------------------

        private IEnumerable<int[]> LeafPages(int root)
        {
            List<int> stack = new List<int>();
            HashSet<int> seen = new HashSet<int>();
            stack.Add(root);
            while (stack.Count > 0)
            {
                int pg = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                if (!seen.Add(pg)) continue;
                byte[] page = Page(pg);
                if (page == null) continue;
                int hdr = (pg == 1) ? 100 : 0;
                byte t = page[hdr];
                if (t == 13 || t == 10)
                {
                    yield return new int[] { pg, hdr };
                }
                else if (t == 5 || t == 2)
                {
                    int ncell = (page[hdr + 3] << 8) | page[hdr + 4];
                    for (int i = 0; i < ncell; i++)
                    {
                        int p = (page[hdr + 8 + 2 * i] << 8) | page[hdr + 9 + 2 * i];
                        if (p + 4 > PageSize) continue;
                        int child = (page[p] << 24) | (page[p + 1] << 16) |
                                    (page[p + 2] << 8) | page[p + 3];
                        if (child > 0) stack.Add(child);
                    }
                    int rm = (page[hdr + 8] << 24) | (page[hdr + 9] << 16) |
                             (page[hdr + 10] << 8) | page[hdr + 11];
                    if (rm > 0) stack.Add(rm);
                }
            }
        }

        /// <summary>All rows of the table whose b-tree starts at <paramref name="root"/>.</summary>
        public List<SqlRow> TableRows(int root)
        {
            List<SqlRow> rows = new List<SqlRow>();
            foreach (int[] lp in LeafPages(root))
            {
                int pg = lp[0], hdr = lp[1];
                byte[] page = Page(pg);
                if (page == null) continue;
                int ncell = (page[hdr + 3] << 8) | page[hdr + 4];
                for (int i = 0; i < ncell; i++)
                {
                    int p = (page[hdr + 8 + 2 * i] << 8) | page[hdr + 9 + 2 * i];
                    if (p < hdr + 8 || p >= PageSize) return rows;
                    long plen, rowid;
                    int n1 = Varint(page, p, PageSize, out plen);
                    if (n1 == 0) return rows;
                    int n2 = Varint(page, p + n1, PageSize, out rowid);
                    if (n2 == 0) return rows;
                    int start = p + n1 + n2;
                    int localLen;
                    byte[] rec = Assemble(page, start, plen, pg, out localLen);
                    if (rec == null) continue;
                    object[] vals = ParseRecord(rec, rec.Length);
                    if (vals == null) continue;
                    SqlRow r = new SqlRow();
                    r.RowId = rowid;
                    r.Values = vals;
                    rows.Add(r);
                }
            }
            return rows;
        }

        /// <summary>Rows of sqlite_master: type, name, tbl_name, rootpage, sql.</summary>
        public List<object[]> Master()
        {
            if (_master != null) return _master;
            _master = new List<object[]>();
            foreach (SqlRow r in TableRows(1))
            {
                if (r.Values.Length >= 5) _master.Add(r.Values);
            }
            return _master;
        }

        public Dictionary<string, int> Tables()
        {
            Dictionary<string, int> t = new Dictionary<string, int>();
            foreach (object[] v in Master())
            {
                if (v.Length >= 5 && v[0] is string && (string)v[0] == "table" &&
                    v[1] is string && v[3] is long)
                {
                    t[(string)v[1]] = (int)(long)v[3];
                }
            }
            return t;
        }

        public bool HasTable(string name)
        {
            return Tables().ContainsKey(name);
        }
    }

    #endregion

    #region reader

    public sealed class WxSession
    {
        public string UserName;
        public long LastTimestamp;
        public long SortTimestamp;
        public int Unread;
        public long LastMsgType;
        public string LastSender;
        public string LastSenderName;
        public string Summary;
    }

    public sealed class WxMessage
    {
        public string Table;
        public long LocalId;
        public long CreateTime;
        public long LocalType;
        public long Sender;
        public string Text;
        public bool FromMe;
        public bool Compressed;
    }

    /// <summary>Everything readable from WeChat's live memory at one instant.</summary>
    public sealed class WxSnapshot
    {
        public List<WxDb> Dbs = new List<WxDb>();
        public MemImage Img;
        public string Note = "";

        private static readonly byte[] SqliteMagic =
            Encoding.ASCII.GetBytes("SQLite format 3\0");

        // ---------- capture ------------------------------------------

        public static uint PidOfWeChatWindow(IntPtr hwnd)
        {
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            return pid;
        }

        public static WxSnapshot Capture(int pid)
        {
            WxSnapshot s = new WxSnapshot();
            MemImage img = ReadMemory(pid);
            if (img == null) { s.Note = "OpenProcess/读取内存失败"; return s; }
            s.Img = img;
            Dictionary<long, Dictionary<int, int>> pagers = EnumeratePageCache(img);
            s.Note = string.Format("内存 {0:F1} MB / {1} 区域, pager {2} 个",
                                   img.Data.Length / 1048576.0, img.RegionCount,
                                   pagers.Count);
            s.Dbs = GroupDatabases(img, pagers);
            return s;
        }

        private static MemImage ReadMemory(int pid)
        {
            IntPtr h = Native.OpenProcess(
                Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_VM_READ,
                false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                List<long> addr = new List<long>();
                List<int> off = new List<int>();
                List<int> size = new List<int>();
                List<byte[]> chunks = new List<byte[]>();

                Native.MBI64 mbi = new Native.MBI64();
                long a = 0;
                long total = 0;
                const int CHUNK = 8 << 20;
                while (a < 0x7FFFFFFFFFFF)
                {
                    int got = Native.VirtualQueryEx(h, (IntPtr)a, out mbi,
                                                    Marshal.SizeOf(mbi));
                    if (got != Marshal.SizeOf(mbi)) break;
                    long b = mbi.BaseAddress.ToInt64();
                    long rs = mbi.RegionSize.ToInt64();
                    if (b < a) break;
                    a = b + rs;
                    if (mbi.State != Native.MEM_COMMIT) continue;
                    if ((mbi.Protect & (Native.PAGE_GUARD | Native.PAGE_NOACCESS)) != 0)
                        continue;
                    if (mbi.Type == Native.MEM_IMAGE) continue;
                    if (rs <= 0 || rs > int.MaxValue) continue;
                    if (total + rs > 1400L * 1024 * 1024) break;

                    int regionOff = 0;
                    for (int i = 0; i < chunks.Count; i++) regionOff += chunks[i].Length;
                    int written = 0;
                    long o = 0;
                    while (o < rs)
                    {
                        int n = (int)Math.Min((long)CHUNK, rs - o);
                        byte[] buf = new byte[n];
                        IntPtr rd;
                        if (Native.ReadProcessMemory(h, (IntPtr)(b + o), buf, n,
                                                     out rd) && rd.ToInt64() > 0)
                        {
                            int rn = (int)rd.ToInt64();
                            if (rn == n) chunks.Add(buf);
                            else
                            {
                                byte[] part = new byte[rn];
                                Buffer.BlockCopy(buf, 0, part, 0, rn);
                                chunks.Add(part);
                            }
                            written += rn;
                        }
                        o += n;
                    }
                    if (written > 0)
                    {
                        addr.Add(b); off.Add(regionOff); size.Add(written);
                        total += written;
                    }
                }

                MemImage img = new MemImage();
                img.Data = new byte[total];
                int p = 0;
                foreach (byte[] c in chunks)
                {
                    Buffer.BlockCopy(c, 0, img.Data, p, c.Length);
                    p += c.Length;
                }
                img.Addr = addr.ToArray();
                img.Off = off.ToArray();
                img.Size = size.ToArray();
                return img;
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }

        // ---------- page cache ---------------------------------------

        private static bool BTreeHeaderAt(byte[] d, int off, out int hdr)
        {
            hdr = -1;
            for (int k = 0; k < 2; k++)
            {
                int h = (k == 0) ? 0 : 100;
                byte t = d[off + h];
                if (t != 2 && t != 5 && t != 10 && t != 13) continue;
                int ncell = (d[off + h + 3] << 8) | d[off + h + 4];
                int cstart = (d[off + h + 5] << 8) | d[off + h + 6];
                if (cstart == 0) cstart = 65536;
                if (ncell > 0 && ncell <= 1500 &&
                    h + 8 + 2 * ncell <= cstart && cstart <= 4097)
                {
                    hdr = h;
                    return true;
                }
            }
            return false;
        }

        private static Dictionary<long, Dictionary<int, int>>
            EnumeratePageCache(MemImage img)
        {
            byte[] d = img.Data;
            int len = d.Length;

            // Find PgHdr1 structs: +0 pBuf -> page data, +16 iKey = page number,
            //                    +32 pCache -> identifies the pager.
            Dictionary<long, List<int>> seeds = new Dictionary<long, List<int>>();
            for (int x = 0; x + 56 <= len; x += 8)
            {
                long pbuf = BitConverter.ToInt64(d, x);
                if (pbuf <= 0x10000 || pbuf >= 0x7FFFFFFFFFFF) continue;
                int po = img.AddrToOff(pbuf);
                if (po < 0 || po + WxDb.PageSize > len) continue;
                int hdr;
                if (!BTreeHeaderAt(d, po, out hdr)) continue;
                uint pgno = BitConverter.ToUInt32(d, x + 16);
                if (pgno < 1 || pgno > 400000) continue;
                // page 1 keeps its b-tree header at offset 100, all others at 0
                if (pgno == 1)
                {
                    if (hdr != 100) continue;
                }
                else if (hdr != 0) continue;
                long pc = BitConverter.ToInt64(d, x + 32);
                if (pc <= 0x10000 || pc >= 0x7FFFFFFFFFFF) continue;
                if (img.AddrToOff(pc) < 0) continue;
                List<int> l;
                if (!seeds.TryGetValue(pc, out l))
                {
                    l = new List<int>();
                    seeds[pc] = l;
                }
                l.Add(x);
            }

            Dictionary<long, Dictionary<int, int>> pagers =
                new Dictionary<long, Dictionary<int, int>>();
            foreach (KeyValuePair<long, List<int>> kv in seeds)
            {
                Dictionary<int, int> pages = new Dictionary<int, int>();
                pagers[kv.Key] = pages;
                foreach (int x0 in kv.Value)
                {
                    WalkLru(img, kv.Key, x0, 40, pages);   // pLruNext
                    WalkLru(img, kv.Key, x0, 48, pages);   // pLruPrev
                }
            }
            return pagers;
        }

        private static void WalkLru(MemImage img, long pager, int x0, int field,
                                    Dictionary<int, int> pages)
        {
            byte[] d = img.Data;
            int cur = x0;
            HashSet<int> seen = new HashSet<int>();
            while (cur >= 0 && cur + 56 <= d.Length && seen.Add(cur))
            {
                long pbuf = BitConverter.ToInt64(d, cur);
                uint pgno = BitConverter.ToUInt32(d, cur + 16);
                long pc = BitConverter.ToInt64(d, cur + 32);
                if (pc != pager) return;
                int po = img.AddrToOff(pbuf);
                if (po >= 0 && po + WxDb.PageSize <= d.Length &&
                    pgno >= 1 && pgno <= 400000)
                {
                    if (!pages.ContainsKey((int)pgno)) pages[(int)pgno] = po;
                }
                long nxt = BitConverter.ToInt64(d, cur + field);
                if (nxt == 0) return;
                int no = img.AddrToOff(nxt);
                if (no < 0) return;
                cur = no;
            }
        }

        // ---------- grouping -----------------------------------------

        private static List<WxDb> GroupDatabases(
            MemImage img, Dictionary<long, Dictionary<int, int>> pagers)
        {
            List<WxDb> dbs = new List<WxDb>();
            List<KeyValuePair<long, Dictionary<int, int>>> orphans =
                new List<KeyValuePair<long, Dictionary<int, int>>>();

            // WeChat opens the same file through several connections, each with
            // its own page cache: merge pagers that describe the same database
            // (same page count and file change counter) so the union of their
            // cached pages is available to the b-tree walk.
            Dictionary<string, WxDb> byKey = new Dictionary<string, WxDb>();
            foreach (KeyValuePair<long, Dictionary<int, int>> kv in pagers)
            {
                int o;
                if (kv.Value.TryGetValue(1, out o) &&
                    o + 15 <= img.Data.Length &&
                    MatchAt(img.Data, o, SqliteMagic))
                {
                    int dbsz = (int)BE32(img.Data, o + 28);
                    long cc = BE32(img.Data, o + 24);
                    string key = dbsz + "/" + cc;
                    WxDb db;
                    if (!byKey.TryGetValue(key, out db))
                    {
                        db = new WxDb(img);
                        db.DbPages = dbsz;
                        db.ChangeCounter = cc;
                        db.Label = key;
                        byKey[key] = db;
                        dbs.Add(db);
                    }
                    foreach (KeyValuePair<int, int> pg in kv.Value)
                    {
                        if (!db.PageOff.ContainsKey(pg.Key))
                            db.PageOff[pg.Key] = pg.Value;
                    }
                }
                else orphans.Add(kv);
            }

            // A pager whose largest page number fits a database belongs to the
            // SMALLEST such database, provided its pages do not contradict what
            // that database already has.
            orphans.Sort(delegate (KeyValuePair<long, Dictionary<int, int>> a,
                                   KeyValuePair<long, Dictionary<int, int>> b)
            {
                return b.Value.Count.CompareTo(a.Value.Count);
            });
            foreach (KeyValuePair<long, Dictionary<int, int>> kv in orphans)
            {
                if (kv.Value.Count < 2) continue;
                int maxpg = 0;
                foreach (int p in kv.Value.Keys) if (p > maxpg) maxpg = p;
                List<WxDb> cand = new List<WxDb>();
                foreach (WxDb db in dbs) if (db.DbPages >= maxpg) cand.Add(db);
                cand.Sort(delegate (WxDb a, WxDb b)
                {
                    return a.DbPages.CompareTo(b.DbPages);
                });
                foreach (WxDb db in cand)
                {
                    bool clash = false;
                    foreach (KeyValuePair<int, int> pg in kv.Value)
                    {
                        int e;
                        if (db.PageOff.TryGetValue(pg.Key, out e) &&
                            !SameBytes(img.Data, e, pg.Value, WxDb.PageSize))
                        { clash = true; break; }
                    }
                    if (clash) continue;
                    foreach (KeyValuePair<int, int> pg in kv.Value)
                        if (!db.PageOff.ContainsKey(pg.Key))
                            db.PageOff[pg.Key] = pg.Value;
                    break;
                }
            }
            return dbs;
        }

        private static bool MatchAt(byte[] d, int o, byte[] pat)
        {
            for (int i = 0; i < pat.Length; i++) if (d[o + i] != pat[i]) return false;
            return true;
        }

        private static bool SameBytes(byte[] d, int a, int b, int n)
        {
            if (a + n > d.Length || b + n > d.Length) return false;
            for (int i = 0; i < n; i++) if (d[a + i] != d[b + i]) return false;
            return true;
        }

        private static long BE32(byte[] d, int o)
        {
            return ((long)d[o] << 24) | ((long)d[o + 1] << 16) |
                   ((long)d[o + 2] << 8) | d[o + 3];
        }

        // ---------- queries ------------------------------------------

        public List<WxSession> Sessions()
        {
            List<WxSession> list = new List<WxSession>();
            WxDb db = PooledWithTable("SessionTable");
            if (db == null) return list;
            int root = db.Tables()["SessionTable"];
            foreach (SqlRow r in db.TableRows(root))
            {
                object[] v = r.Values;
                if (v.Length < 19 || !(v[0] is string)) continue;
                WxSession s = new WxSession();
                s.UserName = (string)v[0];
                s.Unread = (int)AsLong(v[2]);
                s.Summary = v[7] as string;
                s.LastTimestamp = AsLong(v[10]);
                s.SortTimestamp = AsLong(v[11]);
                s.LastMsgType = AsLong(v[14]);
                s.LastSender = v[16] as string;
                s.LastSenderName = v[17] as string;
                list.Add(s);
            }
            list.Sort(delegate (WxSession a, WxSession b)
            {
                return b.LastTimestamp.CompareTo(a.LastTimestamp);
            });
            return list;
        }

        /// <summary>
        /// The most recent cached version of a database that contains the table.
        /// Caches hold several versions of the same file; the file change
        /// counter tells us which one is current.
        /// </summary>
        public WxDb BestWithTable(string table)
        {
            List<WxDb> sorted = new List<WxDb>(Dbs);
            sorted.Sort(delegate (WxDb a, WxDb b)
            {
                return b.ChangeCounter.CompareTo(a.ChangeCounter);
            });
            foreach (WxDb db in sorted)
            {
                try { if (db.HasTable(table)) return db; }
                catch { }
            }
            return null;
        }

        private static bool HasTableSafe(WxDb db, string table)
        {
            try { return db.HasTable(table); }
            catch { return false; }
        }

        /// <summary>Two page caches describe the same file if their schemas agree.</summary>
        private static bool SameFile(WxDb a, WxDb b)
        {
            HashSet<string> A, B;
            try
            {
                A = new HashSet<string>(a.Tables().Keys);
                B = new HashSet<string>(b.Tables().Keys);
            }
            catch { return false; }
            if (A.Count == 0 || B.Count == 0) return false;
            int common = 0;
            foreach (string k in A) if (B.Contains(k)) common++;
            int small = Math.Min(A.Count, B.Count);
            return common >= 3 && common * 2 >= small;
        }

        /// <summary>
        /// Page pool for reading a table: the newest revision of every page we
        /// hold, with gaps filled from older revisions of the same file.  Page
        /// numbers are stable across revisions, so this maximises how much of
        /// the b-tree is reachable while still preferring fresh data.
        /// </summary>
        public WxDb PooledWithTable(string table)
        {
            List<WxDb> sorted = new List<WxDb>(Dbs);
            sorted.Sort(delegate (WxDb a, WxDb b)
            {
                return b.ChangeCounter.CompareTo(a.ChangeCounter);
            });
            WxDb best = null;
            foreach (WxDb db in sorted)
                if (HasTableSafe(db, table)) { best = db; break; }
            if (best == null) return null;
            WxDb pool = new WxDb(Img);
            pool.DbPages = best.DbPages;
            pool.ChangeCounter = best.ChangeCounter;
            pool.Label = best.Label;
            foreach (WxDb db in sorted)
            {
                if (db != best && !SameFile(best, db)) continue;
                foreach (KeyValuePair<int, int> kv in db.PageOff)
                    if (!pool.PageOff.ContainsKey(kv.Key))
                        pool.PageOff[kv.Key] = kv.Value;
            }
            return pool;
        }

        public static string Md5Hex(string s)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
                StringBuilder sb = new StringBuilder();
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>Messages of one conversation, newest first.</summary>
        public List<WxMessage> Messages(string sessionId, out string note)
        {
            string table = "Msg_" + Md5Hex(sessionId);
            List<WxMessage> outl = new List<WxMessage>();
            note = "";
            WxDb db = PooledWithTable(table);
            if (db == null)
            {
                note = "页缓存里没有该会话的消息表";
                return outl;
            }
            {
                int root = db.Tables()[table];
                foreach (SqlRow r in db.TableRows(root))
                {
                    object[] v = r.Values;
                    if (v.Length != 17) continue;
                    WxMessage m = new WxMessage();
                    m.Table = table;
                    m.LocalId = r.RowId;
                    m.LocalType = AsLong(v[2]);
                    m.Sender = AsLong(v[4]);
                    m.CreateTime = AsLong(v[5]);
                    if (v[12] is string) m.Text = (string)v[12];
                    else if (v[12] is byte[])
                    {
                        byte[] raw = (byte[])v[12];
                        if (raw.Length >= 4 && raw[0] == 0x28 && raw[1] == 0xB5 &&
                            raw[2] == 0x2F && raw[3] == 0xFD) m.Compressed = true;
                        else m.Text = Encoding.UTF8.GetString(raw);
                    }
                    outl.Add(m);
                }
                note = string.Format("库 DbPages={0} change={1} 缺 {2} 页",
                                     db.DbPages, db.ChangeCounter,
                                     db.MissingCount);
            }
            outl.Sort(delegate (WxMessage a, WxMessage b)
            {
                return b.CreateTime.CompareTo(a.CreateTime);
            });
            return outl;
        }

        private static long AsLong(object o)
        {
            if (o is long) return (long)o;
            if (o is int) return (int)o;
            return 0;
        }
    }

    #endregion
}
