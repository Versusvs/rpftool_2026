using System;
using System.IO;
using System.Collections.Generic;
using System.Windows.Forms;
using RPFLib.Common;
namespace RPFLib.RPF3
{
    internal class File
    {
        private Stream _stream;
        private const int TocOffset = 0x800;  // TOC всегда начинается с 0x800
        private const int Page = 0x1000;      // страница упаковки данных билдера MC:LA
        private const int Cluster = 0x4000;   // кластер округления EOF билдера MC:LA
        private const int BlockAlign = 0x800; // используется только для SizeUsed
        private const int ResAlign = 256;     // аварийное выравнивание ресурсов (см. save)
        // Снапшот хвоста открытого файла (страховка для архивов с нестандартным хвостом)
        private long _openEof = 0;
        private long _openMaxEnd = 0;
        private bool _openFollowsClusterRule = true;
        // ДОБАВЛЕНО: служебные зоны оригинала, захваченные при открытии,
        // чтобы при каждом сохранении воспроизводить их дословно
        private byte[] _headerTail = null;  // байты 0x14..0x800 (хвост заголовка/паддинг)
        private byte[] _tocPad = null;      // байты между концом TOC и стартом данных
        public File()
        {
            Header = new Header(this);
            TOC = new TOC(this);
        }
        public Header Header { get; private set; }
        public TOC TOC { get; private set; }
        public int Open(string filename)
        {
            _stream = new FileStream(filename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            var br = new BinaryReader(_stream);
            Header.Read(br);
            if (!Enum.IsDefined(typeof(HeaderIDs), (int)Header.Identifier))
            {
                _stream.Close();
                return 0;
            }
            _stream.Seek(TocOffset, SeekOrigin.Begin);
            TOC.Read(br);
            // Снапшот EOF и границ данных открытого файла
            long maxEndSnap = 0;
            long minOffset = long.MaxValue;
            foreach (var e in TOC)
            {
                var fe = e as FileEntry;
                if (fe == null || fe.SizeInArchive == 0) continue;
                if (fe.Offset < minOffset) minOffset = fe.Offset;
                long fe2 = fe.Offset + fe.SizeInArchive;
                if (fe2 > maxEndSnap) maxEndSnap = fe2;
            }
            if (minOffset == long.MaxValue) minOffset = 0;
            _openEof = _stream.Length;
            _openMaxEnd = maxEndSnap;
            _openFollowsClusterRule = (_openEof == Align(maxEndSnap, Cluster));
            // ДОБАВЛЕНО: захват служебных зон оригинала
            _headerTail = new byte[TocOffset - 0x14];
            _stream.Seek(0x14, SeekOrigin.Begin);
            _stream.Read(_headerTail, 0, _headerTail.Length);
            long tocEnd = TocOffset + Header.TOCSize;
            if (minOffset > tocEnd)
            {
                _tocPad = new byte[minOffset - tocEnd];
                _stream.Seek(tocEnd, SeekOrigin.Begin);
                _stream.Read(_tocPad, 0, _tocPad.Length);
            }
            else
            {
                _tocPad = new byte[0];
            }
            // ВРЕМЕННАЯ ДИАГНОСТИКА: дамп раскладки (удалить после финальной проверки)
            //            try
            //            {
            //                DumpLayout(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rpf_layout_dump.txt"));
            //            }
            //            catch { /* диагностика не должна ломать открытие */ }
            return Header.EntryCount;
        }
        public void Close()
        {
            if (_stream != null)
            {
                _stream.Close();
                _stream = null;
            }
        }
        public byte[] ReadData(long offset, int length)
        {
            var buffer = new byte[length];
            _stream.Seek(offset, SeekOrigin.Begin);
            _stream.Read(buffer, 0, length);
            return buffer;
        }
        private static long Gcd(long a, long b)
        {
            while (b != 0)
            {
                long t = a % b;
                a = b;
                b = t;
            }
            return a;
        }
        private static long Align(long x, long a)
        {
            return (x + a - 1) / a * a;
        }
        // Описание одной операции записи блока данных
        private struct CopyJob
        {
            public bool FromStream;   // true - копировать из исходного потока кусками
            public long SrcOffset;    // старый офсет в исходном файле
            public long DstOffset;    // новый офсет во временном файле
            public long Length;
            public byte[] Data;       // для изменённых/добавленных записей
        }
        // Потоковое копирование диапазона между потоками без выделения всего файла в памяти
        private static void CopyRange(Stream src, long srcOffset, Stream dst, long dstOffset, long length)
        {
            if (length <= 0) return;
            byte[] buf = new byte[1 << 22]; // буфер 4 МБ
            src.Seek(srcOffset, SeekOrigin.Begin);
            dst.Seek(dstOffset, SeekOrigin.Begin);
            long left = length;
            while (left > 0)
            {
                int n = (int)Math.Min(buf.Length, left);
                int read = src.Read(buf, 0, n);
                if (read <= 0) break;
                dst.Write(buf, 0, read);
                left -= read;
            }
        }
        // ВРЕМЕННАЯ ДИАГНОСТИКА: дамп раскладки
        public void DumpLayout(string path)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# RPF3 layout dump");
            sb.AppendLine("file: " + ((FileStream)_stream).Name);
            sb.AppendLine(string.Format(
                "identifier=0x{0:X8} tocSize=0x{1:X8} entryCount=0x{2:X8} encrypted={3}",
                (int)Header.Identifier, Header.TOCSize, Header.EntryCount, Header.Encrypted));
            long eof = _stream.Length;
            var tocIndex = new Dictionary<FileEntry, int>();
            int ti = 0;
            foreach (var e in TOC)
            {
                var f = e as FileEntry;
                if (f != null) tocIndex[f] = ti;
                ti++;
            }
            var files = new List<FileEntry>(tocIndex.Keys);
            files.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            long minOff = files.Count > 0 ? files[0].Offset : 0;
            long maxEnd = 0;
            foreach (var fe in files)
            {
                long e2 = fe.Offset + fe.SizeInArchive;
                if (e2 > maxEnd) maxEnd = e2;
            }
            sb.AppendLine(string.Format(
                "eof=0x{0:X} dataStart=0x{1:X} maxEnd=0x{2:X} trailing=0x{3:X}",
                eof, minOff, maxEnd, eof - maxEnd));
            sb.AppendLine(string.Format(
                "eofMod: m4={0} m16={1} m256={2} m2048={3} m4096={4}",
                eof % 4, eof % 16, eof % 256, eof % 2048, eof % 4096));
            sb.AppendLine(string.Format(
                "dataStartMod: m4={0} m16={1} m256={2} m4096={3}  tocEnd=0x{4:X}  gapTocToData=0x{5:X}",
                minOff % 4, minOff % 16, minOff % 256, minOff % 4096,
                TocOffset + Header.TOCSize, minOff - (TocOffset + Header.TOCSize)));
            long gOff = 0;
            foreach (var fe in files) gOff = Gcd(gOff, fe.Offset);
            sb.AppendLine("gcdOffsets=0x" + gOff.ToString("X"));
            sb.AppendLine("# tocIdx offset len m4 m16 m256 m4096 gap pageJump res cmp");
            long prevEnd = TocOffset + Header.TOCSize;
            foreach (var fe in files)
            {
                long gap = fe.Offset - prevEnd;
                string jump = (gap > 0 && fe.Offset % Page == 0) ? "YES" : (gap > 0 ? "odd!" : "-");
                sb.AppendLine(string.Format(
                    "{0} 0x{1:X} 0x{2:X} {3} {4} {5} {6} 0x{7:X} {8} {9} {10}",
                    tocIndex[fe],
                    fe.Offset,
                    fe.SizeInArchive,
                    fe.Offset % 4, fe.Offset % 16, fe.Offset % 256, fe.Offset % 4096,
                    gap,
                    jump,
                    fe.IsResourceFile ? 1 : 0,
                    fe.IsCompressed ? 1 : 0));
                prevEnd = fe.Offset + fe.SizeInArchive;
            }
            System.IO.File.WriteAllText(path, sb.ToString());
        }
        // Сохранение: шаблон оригинала + дословное воспроизведение служебных зон.
        //   1) временный файл = побайтовая копия текущей версии;
        //   2) поверх пишутся: заголовок, хвост заголовка (из снимка первого открытия),
        //      TOC, паддинг TOC->данные (из снимка, с обрезкой если TOC вырос),
        //      блоки данных; трейлинг явно зануляется;
        //   3) упаковка: ПРЕЖНЯЯ (dataStart = Align(0x800+TOCSize, 0x1000), страницы 0x1000,
        //      нетронутые в порядке исходных офсетов, изменённые в конце, плотная укладка);
        //      отличие только в том, что нетронутые блоки не читаются в память,
        //      а копируются потоком при записи (лечение OutOfMemoryException);
        //   4) EOF = Align(конец, 0x4000) + страховка нестандартного хвоста.
        public void save()
        {
            string originalPath = null;
            string tempPath = null;
            try
            {
                if (_stream == null || _stream.Length == 0)
                {
                    return;
                }
                originalPath = ((FileStream)_stream).Name;
                tempPath = originalPath + ".tmp";
                // --- 1. Пересчёт заголовка ---
                Header.EntryCount = TOC.Count;
                Header.TOCSize = TOC.GetStoredSize();
                long dataStart = Align(TocOffset + Header.TOCSize, Page);
                // --- 2. Сбор данных: нетронутые в порядке офсетов, изменённые в конце ---
                var untouched = new List<FileEntry>();
                var changed = new List<FileEntry>();
                foreach (var entry in TOC)
                {
                    var fe = entry as FileEntry;
                    if (fe == null) continue;
                    if (fe.CustomData != null) changed.Add(fe);
                    else untouched.Add(fe);
                }
                untouched.Sort((a, b) => a.Offset.CompareTo(b.Offset));
                var jobs = new List<CopyJob>();
                long cursor = dataStart;
                // --- 3. Страничная упаковка (формулы прежние) ---
                for (int pass = 0; pass < 2; pass++)
                {
                    var list = (pass == 0) ? untouched : changed;
                    foreach (var fe in list)
                    {
                        CopyJob job = new CopyJob();
                        int len;
                        if (fe.CustomData != null)
                        {
                            len = fe.CustomData.Length;
                            fe.SizeInArchive = len;
                            if (fe.IsResourceFile)
                            {
                                fe.Size = len;
                            }
                            else
                            {
                                fe.Size = fe.customsize > 0 ? fe.customsize : len;
                            }
                            job.FromStream = false;
                            job.Data = fe.CustomData;
                        }
                        else
                        {
                            // ТЕЛО В ПАМЯТЬ НЕ ЧИТАЕМ: длина та же (SizeInArchive),
                            // байты будут скопированы потоком на этапе записи
                            len = fe.SizeInArchive;
                            job.FromStream = true;
                            job.SrcOffset = fe.Offset; // СТАРЫЙ офсет, до переназначения ниже
                        }
                        long pagePos = cursor % Page;
                        long off = (pagePos + len <= Page) ? cursor : Align(cursor, Page);
                        // АВАРИЙНОЕ выравнивание: только если ресурс встал не на 256-байтную
                        // границу (FileEntry.Write в этом случае бросает исключение).
                        // В штатных раскладках off уже кратен ResAlign и ветка ничего не меняет
                        if (fe.IsResourceFile && (off % ResAlign) != 0)
                        {
                            long pageEnd = Align(cursor, Page);
                            off = Align(off, ResAlign);
                            if (off + len > pageEnd) off = pageEnd;
                        }
                        fe.Offset = off;
                        fe.SizeUsed = ((len + BlockAlign - 1) / BlockAlign) * BlockAlign;
                        job.DstOffset = off;
                        job.Length = len;
                        jobs.Add(job);
                        cursor = off + len;
                    }
                }
                // --- 4. EOF по правилу билдера + страховка ---
                long newLength = Align(cursor, Cluster);
                if (cursor <= _openMaxEnd && !_openFollowsClusterRule)
                {
                    newLength = Math.Max(_openEof, newLength);
                }
                // --- 5. Шаблон: побайтовая копия текущей версии ---
                System.IO.File.Copy(originalPath, tempPath, true);
                using (var outStream = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                using (var bw = new BinaryWriter(outStream))
                {
                    // Заголовок
                    outStream.Position = 0;
                    Header.Write(bw);
                    // Хвост заголовка 0x14..0x800 - дословно из снимка оригинала
                    if (_headerTail != null)
                    {
                        outStream.Position = 0x14;
                        bw.Write(_headerTail);
                    }
                    // TOC
                    outStream.Position = TocOffset;
                    TOC.Write(bw);
                    // Паддинг между TOC и стартом данных - дословно из снимка,
                    // с обрезкой, если разросшийся TOC съел часть этой зоны
                    if (_tocPad != null && _tocPad.Length > 0)
                    {
                        long pos = TocOffset + Header.TOCSize;
                        int len = (int)Math.Min(_tocPad.Length, dataStart - pos);
                        if (len > 0)
                        {
                            outStream.Position = pos;
                            bw.Write(_tocPad, 0, len);
                        }
                    }
                    // Блоки данных: нетронутые копируются потоком из оригинала кусками по 4 МБ,
                    // изменённые/добавленные пишутся из памяти
                    foreach (var job in jobs)
                    {
                        if (job.FromStream)
                        {
                            CopyRange(_stream, job.SrcOffset, outStream, job.DstOffset, job.Length);
                        }
                        else
                        {
                            outStream.Position = job.DstOffset;
                            bw.Write(job.Data);
                        }
                    }
                    // Трейлинг: явно нули от конца данных до EOF
                    // (вычищает остатки старых данных из шаблона)
                    if (newLength > cursor)
                    {
                        outStream.Position = cursor;
                        bw.Write(new byte[newLength - cursor]);
                    }
                    outStream.SetLength(newLength);
                }
                // --- 6. Подмена оригинала ---
                _stream.Close();
                _stream = null;
                System.IO.File.Delete(originalPath);
                System.IO.File.Move(tempPath, originalPath);
                tempPath = null;
                _stream = new FileStream(originalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
            catch (Exception ex)
            {
                if (tempPath != null && System.IO.File.Exists(tempPath))
                {
                    try { System.IO.File.Delete(tempPath); }
                    catch { /* ignore */ }
                }
                if (_stream == null && originalPath != null && System.IO.File.Exists(originalPath))
                {
                    try
                    {
                        _stream = new FileStream(originalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    }
                    catch { /* ignore */ }
                }
                MessageBox.Show(ex.Message + Environment.NewLine + ex.StackTrace,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
        }
    }
}