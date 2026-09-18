using System.IO;
using RPFLib.Common;

namespace RPFLib.RPF3
{
    internal class Header
    {
        // Раскладка заголовка RPF3 (20 байт с offsets 0x00):
        //   0x00  Identifier    (magic "RPF3" = 0x33465052)
        //   0x04  TOCSize       (размер TOC НА ДИСКЕ, т.е. включая шифрование/padding)
        //   0x08  EntryCount    (число записей в TOC)
        //   0x0C  Unknown1      (назначение неизвестно, круговорачиваем как есть)
        //   0x10  EncryptedFlag (0 = TOC открыт; ненулевое (у MC: -1) = TOC зашифрован AES)
        // Далее до 0x800 — паддинг нулями, TOC начинается с 0x800.

        public Header(File file)
        {
            File = file;
        }

        public HeaderIDs Identifier { get; set; }
        public int TOCSize { get; set; }
        public int EntryCount { get; set; }

        private int Unknown1 { get; set; }
        private int EncryptedFlag { get; set; }

        public File File { get; private set; }

        public bool Encrypted
        {
            get { return EncryptedFlag != 0; }
            set { EncryptedFlag = value ? -1 : 0; }
        }

        public void Read(BinaryReader br)
        {
            Identifier = (HeaderIDs)br.ReadInt32();
            TOCSize = br.ReadInt32();
            EntryCount = br.ReadInt32();
            Unknown1 = br.ReadInt32();
            EncryptedFlag = br.ReadInt32();
        }

        public void Write(BinaryWriter bw)
        {
            bw.Write((int)Identifier);
            bw.Write(TOCSize);      // актуальный размер TOC на диске (ставит save())
            bw.Write(EntryCount);   // актуальное число записей (ставит save())
            bw.Write(Unknown1);     // как прочитали, так и пишем

            // ИЗМЕНЕНО: пишем фактический флаг шифрования вместо константы -1.
            // Архив был зашифрован -> останется зашифрованным (TOC.Write зашифрует буфер).
            // Архив был открытым -> останется открытым.
            bw.Write(EncryptedFlag);
        }
    }
}