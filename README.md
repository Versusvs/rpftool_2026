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

### Adding / deleting files (`RPFLib/Version3.cs`, `mainForm.cs`)
- Context-menu **Add file** (multi-select) into the selected or current directory; new entries are
  deflate-compressed with the compressed flag set *before* packing (fixes the `None` attribute bug).
- **Replace** now writes replacements compressed, like native MC:LA files.
- **Delete file** removes entries with correct TOC bookkeeping.
- Add/Delete are exposed only for RPF3 and hidden for other games.

### File names (`RPFLib/Version3.cs`)
RPF3 stores name hashes only; display names now resolve from three sources:
- built-in base list;
- `FilenamesMCLA.txt` — external MCLA name list next to the exe (read-only, warns if missing);
- `CustomFilenames.txt` — user names: appended when a new file is added (no duplicate lines),
  de-duplicated at startup.
