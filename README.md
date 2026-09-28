# RPFTool — Midnight Club: LA edition

A fork of RPFTool that adds **write support for RPF3 (Midnight Club: LA)** archives.
Viewing/extraction for other RPF versions is unchanged.

## Changes

### RPF3 saving (`RPFLib/RPF3/File.cs`, `TOC.cs`)
- Save rewrites the archive over a byte-for-byte copy of the original: only the header, TOC and
  data blocks are overwritten; all builder service bytes are preserved.
- Data layout matches the game builder: data starts at `Align(0x800 + TOCSize, 0x1000)`, files are
  packed densely inside `0x1000` pages, EOF = `Align(end, 0x4000)`.
- The encrypted TOC spans the whole region up to the data start
  (`TOCSize = Align(0x800 + N*16, 0x1000) − 0x800`), plaintext zero-padded to the region size.
- Result: an *add → save → delete → save* cycle yields an archive byte-identical to the original
  (`fc /b` clean); saving with no edits changes nothing.
- Fixed `TOC.Delete` (parent child-count, directory index shifts); added `TOC.InsertEntry`
  for mid-TOC insertion.
- Fixed empty-folder insertion: parent's `ContentEntryIndex` is post-corrected after `InsertEntry`
  to prevent the inclusive shift from moving the empty parent's range (fixes files disappearing
  when added to newly created folders).

### Adding / deleting files and folders (`RPFLib/Version3.cs`, `mainForm.cs`)
- Context-menu **Add file** (multi-select) into the selected or current directory; new entries are
  deflate-compressed with the compressed flag set *before* packing (fixes the `None` attribute bug).
- **Replace** now writes replacements compressed, like native MC:LA files.
- **Delete file** removes entries with correct TOC bookkeeping.
- **Create folder** (context menu) inserts a directory entry at the end of the parent's child range,
  registers the name in `CustomFilenames.txt`, and creates the GUI object with proper `_dirLinks`.
- **Delete folder** (context menu) recursively removes all children (in reverse order to preserve
  sibling indices) and the directory entry itself; handles deletion from within the deleted subtree
  by navigating up to the parent.
- Add/Delete are exposed only for RPF3 and hidden for other games.

### Resource files (`RPFLib/Resources/RSCFile.cs`)
Full support for RSC resource containers (LZX, zlib, AES-encrypted LZX):
- **Unpacking**: auto-detects magic (`0x05435352` LZX, `0x06435352` zlib, `0x85435352` encrypted),
  extracts the flat payload using `xcompress32.dll` or managed `DeflateStream`.
- **Repacking** (`Pack`): preserves the donor header (magic, version, flags) and recompresses the
  payload with the same codec; trims LZX streams to actual usage.
- **Byte-identical zlib repacks** (`PackZlib`): uses `zlib1.dll` P/Invoke to match the donor's
  compression level by trial (checks levels 1,5,6,9 and hint from FLG byte); falls back to managed
  compression if the native library is unavailable or produces a different stream.
- **Resize support** (`PackResized`): when the edited flat differs from the donor's V+P size,
  classifies the edit (tail append/truncate vs. virtual growth/shrink), rebases absolute physical
  pointers (`0x60xxxxxx`) by delta, pads to 4096-byte pages, and recompresses with the donor's codec.
- **New resource synthesis** (`PackNew`): builds a container from scratch with auto-detected V/P
  split (scans `0x50`/`0x60` pointers, scores candidate boundaries) or manual entry via `PackNewResourceForm`.

### Integrity tools (`mainForm.cs`, `RPFLib/RPF3/File.cs`, `RpfStat.cs`)
- **Audit** (toolbar button): full structural integrity check of a single archive — validates header
  fields, TOC geometry, entry offsets/sizes, directory ranges, block overlaps, decompression; reports
  errors (format violations) and warnings (builder-rule deviations).
- **Verify archive** (menu): compares the current archive against a backup RPF3 by matching files
  by name hash, checking byte-identity of extracted data; produces `verify_report.txt` with
  OK/MISMATCH/ONLY_IN_CURRENT/ONLY_IN_BACKUP counts.
- **Statistics** (menu): analyzes layout differences between two archives (original vs. modified) —
  reports moved/resized files, added bytes, trailing consumption, and produces `rpf_stats.txt`.

### File names (`RPFLib/Version3.cs`)
RPF3 stores name hashes only; display names now resolve from three sources:
- built-in base list;
- `FilenamesMCLA.txt` — external MCLA name list next to the exe (read-only, warns if missing);
- `CustomFilenames.txt` — user names: appended when a new file/folder is added (no duplicate lines),
  de-duplicated at startup.

### Other changes
- **Update check disabled** (`Loader.cs`): removed the `XDocument.Load("http://tmacdev.com/updates/update.xml")`
  call and `DownloadForm` invocation from the startup sequence.
- **AddResourceForm** (`AddResourceForm.cs`): dialog for selecting `ResourceType` (the "Version" in
  the Attributes column) and `RSCFlags` when adding RSC files; populates from known types in the
  archive (by extension and globally) with a custom-value fallback.
- **UI fixes** (`AddResourceForm.cs`): dynamic button positioning based on control `Bottom` values
  to prevent clipping at non-100% DPI scaling.

## Building

Requires:
- Visual Studio 2012 with .NET Framework 4.x support
- DevExpress WinForms controls (licensed separately)
- `xcompress32.dll` (LZX codec, place next to the exe)
- `zlib1.dll` (optional, for byte-identical zlib repacks)
- `FilenamesMCLA.txt` (optional, for MCLA name resolution)

## Usage

1. **Open** an RPF3 archive (Midnight Club: LA).
2. **Navigate** the directory tree (double-click folders, breadcrumb bar, or Backspace to go up).
3. **Extract** files/folders via context menu or toolbar (multi-select supported).
4. **Edit** files externally (hex editor, texture tools, etc.).
5. **Replace** files: select in the tree, context menu → Replace → pick the modified file.
6. **Add** files/folders: context menu → Add file / Create folder.
   - For RSC resources, a dialog will prompt for the resource type (version).
7. **Delete** files/folders: select and press Delete or context menu → Delete.
   - Folder deletion is recursive and removes all contents.
8. **Save** (toolbar or Ctrl+S): writes changes to disk.
   - First save after opening creates a `.bak` backup next to the original.
9. **Verify** integrity or compare against a backup via the Tools menu.

## Known limitations

- **No folder renaming**: RPF3 stores name hashes; renaming would require re-hashing and is not implemented.
- **Resource V/P detection heuristic**: for new RSC files without a donor, the V/P split is inferred
  from pointer patterns; manual override via `PackNewResourceForm` is recommended for complex resources.
- **Encrypted TOC**: the tool preserves encryption but does not expose keys; re-encryption uses the
  same key as the original archive.
