using System;
using System.IO;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using RPFLib.Common;

namespace RPFLib.RPF3
{
    internal class TOC : IEnumerable<TOCEntry>
    {
        private List<TOCEntry> _entries = new List<TOCEntry>();
        private string _nameStringTable = "";

        public File File { get; private set; }

        public TOC(File file)
        {
            File = file;
        }

        public int Count
        {
            get { return _entries.Count; }
        }

        public TOCEntry this[int index]
        {
            get { return _entries[index]; }
        }

        // Размер TOC на диске, измеренное правило билдера MC:LA:
        // зашифрованный TOC занимает ВСЮ область от 0x800 до старта данных,
        // округлённую до страницы 0x1000; plaintext = записи + нули до конца региона.
        //   TOCSize = Align(0x800 + EntryCount*16, 0x1000) - 0x800
        // Для 792 и 793 записей это даёт 0x3800 (слово 0x0038 в заголовке бэкапа).
        public int GetStoredSize()
        {
            int entries = _entries.Count * 16;
            if (File.Header.Encrypted)
            {
                int full = 0x800 + entries;
                int aligned = (full + 0xFFF) & ~0xFFF;
                return aligned - 0x800;
            }
            return entries;
        }

        public void Add(TOCEntry entry)
        {
            _entries.Add(entry);
        }

        public void InsertEntry(int index, TOCEntry entry)
        {
            if (index < 0 || index > _entries.Count)
            {
                throw new Exception("InsertEntry: index out of range: " + index);
            }

            _entries.Insert(index, entry);

            foreach (var e in _entries)
            {
                var dir = e as DirectoryEntry;
                if (dir != null && dir.ContentEntryIndex >= index)
                {
                    dir.ContentEntryIndex++;
                }
            }
        }

        public void Delete(TOCEntry entry)
        {
            int pos = _entries.IndexOf(entry);
            if (pos < 0)
            {
                return;
            }

            foreach (var e in _entries)
            {
                var dir = e as DirectoryEntry;
                if (dir != null && !ReferenceEquals(dir, entry) &&
                    pos >= dir.ContentEntryIndex &&
                    pos < dir.ContentEntryIndex + dir.ContentEntryCount)
                {
                    dir.ContentEntryCount--;
                }
            }

            _entries.RemoveAt(pos);

            foreach (var e in _entries)
            {
                var dir = e as DirectoryEntry;
                if (dir != null && dir.ContentEntryIndex > pos)
                {
                    dir.ContentEntryIndex--;
                }
            }
        }

        public IEnumerator<TOCEntry> GetEnumerator()
        {
            return _entries.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public void Read(BinaryReader br)
        {
            if (File.Header.Encrypted)
            {
                int tocSize = File.Header.TOCSize;
                byte[] tocData = br.ReadBytes(tocSize);

                tocData = DataUtil.Decrypt(tocData);

                var ms = new MemoryStream(tocData);
                br = new BinaryReader(ms);
            }

            int entryCount = File.Header.EntryCount;
            for (int i = 0; i < entryCount; i++)
            {
                TOCEntry entry;
                if (TOCEntry.ReadAsDirectory(br))
                {
                    entry = new DirectoryEntry(this);
                }
                else
                {
                    entry = new FileEntry(this);
                }
                entry.Read(br);
                _entries.Add(entry);
            }

            // Остаток региона (нулевой паддинг plaintext, после расшифровки - нули).
            // В RPF3 не используется, но читается, чтобы размер сошёлся
            int stringDataSize = File.Header.TOCSize - File.Header.EntryCount * 16;
            if (stringDataSize > 0)
            {
                byte[] stringData = br.ReadBytes(stringDataSize);
                _nameStringTable = Encoding.ASCII.GetString(stringData);
            }
            else
            {
                _nameStringTable = "";
            }
        }

        public string GetName(int nameOffset)
        {
            if (nameOffset >= 0 && nameOffset < _nameStringTable.Length)
            {
                int nullIdx = _nameStringTable.IndexOf('\0', nameOffset);
                if (nullIdx != -1)
                {
                    return _nameStringTable.Substring(nameOffset, nullIdx - nameOffset);
                }
                return _nameStringTable.Substring(nameOffset);
            }
            return "";
        }

        // Сериализация записей -> дополнение plaintext нулями до размера региона
        // (GetStoredSize) -> шифрование всего региона -> запись.
        // Тогда шифроблок побайтово равен бэкаповому, включая хвост с 0x3980.
        public void Write(BinaryWriter bw)
        {
            int target = GetStoredSize();

            byte[] tocBytes;
            using (var ms = new MemoryStream())
            using (var tempbw = new BinaryWriter(ms))
            {
                foreach (var entry in _entries)
                {
                    entry.Write(tempbw);
                }
                tocBytes = ms.ToArray();
            }

            if (File.Header.Encrypted)
            {
                // дополняем нулями до полного региона (или до блока AES, если вдруг больше)
                int padded = tocBytes.Length;
                if (padded < target) padded = target;
                padded = (padded + 15) & ~15;
                if (padded != tocBytes.Length)
                {
                    Array.Resize(ref tocBytes, padded);
                }
                tocBytes = DataUtil.Encrypt(tocBytes);
            }

            bw.Write(tocBytes);
        }
    }
}