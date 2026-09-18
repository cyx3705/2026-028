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
using System.Diagnostics;
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
                if (i == 8) { v = (v << 8) + c; return 9; }
                v = (v << 7) + (c & 0x7F);
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
                    // 内部页的头部多一个 4 字节"最右孩子"指针（hdr+8..11），
                    // 单元指针数组从 hdr+12 开始；叶子页才是 hdr+8。原来统一
                    // 按 hdr+8 读，内部页的指针全错位四字节。
                    int ncell = (page[hdr + 3] << 8) | page[hdr + 4];
                    for (int i = 0; i < ncell; i++)
                    {
                        int pp = hdr + 12 + 2 * i;
                        if (pp + 2 > PageSize) break;
                        int p = (page[pp] << 8) | page[pp + 1];
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

        /// <summary>Rows of one leaf page, in cell (rowid) order.</summary>
        private List<SqlRow> LeafCellRows(byte[] page, int hdr, int pg)
        {
            List<SqlRow> rows = new List<SqlRow>();
            int ncell = (page[hdr + 3] << 8) | page[hdr + 4];
            for (int i = 0; i < ncell; i++)
            {
                int p = (page[hdr + 8 + 2 * i] << 8) | page[hdr + 9 + 2 * i];
                if (p < hdr + 8 || p >= PageSize) break;
                long plen, rowid;
                int n1 = Varint(page, p, PageSize, out plen);
                if (n1 == 0) break;
                int n2 = Varint(page, p + n1, PageSize, out rowid);
                if (n2 == 0) break;
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
            return rows;
        }

        /// <summary>
        /// The right-most leaf of a table b-tree.  SQLite orders table b-trees by
        /// rowid, so the newest rows live there -- which means finding new
        /// messages costs O(tree depth) page reads instead of a full scan.
        /// </summary>
        public int[] RightmostLeaf(int root)
        {
            int pg = root;
            for (int guard = 0; guard < 64; guard++)
            {
                byte[] page = Page(pg);
                if (page == null) return null;
                int hdr = (pg == 1) ? 100 : 0;
                byte t = page[hdr];
                if (t == 13) return new int[] { pg, hdr };
                if (t != 5) return null;              // 只处理表 b-tree
                int rm = (page[hdr + 8] << 24) | (page[hdr + 9] << 16) |
                         (page[hdr + 10] << 8) | page[hdr + 11];
                if (rm <= 0) return null;
                pg = rm;
            }
            return null;
        }

        /// <summary>The last <paramref name="max"/> rows of a table, oldest first.</summary>
        public List<SqlRow> TailRows(int root, int max)
        {
            List<SqlRow> all = new List<SqlRow>();
            int[] leaf = RightmostLeaf(root);
            if (leaf != null)
            {
                byte[] page = Page(leaf[0]);
                if (page != null) all = LeafCellRows(page, leaf[1], leaf[0]);
            }
            if (all.Count == 0) return all;           // 最右叶子不在缓存里
            if (all.Count > max) all.RemoveRange(0, all.Count - max);
            return all;
        }

        /// <summary>
        /// 严格校验：把 <paramref name="d"/> 的 <paramref name="off"/> 处当成一个
        /// SQLite b-tree 页来验。单元指针必须全部落在 [cellstart, 4096) 内、
        /// 互不相同，并且最小的那个正好等于 cellstart（SQLite 从 cellstart
        /// 往上连续堆放单元）。随机数据几乎不可能同时满足，所以这个校验可以
        /// 用来在整块内存里认出"真的页"，不必依赖 pcache 的 PgHdr1 版式。
        /// </summary>
        public static bool StrictPage(byte[] d, int off, out int type, out int ncell,
                                      out int cstart, out int pc0)
        {
            type = d[off];
            ncell = 0; cstart = 0; pc0 = 0;
            if (type != 2 && type != 5 && type != 10 && type != 13) return false;
            ncell = (d[off + 3] << 8) | d[off + 4];
            if (ncell == 0 || ncell > 1200) return false;
            cstart = (d[off + 5] << 8) | d[off + 6];
            if (cstart == 0) cstart = 65536;
            pc0 = (type == 2 || type == 5) ? 12 : 8;
            if (cstart < pc0 + 2 * ncell || cstart > 4097) return false;
            int minp = 65536;
            HashSet<int> seen = new HashSet<int>();
            for (int i = 0; i < ncell; i++)
            {
                int pp = off + pc0 + 2 * i;
                int p = (d[pp] << 8) | d[pp + 1];
                if (p < cstart || p + 4 > PageSize) return false;
                if (!seen.Add(p)) return false;
                if (p < minp) minp = p;
            }
            if (cstart != 65536 && minp != cstart) return false;
            return true;
        }

        /// <summary>内部页的 (孩子页号, 该子树最大 rowid) 列表。</summary>
        public static List<long[]> InteriorCells(byte[] d, int off)
        {
            List<long[]> cells = new List<long[]>();
            int type, ncell, cstart, pc0;
            if (!StrictPage(d, off, out type, out ncell, out cstart, out pc0)) return cells;
            if (type != 2 && type != 5) return cells;
            for (int i = 0; i < ncell; i++)
            {
                int pp = off + pc0 + 2 * i;
                int p = off + ((d[pp] << 8) | d[pp + 1]);
                long child = ((long)d[p] << 24) | ((long)d[p + 1] << 16) |
                             ((long)d[p + 2] << 8) | d[p + 3];
                // 表内部页的单元 = 4 字节孩子页号 + varint(rowid)，
                // 这个 rowid 就是该子树里的最大 rowid。
                long key = 0;
                for (int k = 0; k < 9; k++)
                {
                    byte c = d[p + 4 + k];
                    if (k == 8) { key = (key << 8) + c; break; }
                    key = (key << 7) + (long)(c & 0x7F);
                    if ((c & 0x80) == 0) break;
                }
                cells.Add(new long[] { child, key });
            }
            // 最右孩子没有 key，需要用父页自己的上限，调用方另行处理
            return cells;
        }

        /// <summary>把内存里任意偏移的一个叶子页解析成行（诊断/兜底用）。</summary>
        public List<SqlRow> RowsAt(byte[] d, int off, int pgno)
        {
            byte[] page = new byte[PageSize];
            Buffer.BlockCopy(d, off, page, 0, PageSize);
            return LeafCellRows(page, (pgno == 1) ? 100 : 0, pgno);
        }

        /// <summary>表叶子页里 rowid 的最小值/最大值（用来认页）。</summary>
        public static bool LeafRowIds(byte[] d, int off, out long min, out long max)
        {
            min = long.MaxValue; max = long.MinValue;
            int type, ncell, cstart, pc0;
            if (!StrictPage(d, off, out type, out ncell, out cstart, out pc0)) return false;
            if (type != 13) return false;
            for (int i = 0; i < ncell; i++)
            {
                int pp = off + pc0 + 2 * i;
                int p = off + ((d[pp] << 8) | d[pp + 1]);
                int j = 0;
                long plen = 0;
                for (int k = 0; k < 9; k++)
                {
                    byte c = d[p + j];
                    if (k == 8) { plen = (plen << 8) + c; j++; break; }
                    plen = (plen << 7) + (long)(c & 0x7F);
                    j++;
                    if ((c & 0x80) == 0) break;
                }
                long rowid = 0;
                for (int k = 0; k < 9; k++)
                {
                    byte c = d[p + j + k];
                    if (k == 8) { rowid = (rowid << 8) + c; break; }
                    rowid = (rowid << 7) + (long)(c & 0x7F);
                    if ((c & 0x80) == 0) break;
                }
                if (rowid < min) min = rowid;
                if (rowid > max) max = rowid;
            }
            return min <= max;
        }

        /// <summary>All rows of the table whose b-tree starts at <paramref name="root"/>.</summary>
        public List<SqlRow> TableRows(int root)
        {
            List<SqlRow> rows = new List<SqlRow>();
            foreach (int[] lp in LeafPages(root))
            {
                byte[] page = Page(lp[0]);
                if (page == null) continue;
                rows.AddRange(LeafCellRows(page, lp[1], lp[0]));
            }
            return rows;
        }

        /// <summary>
        /// 诊断用：把某一页当表叶子页解析，不问它属于哪张表 —— 用来确认
        /// "某个页面里到底是不是我们要的那些行"。
        /// </summary>
        public List<SqlRow> PageRows(int pgno)
        {
            byte[] page = Page(pgno);
            if (page == null) return new List<SqlRow>();
            int hdr = (pgno == 1) ? 100 : 0;
            byte t = page[hdr];
            if (t != 13 && t != 10) return new List<SqlRow>();
            return LeafCellRows(page, hdr, pgno);
        }

        /// <summary>诊断用：页类型（2/5 内部页，10/13 叶子页，-1 不在缓存里）。</summary>
        public int PageType(int pgno)
        {
            byte[] page = Page(pgno);
            if (page == null) return -1;
            return page[(pgno == 1) ? 100 : 0];
        }

        /// <summary>诊断用：内部页的孩子页号（含最右孩子）；不是内部页返回空。</summary>
        public List<int> ChildrenOf(int pgno)
        {
            List<int> kids = new List<int>();
            byte[] page = Page(pgno);
            if (page == null) return kids;
            return ChildrenOfPage(page, (pgno == 1) ? 100 : 0);
        }

        /// <summary>
        /// 内部页的孩子页号。内部页头部：+8 是 4 字节最右孩子指针，+12 起才是
        /// 单元指针数组（叶子页没有那个指针，数组从 +8 开始）。
        /// </summary>
        public static List<int> ChildrenOfPage(byte[] page, int hdr)
        {
            List<int> kids = new List<int>();
            byte t = page[hdr];
            if (t != 5 && t != 2) return kids;
            int ncell = (page[hdr + 3] << 8) | page[hdr + 4];
            for (int i = 0; i < ncell; i++)
            {
                int pp = hdr + 12 + 2 * i;
                if (pp + 2 > PageSize) break;
                int p = (page[pp] << 8) | page[pp + 1];
                if (p + 4 > PageSize) continue;
                int c = (page[p] << 24) | (page[p + 1] << 16) |
                        (page[p + 2] << 8) | page[p + 3];
                if (c > 0) kids.Add(c);
            }
            int rm = (page[hdr + 8] << 24) | (page[hdr + 9] << 16) |
                     (page[hdr + 10] << 8) | page[hdr + 11];
            if (rm > 0) kids.Add(rm);
            return kids;
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

    /// <summary>
    /// 内存里"真的 SQLite 页"的索引 —— 不依赖 pcache 的 PgHdr1 版式。
    ///
    /// 为什么需要它：WeChat 用 SQLCipher，页解密后落在页缓存里，我们靠
    /// PgHdr1 结构能把那些页捞出来；但最新写入的那几页常常**不在**任何
    /// 页缓存里（连接提交后页就被换出/释放了），只以零散的页映像留在内存
    /// 别处。实测：会话表停在几小时前，消息表整个对话一行都读不出来。
    /// 用严格页校验在整块内存里扫一遍，就能把这些页映像一并收进来。
    /// </summary>
    public sealed class MemPageIndex
    {
        /// <summary>内部页：偏移 + 孩子页号 + 每个孩子的最大 rowid + 最右孩子。</summary>
        public sealed class Interior
        {
            public int Off;
            public int Right;
            public int[] Children;
            public long[] Keys;
        }

        /// <summary>表叶子页：偏移 + rowid 范围（rowid 范围用来认页）。</summary>
        public sealed class Leaf
        {
            public int Off;
            public long Min;
            public long Max;
        }

        public List<Interior> Interiors = new List<Interior>();
        public List<Leaf> Leaves = new List<Leaf>();
        public int Scanned;

        public static MemPageIndex Build(byte[] d)
        {
            MemPageIndex ix = new MemPageIndex();
            for (int o = 0; o + WxDb.PageSize <= d.Length; o++)
            {
                byte t = d[o];
                if (t != 2 && t != 5 && t != 10 && t != 13) continue;
                int type, ncell, cstart, pc0;
                if (!WxDb.StrictPage(d, o, out type, out ncell, out cstart, out pc0))
                    continue;
                ix.Scanned++;
                if (type == 2 || type == 5)
                {
                    List<long[]> cells = WxDb.InteriorCells(d, o);
                    if (cells.Count == 0) continue;
                    Interior it = new Interior();
                    it.Off = o;
                    it.Right = (d[o + 8] << 24) | (d[o + 9] << 16) |
                               (d[o + 10] << 8) | d[o + 11];
                    it.Children = new int[cells.Count];
                    it.Keys = new long[cells.Count];
                    for (int i = 0; i < cells.Count; i++)
                    {
                        it.Children[i] = (int)cells[i][0];
                        it.Keys[i] = cells[i][1];
                    }
                    ix.Interiors.Add(it);
                }
                else if (type == 13)
                {
                    long mn, mx;
                    if (!WxDb.LeafRowIds(d, o, out mn, out mx)) continue;
                    Leaf lf = new Leaf();
                    lf.Off = o;
                    lf.Min = mn;
                    lf.Max = mx;
                    ix.Leaves.Add(lf);
                }
            }
            return ix;
        }
    }

    /// <summary>Everything readable from WeChat's live memory at one instant.</summary>
    public sealed class WxSnapshot
    {
        public List<WxDb> Dbs = new List<WxDb>();
        public MemImage Img;
        public string Note = "";
        private MemPageIndex _index;

        /// <summary>整块内存里的"真页"索引，第一次用到时才扫。</summary>
        public MemPageIndex PageIndex
        {
            get
            {
                if (_index == null && Img != null) _index = MemPageIndex.Build(Img.Data);
                return _index;
            }
        }

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

        /// <summary>
        /// Capture from the given pid, but if that process turns out to hold no
        /// page cache, try the other Weixin processes.  A window handle can
        /// belong to a tray or renderer process rather than the main one, and
        /// only the main process owns the databases.
        /// </summary>
        public static WxSnapshot CaptureAuto(int preferredPid)
        {
            WxSnapshot s = Capture(preferredPid);
            if (s.Dbs.Count > 0) return s;
            string note = s.Note;
            try
            {
                Process[] ps = Process.GetProcessesByName("Weixin");
                for (int i = 0; i < ps.Length; i++)
                {
                    if (ps[i].Id == preferredPid) continue;
                    WxSnapshot t = Capture(ps[i].Id);
                    if (t.Dbs.Count > 0)
                    {
                        t.Note = t.Note + "（pid " + ps[i].Id
                               + "，不是窗口所属的 " + preferredPid + "）";
                        return t;
                    }
                }
            }
            catch { }
            s.Note = note + "；其它 Weixin 进程里也没有页缓存";
            return s;
        }

        /// <summary>
        /// 诊断用（wxread pagers）：列出进程里每一个页缓存，以及它第 1 页所
        /// 描述的库身份 —— 也就是分组之前的样子。用来回答"某张表的页压根不在
        /// 内存里"，还是"在，但被 GroupDatabases 归到别的库去了"。
        /// </summary>
        public static List<string> PagerReport(int pid, out string note,
                                               out Dictionary<int, string> owner)
        {
            List<string> lines = new List<string>();
            owner = new Dictionary<int, string>();
            MemImage img = ReadMemory(pid);
            if (img == null) { note = "读取内存失败"; return lines; }
            note = string.Format("内存 {0:F1} MB / {1} 区域", img.Data.Length / 1048576.0,
                                 img.RegionCount);
            Dictionary<long, Dictionary<int, int>> pagers = EnumeratePageCache(img);
            List<string> rows = new List<string>();
            foreach (KeyValuePair<long, Dictionary<int, int>> kv in pagers)
            {
                int o, dbsz = 0, maxpg = 0;
                long cc = -1;
                bool p1 = kv.Value.TryGetValue(1, out o) &&
                          o + 32 <= img.Data.Length &&
                          MatchAt(img.Data, o, SqliteMagic);
                if (p1) { dbsz = (int)BE32(img.Data, o + 28); cc = BE32(img.Data, o + 24); }
                foreach (KeyValuePair<int, int> pg in kv.Value)
                {
                    if (pg.Key > maxpg) maxpg = pg.Key;
                    string prev;
                    string tag = string.Format("pCache=0x{0:x} pgno={1} dbsz={2} cc={3}",
                                               kv.Key, pg.Key, dbsz, cc);
                    if (owner.TryGetValue(pg.Value, out prev)) owner[pg.Value] = prev + " | " + tag;
                    else owner[pg.Value] = tag;
                }
                rows.Add(string.Format(
                    "pCache=0x{0,-12:x} 页={1,-5} maxpg={2,-6} 有第1页={3,-6} dbsz={4,-6} cc={5}",
                    kv.Key, kv.Value.Count, maxpg, p1 ? "是" : "否", dbsz, cc));
            }
            rows.Sort(StringComparer.Ordinal);
            lines.AddRange(rows);
            return lines;
        }

        public static List<string> PagerReport(int pid, out string note)
        {
            Dictionary<int, string> unused;
            return PagerReport(pid, out note, out unused);
        }

        /// <summary>诊断用：页数据在镜像里的偏移 -> 归属的页缓存与页号。</summary>
        public static Dictionary<int, string> PageOwners(MemImage img)
        {
            Dictionary<long, Dictionary<int, int>> pagers = EnumeratePageCache(img);
            Dictionary<int, string> owner = new Dictionary<int, string>();
            foreach (KeyValuePair<long, Dictionary<int, int>> kv in pagers)
            {
                int o, dbsz = 0;
                long cc = -1;
                bool p1 = kv.Value.TryGetValue(1, out o) &&
                          o + 32 <= img.Data.Length &&
                          MatchAt(img.Data, o, SqliteMagic);
                if (p1) { dbsz = (int)BE32(img.Data, o + 28); cc = BE32(img.Data, o + 24); }
                foreach (KeyValuePair<int, int> pg in kv.Value)
                {
                    string tag = string.Format("pCache=0x{0:x} pgno={1} dbsz={2} cc={3}",
                                               kv.Key, pg.Key, dbsz, cc);
                    string prev;
                    if (owner.TryGetValue(pg.Value, out prev)) owner[pg.Value] = prev + " | " + tag;
                    else owner[pg.Value] = tag;
                }
            }
            return owner;
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
                    WalkLru(img, kv.Key, x0, 24, pages);   // pNext（哈希链）
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
            // 会话表的页缓存同样是"一个连接一份、快慢不一"：按 change counter
            // 挑"最新修订版"反而会挑到旧的那一份 —— 实测同一次抓取里
            // cc=3639721513 的那版把 TT 停在 17:14，而 cc=1166134073 的那版
            // 界面上已经是 22:31。change counter 只是连接自己缓存里的
            // page 1 副本，不能当新鲜度用。
            //
            // 改成每一版都读一遍，同一个 username 取 last_timestamp 最大的
            // 那一行：时间戳本身就是新鲜度的证据。
            Dictionary<string, WxSession> byName = new Dictionary<string, WxSession>();
            foreach (WxDb db in Dbs)
            {
                Dictionary<string, int> t;
                int root;
                try { t = db.Tables(); }
                catch { continue; }
                if (!t.TryGetValue("SessionTable", out root)) continue;
                foreach (SqlRow r in db.TableRows(root))
                {
                    object[] v = r.Values;
                    if (v.Length < 19 || !(v[0] is string)) continue;
                    string u = (string)v[0];
                    WxSession s = new WxSession();
                    s.UserName = u;
                    s.Unread = (int)AsLong(v[2]);
                    s.Summary = v[7] as string;
                    s.LastTimestamp = AsLong(v[10]);
                    s.SortTimestamp = AsLong(v[11]);
                    s.LastMsgType = AsLong(v[14]);
                    s.LastSender = v[16] as string;
                    s.LastSenderName = v[17] as string;
                    WxSession old;
                    if (byName.TryGetValue(u, out old) &&
                        old.LastTimestamp >= s.LastTimestamp) continue;
                    byName[u] = s;
                }
            }
            List<WxSession> list = new List<WxSession>(byName.Values);
            list.Sort(delegate (WxSession a, WxSession b)
            {
                return b.LastTimestamp.CompareTo(a.LastTimestamp);
            });
            return list;
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
            if (common >= 3 && common * 2 >= small) return true;
            // 缓存里只有一两张表的修订版（sqlite_master 的叶子大多没被缓存）
            // 靠"表名重合"是认不出来的，改看页数：同一个文件的各修订版页数
            // 几乎相同，不同的库（message_0 1881 与 biz_message_0 3702）差很远。
            if (common >= 1)
            {
                int d = Math.Abs(a.DbPages - b.DbPages);
                if (a.DbPages > 0 && b.DbPages > 0 &&
                    d <= Math.Max(4, a.DbPages / 100)) return true;
            }
            return false;
        }

        /// <summary>
        /// 一张表的所有页池。同一个文件在页缓存里通常有好几份（每个连接一份，
        /// 快慢不一），只看其中一份会漏掉最新数据：实测 change counter 大的
        /// 那一份反而是旧的（page 1 是连接自己缓存里的老副本），最新的消息
        /// 只在另一份里。所以根页号取"能读到 sqlite_master 的那一版"，数据页
        /// 则每一版都读一遍再合并 —— 页号在修订版之间是稳定的。
        /// </summary>
        public List<WxDb> PoolsWithTable(string table, out int root)
        {
            root = 0;
            List<WxDb> sorted = new List<WxDb>(Dbs);
            sorted.Sort(delegate (WxDb a, WxDb b)
            {
                return b.ChangeCounter.CompareTo(a.ChangeCounter);
            });
            WxDb schema = null;
            foreach (WxDb db in sorted)
            {
                Dictionary<string, int> t;
                int r;
                try { t = db.Tables(); }
                catch { continue; }
                if (!t.TryGetValue(table, out r) || r <= 0) continue;
                schema = db;
                root = r;
                break;
            }
            List<WxDb> pools = new List<WxDb>();
            if (schema == null) return pools;
            foreach (WxDb db in sorted)
                if (db == schema || SameFile(schema, db)) pools.Add(db);
            return pools;
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

        /// <summary>One Msg_ row -> a message, or null if the shape is wrong.</summary>
        private static WxMessage ToMessage(string table, SqlRow r)
        {
            object[] v = r.Values;
            if (v.Length != 17) return null;
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
            return m;
        }

        /// <summary>Messages of one conversation, newest first.</summary>
        public List<WxMessage> Messages(string sessionId, out string note)
        {
            return MessagesByTable("Msg_" + Md5Hex(sessionId), out note);
        }

        /// <summary>Messages of one Msg_ table, newest first.</summary>
        public List<WxMessage> MessagesByTable(string table, out string note)
        {
            int root;
            List<WxDb> pools = PoolsWithTable(table, out root);
            note = "";
            if (pools.Count == 0)
            {
                note = "页缓存里没有这张表";
                return new List<WxMessage>();
            }
            Dictionary<long, WxMessage> byId = new Dictionary<long, WxMessage>();
            StringBuilder det = new StringBuilder();
            foreach (WxDb db in pools)
            {
                foreach (SqlRow r in db.TableRows(root))
                {
                    WxMessage m = ToMessage(table, r);
                    if (m == null) continue;
                    WxMessage old;
                    if (byId.TryGetValue(m.LocalId, out old) &&
                        old.CreateTime >= m.CreateTime) continue;
                    byId[m.LocalId] = m;
                }
                det.AppendFormat("{0}页/缺{1} ", db.PageOff.Count, db.MissingCount);
            }
            string freshNote;
            foreach (WxMessage m in FreshTail(table, 0, out freshNote))
            {
                WxMessage old;
                if (byId.TryGetValue(m.LocalId, out old) &&
                    old.CreateTime >= m.CreateTime) continue;
                byId[m.LocalId] = m;
            }
            List<WxMessage> outl = new List<WxMessage>(byId.Values);
            note = string.Format("{0} 个页池（{1}）；{2}", pools.Count,
                                 det.ToString().Trim(), freshNote);
            outl.Sort(delegate (WxMessage a, WxMessage b)
            {
                return b.CreateTime.CompareTo(a.CreateTime);
            });
            return outl;
        }

        /// <summary>
        /// 最新那些行 —— 从内存里散落的页映像里取。
        ///
        /// 页缓存那份往往停在几小时前（实测 TT 的会话停在 17:14、消息一行都
        /// 读不出来，而界面上是 22:31）。新写入的页不在任何页缓存里，只以
        /// 页映像的形式留在内存别处。这里：
        ///   1. 用"孩子集合和缓存里的根页高度重合"认出根页的新副本；
        ///   2. 新副本最右单元给出 (孩子页号, 该子树最大 rowid)；
        ///   3. 再用最大/最小 rowid 去内存里认出对应的叶子页映像，读出行。
        /// 认错的代价是读到别的对话的行，所以匹配条件是 rowid 范围必须接得上。
        /// </summary>
        public List<WxMessage> FreshTail(string table, int max, out string note)
        {
            note = "";
            List<WxMessage> outl = new List<WxMessage>();
            int root;
            List<WxDb> pools = PoolsWithTable(table, out root);
            if (pools.Count == 0) { note = "没有页池"; return outl; }
            MemPageIndex ix = PageIndex;
            if (ix == null || ix.Interiors.Count == 0) { note = "内存里没有可用页"; return outl; }

            // 缓存里根页的孩子（用来认"同一页的新副本"）
            HashSet<int> known = new HashSet<int>();
            foreach (WxDb db in pools)
                foreach (int c in db.ChildrenOf(root))
                    if (c > 0 && c < 1000000) known.Add(c);
            if (known.Count < 8) { note = "根页不在缓存里"; return outl; }

            MemPageIndex.Interior best = null;
            long bestKey = long.MinValue;
            foreach (MemPageIndex.Interior it in ix.Interiors)
            {
                if (it.Children.Length < 8) continue;
                int ov = 0;
                for (int i = 0; i < it.Children.Length; i++)
                    if (known.Contains(it.Children[i])) ov++;
                if (ov < 8 || ov * 2 < known.Count) continue;
                long last = it.Keys[it.Keys.Length - 1];
                if (best == null || last > bestKey) { best = it; bestKey = last; }
            }
            if (best == null) { note = "没找到更新的根页"; return outl; }

            Dictionary<long, int> keyPg = new Dictionary<long, int>();
            for (int i = 0; i < best.Keys.Length; i++)
                if (!keyPg.ContainsKey(best.Keys[i])) keyPg[best.Keys[i]] = best.Children[i];
            long lastCellKey = best.Keys[best.Keys.Length - 1];

            byte[] d = Img.Data;
            Dictionary<long, WxMessage> byId = new Dictionary<long, WxMessage>();
            int byPage = 0, byImage = 0;
            bool rightRead = false;

            // (a) 最稳的一条：新根页直接给了孩子页号，页号在页缓存里就直接读。
            //     最右孩子（最右指针）没带 key，但它就在页号上摆着。
            List<int> pgs = new List<int>();
            if (best.Right > 0 && best.Right < 1000000) pgs.Add(best.Right);
            for (int i = best.Children.Length - 1; i >= 0 && pgs.Count < 5; i--)
                if (best.Children[i] > 0 && best.Children[i] < 1000000) pgs.Add(best.Children[i]);
            foreach (int pg in pgs)
            {
                List<SqlRow> rows = null;
                foreach (WxDb db in pools)
                {
                    if (!db.PageOff.ContainsKey(pg)) continue;
                    rows = db.PageRows(pg);
                    if (rows.Count == 0 && db.PageType(pg) == 5)
                    {
                        int[] lp = db.RightmostLeaf(pg);
                        if (lp != null) rows = db.PageRows(lp[0]);
                    }
                    break;
                }
                if (rows == null || rows.Count == 0) continue;
                if (pg == best.Right) rightRead = true;
                byPage++;
                AddRows(table, rows, byId);
            }

            // (b) 兜底：孩子页号不在页缓存里时，用 rowid 去内存页映像里认页。
            //     两道锁：只认"最大的那个 key"，而且认到的页必须真的比页缓存里
            //     的更新 —— 否则同一段小 rowid 会把别的对话的旧页认成自己的。
            //     最右孩子只有在 (a) 拿不到时才敢按 rowid 猜，而且要挨着接上：
            //     实测放宽到 200 就会把文件传输助手的页认成群聊的。
            long knownMaxTime = 0;
            foreach (WxDb db in pools)
                foreach (SqlRow r in db.TailRows(root, 24))
                    if (r.Values.Length == 17)
                    {
                        long t = AsLong(r.Values[5]);
                        if (t > knownMaxTime) knownMaxTime = t;
                    }
            foreach (MemPageIndex.Leaf lf in ix.Leaves)
            {
                bool same = (lf.Max == lastCellKey);
                bool next = !same && !rightRead && lf.Min > lastCellKey &&
                            lf.Min - lastCellKey <= 20;
                if (!same && !next) continue;
                List<SqlRow> rows = pools[0].RowsAt(d, lf.Off, 0);
                if (rows.Count == 0) continue;
                long newest = 0;
                foreach (SqlRow r in rows)
                    if (r.Values.Length == 17)
                    {
                        long t = AsLong(r.Values[5]);
                        if (t > newest) newest = t;
                    }
                if (newest <= knownMaxTime) continue;
                byImage++;
                AddRows(table, rows, byId);
            }
            outl.AddRange(byId.Values);
            outl.Sort(delegate (WxMessage a, WxMessage b)
            {
                return a.CreateTime.CompareTo(b.CreateTime);
            });
            if (max > 0 && outl.Count > max) outl.RemoveRange(0, outl.Count - max);
            note = string.Format("新根页 最大rowid={0}，按页号 {1} 页、按映像 {2} 页",
                                 lastCellKey, byPage, byImage);
            return outl;
        }

        private static void AddRows(string table, List<SqlRow> rows,
                                   Dictionary<long, WxMessage> byId)
        {
            foreach (SqlRow r in rows)
            {
                WxMessage m = ToMessage(table, r);
                if (m == null) continue;
                WxMessage old;
                if (byId.TryGetValue(m.LocalId, out old) &&
                    old.CreateTime >= m.CreateTime) continue;
                byId[m.LocalId] = m;
            }
        }

        /// <summary>
        /// Newest rows of one Msg_ table: 先按页缓存走最右叶子（O(树深)，
        /// 这是轮询的主路径），再补上"只在内存页映像里"的那几页 —— 最新写入
        /// 的那一页通常恰恰不在页缓存里。
        /// </summary>
        public List<WxMessage> TableTail(string table, int max, out string note)
        {
            int root;
            List<WxDb> pools = PoolsWithTable(table, out root);
            note = "";
            if (pools.Count == 0) { note = "不在页缓存里"; return new List<WxMessage>(); }
            Dictionary<long, WxMessage> byId = new Dictionary<long, WxMessage>();
            foreach (WxDb db in pools)
            {
                // 池子里的最右叶子可能是旧的，所以一次多取几行，再按 local_id
                // 合并、按时间挑新的。
                foreach (SqlRow r in db.TailRows(root, Math.Max(max * 4, 24)))
                {
                    WxMessage m = ToMessage(table, r);
                    if (m == null) continue;
                    WxMessage old;
                    if (byId.TryGetValue(m.LocalId, out old) &&
                        old.CreateTime >= m.CreateTime) continue;
                    byId[m.LocalId] = m;
                }
            }
            string freshNote;
            foreach (WxMessage m in FreshTail(table, 0, out freshNote))
            {
                WxMessage old;
                if (byId.TryGetValue(m.LocalId, out old) &&
                    old.CreateTime >= m.CreateTime) continue;
                byId[m.LocalId] = m;
            }
            List<WxMessage> outl = new List<WxMessage>(byId.Values);
            outl.Sort(delegate (WxMessage a, WxMessage b)
            {
                return a.CreateTime.CompareTo(b.CreateTime);
            });
            if (outl.Count > max) outl.RemoveRange(0, outl.Count - max);
            note = string.Format("{0} 个页池；{1}", pools.Count, freshNote);
            return outl;
        }

        /// <summary>"Msg_" + 32 位小写十六进制，且不是 _SENDERID 之类的索引表。</summary>
        public static bool IsMsgTable(string n)
        {
            if (n == null || n.Length != 4 + 32) return false;
            if (!n.StartsWith("Msg_", StringComparison.Ordinal)) return false;
            for (int i = 4; i < n.Length; i++)
            {
                char c = n[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        /// <summary>
        /// 所有会话消息表的名字：把每个页缓存里认得出的 Msg_ 表都并起来，
        /// 不再只看"某一个最新的库" —— 消息库有两个（message_0.db 与
        /// biz_message_0.db），而且每个库在缓存里还有好几个修订版。
        /// </summary>
        public List<string> MessageTables()
        {
            List<string> names = new List<string>();
            HashSet<string> seen = new HashSet<string>();
            foreach (WxDb db in Dbs)
            {
                Dictionary<string, int> t;
                try { t = db.Tables(); }
                catch { continue; }
                foreach (string k in t.Keys)
                    if (IsMsgTable(k) && seen.Add(k)) names.Add(k);
            }
            names.Sort(StringComparer.Ordinal);
            return names;
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
