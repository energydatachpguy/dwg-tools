# DWG Tools: Reference Tree + WBLOCK helper

Map a heavily nested equipment drawing, then turn any block in it into its own clean WBLOCK file,
in master coordinates, so it can be inserted at 0,0 into a new master.

Work on a **deliberate copy** of the equipment model, never the live master.

## Start
**New computer?** Follow [SETUP.md](SETUP.md) (Python, Git, clone, `setup.bat`). Supported: AutoCAD / Plant 3D 2020 and newer (2025+ also needs the matching .NET SDK).

Double-click **`run.bat`**. The page opens at http://localhost:5014 (this computer only; **`run_shared.bat`** lets other
PCs on the network open it). Keep that window open while you work. `run.bat` rebuilds the plugin whenever its source is newer.

**After an update** (`git pull`): restart `run.bat`. Plant 3D does **not** need a restart: every build of the plugin gets its own name
(`plugin\bin\DwgTools_<date-time>.dll`, command `DWGT<date-time>`), so the app loads the newest build next to any older one
already in Plant 3D and calls that build's own command.

## Where you are
The bar at the top always shows the whole workflow as a timeline
(**Drawing > Map it > Pick a block > Light up in CAD > Choose one > Save WBLOCK > Saved**),
with one sentence underneath saying what is happening or what to do next, and a status:
**Working** (blue), **Needs you** (amber), **Completed** (green) or **Failed** (red). Finished steps show how long they took.
The WBLOCK panel has its own timeline for the block you are on
(Light up > Choose > Copy > Explode to 0 > Save file > Check > Saved), and a step-by-step log of what AutoCAD did.
The sidebar lists every drawing you have mapped and every WBLOCK you have saved (click one to open its folder).

## 1. Map the drawing
Pick your copy of the equipment DWG and click **Build tree**. The tree shows every block, what is nested in it,
how many times, and where. **Sort by** puts the most nested blocks first.

## 2. Pick blocks
- **Search** (press `/`): every word counts, case and punctuation don't ("cable 4h" finds `100-M-101$0$2-CABLE TO 4H PAD`).
  The tree shrinks to the way down to each match; Enter / Shift+Enter step through them, Esc clears.
- **Find my CAD selection**: select a block in Plant 3D (or click one when asked) and the tree opens to it,
  with the exact copy marked. Several selected: **Tick all of these** ticks them, narrowed to those copies.
- **Tick** blocks to put them in one WBLOCK (all under the same parent). For a block used more than once,
  **placements** lets you tick which copies; tick just one and that one sets the coordinates.
  Right-click a block for a WBLOCK of just that one.

## 3. Make the WBLOCK (bar at the bottom: Make WBLOCK)
1. **Light it up in CAD.** Every ticked copy is highlighted and CAD zooms to them.
2. **Check what goes in.** Every copy CAD found, each with a tick box; untick any to leave it out.
3. **Save it.** Choose the folder and name (Save as… opens Windows' own dialog) and the **standard layers to keep**
   (from `PEC Standard CAD Layers at Block Level.xlsx` next to run.bat; edit that file to change the standard).
   Each copy is copied, exploded to plain geometry, merged into one file, then sorted by colour:
   an object whose colour is a kept layer's colour goes on that layer (colour ByLayer); every other object goes on
   **layer 0, colour ByLayer, linetype Continuous, lineweight ByLayer**. Base point **0,0,0 = the master's 0,0,0**;
   no geographic location is carried over. After saving, up to 200 real points per file are looked up in the
   saved WBLOCK to prove each one is at the same coordinates.
4. **Insert it back** into the drawing open in CAD: as a block named after the file, at 0,0, on a layer named after the
   file in capitals (editable). Choose **Replace the original** (erase the copies it was made from) or **Keep both** each time.
   A block of the same name already in the drawing is redefined, so every copy updates.

What it guarantees:
- **This tool never saves a drawing.** Inserting changes the drawing open in CAD in memory only; check it and save it
  yourself. UNDO in CAD reverses an insert. Work on a deliberate copy of the master.
- **An existing file is never replaced.** If a WBLOCK name is taken, the page says so before you can save.

## Import Blocks (WBLOCKs you already have)
**Import Blocks** in the top bar inserts existing WBLOCK files into the drawing open in Plant 3D, without making anything first.
Add one or more files (Windows' own dialog; pick several at once), set each one's layer (defaults to the block name in capitals)
and X, Y (default 0,0), then **Import**. A block of the same name already in the drawing, or an existing layer, is asked about first.
Nothing is saved.

**Remove highlight** (top of the WBLOCK panel) clears the highlight in AutoCAD (REGEN does the same).

## Other tabs and pages
- **Block list**: every block definition, sortable and filterable; click a row to go to it in the tree.
- **Source files**: where each block's own .dwg lives (this project, another project, none), matched by name.
  Only folder listings are read.
- **Files & xrefs**: the drawing, its xrefs, and anything outside the project.
- **Past work** (top bar): every WBLOCK saved and every drawing mapped.

## Logs (for tracking problems)
Everything is written to **`logs\dwgtools-<date>.log`** in this folder, one file per day, append only:
app start (which plugin build, whether the last build failed), every tree build, every light-up / pick / WBLOCK
(drawing, block path, handles, save path, each AutoCAD step with a check or a cross, which plugin build answered,
the plugin's own log lines, how long it took), Plant 3D status checks, and errors the page runs into.
Every plugin build is also appended to **`logs\build-history.log`**. Open the log from **Log** in the top bar or at
http://localhost:5014/logs. When something goes wrong, the answer is usually in the last block of today's file.

## File safety
- Only `plugin\src\SafeFs.cs` and `webapp\safe_fs.py` may delete, move or overwrite a file. `run.bat` runs
  `tools\safety_audit.py` at every start and will not start the app if any other code does.
- The tool deletes or replaces only its own temp files, and only ones it created itself (recorded with a SHA-256 fingerprint
  in `%LOCALAPPDATA%\DwgTools\created_files.jsonl`). No drawing file is ever saved or written by the tool except
  the new WBLOCK files you name.
