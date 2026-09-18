using System.IO;
using RPFLib.Common;

namespace RPFLib.RPF3
{
    internal abstract class TOCEntry : Entry
    {
        // ДОБАВЛЕНО: реальное имя элемента (для GUI и словаря KnownFilenames).
        // В архиве хранится НЕ имя, а его хеш (см. NameOffset).
        public string Name { get; set; }

        // В RPF3 это поле содержит ХЕШ имени (Hasher.Hash), а не смещение в таблице строк:
        // таблицы строк в RPF3 нет вообще.
        public int NameOffset { get; set; }

        public TOC TOC { get; set; }

        public abstract bool IsDirectory { get; }

        public abstract void Read(BinaryReader br);
        public abstract void Write(BinaryWriter bw);
        public abstract int newEntryIndex { get; set; }

        public override void Delete()
        {
            TOC.Delete(this);
        }

        internal static bool ReadAsDirectory(BinaryReader br)
        {
            bool dir;

            br.BaseStream.Seek(8, SeekOrigin.Current);
            dir = br.ReadInt32() < 0;
            br.BaseStream.Seek(-12, SeekOrigin.Current);

            return dir;
        }
    }
}