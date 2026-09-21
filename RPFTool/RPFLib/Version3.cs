using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;
using RPFLib.Common;
using RPFLib.RPF3;

namespace RPFLib
{
    internal class Version3 : Archive
    {
        #region Vars
        public override RPFLib.Common.Directory RootDirectory { get; set; }
        private static readonly Dictionary<uint, string> _knownFilenames;
        private RPFLib.RPF3.File _rpfFile;

        // Прямые соответствия "GUI-объект <-> запись TOC" внутри этой версии архива
        private readonly Dictionary<RPFLib.Common.Directory, DirectoryEntry> _dirLinks =
            new Dictionary<RPFLib.Common.Directory, DirectoryEntry>();
        private readonly Dictionary<RPFLib.Common.File, FileEntry> _fileLinks =
            new Dictionary<RPFLib.Common.File, FileEntry>();

        // Дополнительный справочник имён только для MCLA (лежит рядом с exe, только читается)
        private const string MclaNamesFile = "FilenamesMCLA.txt";
        #endregion

        static Version3()
        {
            _knownFilenames = new Dictionary<uint, string>();

            // 1. Встроенный список имён от автора инструмента
            var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("RPFTool.RPFLib.KnownFilenames.txt");
            if (s != null)
            {
                var sw = new StreamReader(s);

                string name;
                while ((name = sw.ReadLine()) != null)
                {
                    uint hash = Hasher.Hash(name);
                    if (!_knownFilenames.ContainsKey(hash))
                    {
                        _knownFilenames.Add(hash, name);
                    }
                }
            }

            // 2. ДОБАВЛЕНО: расширенный список имён только для MCLA.
            //    Файл-справочник: только читается, AddFile в него не пишет.
            //    Если файла нет рядом с exe - выдаём предупреждение
            try
            {
                string mclaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, MclaNamesFile);
                if (System.IO.File.Exists(mclaPath))
                {
                    foreach (var line in System.IO.File.ReadLines(mclaPath))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        uint h = Hasher.Hash(line);
                        if (!_knownFilenames.ContainsKey(h))
                        {
                            _knownFilenames.Add(h, line);
                        }
                    }
                }
                else
                {
                    MessageBox.Show(
                        "MCLA names file not found next to RPFTool.exe: \"" + MclaNamesFile + "\"." + Environment.NewLine +
                        "The extended name list was not loaded; entries with unknown names will be shown as 0x...",
                        "MCLA: Names List",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch { /* не критично: сбой чтения не должен ломать запуск */ }

            // 3. Имена, добавленные пользователем, переживают перезапуск тула
            string customPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CustomFilenames.txt");
            try
            {
                if (System.IO.File.Exists(customPath))
                {
                    var distinct = new List<string>();
                    var seen = new HashSet<uint>();
                    int total = 0;
                    foreach (var line in System.IO.File.ReadLines(customPath))
                    {
                        total++;
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        uint h = Hasher.Hash(line);
                        if (seen.Add(h))
                        {
                            distinct.Add(line);
                            if (!_knownFilenames.ContainsKey(h))
                            {
                                _knownFilenames.Add(h, line);
                            }
                        }
                    }

                    // Одноразовая чистка накопившихся дублей и пустых строк
                    if (distinct.Count != total)
                    {
                        System.IO.File.WriteAllLines(customPath, distinct);
                    }
                }
            }
            catch { /* не критично */ }
        }

        public override void Open(string filename)
        {
            _rpfFile = new RPFLib.RPF3.File();
            int filecount = _rpfFile.Open(filename);
            if (filecount < 1)
            {
                throw new Exception("Could not open RPF file.");
            }

            BuildFS();
        }

        public override List<fileSystemObject> search(RPFLib.Common.Directory dir, string searchText)
        {
            List<fileSystemObject> searchList = new List<fileSystemObject>();
            subsearch(dir, searchList, searchText);
            return searchList;
        }

        public void subsearch(RPFLib.Common.Directory dir, List<fileSystemObject> searchList, string searchText)
        {
            foreach (fileSystemObject item in dir)
            {
                if (item.IsDirectory)
                {
                    var subdir = item as RPFLib.Common.Directory;
                    searchList.AddRange((from pv in subdir._fsObjectsByName
                                         where pv.Key.Contains(searchText)
                                         select pv.Value));
                    subsearch(subdir, searchList, searchText);
                }
            }
        }

        private string GetName(TOCEntry entry)
        {
            string name;
            if (_rpfFile.Header.Identifier < HeaderIDs.Version3 || _rpfFile.Header.Identifier == HeaderIDs.Version4)
            {
                name = _rpfFile.TOC.GetName(entry.NameOffset);
            }
            else
            {
                if (entry == _rpfFile.TOC[0])
                {
                    name = "Root";
                }
                else if (_knownFilenames.ContainsKey((uint)entry.NameOffset))
                {
                    name = _knownFilenames[(uint)entry.NameOffset];
                }
                else
                {
                    name = string.Format("0x{0:x}", entry.NameOffset);
                }
            }
            return name;
        }

        private byte[] LoadData(FileEntry entry, bool getCustom)
        {
            byte[] data;
            if (getCustom && entry.CustomData != null)
                data = entry.CustomData;
            else
                data = _rpfFile.ReadData(entry.Offset, entry.SizeInArchive);
            if (entry.IsCompressed && !entry.IsResourceFile)
            {
                data = DataUtil.DecompressDeflate(data, (int)entry.Size);
            }
            return data;
        }

        // Замена, как и родные файлы MC:LA, пишется сжатой (кроме ресурсов).
        // Флаг выставляется ДО упаковки - иначе байты лягут сырыми и будет "None"
        private void StoreData(FileEntry entry, byte[] data)
        {
            if (!entry.IsResourceFile)
            {
                entry.IsCompressed = true;
            }
            entry.SetCustomData(data);
        }

        public override void Close()
        {
            _rpfFile.Close();
        }

        private void BuildFSDirectory(DirectoryEntry dirEntry, RPFLib.Common.Directory fsDirectory)
        {
            try
            {
                fsDirectory.Name = GetName(dirEntry);
                for (int i = 0; i < dirEntry.ContentEntryCount; i++)
                {
                    TOCEntry entry = _rpfFile.TOC[dirEntry.ContentEntryIndex + i];
                    if (entry.IsDirectory)
                    {
                        var subdirEntry = entry as DirectoryEntry;
                        var dir = new RPFLib.Common.Directory();
                        dir._Contentcount = nCount => subdirEntry.setContentcount(nCount);
                        dir._ContentIndex = ContentIndex => subdirEntry.setContentIndex(ContentIndex);
                        dir._Index = NewContentIndex => subdirEntry.setNewContentIndex(NewContentIndex);
                        dir.Attributes = "Folder";
                        dir.ParentDirectory = fsDirectory;
                        _dirLinks[dir] = subdirEntry; // связь GUI <-> TOC
                        BuildFSDirectory(entry as DirectoryEntry, dir);
                        fsDirectory.AddObject(dir);
                    }
                    else
                    {
                        var fileEntry = entry as FileEntry;
                        var file = new Common.File();
                        file._dataLoad = getCustom => LoadData(fileEntry, getCustom);
                        file._dataStore = data => StoreData(fileEntry, data);
                        file._dataCustom = () => fileEntry.CustomData != null;
                        file.d1 = () => fileEntry.getSize();
                        file._Index = nIndex => fileEntry.setIndex(nIndex);
                        file._delete = () => fileEntry.Delete();

                        file.CompressedSize = fileEntry.SizeInArchive;
                        file.IsCompressed = fileEntry.IsCompressed;
                        file.Name = GetName(fileEntry);
                        file.IsResource = fileEntry.IsResourceFile;
                        file.ParentDirectory = fsDirectory;
                        _fileLinks[file] = fileEntry; // связь GUI <-> TOC
                        StringBuilder attributes = new StringBuilder();
                        if (file.IsResource)
                        {
                            attributes.Append(string.Format("Resource [Version {0}", fileEntry.ResourceType));
                            if (file.IsCompressed)
                            {
                                attributes.Append("Compressed");
                            }
                            attributes.Append("]");
                        }
                        else if (file.IsCompressed)
                        {
                            attributes.Append("Compressed");
                        }
                        else
                            attributes.Append("None");
                        file.Attributes = attributes.ToString();
                        fsDirectory.AddObject(file);
                    }
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(ex.Message + Environment.NewLine + ex.StackTrace);
            }
        }

        public override void Save()
        {
            _rpfFile.save();
        }

        // Проброс аудита целостности для обработчика кнопки сверки
        public string Audit(out int errorCount, out int warnCount)
        {
            return _rpfFile.Audit(out errorCount, out warnCount);
        }

        private void BuildFS()
        {
            // Связи строятся заново под каждый открытый архив
            _dirLinks.Clear();
            _fileLinks.Clear();

            RootDirectory = new RPFLib.Common.Directory();
            _dirLinks[RootDirectory] = _rpfFile.TOC[0] as DirectoryEntry; // корень <-> первая запись TOC

            TOCEntry entry = _rpfFile.TOC[0];
            BuildFSDirectory(entry as DirectoryEntry, RootDirectory);
        }

        #region ДОБАВЛЕНО: добавление файлов в архив

        // Ищет запись-директорию в TOC, соответствующую директории из GUI:
        // сначала по прямой связи в словаре, затем фолбэк по хешу имени
        private DirectoryEntry FindDirEntry(RPFLib.Common.Directory fsDir)
        {
            DirectoryEntry linked;
            if (_dirLinks.TryGetValue(fsDir, out linked))
            {
                return linked;
            }

            if (fsDir.ParentDirectory == null)
            {
                return _rpfFile.TOC[0] as DirectoryEntry;
            }

            uint hash;
            if (fsDir.Name.StartsWith("0x"))
            {
                hash = uint.Parse(fsDir.Name.Substring(2), NumberStyles.HexNumber);
            }
            else
            {
                hash = Hasher.Hash(fsDir.Name);
            }

            foreach (var e in _rpfFile.TOC)
            {
                var d = e as DirectoryEntry;
                if (d != null && (uint)d.NameOffset == hash)
                {
                    return d;
                }
            }
            return null;
        }

        // Добавляет файл в указанную директорию архива.
        // Реальная запись на диск произойдёт при вызове Save()
        public void AddFile(RPFLib.Common.Directory parentFsDir, string fileName, byte[] data)
        {
            if (parentFsDir == null)
                throw new Exception("AddFile: target directory is null.");
            if (string.IsNullOrEmpty(fileName))
                throw new Exception("AddFile: file name is empty.");
            if (data == null)
                throw new Exception("AddFile: file data is null.");

            // 1. Родительская директория в TOC
            DirectoryEntry parentDirEntry = FindDirEntry(parentFsDir);
            if (parentDirEntry == null)
            {
                throw new Exception("AddFile: parent directory '" + parentFsDir.Name + "' not found in TOC.");
            }

            // 2. Новая запись; имя в архиве хранится хешем
            var newEntry = new FileEntry(_rpfFile.TOC);
            newEntry.Name = fileName;
            newEntry.NameHash = Hasher.Hash(fileName);
            newEntry.Size = data.Length;          // размер БЕЗ сжатия
            newEntry.SizeInArchive = data.Length; // уточнится в save() после упаковки
            newEntry.IsResourceFile = false;
            newEntry.ResourceType = 0;
            newEntry.RSCFlags = 0;

            // КЛЮЧЕВОЙ ПОРЯДОК: флаг ДО упаковки, иначе байты лягут сырыми ("None")
            newEntry.IsCompressed = true;
            newEntry.SetCustomData(data);

            // 3. Вставка в плоский TOC в конец диапазона детей родителя
            int insertIndex = parentDirEntry.ContentEntryIndex + parentDirEntry.ContentEntryCount;
            _rpfFile.TOC.InsertEntry(insertIndex, newEntry);
            parentDirEntry.ContentEntryCount++;

            // 4. Имя в словаре + персист на диск.
            //    Дописываем строку в CustomFilenames.txt ТОЛЬКО если имя было
            //    неизвестно до этого вызова (из ресурса, FilenamesMCLA.txt или
            //    прошлых сессий) - иначе add->delete->add плодил бы одинаковые строки
            bool alreadyKnown = _knownFilenames.ContainsKey(newEntry.NameHash);
            if (!alreadyKnown)
            {
                _knownFilenames.Add(newEntry.NameHash, fileName);
                try
                {
                    string customPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CustomFilenames.txt");
                    System.IO.File.AppendAllText(customPath, fileName + Environment.NewLine);
                }
                catch { /* не критично */ }
            }

            // 5. GUI-объект, чтобы файл сразу появился в списке
            var file = new RPFLib.Common.File();
            file._dataLoad = getCustom => LoadData(newEntry, getCustom);
            file._dataStore = d => StoreData(newEntry, d);
            file._dataCustom = () => newEntry.CustomData != null;
            file.d1 = () => newEntry.getSize();
            file._Index = nIndex => newEntry.setIndex(nIndex);
            file._delete = () => newEntry.Delete();
            file.CompressedSize = newEntry.SizeInArchive;
            file.IsCompressed = newEntry.IsCompressed; // true
            file.Name = fileName;
            file.IsResource = newEntry.IsResourceFile;
            file.Attributes = "Compressed";
            file.ParentDirectory = parentFsDir;
            _fileLinks[file] = newEntry;
            parentFsDir.AddObject(file);
        }

        #endregion
    }
}