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

        // Родной старт данных (офсет первого файла) из открытого оригинала.
        // Билдер MC:LA может оставлять зазор между регионом TOC и первым файлом
        // (например, регион кончается на 0x5E000, а данные начинаются с 0x60000),
        // поэтому вычислять dataStart только по TOCSize нельзя
        private long _openDataStart = 0;

        // Служебные зоны оригинала, захваченные при открытии,
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

            // Родной старт данных = офсет первого файла в оригинале
            long minOffset = long.MaxValue;
            foreach (var e in TOC)
            {
                var fe = e as FileEntry;
                if (fe == null || fe.SizeInArchive == 0) continue;
                if (fe.Offset < minOffset) minOffset = fe.Offset;
            }
            if (minOffset == long.MaxValue) minOffset = 0;
            _openDataStart = minOffset;

            // Захват служебных зон оригинала
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
            // try
            // {
            //     DumpLayout(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rpf_layout_dump.txt"));
            // }
            // catch { /* диагностика не должна ломать открытие */ }

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

        // Чтение блока данных (используется извлечением/просмотром, сохранением не вызывается)
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

        // ПОЛНЫЙ АУДИТ ЦЕЛОСТНОСТИ И СТРУКТУРЫ АРХИВА (одного файла, без сравнений).
        // ОШИБКИ = нарушения формата, при которых игра/тул не смогут читать данные:
        //   несовпадение EntryCount/TOCSize с фактическими и со страничным правилом;
        //   регион TOC за EOF; блок за EOF; офсет внутри/раньше региона TOC;
        //   перекрытия блоков; не-ресурс с SizeInArchive вне 24 бит; офсет > int.MaxValue;
        //   ресурс с офсетом не кратным 256 (младший байт офсета занят ResourceType);
        //   диапазоны директорий вне границ TOC и пересечения без вложения;
        //   блок не дочитывается; сжатый блок не распаковывается или длина != Size.
        // ПРЕДУПРЕЖДЕНИЯ = отклонения от правил билдера и аномалии, чтению не мешающие:
        //   ресурс не с границы страницы 0x1000; обычный блок пересекает страницу;
        //   ресурс с SizeInArchive != Size; Size < SizeInArchive у сжатого; нулевой размер;
        //   офсет раньше страничного dataStart; дыры; записи вне директорий.
        // Ресурсы >16MB легальны (SizeInArchive живёт в 32-битном поле Size):
        // они не предупреждаются, а учитываются счётчиком largeResources.
        public string Audit(out int errorCount, out int warnCount)
        {
            var sb = new System.Text.StringBuilder();
            var errors = new List<string>();
            var warns = new List<string>();

            long eof = _stream.Length;

            // --- 1. Заголовок и геометрия TOC ---
            if (!Enum.IsDefined(typeof(HeaderIDs), (int)Header.Identifier))
                errors.Add("header: unknown identifier 0x" + ((int)Header.Identifier).ToString("X8"));
            if (Header.EntryCount != TOC.Count)
                errors.Add(string.Format("header: EntryCount={0}, but TOC holds {1} entries",
                    Header.EntryCount, TOC.Count));
            int expectToc = TOC.GetStoredSize();
            if (Header.TOCSize != expectToc)
                errors.Add(string.Format("header: TOCSize=0x{0:X}, page rule gives 0x{1:X}",
                    Header.TOCSize, expectToc));
            long tocEnd = TocOffset + (long)Header.TOCSize;
            if (tocEnd > eof)
                errors.Add("geometry: TOC region end 0x" + tocEnd.ToString("X") + " is beyond EOF");
            long dataStartRule = Align(tocEnd, Page);

            // --- 2. Сбор записей с глобальными индексами ---
            var files = new List<FileEntry>();
            var dirs = new List<DirectoryEntry>();
            var fileIndex = new Dictionary<FileEntry, int>();
            int gi = 0;
            foreach (var e in TOC)
            {
                var d = e as DirectoryEntry;
                if (d != null) { dirs.Add(d); gi++; continue; }
                var f = e as FileEntry;
                if (f == null) { errors.Add("toc[" + gi + "]: unknown entry type"); gi++; continue; }
                fileIndex[f] = gi;
                files.Add(f);
                gi++;
            }

            // --- 3. Проверки каждой файловой записи + структурные счётчики ---
            long firstData = long.MaxValue, maxEnd = 0;
            long gcdOff = 0;
            int compressedCount = 0, resourceCount = 0, zeroSizeCount = 0, largeResources = 0;
            foreach (var f in files)
            {
                long off = f.Offset;
                int len = f.SizeInArchive;
                string tag = string.Format("entry[{0}] 0x{1:x8}", fileIndex[f], (uint)f.NameOffset);

                if (f.IsCompressed) compressedCount++;
                if (f.IsResourceFile) resourceCount++;
                gcdOff = Gcd(gcdOff, off);

                if (len <= 0)
                {
                    zeroSizeCount++;
                    warns.Add(tag + ": SizeInArchive<=0");
                }
                else if (f.IsResourceFile)
                {
                    // У ресурсов SizeInArchive берётся из 32-битного поля Size:
                    // размеры >16MB легальны, предупреждения не требуют
                    if (len > 0xFFFFFF) largeResources++;
                    if (f.Size != len)
                        warns.Add(tag + ": resource SizeInArchive != Size (0x" +
                            len.ToString("X") + " vs 0x" + f.Size.ToString("X") + ")");
                }
                else
                {
                    // Не-ресурс: размер упакован в 24 бита, старший байт = флаг 0x40
                    if (len > 0xFFFFFF)
                        errors.Add(tag + ": non-resource SizeInArchive 0x" + len.ToString("X") +
                            " exceeds 24-bit field (16777215) - cannot be stored in RPF3");
                }

                if (off > int.MaxValue)
                    errors.Add(tag + ": offset 0x" + off.ToString("X") +
                        " exceeds int32 address space of RPF3");
                if (off < tocEnd)
                    errors.Add(tag + ": offset 0x" + off.ToString("X") + " inside/before TOC region");
                else if (off < dataStartRule)
                    warns.Add(tag + ": offset 0x" + off.ToString("X") +
                        " before page-rounded dataStart 0x" + dataStartRule.ToString("X"));

                if (off + (long)len > eof)
                    errors.Add(tag + ": block end 0x" + (off + len).ToString("X") + " beyond EOF");

                if (f.IsResourceFile)
                {
                    // Формат: младший байт офсета хранит ResourceType => кратность 256 обязательна
                    if ((off & 0xFF) != 0)
                        errors.Add(tag + ": resource offset 0x" + off.ToString("X") +
                            " not 256-byte aligned (low byte holds ResourceType)");
                    // Правило билдера: ресурсы стартуют с новой страницы 0x1000
                    else if (off % Page != 0)
                        warns.Add(tag + ": resource offset 0x" + off.ToString("X") +
                            " not on 0x1000 page boundary (native builder always page-aligns)");
                }
                else
                {
                    // Правило билдера: обычный блок не пересекает границу страницы
                    long inPage = off % Page;
                    if (inPage != 0 && inPage + (long)len > Page)
                        warns.Add(tag + ": non-resource block crosses page boundary (0x" +
                            off.ToString("X") + ", len 0x" + len.ToString("X") + ")");
                }

                if (f.IsCompressed && f.Size < len)
                    warns.Add(tag + ": compressed Size < SizeInArchive");

                if (off < firstData) firstData = off;
                long end = off + (long)len;
                if (end > maxEnd) maxEnd = end;
            }
            if (firstData == long.MaxValue) firstData = 0;

            // --- 4. Перекрытия и зазоры: pagePadding / reserveGaps(с образцами) / holes ---
            var sorted = new List<FileEntry>(files);
            sorted.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            long prevEnd = -1;
            long holesTotal = 0, padTotal = 0, reserveTotal = 0;
            int holesCount = 0, padCount = 0, reserveCount = 0;
            var reserveSamples = new List<string>();
            foreach (var f in sorted)
            {
                long end = f.Offset + (long)f.SizeInArchive;
                if (prevEnd >= 0)
                {
                    if (f.Offset < prevEnd)
                    {
                        errors.Add(string.Format(
                            "overlap: [0x{0:X},0x{1:X}) intersects previous block ending 0x{2:X}",
                            f.Offset, end, prevEnd));
                    }
                    else if (f.Offset > prevEnd)
                    {
                        long gap = f.Offset - prevEnd;
                        if (f.Offset % Page == 0 && gap < Page)
                        {
                            padCount++;
                            padTotal += gap;
                        }
                        else if (f.Offset % Page == 0)
                        {
                            reserveCount++;
                            reserveTotal += gap;
                            if (reserveCount <= 30)
                                reserveSamples.Add(string.Format(
                                    "    reserve: [0x{0:X},0x{1:X}) gap=0x{2:X} ({2}) nextLen=0x{3:X} res={4} cmp={5}",
                                    prevEnd, f.Offset, gap, f.SizeInArchive,
                                    f.IsResourceFile ? 1 : 0, f.IsCompressed ? 1 : 0));
                        }
                        else
                        {
                            holesCount++;
                            holesTotal += gap;
                            if (holesCount <= 50)
                                warns.Add(string.Format("hole: [0x{0:X},0x{1:X}) len=0x{2:X} ({2} bytes)",
                                    prevEnd, f.Offset, gap));
                        }
                    }
                }
                if (end > prevEnd) prevEnd = end;
            }
            long trailing = eof - maxEnd;

            // --- 5. Директории: границы, ламинарность, покрытие ---
            int total = TOC.Count;
            foreach (var d in dirs)
            {
                int s = d.ContentEntryIndex, c = d.ContentEntryCount;
                if (s < 0 || c < 0 || s + c > total)
                    errors.Add(string.Format("dir 0x{0:x8}: range [{1},{2}) out of TOC bounds",
                        (uint)d.NameOffset, s, s + c));
            }
            for (int i = 0; i < dirs.Count; i++)
            {
                int s1 = dirs[i].ContentEntryIndex, e1 = s1 + dirs[i].ContentEntryCount;
                for (int j = i + 1; j < dirs.Count; j++)
                {
                    int s2 = dirs[j].ContentEntryIndex, e2 = s2 + dirs[j].ContentEntryCount;
                    bool disjoint = (e1 <= s2) || (e2 <= s1);
                    bool nested = (s2 >= s1 && e2 <= e1) || (s1 >= s2 && e1 <= e2);
                    if (!disjoint && !nested)
                        errors.Add(string.Format("dirs 0x{0:x8}/0x{1:x8}: intersecting (non-nested) ranges",
                            (uint)dirs[i].NameOffset, (uint)dirs[j].NameOffset));
                }
            }
            var covered = new bool[total];
            foreach (var d in dirs)
            {
                int s = d.ContentEntryIndex, c = d.ContentEntryCount;
                for (int k = s; k < s + c && k < total; k++)
                {
                    if (k >= 0) covered[k] = true;
                }
            }
            int orphans = 0;
            for (int k = 1; k < total; k++)
            {
                if (!covered[k]) orphans++;
            }
            if (orphans > 0)
                warns.Add(orphans + " entries not covered by any directory range");

            // --- 6. Физическая доступность и распаковываемость каждого блока ---
            int readFail = 0, decompressFail = 0;
            foreach (var f in sorted)
            {
                int len = f.SizeInArchive;
                if (len <= 0 || f.Offset + (long)len > eof) continue;
                string tag = string.Format("entry[{0}] 0x{1:x8}", fileIndex[f], (uint)f.NameOffset);
                try
                {
                    byte[] block = new byte[len];
                    _stream.Seek(f.Offset, SeekOrigin.Begin);
                    int got = 0;
                    while (got < len)
                    {
                        int n = _stream.Read(block, got, len - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got != len)
                    {
                        readFail++;
                        if (readFail <= 20)
                            errors.Add(tag + ": short read (" + got + " of " + len + ")");
                        continue;
                    }
                    if (f.IsCompressed)
                    {
                        try
                        {
                            byte[] raw = DataUtil.DecompressDeflate(block, f.Size);
                            if (raw.Length != f.Size)
                            {
                                decompressFail++;
                                if (decompressFail <= 20)
                                    errors.Add(tag + ": decompressed length " + raw.Length + " != Size " + f.Size);
                            }
                        }
                        catch (Exception ex)
                        {
                            decompressFail++;
                            if (decompressFail <= 20)
                                errors.Add(tag + ": decompress failed: " + ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    readFail++;
                    if (readFail <= 20)
                        errors.Add(tag + ": read failed: " + ex.Message);
                }
            }

            // --- 7. Отчёт ---
            sb.AppendLine("# RPF3 INTEGRITY AUDIT");
            sb.AppendLine("file: " + ((FileStream)_stream).Name);
            sb.AppendLine(string.Format(
                "eof=0x{0:X} ({0})  tocSize=0x{1:X}  entries={2}  encrypted={3}",
                eof, Header.TOCSize, Header.EntryCount, Header.Encrypted ? 1 : 0));

            sb.AppendLine("== STRUCTURE");
            sb.AppendLine(string.Format(
                "tocEnd=0x{0:X}  dataStart(page rule)=0x{1:X}  firstData=0x{2:X}  gapTocToData=0x{3:X} ({3} bytes)",
                tocEnd, dataStartRule, firstData, firstData - tocEnd));
            sb.AppendLine(string.Format(
                "maxEnd=0x{0:X}  trailing=0x{1:X} ({1} bytes)  clusterRule(eof==Align(maxEnd,0x4000))={2}",
                maxEnd, trailing, (eof == Align(maxEnd, Cluster)) ? "YES" : "NO"));
            sb.AppendLine(string.Format(
                "eofMod: m4={0} m16={1} m256={2} m2048={3} m4096={4}",
                eof % 4, eof % 16, eof % 256, eof % 2048, eof % 4096));
            sb.AppendLine(string.Format(
                "firstDataMod: m4={0} m16={1} m256={2} m4096={3}  gcdOffsets=0x{4:X}",
                firstData % 4, firstData % 16, firstData % 256, firstData % 4096, gcdOff));
            sb.AppendLine(string.Format(
                "files={0} dirs={1} compressed={2} resources={3} (large>16MB: {4}) zeroSize={5}",
                files.Count, dirs.Count, compressedCount, resourceCount, largeResources, zeroSizeCount));
            sb.AppendLine(string.Format(
                "gaps: all={0} (0x{1:X} bytes)  pagePadding={2} (0x{3:X})  reserveGaps={4} (0x{5:X})  holes={6} (0x{7:X})",
                padCount + reserveCount + holesCount, padTotal + reserveTotal + holesTotal,
                padCount, padTotal, reserveCount, reserveTotal, holesCount, holesTotal));
            if (reserveSamples.Count > 0)
            {
                sb.AppendLine("reserve gap samples (first 30):");
                foreach (var s in reserveSamples) sb.AppendLine(s);
            }
            sb.AppendLine(string.Format(
                "readFail={0} decompressFail={1}", readFail, decompressFail));

            sb.AppendLine();
            sb.AppendLine("== ERRORS (" + errors.Count + ")");
            foreach (var s in errors) sb.AppendLine("  " + s);
            sb.AppendLine();
            sb.AppendLine("== WARNINGS (" + warns.Count + ")");
            foreach (var s in warns) sb.AppendLine("  " + s);

            errorCount = errors.Count;
            warnCount = warns.Count;
            return sb.ToString();
        }

        // СОХРАНЕНИЕ С ПОЛНЫМ ПЕРЕСЧЁТОМ РАСКЛАДКИ (единственный режим).
        // Семантика замены файла:
        //   - заменённая запись (CustomData != null) остаётся НА СВОЕЙ ПОЗИЦИИ
        //     в порядке офсетов: порядок упаковки = все файловые записи,
        //     отсортированные по их ТЕКУЩИМ (старым) офсетам;
        //   - её новый размер определяет конец её блока, и ВСЕ записи ниже
        //     сдвигаются ровно настолько, чтобы освободить место (больший файл)
        //     или занять освободившееся (меньший файл);
        //   - вновь добавленные записи (ещё не размещённые, Offset <= 0)
        //     дописываются в хвост в порядке TOC;
        //   - модульность соблюдается: ресурсы стартуют с новой страницы 0x1000
        //     (=> кратны 256, младший байт офсета свободен под ResourceType),
        //     обычные блоки не пересекают границу страницы, EOF кратен 0x4000;
        //   - TOC пересчитывается целиком: EntryCount, TOCSize (страничное правило),
        //     офсеты/SizeInArchive/Size/SizeUsed каждой записи, затем регион TOC
        //     перешифровывается целиком (TOC.Write);
        //   - EOF = Align(конец данных, 0x4000) СТРОГО: длина - чистая функция
        //     содержимого, поэтому замена на меньший файл сжимает архив обратно;
        //   - нетронутые блоки не читаются в память: копируются потоком из оригинала.
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

                // --- 1. Пересчёт заголовка и размера TOC ---
                Header.EntryCount = TOC.Count;
                Header.TOCSize = TOC.GetStoredSize();

                // --- 2. Точка старта данных: родная из снимка, если совместима с текущим TOC ---
                long minDataStart = TocOffset + Header.TOCSize;
                long dataStart;
                if (_openDataStart > 0 && _openDataStart >= minDataStart)
                {
                    dataStart = _openDataStart;
                }
                else
                {
                    // TOC разросся и съел родной зазор - фолбэк на страничное округление
                    dataStart = Align(minDataStart, Page);
                }

                // --- 3. Единый порядок упаковки: все записи по текущим офсетам;
                //     заменённые остаются на своей позиции, новые (Offset<=0) - в хвосте ---
                var order = new List<FileEntry>();
                var orderKey = new Dictionary<FileEntry, long>();
                int seq = 0;
                foreach (var entry in TOC)
                {
                    var fe = entry as FileEntry;
                    if (fe == null) continue;
                    long key = (fe.Offset > 0) ? fe.Offset : (long.MaxValue / 2) + seq;
                    seq++;
                    orderKey[fe] = key;
                    order.Add(fe);
                }
                order.Sort((a, b) => orderKey[a].CompareTo(orderKey[b]));

                // --- 4. Переупаковка: каждая запись на своей позиции, всё ниже сдвигается ---
                var jobs = new List<CopyJob>();
                long cursor = dataStart;
                foreach (var fe in order)
                {
                    CopyJob job = new CopyJob();
                    int len;

                    if (fe.CustomData != null)
                    {
                        // Заменённая/добавленная запись: новые байты и новый размер
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
                        // Нетронутая запись: тело копируется потоком из оригинала
                        len = fe.SizeInArchive;
                        job.FromStream = true;
                        job.SrcOffset = fe.Offset; // СТАРЫЙ офсет, до переназначения ниже
                    }

                    long off;
                    if (fe.IsResourceFile)
                    {
                        // Ресурсы стартуют с новой страницы 0x1000 (и кратны 256)
                        off = Align(cursor, Page);
                    }
                    else
                    {
                        // Обычные файлы пакуются плотно, не пересекая границу страницы
                        long pageEnd = Align(cursor, Page);
                        off = (cursor + len <= pageEnd) ? cursor : pageEnd;
                    }

                    fe.Offset = off;
                    fe.SizeUsed = ((len + BlockAlign - 1) / BlockAlign) * BlockAlign;

                    job.DstOffset = off;
                    job.Length = len;
                    jobs.Add(job);
                    cursor = off + len;
                }

                // --- 5. EOF строго по содержимому: без страховок по истории открытий ---
                long newLength = Align(cursor, Cluster);

                // --- 6. Шаблон: побайтовая копия текущей версии ---
                System.IO.File.Copy(originalPath, tempPath, true);

                using (var outStream = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                using (var bw = new BinaryWriter(outStream))
                {
                    // Заголовок (EntryCount и TOCSize уже пересчитаны)
                    outStream.Position = 0;
                    Header.Write(bw);

                    // Хвост заголовка 0x14..0x800 - дословно из снимка оригинала
                    if (_headerTail != null)
                    {
                        outStream.Position = 0x14;
                        bw.Write(_headerTail);
                    }

                    // TOC целиком с новыми офсетами/размерами (перешифровывается в TOC.Write)
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

                    // Блоки данных в пересчитанной раскладке
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
                    if (newLength > cursor)
                    {
                        outStream.Position = cursor;
                        byte[] zero = new byte[1 << 16];
                        long left = newLength - cursor;
                        while (left > 0)
                        {
                            int n = (int)Math.Min(zero.Length, left);
                            bw.Write(zero, 0, n);
                            left -= n;
                        }
                    }

                    outStream.SetLength(newLength);
                }

                // --- 7. Подмена оригинала ---
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