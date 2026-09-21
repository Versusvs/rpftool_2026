using System;
using System.Collections.Generic;
using System.Text;
using RPFLib.Common;
using RPFLib.RPF3;

namespace RPFTool
{
    // Полная статистика раскладки RPF3 и сравнение двух архивов (оригинал A / результат B).
    // Отчёт: rpf_stats.txt рядом с exe.
    // Секции: сводка по каждому архиву, таблица записей, дельты записей
    // (добавленные/удалённые/сдвинутые), использование нулевого хвоста,
    // побайтовые зоны различий с привязкой к регионам файла.
    internal static class RpfStat
    {
        private const long Page = 0x1000;
        private const long Cluster = 0x4000;

        private class Snap
        {
            public string Path;
            public long Eof, TocSize, DataStart, MinOff, MaxEnd, Trailing, GapTocToData;
            public bool ClusterRule, Encrypted;
            public int EntryCount, Dirs, Files, Compressed, Resources;
            public List<FileEntry> Entries = new List<FileEntry>();          // файлы, сортировка по офсету
            public Dictionary<uint, FileEntry> ByHash = new Dictionary<uint, FileEntry>();
            public List<string> Gaps = new List<string>();
            public long GapsTotal;
        }

        private static long Align(long x, long a)
        {
            return (x + a - 1) / a * a;
        }

