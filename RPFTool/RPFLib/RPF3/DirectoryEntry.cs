using System.IO;
using RPFLib.Common;
using System;

namespace RPFLib.RPF3
{
    internal class DirectoryEntry : TOCEntry
    {
        public DirectoryEntry(TOC toc)
        {
            TOC = toc;
        }

        public int Flags { get; set; }
        public int ContentEntryIndex { get; set; }
        public int ContentEntryCount { get; set; }
        public override int newEntryIndex { get; set; }

        // ДОБАВЛЕНО: мост к NameOffset, который в RPF3 хранит ХЕШ имени директории
        public uint NameHash
        {
            get { return (uint)NameOffset; }
            set { NameOffset = (int)value; }
        }

        public override bool IsDirectory
        {
            get { return true; }
        }

        public void setContentcount(int ContentCount)
        {
            ContentEntryCount = ContentCount; ;
        }

        public void setContentIndex(int newcontentindex)
        {
            ContentEntryIndex = newcontentindex;
        }

        public void setNewContentIndex(int neEntrywcontentindex)
        {
            newEntryIndex = neEntrywcontentindex;
        }

        public override void Read(BinaryReader br)
        {
            NameOffset = br.ReadInt32();
            Flags = br.ReadInt32();
            ContentEntryIndex = (int)(br.ReadUInt32() & 0x7fffffff);
            ContentEntryCount = br.ReadInt32() & 0x0fffffff;
        }

        public override void Write(BinaryWriter bw)
        {
            // ДОБАВЛЕНО: контроль границ полей (маски взяты из Read)
            if ((ContentEntryIndex & ~0x7fffffff) != 0)
            {
                throw new Exception(string.Format(
                    "Directory '{0}': ContentEntryIndex ({1}) exceeds 31-bit limit.",
                    Name ?? ("hash 0x" + NameHash.ToString("x")), ContentEntryIndex));
            }
            if ((ContentEntryCount & ~0x0fffffff) != 0)
            {
                throw new Exception(string.Format(
                    "Directory '{0}': ContentEntryCount ({1}) exceeds 28-bit limit.",
                    Name ?? ("hash 0x" + NameHash.ToString("x")), ContentEntryCount));
            }

            bw.Write(NameOffset);  // хеш имени директории
            bw.Write(Flags);

            // Бит 0x80000000 — маркер директории (его проверяет ReadAsDirectory через знак числа)
            uint temp = (uint)ContentEntryIndex | 0x80000000;
            bw.Write(temp);
            bw.Write(ContentEntryCount);
        }
    }
}