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

        // Подсказки для диалога выбора версии ресурса: живут ТОЛЬКО в памяти
        // в рамках открытого архива, на диск не пишутся
        private readonly Dictionary<string, Dictionary<byte, int>> _rscTypeByExt =
            new Dictionary<string, Dictionary<byte, int>>();
        private readonly Dictionary<byte, uint> _rscFlagsByType =
            new Dictionary<byte, uint>();

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
                        file.resourcetype = fileEntry.ResourceType;
                        file.ParentDirectory = fsDirectory;
                        _fileLinks[file] = fileEntry; // связь GUI <-> TOC

                        // Наполняем подсказки для диалога выбора версии (только память)
                        if (fileEntry.IsResourceFile)
                        {
                            string ext = Path.GetExtension(file.Name).ToLowerInvariant();
                            Dictionary<byte, int> bag;
                            if (!_rscTypeByExt.TryGetValue(ext, out bag))
                            {
                                bag = new Dictionary<byte, int>();
                                _rscTypeByExt[ext] = bag;
                            }
                            if (bag.ContainsKey(fileEntry.ResourceType)) bag[fileEntry.ResourceType]++;
                            else bag[fileEntry.ResourceType] = 1;

                            if (!_rscFlagsByType.ContainsKey(fileEntry.ResourceType))
                                _rscFlagsByType[fileEntry.ResourceType] = fileEntry.RSCFlags;
                        }

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
            // Связи и подсказки строятся заново под каждый открытый архив
            _dirLinks.Clear();
            _fileLinks.Clear();
            _rscTypeByExt.Clear();
            _rscFlagsByType.Clear();

            RootDirectory = new RPFLib.Common.Directory();
            _dirLinks[RootDirectory] = _rpfFile.TOC[0] as DirectoryEntry; // корень <-> первая запись TOC

            TOCEntry entry = _rpfFile.TOC[0];
            BuildFSDirectory(entry as DirectoryEntry, RootDirectory);
        }

        #region ДОБАВЛЕНО: добавление файлов в архив


        // Удаляет папку из архива вместе со всем содержимым (рекурсивно).
        // Реальная запись на диск произойдёт при вызове Save().
        public void DeleteDirectory(RPFLib.Common.Directory fsDir)
        {
            if (fsDir == null)
                throw new Exception("DeleteDirectory: directory is null.");
            if (fsDir.ParentDirectory == null)
                throw new Exception("DeleteDirectory: cannot delete the root directory.");

            DirectoryEntry dirEntry = FindDirEntry(fsDir);
            if (dirEntry == null)
                throw new Exception("DeleteDirectory: entry for '" + fsDir.Name + "' not found in TOC.");

            // 1) Убираем поддерево из TOC: сначала дети (с последнего), затем сама запись
            DeleteSubtreeFromToc(dirEntry);

            // 2) Чистим связи GUI <-> TOC и убираем объект из родительской папки
            PurgeLinks(fsDir);
            fsDir.ParentDirectory.DeleteObject(fsDir);
        }

        // Рекурсивное удаление записей поддерева из плоского TOC.
        // Порядок важен: удаляем детей с КОНЦА диапазона и только потом саму директорию,
        // чтобы фикс-апы индексов в TOC.Delete не рассинхронизировали обход.
        private void DeleteSubtreeFromToc(DirectoryEntry dirEntry)
        {
            int count = dirEntry.ContentEntryCount;
            int baseIndex = dirEntry.ContentEntryIndex;
            for (int i = count - 1; i >= 0; i--)
            {
                TOCEntry child = _rpfFile.TOC[baseIndex + i];
                var childDir = child as DirectoryEntry;
                if (childDir != null)
                {
                    DeleteSubtreeFromToc(childDir);   // внуки, затем запись ребёнка-директории
                }
                else
                {
                    _rpfFile.TOC.Delete(child);       // запись-файл
                }
            }
            _rpfFile.TOC.Delete(dirEntry);            // сама папка (родитель получит count--)
        }

        // Рекурсивно снимает связи GUI-объектов поддерева, чтобы словари не текли
        // и не указывали на удалённые записи TOC
        private void PurgeLinks(RPFLib.Common.Directory dir)
        {
            _dirLinks.Remove(dir);
            foreach (var obj in dir.ToList())
            {
                var sub = obj as RPFLib.Common.Directory;
                if (sub != null)
                {
                    PurgeLinks(sub);
                }
                else
                {
                    var f = obj as RPFLib.Common.File;
                    if (f != null) _fileLinks.Remove(f);
                }
            }
        }


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

        // Мода ResourceType по расширению + флаги, встреченные для этого типа в архиве
        private bool TryGetModeType(string ext, out byte type, out uint flags)
        {
            type = 1;
            flags = 0xC0000000u;
            Dictionary<byte, int> bag;
            if (!_rscTypeByExt.TryGetValue((ext ?? "").ToLowerInvariant(), out bag) || bag.Count == 0)
                return false;
            int best = 0;
            foreach (var kv in bag)
                if (kv.Value > best) { best = kv.Value; type = kv.Key; }
            uint f;
            if (_rscFlagsByType.TryGetValue(type, out f)) flags = f;
            return true;
        }

        private IEnumerable<byte> GetKnownTypesForExt(string ext)
        {
            Dictionary<byte, int> bag;
            if (_rscTypeByExt.TryGetValue((ext ?? "").ToLowerInvariant(), out bag))
                return bag.Keys.ToList();
            return Enumerable.Empty<byte>();
        }

        private IEnumerable<byte> GetKnownTypesGlobal()
        {
            var set = new HashSet<byte>();
            foreach (var kv in _rscTypeByExt)
                foreach (var t in kv.Value.Keys) set.Add(t);
            return set;
        }

        // Создаёт новую пустую папку в указанной директории архива.
        // Реальная запись на диск произойдёт при вызове Save().
        // Возвращает GUI-объект, чтобы список можно было обновить сразу.
        public RPFLib.Common.Directory CreateDirectory(RPFLib.Common.Directory parentFsDir, string folderName)
        {
            if (parentFsDir == null)
                throw new Exception("CreateDirectory: parent directory is null.");
            if (string.IsNullOrEmpty(folderName))
                throw new Exception("CreateDirectory: folder name is empty.");

            foreach (char ch in System.IO.Path.GetInvalidFileNameChars())
                if (folderName.IndexOf(ch) >= 0)
                    throw new Exception("CreateDirectory: invalid character '" + ch + "' in folder name.");

            if (parentFsDir.FindByName(folderName) != null)
                throw new Exception("CreateDirectory: '" + folderName + "' already exists in '" + parentFsDir.Name + "'.");

            // 1. Родительская директория в TOC
            DirectoryEntry parentDirEntry = FindDirEntry(parentFsDir);
            if (parentDirEntry == null)
                throw new Exception("CreateDirectory: parent directory '" + parentFsDir.Name + "' not found in TOC.");

            // 2. Новая запись-директория; имя в архиве хранится хешем
            var newDirEntry = new DirectoryEntry(_rpfFile.TOC);
            newDirEntry.Name = folderName;
            newDirEntry.NameOffset = (int)Hasher.Hash(folderName);
            newDirEntry.ContentEntryCount = 0;

            // 3. Вставка
            bool parentWasEmpty = (parentDirEntry.ContentEntryCount == 0);
            int insertIndex = parentDirEntry.ContentEntryIndex + parentDirEntry.ContentEntryCount;
            _rpfFile.TOC.InsertEntry(insertIndex, newDirEntry);
            parentDirEntry.ContentEntryCount++;
            if (parentWasEmpty)
                parentDirEntry.ContentEntryIndex = insertIndex;

            // CEI новой пустой папки ставим ПОСЛЕ InsertEntry (чтобы её саму не сдвинуло)
            newDirEntry.ContentEntryIndex = insertIndex + 1;

            // ИСПРАВЛЕНО: ContentEntryIndex ставим ПОСЛЕ InsertEntry, чтобы цикл сдвига
            // в InsertEntry его не тронул (он сдвигает записи с ContentEntryIndex >= index)
            newDirEntry.ContentEntryIndex = insertIndex + 1;

            // 4. Имя в словаре + персист, чтобы после переоткрытия папка не стала 0x...
            uint h = (uint)newDirEntry.NameOffset;
            if (!_knownFilenames.ContainsKey(h))
            {
                _knownFilenames.Add(h, folderName);
                try
                {
                    string customPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CustomFilenames.txt");
                    System.IO.File.AppendAllText(customPath, folderName + Environment.NewLine);
                }
                catch { /* не критично */ }
            }

            // 5. GUI-объект с теми же делегатами, что ставит BuildFSDirectory
            var dir = new RPFLib.Common.Directory();
            dir._Contentcount = nCount => newDirEntry.setContentcount(nCount);
            dir._ContentIndex = ci => newDirEntry.setContentIndex(ci);
            dir._Index = nci => newDirEntry.setNewContentIndex(nci);
            dir.Attributes = "Folder";
            dir.Name = folderName;
            dir.ParentDirectory = parentFsDir;
            _dirLinks[dir] = newDirEntry;   // связь GUI <-> TOC для FindDirEntry
            parentFsDir.AddObject(dir);

            return dir;
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

            // 2b. Автоопределение RSC-ресурса по magic контейнера
            bool isRsc = false;
            if (data.Length >= 4)
            {
                uint magic = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
                isRsc = (magic == 0x05435352u || magic == 0x06435352u || magic == 0x85435352u);
            }

            if (isRsc)
            {
                // ВСЕГДА спрашиваем пользователя: версия ресурса (ResourceType) и флаги.
                // Диалог предзаполнен подсказками из текущего архива (мода по расширению,
                // список встреченных типов), но решение принимает пользователь.
                string ext = Path.GetExtension(fileName) ?? "";
                byte defType; uint defFlags;
                TryGetModeType(ext, out defType, out defFlags);

                byte chosenType;
                uint chosenFlags;
                using (var frm = new RPFTool.AddResourceForm(
                    fileName, ext,
                    GetKnownTypesForExt(ext),
                    GetKnownTypesGlobal(),
                    defType, defFlags))
                {
                    if (frm.ShowDialog() != DialogResult.OK)
                    {
                        return; // пользователь отменил добавление
                    }
                    chosenType = frm.ChosenType;
                    chosenFlags = frm.ChosenFlags;
                }

                newEntry.IsResourceFile = true;
                newEntry.ResourceType = chosenType;
                newEntry.RSCFlags = chosenFlags;
                newEntry.IsCompressed = false;        // ресурсы хранятся сырыми
                newEntry.SizeInArchive = data.Length;
                newEntry.SetCustomData(data);         // при IsCompressed=false дефлейт не применяется

                // Обновляем подсказки для следующих добавлений в этой сессии
                string ext2 = ext.ToLowerInvariant();
                Dictionary<byte, int> bag2;
                if (!_rscTypeByExt.TryGetValue(ext2, out bag2))
                {
                    bag2 = new Dictionary<byte, int>();
                    _rscTypeByExt[ext2] = bag2;
                }
                if (bag2.ContainsKey(chosenType)) bag2[chosenType]++;
                else bag2.Add(chosenType, 1);
                if (!_rscFlagsByType.ContainsKey(chosenType))
                    _rscFlagsByType[chosenType] = chosenFlags;
            }
            else
            {
                newEntry.IsResourceFile = false;
                newEntry.ResourceType = 0;
                newEntry.RSCFlags = 0;
                newEntry.SizeInArchive = data.Length; // уточнится в save() после упаковки

                // КЛЮЧЕВОЙ ПОРЯДОК: флаг ДО упаковки, иначе байты лягут сырыми ("None")
                newEntry.IsCompressed = true;
                newEntry.SetCustomData(data);
            }

            // 3. Вставка в плоский TOC в конец диапазона детей родителя
            bool parentWasEmpty = (parentDirEntry.ContentEntryCount == 0);
            int insertIndex = parentDirEntry.ContentEntryIndex + parentDirEntry.ContentEntryCount;
            _rpfFile.TOC.InsertEntry(insertIndex, newEntry);
            parentDirEntry.ContentEntryCount++;
            // InsertEntry сдвигает CEI всех директорий с CEI >= index (включительно).
            // У пустого родителя CEI == index, и его сдвигает ложно: ребёнок лёг ровно на
            // index, поэтому диапазон обязан начинаться там же. Возвращаем CEI на место.
            if (parentWasEmpty)
                parentDirEntry.ContentEntryIndex = insertIndex;

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
            file.IsCompressed = newEntry.IsCompressed;
            file.Name = fileName;
            file.IsResource = newEntry.IsResourceFile;
            file.resourcetype = newEntry.ResourceType;

            // Строка Attributes строится ТОЧНО как при загрузке архива
            StringBuilder attributes = new StringBuilder();
            if (file.IsResource)
            {
                attributes.Append(string.Format("Resource [Version {0}", newEntry.ResourceType));
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
            {
                attributes.Append("None");
            }
            file.Attributes = attributes.ToString();

            file.ParentDirectory = parentFsDir;
            _fileLinks[file] = newEntry;
            parentFsDir.AddObject(file);
        }

        #endregion
    }
}