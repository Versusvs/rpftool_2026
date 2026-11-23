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

        // Plaintext-хвост региона TOC (байты после записей), захваченный ДОСЛОВНО
        // при расшифровке в Read. Нужен, чтобы при каждом сохранении воспроизводить
        // весь регион байт-в-байт, включая инвариантные ненулевые байты хвоста
        private byte[] _padPlain = null;

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
        // округлённую до страницы 0x1000.
        //   TOCSize = Align(0x800 + EntryCount*16, 0x1000) - 0x800
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

        // Вставка записи по индексу index со сдвигом ContentEntryIndex
        // всех директорий, чьи дети начинаются с index или позже
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

        // Удаление записи: уменьшение ContentEntryCount родительской директории,
        // затем сдвиг ContentEntryIndex директорий, стоявших после точки удаления
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
            byte[] tocData = null;

            if (File.Header.Encrypted)
            {
                int tocSize = File.Header.TOCSize;
                tocData = br.ReadBytes(tocSize);
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

            // Остаток региона: plaintext-хвост после записей.
            // Запоминаем ДОСЛОВНО (нули или нет - не предполагаем),
            // чтобы при сохранении воспроизвести весь регион байт-в-байт
            int stringDataSize = File.Header.TOCSize - File.Header.EntryCount * 16;
            if (stringDataSize > 0)
            {
                byte[] stringData = br.ReadBytes(stringDataSize);
                _padPlain = stringData;
                _nameStringTable = Encoding.ASCII.GetString(stringData);
            }
            else
            {
                _padPlain = new byte[0];
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

        // Сериализация: записи + plaintext-хвост, затем шифрование ВСЕГО региона.
        // Хвост якорится к КОНЦУ региона (конец региона фиксирован, записи растут
        // от начала): при росте числа записей хвост съедается с ГОЛОВЫ - берём
        // хвостовые байты снимка; при уменьшении - недостающие байты дописываются
        // нулями спереди. Таким образом инвариантные байты конца региона
        // (в т.ч. ненулевой последний блок) переживают любые колебания числа записей
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

                if (File.Header.Encrypted)
                {
                    int entriesLen = (int)ms.Length;
                    int padWant = target - entriesLen;
                    if (padWant > 0)
                    {
                        byte[] pad = _padPlain ?? new byte[0];
                        if (pad.Length >= padWant)
                        {
                            // Берём ХВОСТ снимка: инвариантные байты конца региона
                            // (включая ненулевой последний блок) остаются на своих местах
                            tempbw.Write(pad, pad.Length - padWant, padWant);
                        }
                        else
                        {
                            // Снимок короче нужного: недостающие байты - нули спереди,
                            // затем весь снимок дословно
                            tempbw.Write(new byte[padWant - pad.Length]);
                            if (pad.Length > 0)
                            {
                                tempbw.Write(pad, 0, pad.Length);
                            }
                        }
                    }
                }

                tocBytes = ms.ToArray();
            }

            if (File.Header.Encrypted)
            {
                int padded = (tocBytes.Length + 15) & ~15;
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