        // Имена для человекочитаемости: те же три источника, что и у Version3
        private static Dictionary<uint, string> LoadNames()
        {
            var map = new Dictionary<uint, string>();
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var fn in new[] { "KnownFilenames.txt", "FilenamesMCLA.txt", "CustomFilenames.txt" })
            {
                try
                {
                    string p = System.IO.Path.Combine(baseDir, fn);
                    if (!System.IO.File.Exists(p)) continue;
                    foreach (var line in System.IO.File.ReadLines(p))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        uint h = Hasher.Hash(line);
                        if (!map.ContainsKey(h)) map.Add(h, line);
                    }
                }
                catch { /* не критично */ }
            }
            return map;
        }

        private static string NameOf(Dictionary<uint, string> names, uint hash)
        {
            string n;
            return names.TryGetValue(hash, out n) ? n : string.Format("0x{0:x8}", hash);
        }

        private static Snap Take(RPFLib.RPF3.File f, string path)
        {
            var s = new Snap { Path = path };
            s.Eof = new System.IO.FileInfo(path).Length;
            s.TocSize = f.Header.TOCSize;
            s.EntryCount = f.Header.EntryCount;
            s.Encrypted = f.Header.Encrypted;
            s.DataStart = Align(0x800 + s.TocSize, Page);

            long maxEnd = 0, minOff = long.MaxValue;
            foreach (var e in f.TOC)
            {
                if (e is DirectoryEntry) { s.Dirs++; continue; }
                var fe = e as FileEntry;
                if (fe == null) continue;
                s.Files++;
                if (fe.IsCompressed) s.Compressed++;
                if (fe.IsResourceFile) s.Resources++;
                if (fe.SizeInArchive == 0) continue;
                long end = fe.Offset + (long)fe.SizeInArchive;
                if (end > maxEnd) maxEnd = end;
                if (fe.Offset < minOff) minOff = fe.Offset;
                s.Entries.Add(fe);
                uint h = (uint)fe.NameOffset;
                if (!s.ByHash.ContainsKey(h)) s.ByHash.Add(h, fe);
            }
            if (minOff == long.MaxValue) minOff = 0;

            s.MinOff = minOff;
            s.MaxEnd = maxEnd;
            s.Trailing = s.Eof - maxEnd;
            s.GapTocToData = minOff - (0x800 + s.TocSize);
            s.ClusterRule = (s.Eof == Align(maxEnd, Cluster));

            s.Entries.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            long prev = 0x800 + s.TocSize;
            foreach (var fe in s.Entries)
            {
                long gap = fe.Offset - prev;
                if (gap > 0)
                {
                    s.GapsTotal += gap;
                    s.Gaps.Add(string.Format("    off=0x{0:X} gap=0x{1:X} ({1} bytes)", fe.Offset, gap));
                }
                prev = fe.Offset + (long)fe.SizeInArchive;
            }
            return s;
        }

        private static string Region(Snap s, long off)
        {
            if (off < 0x14) return "HEADER";
            if (off < 0x800) return "HEADER_TAIL";
            if (off < 0x800 + s.TocSize) return "TOC";
            if (off < s.MinOff) return "TOC_PAD";
            if (off < s.MaxEnd) return "DATA";
            if (off < s.Eof) return "TRAILING";
            return "BEYOND_EOF";
        }

        private static void AppendSnap(StringBuilder sb, string tag, Snap s, Dictionary<uint, string> names)
        {
            sb.AppendLine("== ARCHIVE " + tag + ": " + s.Path);
            sb.AppendLine(string.Format(
                "eof=0x{0:X} ({0})  tocSize=0x{1:X}  entries={2}  encrypted={3}",
                s.Eof, s.TocSize, s.EntryCount, s.Encrypted ? 1 : 0));
            sb.AppendLine(string.Format(
                "dataStart=0x{0:X}  firstData=0x{1:X}  maxEnd=0x{2:X}  trailing=0x{3:X} ({3} bytes)  gapTocToData=0x{4:X}",
                s.DataStart, s.MinOff, s.MaxEnd, s.Trailing, s.GapTocToData));
            sb.AppendLine(string.Format(
                "clusterRule(eof==Align(maxEnd,0x4000))={0}", s.ClusterRule ? "YES" : "NO"));
            sb.AppendLine(string.Format(
                "dirs={0} files={1} compressed={2} resources={3}", s.Dirs, s.Files, s.Compressed, s.Resources));
            sb.AppendLine(string.Format(
                "internal gaps: count={0} total=0x{1:X} ({1} bytes)", s.Gaps.Count, s.GapsTotal));
            foreach (var g in s.Gaps) sb.AppendLine(g);
            sb.AppendLine("  # idx offset len m256 gap cmp res name");
            long prev = 0x800 + s.TocSize;
            int idx = 0;
            foreach (var fe in s.Entries)
            {
                long gap = fe.Offset - prev;
                sb.AppendLine(string.Format("  {0} 0x{1:X} 0x{2:X} {3} 0x{4:X} {5} {6} {7}",
                    idx, fe.Offset, fe.SizeInArchive, fe.Offset % 256, gap,
                    fe.IsCompressed ? 1 : 0, fe.IsResourceFile ? 1 : 0,
                    NameOf(names, (uint)fe.NameOffset)));
                prev = fe.Offset + (long)fe.SizeInArchive;
                idx++;
            }
            sb.AppendLine();
        }

        private static void AppendDelta(StringBuilder sb, Snap A, Snap B, Dictionary<uint, string> names)
        {
            sb.AppendLine("== ENTRY DELTA (A -> B, match by name hash)");
            int same = 0, moved = 0, resized = 0;
            foreach (var kv in A.ByHash)
            {
                FileEntry b;
                if (!B.ByHash.TryGetValue(kv.Key, out b)) continue;
                var a = kv.Value;
                if (a.Offset != b.Offset)
                {
                    moved++;
                    if (moved <= 30)
                        sb.AppendLine(string.Format("  MOVED   {0}: 0x{1:X} -> 0x{2:X} (delta {3})",
                            NameOf(names, kv.Key), a.Offset, b.Offset, b.Offset - a.Offset));
                }
                else same++;
                if (a.SizeInArchive != b.SizeInArchive)
                {
                    resized++;
                    if (resized <= 30)
                        sb.AppendLine(string.Format("  RESIZED {0}: 0x{1:X} -> 0x{2:X}",
                            NameOf(names, kv.Key), a.SizeInArchive, b.SizeInArchive));
                }
            }
            sb.AppendLine(string.Format("  sameOffset={0} moved={1} resized={2}", same, moved, resized));

            long addedBytes = 0;
            sb.AppendLine("  ONLY_IN_B (added):");
            foreach (var kv in B.ByHash)
            {
                if (A.ByHash.ContainsKey(kv.Key)) continue;
                var fe = kv.Value;
                long end = fe.Offset + (long)fe.SizeInArchive;
                addedBytes += fe.SizeInArchive;
                string zone;
                if (fe.Offset >= A.MaxEnd && end <= A.Eof) zone = "INSIDE ORIGINAL TRAILING ZEROS";
                else if (fe.Offset >= A.Eof) zone = "BEYOND ORIGINAL EOF";
                else if (fe.Offset >= A.MinOff) zone = "INSIDE ORIGINAL DATA REGION (CHECK OVERLAPS!)";
                else zone = "INSIDE TOC/PAD REGION (ERROR!)";
                sb.AppendLine(string.Format("    {0}: off=0x{1:X} len=0x{2:X} zone={3}",
                    NameOf(names, kv.Key), fe.Offset, fe.SizeInArchive, zone));
            }
            sb.AppendLine("  ONLY_IN_A (deleted):");
            foreach (var kv in A.ByHash)
            {
                if (B.ByHash.ContainsKey(kv.Key)) continue;
                var fe = kv.Value;
                sb.AppendLine(string.Format("    {0}: off=0x{1:X} len=0x{2:X}",
                    NameOf(names, kv.Key), fe.Offset, fe.SizeInArchive));
            }

            long consumed = A.Trailing - B.Trailing;
            sb.AppendLine("== TAIL USAGE");
            sb.AppendLine(string.Format(
                "A.trailing=0x{0:X} ({0})  B.trailing=0x{1:X} ({1})  consumedByNewData=0x{2:X} ({2})  addedBytes=0x{3:X} ({3})",
                A.Trailing, B.Trailing, consumed, addedBytes));
            sb.AppendLine(consumed > 0 && addedBytes <= A.Trailing
                ? "  verdict: added data FIT INTO ORIGINAL TRAILING ZEROS (archive length may stay unchanged)"
                : "  verdict: added data DID NOT fit into original trailing (EOF growth expected)");
            sb.AppendLine();
        }

        private static void AppendByteDiff(StringBuilder sb, Snap A, Snap B)
        {
            sb.AppendLine("== BYTE DIFF ZONES (A vs B)");
            long maxLen = Math.Max(A.Eof, B.Eof);
            var ranges = new List<long[]>();
            const int Chunk = 1 << 16;
            byte[] ba = new byte[Chunk], bb = new byte[Chunk];
            long runStart = -1;

            using (var fa = System.IO.File.OpenRead(A.Path))
            using (var fb = System.IO.File.OpenRead(B.Path))
            {
                long pos = 0;
                while (pos < maxLen)
                {
                    int n = (int)Math.Min(Chunk, maxLen - pos);
                    int ra = fa.Read(ba, 0, n);
                    int rb = fb.Read(bb, 0, n);
                    int m = Math.Min(ra, rb);
                    for (int i = 0; i < m; i++)
                    {
                        bool diff = ba[i] != bb[i];
                        if (diff && runStart < 0) runStart = pos + i;
                        if (!diff && runStart >= 0) { ranges.Add(new[] { runStart, pos + i }); runStart = -1; }
                    }
                    if (m < n && runStart < 0) runStart = pos + m;
                    pos += n;
                }
                if (runStart >= 0) ranges.Add(new[] { runStart, maxLen });
            }

            if (ranges.Count == 0)
            {
                sb.AppendLine("  files are byte-identical");
                sb.AppendLine();
                return;
            }

            long total = 0;
            int shown = 0;
            foreach (var r in ranges)
            {
                total += r[1] - r[0];
                if (shown < 40)
                {
                    sb.AppendLine(string.Format("  [0x{0:X}, 0x{1:X})  len=0x{2:X}  regionA={3}  regionB={4}",
                        r[0], r[1], r[1] - r[0], Region(A, r[0]), Region(B, r[0])));
                    shown++;
                }
            }
            sb.AppendLine(string.Format("  diffRanges={0} totalDiffBytes=0x{1:X} ({1})  firstDiff=0x{2:X}",
                ranges.Count, total, ranges[0][0]));
            sb.AppendLine();
        }

        public static string BuildReport(string pathA, string pathB)
        {
            var names = LoadNames();

            var fa = new RPFLib.RPF3.File();
            fa.Open(pathA);
            var A = Take(fa, pathA);
            fa.Close();

            var fb = new RPFLib.RPF3.File();
            fb.Open(pathB);
            var B = Take(fb, pathB);
            fb.Close();

            var sb = new StringBuilder();
            sb.AppendLine("# RPF3 STATISTICS / COMPARE REPORT");
            sb.AppendLine("A(original)  = " + pathA);
            sb.AppendLine("B(modified)  = " + pathB);
            sb.AppendLine();
            AppendSnap(sb, "A", A, names);
            AppendSnap(sb, "B", B, names);
            AppendDelta(sb, A, B, names);
            AppendByteDiff(sb, A, B);
            return sb.ToString();
        }
    }
}