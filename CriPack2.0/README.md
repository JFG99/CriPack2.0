# CPK Toolkit

WinForms viewer, extractor and repacker for CRIWARE CPK archives (.NET 10, Windows).

Build / run:  `dotnet run`   (or open CpkToolkit.csproj in Visual Studio 2026 / Rider)

## Project layout
- Program.cs                      entry point
- Gui/MainForm.cs                 window: Contents, Summary, CPK header and Sections tabs
- Gui/ListViewColumnSorter.cs     sortable columns
- Core/                           no UI code; one type per file
  - Utf*.cs, BigEndianConverter   @UTF table parser (cells remember their byte positions)
  - CpkPacket, CpkPacketCipher    16-byte packet header + XOR-obfuscated payload
  - CpkArchive                    header, sections, entries, descriptions, layout validation
  - CpkExtractor + ExtractionManifest*   streaming extract + manifest of what was extracted
  - ModifiedFileDetector          decides which files in a folder are real edits
  - CpkRepacker (+ plan/report)   plans the layout, then writes one sequential pass
  - CpkItocUpdater                patches per-ID sizes in the ITOC (only used if the archive has one)
  - LaylaDecompressor             CRILAYLA decompression

## Workflow
1. Open CPK -> Extract all...   (writes _cpk_manifest.tsv next to the files)
2. Edit files in place (any size). Leave _cpk_manifest.tsv where it is.
3. Repack from folder... -> pick the folder, choose the output .cpk.
   Only edited files are replaced; everything else is copied byte-for-byte from the original.
4. Verify layout checks the result (also done automatically after a repack).

### Which files are replaced?
The folder can hold **any subset** of the archive's files (for example only the 80 text files you edited):
archive entries that have no file in the folder are left exactly as they are, and files in the folder that are
not part of the archive are ignored.

If the folder contains the extraction manifest, a file counts as edited only when its **SHA-256 differs** from the
hash recorded at extraction; timestamps are never used. (A different file length proves a change by itself, so
those files are not hashed.) A file the manifest does not list, or a folder without a manifest, means every file
present replaces its entry.

After each repack the result message counts the files in the folder that match an archive entry and says how many
were used, how many were skipped because they are byte-identical to the extracted original, and how many archive
entries had no file in the folder. A per-file CSV is written next to the new CPK (`<name>_repack_report.csv`).
The manifest also remembers which CPK it was extracted from; if you repack a different CPK, a warning says so,
because identical files then keep the data of the CPK you are repacking, not of the one you extracted.
Toolbar -> **Ignore manifest** forces every file present in the folder in, even byte-identical ones.

## Compare two CPKs

Toolbar -> **Compare CPKs...** opens a window where you choose the original and the patched archive.
Files are matched by path. Differing sizes are reported instantly; with "Compare file contents" ticked,
files of equal size are hashed too (compressed files are decompressed first), so recompressed-but-identical
files show as "Same content, stored differently". The result list can be filtered, exported to CSV, copied,
or the differing files extracted from either archive (select rows, or extract everything shown).
Without the content check, same-size files are counted but their bytes are NOT compared.

## Files that cannot be decompressed

The Layla decoder locates the bit stream through the block header (as the CRI tools do) and falls back to the
older end-of-data assumption, so blocks written by third-party compressors with trailing bytes decode too.
If a file still fails, extraction and comparison continue: extraction saves its stored bytes in
`_failed_decompress/<name>.crilayla` and lists the reason (file name, offsets, how far decoding got);
the comparison marks it "Could not decompress to compare".

In the Compare window, select one file and press **Byte differences of selected file...** to see exactly which
byte ranges differ between the two versions (decompressed first), with a hex and text preview of both sides.
