# About this fork

This repository is a fork of [IOExtend/NwcIO](https://github.com/IOExtend/NwcIO) (MIT License, copyright (c) 2026 mahach666),
based on release 0.18.0 (commit `3656529`). The original license is kept in [LICENSE](LICENSE).

## What is changed

### 1. Unknown objects in a `.nwd` partition trailer are skipped instead of failing the whole hierarchy

Files: `src/NwcIO/Internal/Parsing/HierarchyParser.cs`, `src/NwcIO/Internal/Parsing/LcObjectReader.cs`

**Problem.** Upstream reads the trailer after a class-32 partition with a fixed grammar,
`[i32 2][i32] 3 x f64 ...`, and assumes it holds no objects (docs/format-notes.md §2.2). Some `.nwd` files (seen: Revit exports
that contain photometric lights) store objects there: the second `i32` is a count, and that many new objects of class 27 follow, each
with a class-185 material JSON (`"tag":"GenericPhotometricLight"`). The fixed grammar then reads the objects as doubles, falls out of
alignment, and fails a few bytes later with an error such as

```
LcOpNwdLogicalHierarchy at byte N: 1869181810 bytes pass the end of the data
```

(1869181810 is `"rsio"`, the 5th–8th characters of the JSON, read as a string length). Every label after that point is lost, so the
geometry can only be exported with `--no-tree`, without categories, levels or properties.

**Change.** When the trailer count is positive and a new object of an unknown class follows, the parser skips to the next point where
the stream can be read again: the next top-level pointer (`[next id][32|53]`) or the hierarchy tail (`[1]` + zero pad). Skipped ids are
registered, so later object numbering is unchanged; a back-reference to a skipped object is accepted only in attribute slots and fails
in node, username or element-id slots. If a later read fails, the next resync candidate is tried, and if every candidate fails the
original error is thrown. Each skip is logged to stderr (byte range, object class, ids, readable text).

Nothing is guessed silently. The existing strict checks still apply after a skip: the stream must end exactly at the zero pad, element
indices must stay inside the GUID table, and every fragment must land on a class-22 leaf whose bounding box and primitive count match
its geometry record (`ModelGraphReader`). A wrong skip fails these checks instead of producing mislabelled meshes.

### 2. `tools/nwdtool`: inventory and filtered export

A small command line tool on top of the library:

- Labels come from the node's ancestors. Which ancestor is the model file, level, category and family depends on the exporter
  (Revit: file > level > category > family > type > element, i.e. 1/2/3/4). Nothing is assumed: a `depth` map declares it
  (Revit layout by default), and rules can also match the whole ancestor `path`.
- `nwdtool inventory <in.nwd> <out_dir> [depth]` writes `inventory.json`: length unit, sample ancestor paths per model file (to check
  `depth`), model files, levels, categories and the 40 heaviest families per category, each with mesh and triangle counts and a bounding
  box in metres. Per level it gives `floor_top_m`, the mode of the top elevation of flat, large meshes (height < 0.5 m, area > 1 m2),
  which does not depend on category names or language, and `base_z_mode_m`, the mode of all mesh bottoms (often the slab soffit).
- `nwdtool export <in.nwd> <keep.json> <out_dir>` keeps the meshes selected by `keep` / `drop` rules (regular expressions on model file,
  level, category, family or path), converts to metres, shifts to an origin and a ground level, groups meshes by module, model file,
  level, category and material, and writes raw float32 vertices / uint32 indices per group with a `groups.json` manifest. Rules under
  `lights` export only the positions of those elements to `lights.json`; rules under `ground` put kept meshes in the Ground module.

See the header of `tools/nwdtool/Program.cs` for the `keep.json` format.

## Build

```
docker run --rm -v "$PWD:/src" -w /src/tools/nwdtool mcr.microsoft.com/dotnet/sdk:10.0 dotnet build -c Release
```
