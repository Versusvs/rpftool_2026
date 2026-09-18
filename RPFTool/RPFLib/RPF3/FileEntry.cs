using System;
using System.IO;
using System.Windows.Forms;
//using RageLib.Common.Resources;
using RPFLib.Common;
using ICSharpCode.SharpZipLib.Zip;

namespace RPFLib.RPF3
{
    internal class FileEntry : TOCEntry
    {
        public const int BlockSize = 0x800;

        public FileEntry(TOC toc)
        {
            TOC = toc;
        }

        public int SizeUsed { get; set; }

        public int Size; // (ala uncompressed size)
        public Int64 Offset;
        public int SizeInArchive;
        public bool IsCompressed;
        public bool IsResourceFile;
        public byte ResourceType;
        public uint RSCFlags;
        public int customsize;

        public byte[] CustomData { get; private set; }
        public override int newEntryIndex { get; set; }

        // ДОБАВЛЕНО: мост к полю NameOffset, которое в RPF3 хранит ХЕШ имени
        public uint NameHash
        {
            get { return (uint)NameOffset; }
            set { NameOffset = (int)value; }
        }

        public int getSize()
        {
            return Size;
        }

        public void setIndex(int index)
        {
            newEntryIndex = index; ;
        }

        public void SetCustomData(byte[] data)
        {
            try
            {
                if (data == null)
                {
                    CustomData = null;
                }
                else
                {
                    customsize = data.Length;

                    if (IsCompressed)
                    {
                        data = DataUtil.Compress(data, ICSharpCode.SharpZipLib.Zip.Compression.Deflater.BEST_COMPRESSION);
                    }
                    CustomData = data;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        public override bool IsDirectory
        {
            get { return false; }
        }

        public override void Read(BinaryReader br)
        {
            try
            {
                NameOffset = br.ReadInt32();
                Size = br.ReadInt32();
                Offset = br.ReadInt32();

                uint temp = br.ReadUInt32();
                byte[] compressed = BitConverter.GetBytes(temp);

                IsResourceFile = (temp & 0xC0000000) == 0xC0000000;

                if (IsResourceFile)
                {
                    ResourceType = (byte)(Offset & 0xFF);
                    Offset = (Offset & 0xffffff00);
                    SizeInArchive = Size;
                    IsCompressed = false;
                    RSCFlags = temp;
                }
                else
                {
                    SizeInArchive = (int)(temp & 0x00ffffff);
                    IsCompressed = compressed[3] == 0x40;
                }

                SizeUsed = (int)Math.Ceiling((float)SizeInArchive / BlockSize) * BlockSize;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        public override void Write(BinaryWriter bw)
        {
            // ДОБАВЛЕНО: контроль границ полей перед записью
            if (!IsResourceFile)
            {
                if ((SizeInArchive & ~0x00ffffff) != 0)
                {
                    throw new Exception(string.Format(
                        "File '{0}': size in archive ({1} bytes) exceeds the 24-bit limit (16777215). RPF3 cannot store it.",
                        Name ?? ("hash 0x" + NameHash.ToString("x")), SizeInArchive));
                }
                if (Offset > int.MaxValue)
                {
                    throw new Exception(string.Format(
                        "File '{0}': offset {1} exceeds 32-bit address space of RPF3.",
                        Name ?? ("hash 0x" + NameHash.ToString("x")), Offset));
                }
            }
            else
            {
                // у resource-записей младший байт поля смещения занят типом ресурса
                if ((Offset & 0xFF) != 0)
                {
                    throw new Exception(string.Format(
                        "Resource '{0}': offset must be 256-byte aligned, got {1}.",
                        Name ?? ("hash 0x" + NameHash.ToString("x")), Offset));
                }
            }

            bw.Write(NameOffset);   // хеш имени
            bw.Write(Size);         // размер без сжатия

            if (IsResourceFile)
            {
                bw.Write((int)(Offset | (byte)ResourceType));
                bw.Write(RSCFlags);
            }
            else
            {
                bw.Write((int)Offset);

                var temp = SizeInArchive;
                if (IsCompressed)
                {
                    temp |= 0x40000000; // флаг deflate-сжатия в старшем байте
                }
                bw.Write(temp);
            }
        }
    }
}