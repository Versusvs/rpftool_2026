using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization;
using RPFLib.Common;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace RPFLib.Resources
{
    public class RSCFile
    {
        #region Vars
        private Stream xscStream;
        private RSCHeader hdr;
        public byte[] fileData;

        // Magic numbers (big-endian)
        private const int RSC_MAGIC_PACKED_V1 = 0x05435352;  //  88298322  — LZX compressed
        private const int RSC_MAGIC_PACKED_V2 = 0x06435352;  // 107942738  — zlib compressed
        private const int RSC_MAGIC_PACKED_ENC = unchecked((int)0x85435352); // -2059185326 — encrypted (LZX)
        #endregion

        #region Constructor
        public RSCFile(byte[] fileData)
            : this(new MemoryStream(fileData, false)) { }

        public RSCFile(Stream input)
        {
            this.xscStream = input;
            using (BigEndianBinaryReader reader = new BigEndianBinaryReader(this.xscStream))
            {
                if (!hdr.ReadHeader(reader))
                {
                    MessageBox.Show("Unrecognised header", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                    fileData = null;
                    return;
                }

                byte[] buffer;

                switch (hdr.m_dwMagic)
                {
                    // --------------------------------------------------------
                    // Encrypted LZX (magic 0x85435352)
                    // --------------------------------------------------------
                    case RSC_MAGIC_PACKED_ENC:
                        {
                            string path = System.IO.Path.GetDirectoryName(
                                System.Reflection.Assembly.GetExecutingAssembly().Location)
                                + "\\xcompress32.dll";
                            if (!System.IO.File.Exists(path))
                            {
                                MessageBox.Show(
                                    "xcompress32.dll not found in the RPFTool directory.",
                                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                                fileData = null;
                                return;
                            }

                            if (hdr.m_dwVersion == 2)
                            {
                                reader.BaseStream.Position = 16;
                                buffer = reader.ReadBytes((int)reader.BaseStream.Length - 16);
                                buffer = DataUtil.Decrypt(buffer);
                                using (BigEndianBinaryReader readerFile =
                                    new BigEndianBinaryReader(new MemoryStream(buffer)))
                                {
                                    fileData = new byte[hdr.getSizeV() + hdr.getSizeP()];
                                    readerFile.BaseStream.Position = 8;
                                    if (Decompress(readerFile.ReadBytes(
                                        (int)readerFile.BaseStream.Length), fileData) == -1)
                                    {
                                        MessageBox.Show("Failed to decompress file", "Error",
                                            MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                                        fileData = null;
                                    }
                                }
                            }
                            else
                            {
                                reader.BaseStream.Position = 16;
                                buffer = reader.ReadBytes((int)reader.BaseStream.Length - 16);
                                using (BigEndianBinaryReader readerFile =
                                    new BigEndianBinaryReader(new MemoryStream(buffer)))
                                {
                                    fileData = new byte[hdr.getSizeV() + hdr.getSizeP()];
                                    readerFile.BaseStream.Position = 8;
                                    if (Decompress(readerFile.ReadBytes(
                                        (int)readerFile.BaseStream.Length), fileData) == -1)
                                    {
                                        MessageBox.Show("Failed to decompress file", "Error",
                                            MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                                        fileData = null;
                                    }
                                }
                            }
                        }
                        break;

                    // --------------------------------------------------------
                    // Packed V1 — LZX (magic 0x05435352)
                    // --------------------------------------------------------
                    case RSC_MAGIC_PACKED_V1:
                        {
                            string path = System.IO.Path.GetDirectoryName(
                                System.Reflection.Assembly.GetExecutingAssembly().Location)
                                + "\\xcompress32.dll";
                            if (!System.IO.File.Exists(path))
                            {
                                MessageBox.Show(
                                    "xcompress32.dll not found in the RPFTool directory.",
                                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                                fileData = null;
                                return;
                            }

                            reader.BaseStream.Position = 20;
                            buffer = reader.ReadBytes((int)reader.BaseStream.Length - 20);
                            fileData = new byte[hdr.getSizeV() + hdr.getSizeP()];
                            if (Decompress(buffer, fileData) == -1)
                            {
                                MessageBox.Show("Failed to decompress file", "Error",
                                    MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                                fileData = null;
                            }
                        }
                        break;

                    // --------------------------------------------------------
                    // Packed V2 — zlib (magic 0x06435352)
                    // --------------------------------------------------------
                    case RSC_MAGIC_PACKED_V2:
                        {
                            reader.BaseStream.Position = 12;
                            buffer = reader.ReadBytes((int)reader.BaseStream.Length - 12);
                            fileData = new byte[hdr.getSizeV() + hdr.getSizeP()];
                            if (DecompressZlib(buffer, fileData) == -1)
                            {
                                MessageBox.Show(
                                    "Failed to decompress zlib resource",
                                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                                fileData = null;
                            }
                        }
                        break;

                    default:
                        MessageBox.Show("Unrecognised header", "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                        fileData = null;
                        break;
                }
            }
        }
        #endregion

        #region HeaderStruct
        public struct RSCHeader
        {
            // Header fields
            public int m_dwMagic;
            public int m_dwVersion;
            public int m_dwFlags1;
            public int m_dwFlags2;

            // RSC flags (extended sizes)
            public int dwExtVSize;   // : 14;
            public int dwExtPSize;   // : 14;
            public int _f14_30;      // : 3;
            public bool bUseExtSize; // : 1

            public bool ReadHeader(BigEndianBinaryReader reader)
            {
                try
                {
                    this.m_dwMagic = reader.ReadInt32();
                    switch (m_dwMagic)
                    {
                        case RSC_MAGIC_PACKED_ENC: // 0x85435352 — encrypted
                            this.m_dwVersion = reader.ReadInt32();
                            this.m_dwFlags1 = reader.ReadInt32();
                            this.m_dwFlags2 = reader.ReadInt32();
                            dwExtVSize = (int)(m_dwFlags2 & 0x7FFF);
                            dwExtPSize = (int)((m_dwFlags2 & 0xFFF7000) >> 14);
                            _f14_30 = (int)(m_dwFlags2 & 0x70000000);
                            bUseExtSize = (m_dwFlags2 & unchecked((int)0x80000000))
                                           == unchecked((int)0x80000000);
                            return true;

                        case RSC_MAGIC_PACKED_V1: // 0x05435352 — LZX packed
                            this.m_dwVersion = reader.ReadInt32();
                            this.m_dwFlags1 = reader.ReadInt32();
                            bUseExtSize = false;
                            return true;

                        case RSC_MAGIC_PACKED_V2: // 0x06435352 — zlib packed
                            this.m_dwVersion = reader.ReadInt32();
                            this.m_dwFlags1 = reader.ReadInt32();
                            bUseExtSize = false;
                            return true;

                        default:
                            return false;
                    }
                }
                catch (Exception ex) { return false; }
            }

            public int getSizeV()
            {
                return bUseExtSize
                    ? (dwExtVSize << 12)
                    : ((int)(m_dwFlags1 & 0x7FF) << ((int)((m_dwFlags1 >> 11) & 15) + 8));
            }

            public int getSizeP()
            {
                return bUseExtSize
                    ? (dwExtPSize << 12)
                    : ((int)((m_dwFlags1 >> 15) & 0x7FF) << ((int)((m_dwFlags1 >> 26) & 15) + 8));
            }

            public int getObjectStart()
            {
                return (bUseExtSize && _f14_30 < 4)
                    ? ((dwExtVSize >> (_f14_30 + 1)) << (_f14_30 + 13))
                    : 0;
            }

            public void Dispose() { GC.SuppressFinalize(this); }
        }
        #endregion

        #region Pack support
        public struct RSCLayout
        {
            public int Magic;         // magic (читается BigEndianBinaryReader)
            public int Version;       // dword [4..8)
            public int HeaderEnd;     // конец заголовка = начало 8-байтного преамбула (16 или 12)
            public int PayloadStart;  // начало LZX-данных = HeaderEnd + 8
            public bool Encrypted;    // magic 0x85435352 && version == 2
            public int SizeV;         // размер virtual-секции
            public int SizeP;         // размер physical-секции
        }

        private static readonly byte[] PreambleMarker = { 0x0F, 0xF5, 0x12, 0xF1 };

        public static bool TryParseLayout(byte[] data, out RSCLayout layout)
        {
            layout = new RSCLayout();
            if (data == null || data.Length < 12) return false;

            using (var ms = new MemoryStream(data, false))
            using (var r = new BigEndianBinaryReader(ms))
            {
                layout.Magic = r.ReadInt32();
                layout.Version = r.ReadInt32();
                int flags1 = r.ReadInt32();
                int flags2 = 0;

                switch (layout.Magic)
                {
                    case RSC_MAGIC_PACKED_ENC: // 0x85435352
                        if (data.Length < 16) return false;
                        flags2 = r.ReadInt32();
                        layout.HeaderEnd = 16;
                        layout.Encrypted = (layout.Version == 2);
                        break;

                    case RSC_MAGIC_PACKED_V1: // 0x05435352
                        layout.HeaderEnd = 12;
                        layout.Encrypted = false;
                        flags2 = 0;
                        break;

                    case RSC_MAGIC_PACKED_V2: // 0x06435352
                        layout.HeaderEnd = 12;
                        layout.Encrypted = false;
                        flags2 = 0;
                        break;

                    default:
                        return false;
                }

                layout.PayloadStart = layout.HeaderEnd + 8;

                bool useExt = (flags2 & unchecked((int)0x80000000)) == unchecked((int)0x80000000);
                if (useExt)
                {
                    layout.SizeV = (int)(flags2 & 0x7FFF) << 12;
                    layout.SizeP = (int)((flags2 & 0xFFF7000) >> 14) << 12;
                }
                else
                {
                    layout.SizeV = (int)(flags1 & 0x7FF) << (((int)((flags1 >> 11) & 15)) + 8);
                    layout.SizeP = (int)((flags1 >> 15) & 0x7FF) << (((int)((flags1 >> 26) & 15)) + 8);
                }
                return true;
            }
        }

        private static int FindStreamEnd(byte[] stream, int needBytes)
        {
            int pos = 0, unc = 0;
            while (pos < stream.Length && unc < needBytes)
            {
                int b0 = stream[pos];
                int comp;
                if (b0 == 0xFF)
                {
                    if (pos + 5 > stream.Length) return -1;
                    unc += (stream[pos + 1] << 8) | stream[pos + 2];
                    comp = (stream[pos + 3] << 8) | stream[pos + 4];
                    pos += 5;
                }
                else
                {
                    if (pos + 2 > stream.Length) return -1;
                    unc += 0x8000;
                    comp = (b0 << 8) | stream[pos + 1];
                    pos += 2;
                }
                if (comp < 0 || pos + comp > stream.Length) return -1;
                pos += comp;
            }
            return (unc >= needBytes && pos <= stream.Length) ? pos : -1;
        }

        public static byte[] Pack(byte[] originalPacked, byte[] newFlatData, out string error)
        {
            error = null;

            RSCLayout L;
            if (!TryParseLayout(originalPacked, out L))
            {
                error = "Donor file is not a supported RSC container (bad magic or too short).";
                return null;
            }

            // Для packed_v2 (zlib) используем отдельный метод
            if (L.Magic == RSC_MAGIC_PACKED_V2)
            {
                return PackZlib(originalPacked, newFlatData, out error);
            }

            // Для packed_v1 (LZX) используем стандартный код
            string dllPath = System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) + "\\xcompress32.dll";
            if (!System.IO.File.Exists(dllPath))
            {
                error = "xcompress32.dll not found in the RPFTool directory.";
                return null;
            }

            int total = L.SizeV + L.SizeP;
            if (newFlatData == null || newFlatData.Length != total)
            {
                error = string.Format(
                    "Unpacked data length {0} != donor V+P = {1} (V=0x{2:X}, P=0x{3:X}). " +
                    "In-place edit must preserve the total size.",
                    newFlatData == null ? 0 : newFlatData.Length, total, L.SizeV, L.SizeP);
                return null;
            }

            byte[] compressed = Compress(newFlatData);
            if (compressed == null || compressed.Length == 0)
            {
                error = "LZX compression failed.";
                return null;
            }

            // Шаг 1: обрезаем поток до реально нужного размера, если он длиннее.
            int streamEnd = FindStreamEnd(compressed, total);
            if (streamEnd > 0 && streamEnd < compressed.Length)
                Array.Resize(ref compressed, streamEnd);

            // Копируем 8 байт преамбулы (маркер + размер) из оригинала, если есть.
            byte[] pre = new byte[8];
            if (originalPacked.Length >= L.PayloadStart)
            {
                Buffer.BlockCopy(originalPacked, L.HeaderEnd, pre, 0, 8);
            }
            else
            {
                Buffer.BlockCopy(PreambleMarker, 0, pre, 0, 4);
            }

            // Записываем размер сжатого поля. 
            // Округление до 8 байт ((compressed.Length + 7) & ~7) УБРАНО по вашему требованию.
            // Теперь записывается точная длина сжатых данных.
            int fieldLen = compressed.Length;
            pre[4] = (byte)(fieldLen >> 24);
            pre[5] = (byte)(fieldLen >> 16);
            pre[6] = (byte)(fieldLen >> 8);
            pre[7] = (byte)fieldLen;

            var payload = new MemoryStream();
            payload.Write(pre, 0, 8);
            payload.Write(compressed, 0, compressed.Length);
            byte[] body = payload.ToArray();

            if (L.Encrypted)
            {
                // AES требует кратности 16 байт: дополняем нулями
                int pad = (16 - (body.Length & 15)) & 15;
                if (pad > 0)
                {
                    byte[] padded = new byte[body.Length + pad];
                    Buffer.BlockCopy(body, 0, padded, 0, body.Length);
                    body = padded;
                }
                body = DataUtil.Encrypt(body);
            }

            byte[] result = new byte[L.HeaderEnd + body.Length];
            Buffer.BlockCopy(originalPacked, 0, result, 0, L.HeaderEnd); // заголовок без изменений
            Buffer.BlockCopy(body, 0, result, L.HeaderEnd, body.Length);
            return result;
        }

        public static byte[] PackZlib(byte[] originalPacked, byte[] newFlatData, out string error)
        {
            error = null;
            RSCLayout L;
            if (!TryParseLayout(originalPacked, out L))
            {
                error = "Donor file is not a supported RSC container.";
                return null;
            }
            if (L.Magic != RSC_MAGIC_PACKED_V2)
            {
                error = "PackZlib only supports packed_v2 (zlib) containers.";
                return null;
            }
            int total = L.SizeV + L.SizeP;
            if (newFlatData == null || newFlatData.Length != total)
            {
                error = string.Format(
                    "Unpacked data length {0} != donor V+P = {1} (V=0x{2:X}, P=0x{3:X}).",
                    newFlatData == null ? 0 : newFlatData.Length, total, L.SizeV, L.SizeP);
                return null;
            }
            byte[] donorPayload = new byte[originalPacked.Length - 12];
            Buffer.BlockCopy(originalPacked, 12, donorPayload, 0, donorPayload.Length);
            byte[] compressed = null;
            if (ZlibNative.IsAvailable)
            {
                byte hint = donorPayload[1];
                // КЭША ПО FLG НЕТ: FLG не уникален (разные доноры с одним FLG
                // запакованы с разными уровнями). Уровень подбирается КАЖДЫЙ раз под донора.
                byte[] donorFlat = InflateTo(donorPayload, total);
                int level = ZlibNative.MatchLevelOnDonor(donorFlat, donorPayload,
                                                         ZlibNative.LevelFromFlg(hint));
                if (level < 0)
                {
                    level = ZlibNative.LevelFromFlg(hint);
                    LastZlibReport = string.Format(
                        "zlib1.dll: ни один level не воспроизвёл донора (FLG=0x{0:X2}); " +
                        "взят level {1} (поток валиден, НЕ побайтов)", hint, level);
                }
                else
                {
                    LastZlibReport = string.Format(
                        "zlib1.dll: level {0} воспроизведён на доноре -> репак побайтов", level);
                }
                compressed = ZlibNative.Compress(newFlatData, level);
            }
            if (compressed == null)
            {
                compressed = CompressZlib(newFlatData);
                LastZlibReport = "zlib1.dll не найдена: managed DeflateStream (валидно, НЕ побайтов)";
            }
            if (compressed == null || compressed.Length == 0)
            {
                error = "zlib compression failed.";
                return null;
            }
            byte[] result = new byte[12 + compressed.Length];
            Buffer.BlockCopy(originalPacked, 0, result, 0, 12);
            Buffer.BlockCopy(compressed, 0, result, 12, compressed.Length);
            return result;
        }
        #endregion



        #region zlib1.dll P/Invoke (вариант А: побайтовые репаки)
        public static class ZlibNative
        {
            private const string DllName = "zlib1.dll";
            private const int Z_OK = 0;

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "compress2")]
            private static extern int compress2_native(byte[] dest, ref uint destLen, byte[] src, uint srcLen, int level);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "compressBound")]
            private static extern uint compressBound_native(uint srcLen);

            // ИСПРАВЛЕНО: Указано полное имя System.IO.File и System.IO.Path
            private static readonly bool _available =
                System.IO.File.Exists(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DllName));

            // ИСПРАВЛЕНО: Классический синтаксис свойства для старых версий C#
            public static bool IsAvailable
            {
                get { return _available; }
            }

            // FLEVEL (bits 6-7 FLG) -> представительский level: 0->1, 1->5, 2->6, 3->9
            public static int LevelFromFlg(byte flg)
            {
                return new[] { 1, 5, 6, 9 }[(flg >> 6) & 3];
            }

            // Полный zlib-поток (заголовок + deflate + Adler32) одним вызовом
            public static byte[] Compress(byte[] data, int level)
            {
                if (!_available || data == null || data.Length == 0) return null;
                try
                {
                    uint bound = compressBound_native((uint)data.Length);
                    byte[] dest = new byte[bound];
                    uint destLen = bound;
                    int rc = compress2_native(dest, ref destLen, data, (uint)data.Length, level);
                    if (rc != Z_OK) return null;
                    Array.Resize(ref dest, (int)destLen);
                    return dest;
                }
                catch (DllNotFoundException) { return null; }   // не та битность dll
                catch (BadImageFormatException) { return null; }
            }

            // Самотест на доноре: какой level воспроизводит донорский payload побайтово.
            // Возвращает -1, если ни один level не совпал (другая версия zlib).
            public static int MatchLevelOnDonor(byte[] donorFlat, byte[] donorPayload, int hintLevel)
            {
                if (!_available || donorFlat == null || donorPayload == null) return -1;
                int[] order = { hintLevel, 6, 9, 1, 2, 3, 4, 5, 7, 8 };
                var seen = new HashSet<int>();
                foreach (int lv in order)
                {
                    if (lv < 0 || lv > 9 || !seen.Add(lv)) continue;
                    byte[] cand = Compress(donorFlat, lv);
                    if (cand != null && cand.Length == donorPayload.Length && cand.SequenceEqual(donorPayload))
                        return lv;
                }
                return -1;
            }
        }

        // Диагностика последнего прогона PackZlib (для сообщений/лога)
        // ВАЖНО: Убедитесь, что эти поля находятся ВНУТРИ класса RSCFile, 
        // а не просто висят в пространстве имен RPFLib.Resources!
        public static string LastZlibReport { get; private set; }
        private static bool _zlibLevelCacheValid;
        private static byte _zlibLevelCacheFlg;
        private static int _zlibLevelCache = -1;
        #endregion


        #region De/Compression

        [HandleProcessCorruptedStateExceptionsAttribute]
        int Decompress(byte[] compressedData, byte[] decompressedData)
        {
            int target = decompressedData.Length;
            if (compressedData == null || compressedData.Length == 0 || target <= 0)
                return -1;

            const int Slack = 1 << 20;
            byte[] src = new byte[compressedData.Length + Slack];
            Buffer.BlockCopy(compressedData, 0, src, 0, compressedData.Length);
            byte[] dst = new byte[target + Slack];

            int DecompressionContext = 0;
            int hr = xcompress.XMemCreateDecompressionContext(
                xcompress.XMEMCODEC_TYPE.XMEMCODEC_LZX,
                0, 0, ref DecompressionContext);

            int compressedLen = src.Length;
            int decompressedLen = dst.Length;
            try
            {
                hr = xcompress.XMemDecompress(DecompressionContext,
                    dst, ref decompressedLen,
                    src, compressedLen);
            }
            catch
            {
                hr = -1;
            }

            if (DecompressionContext != 0)
                xcompress.XMemDestroyDecompressionContext(DecompressionContext);

            if (hr != 0 || decompressedLen != target)
                return -1;

            Buffer.BlockCopy(dst, 0, decompressedData, 0, target);
            return 0;
        }

        public static byte[] Compress(byte[] decompressedData)
        {
            int compressionContext = 0;
            int hr = xcompress.XMemCreateCompressionContext(
                xcompress.XMEMCODEC_TYPE.XMEMCODEC_LZX,
                0, 1, ref compressionContext);

            int compressedLen = decompressedData.Length * 2;
            byte[] compressed = new byte[compressedLen];
            int decompressedLen = decompressedData.Length;
            hr = xcompress.XMemCompress(compressionContext,
                compressed, ref compressedLen,
                decompressedData, decompressedLen);

            xcompress.XMemDestroyCompressionContext(compressionContext);

            Array.Resize<byte>(ref compressed, compressedLen);
            return compressed;
        }

        private static byte[] InflateTo(byte[] zlibData, int expectedLen)
        {
            if (zlibData == null || zlibData.Length < 2 + 4 + 1 || expectedLen <= 0) return null;
            try
            {
                byte[] outBuf = new byte[expectedLen];
                using (var ms = new MemoryStream(zlibData, 2, zlibData.Length - 6))   // без CMF/FLG и Adler32
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                {
                    int total = 0;
                    while (total < expectedLen)
                    {
                        int r = ds.Read(outBuf, total, expectedLen - total);
                        if (r <= 0) break;
                        total += r;
                    }
                    return total == expectedLen ? outBuf : null;
                }
            }
            catch { return null; }
        }

        int DecompressZlib(byte[] compressedData, byte[] decompressedData)
        {
            byte[] flat = InflateTo(compressedData, decompressedData.Length);
            if (flat == null) return -1;
            Buffer.BlockCopy(flat, 0, decompressedData, 0, decompressedData.Length);
            return 0;
        }


// ---------------------------------------------------------------
// Zlib-компрессия (для packed_v2)
// ---------------------------------------------------------------
/// <summary>
/// Сжимает данные в Zlib. 
/// По умолчанию используется FLG = 0x9C (Default Compression), так как он наиболее 
/// универсален. 0xDA (Max) тоже валиден, но .NET DeflateStream не умеет честно 
/// выставлять Max уровень, что иногда смущает кастомные парсеры (но не стандартный inflate).
/// </summary>
public static byte[] CompressZlib(byte[] decompressedData, byte flg = 0x9C)
{
    if (decompressedData == null || decompressedData.Length == 0)
        throw new ArgumentException("Данные для сжатия не могут быть пустыми.");

    using (var ms = new MemoryStream())
    {
        // CMF = 0x78 (CM=8 (deflate), CINFO=7 (32K window))
        byte cmf = 0x78;
        
        // Жесткая проверка валидности заголовка (FCHECK)
        // Сумма (CMF * 256 + FLG) должна быть кратна 31
        if ((cmf * 256 + flg) % 31 != 0)
            throw new ArgumentException("Wrong Zlib header: CMF=0x{cmf:X2}, FLG=0x{flg:X2}. Rule FCHECK failed.");

        ms.WriteByte(cmf);
        ms.WriteByte(flg);

        // DeflateStream создает "сырой" DEFLATE поток без zlib-обертки.
        using (var ds = new DeflateStream(ms, CompressionMode.Compress, true))
        {
            ds.Write(decompressedData, 0, decompressedData.Length);
        }

        // Добавляем Adler32 checksum (строго big-endian)
        uint adler = Adler32(decompressedData);
        ms.WriteByte((byte)(adler >> 24));
        ms.WriteByte((byte)(adler >> 16));
        ms.WriteByte((byte)(adler >> 8));
        ms.WriteByte((byte)adler);

        byte[] compressed = ms.ToArray();

        // КОНТРОЛЬ: Проверяем, что мы сами можем распаковать то, что только что сжали.
        // Это гарантирует, что игра не крашнется из-за битого DEFLATE потока.
        if (!ValidateZlibStream(compressed, decompressedData))
        {
            throw new InvalidOperationException("Критическая ошибка валидации: сжатый поток не распаковывается обратно в оригинальные данные!");
        }

        return compressed;
    }
}

/// <summary>
/// Валидация Zlib потока (эмулирует то, что сделает игра при чтении файла)
/// </summary>
private static bool ValidateZlibStream(byte[] compressed, byte[] original)
{
    try
    {
        if (compressed.Length < 6) return false;
        
        // Пропускаем 2 байта заголовка (CMF, FLG)
        int deflateStart = 2;
        // Отсекаем 4 байта Adler32 в конце
        int deflateLen = compressed.Length - 2 - 4; 
        
        if (deflateLen <= 0) return false;

        using (var ms = new MemoryStream(compressed, deflateStart, deflateLen))
        using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
        {
            byte[] decompressed = new byte[original.Length];
            int totalRead = 0;
            while (totalRead < original.Length)
            {
                int read = ds.Read(decompressed, totalRead, original.Length - totalRead);
                if (read == 0) break; // Поток закончился раньше времени
                totalRead += read;
            }
            
            // Если прочитали не все байты — поток битый
            if (totalRead != original.Length) return false;
            
            // Побайтовое сравнение
            for (int i = 0; i < original.Length; i++)
            {
                if (decompressed[i] != original[i]) return false;
            }
            return true;
        }
    }
    catch
    {
        return false;
    }
}

        // ---------------------------------------------------------------
        // Вычисление Adler32 checksum
        // ---------------------------------------------------------------
        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (byte d in data)
            {
                a = (a + d) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }
        #endregion


        // Опции синтеза контейнера с нуля (без донора)
        public struct PackNewOptions
        {
            public bool UseZlib;   // true = packed_v2 (zlib), false = packed_v1 (LZX)
            public bool Encrypt;   // true = AES-контейнер (0x85435352, version 2); только для LZX
            public int Version;
            public int SizeV;
            public int SizeP;
        }

        // Кодирование размера в мантиссу/сдвиг: size = mantissa << (shiftField + 8)
        public static bool TryEncodeSize(int size, out int mant, out int shiftField)
        {
            mant = 0; shiftField = 0;
            if (size == 0) return true;
            for (int s = 0; s < 16; s++)
            {
                int shift = s + 8;
                if ((1 << shift) <= 0) break;
                if (size % (1 << shift) != 0) continue;
                int m = size >> shift;
                if (m <= 0x7FF) { mant = m; shiftField = s; return true; }
            }
            return false;
        }

        // Сборка flags1 из пары размеров (обратное к getSizeV/getSizeP)
        public static bool TryBuildFlags1(int sizeV, int sizeP, out int flags1)
        {
            flags1 = 0;
            int mv, sv, mp, sp;
            if (!TryEncodeSize(sizeV, out mv, out sv)) return false;
            if (!TryEncodeSize(sizeP, out mp, out sp)) return false;
            flags1 = (mv & 0x7FF) | ((sv & 15) << 11) | ((mp & 0x7FF) << 15) | ((sp & 15) << 26);
            return true;
        }

        private static int AlignPage(int x)
        {
            return (x + 4095) & ~4095;
        }

        private static bool IsUnpackedMagic(uint d)
        {
            return d == 0x50C35700u
                || d == 0x08C55700u
                || d == 0xFCC75700u
                || d == 0x28C25700u;
        }

        public static bool ScanSizes(byte[] flat, out int sizeV, out int sizeP)
        {
            sizeV = 0;
            sizeP = 0;
            if (flat == null || flat.Length < 16)
                return false;

            int total = flat.Length;
            List<int> offsV = new List<int>();
            List<int> offsP = new List<int>();

            for (int i = 0; i + 4 <= total; i += 4)
            {
                uint d = (uint)(
                    (flat[i] << 24) |
                    (flat[i + 1] << 16) |
                    (flat[i + 2] << 8) |
                    flat[i + 3]);

                if (i == 0 && IsUnpackedMagic(d))
                    continue;

                uint hi = d & 0xFF000000u;
                if (hi != 0x50000000u && hi != 0x60000000u)
                    continue;

                int off = (int)(d & 0x00FFFFFFu);
                if (off <= 0 || off >= total || (off & 3) != 0)
                    continue;

                if (hi == 0x50000000u) offsV.Add(off);
                else offsP.Add(off);
            }

            if (offsV.Count == 0)
                return false;

            // Кандидаты на sizeV: страницы сразу после каждого 0x50-смещения
            // и страницы, дополняющие каждый 0x60-кандидат physical-размера до total.
            HashSet<int> cands = new HashSet<int>();
            for (int k = 0; k < offsV.Count; k++)
            {
                int v = AlignPage(offsV[k] + 4);
                if (v > 0 && v <= total) cands.Add(v);
            }
            for (int k = 0; k < offsP.Count; k++)
            {
                int v = total - AlignPage(offsP[k] + 4);
                if (v > 0 && v <= total) cands.Add(v);
            }
            cands.Add(total); // вырожденный случай: physical отсутствует

            int bestV = -1;
            long bestScore = long.MaxValue;

            foreach (int v in cands)
            {
                long bad = 0;
                // virtual-смещения не могут дотягивать до sizeV и выше
                for (int k = 0; k < offsV.Count; k++)
                    if (offsV[k] >= v) bad++;

                // physical: выбираем трактовку (relative / absolute) с меньшим числом нарушений
                long rel = 0, abs = 0;
                int sp = total - v;
                for (int k = 0; k < offsP.Count; k++)
                {
                    if (offsP[k] >= sp) rel++;   // нарушение relative-трактовки
                    if (offsP[k] < v) abs++;     // нарушение absolute-трактовки
                }
                bad += (rel < abs) ? rel : abs;

                if (bad < bestScore) { bestScore = bad; bestV = v; }
            }

            if (bestV < 0)
                return false;

            sizeV = bestV;
            sizeP = total - bestV;
            return true;
        }

        private static void WriteBE(byte[] buf, int off, int value)
        {
            buf[off] = (byte)(value >> 24);
            buf[off + 1] = (byte)(value >> 16);
            buf[off + 2] = (byte)(value >> 8);
            buf[off + 3] = (byte)value;
        }

        // Синтез контейнера с нуля: заголовок + payload, без донора
        // Синтез контейнера с нуля: заголовок + payload, без донора
        public static byte[] PackNew(byte[] flat, PackNewOptions opt, out string error)
        {
            error = null;
            if (flat == null || flat.Length == 0) { error = "Flat data is empty."; return null; }

            // Шифрование имеет смысл только для LZX: в конструкторе нет расшифровки для zlib-ветки
            if (opt.Encrypt && opt.UseZlib)
            {
                error = "Encryption is supported only for LZX container (magic 0x85435352).";
                return null;
            }

            int total = opt.SizeV + opt.SizeP;
            if (total != flat.Length)
            {
                error = string.Format("SizeV+SizeP = {0} (0x{0:X}) != flat length {1} (0x{1:X}).",
                    total, flat.Length);
                return null;
            }
            int flags1;
            if (!TryBuildFlags1(opt.SizeV, opt.SizeP, out flags1))
            {
                error = string.Format(
                    "Sizes V=0x{0:X}, P=0x{1:X} not encodable in flags1 (need multiples of 256, mantissa < 2048).",
                    opt.SizeV, opt.SizeP);
                return null;
            }

            if (opt.UseZlib)
            {
                byte[] payload = ZlibNative.IsAvailable ? ZlibNative.Compress(flat, 9) : null;
                if (payload == null) payload = CompressZlib(flat);   // managed fallback
                if (payload == null || payload.Length == 0) { error = "zlib compression failed."; return null; }
                byte[] res = new byte[12 + payload.Length];
                WriteBE(res, 0, RSC_MAGIC_PACKED_V2);
                WriteBE(res, 4, opt.Version);
                WriteBE(res, 8, flags1);
                Buffer.BlockCopy(payload, 0, res, 12, payload.Length);
                return res;
            }
            else
            {
                string dllPath = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) + "\\xcompress32.dll";
                if (!System.IO.File.Exists(dllPath))
                { error = "xcompress32.dll not found in the RPFTool directory."; return null; }

                byte[] payload = Compress(flat);
                if (payload == null || payload.Length == 0) { error = "LZX compression failed."; return null; }

                // ОБРЕЗКА: оставляем только реально использованные байты потока
                int streamEnd = FindStreamEnd(payload, flat.Length);
                if (streamEnd > 0 && streamEnd < payload.Length)
                    Array.Resize(ref payload, streamEnd);

                // body = преамбула (маркер + точный размер) + LZX-поток
                byte[] body = new byte[8 + payload.Length];
                body[0] = 0x0F; body[1] = 0xF5; body[2] = 0x12; body[3] = 0xEF;
                WriteBE(body, 4, payload.Length);
                Buffer.BlockCopy(payload, 0, body, 8, payload.Length);

                int magic = RSC_MAGIC_PACKED_V1;
                int version = opt.Version;
                int headerLen = 12;

                if (opt.Encrypt)
                {
                    // AES — блочный шифр: пад body до кратности 16
                    int pad = (16 - (body.Length & 15)) & 15;
                    if (pad > 0)
                    {
                        byte[] padded = new byte[body.Length + pad];
                        Buffer.BlockCopy(body, 0, padded, 0, body.Length);
                        body = padded;
                    }
                    body = DataUtil.Encrypt(body);
                    magic = RSC_MAGIC_PACKED_ENC;  // 0x85435352
                    version = 2;                  // только при version==2 читатель ждёт AES
                    headerLen = 16;               // шифрованный заголовок на dword длиннее (flags2)
                }

                byte[] res = new byte[headerLen + body.Length];
                WriteBE(res, 0, magic);
                WriteBE(res, 4, version);
                WriteBE(res, 8, flags1);
                if (headerLen == 16)
                    WriteBE(res, 12, 0);          // flags2 = 0: размеры в flags1 (bUseExtSize=false)
                Buffer.BlockCopy(body, 0, res, headerLen, body.Length);
                return res;
            }
        }

    }
}