# DroneDash project workspace

`*.ddproj` is the top-level project container that links the independent DroneDash workflows.
The project file is versioned JSON and stores references rather than copying flight data or
processing products.

## Referenced artifacts

The workspace recognizes and can track:

- DroneDash flight plans (`*.ddplan`);
- DJI Wayline KMZ exports;
- `photogrammetry-manifest.json`;
- `pv-analysis.json`;
- `smart-farming-dataset.json`;
- Smart Farming `field-products.json`;
- local processing plans;
- NodeODM result ZIP archives;
- NDVI / NDRE / GNDVI / scouting-zone rasters;
- thermal and M3M source images when explicitly added;
- source-data directories and processing workspaces.

Files below the `.ddproj` directory are stored as relative paths to keep a project folder portable.
References outside the project directory stay absolute so DroneDash never silently redirects them.
`Speichern unter` rebases references so they still point to the same targets.

## Integrity snapshots

For normal files DroneDash records file size, last-write time and SHA-256. To avoid unexpectedly
hashing very large NodeODM archives or raster products, automatic SHA-256 is skipped above 256 MiB;
those files use the size/timestamp snapshot instead. Directories are checked for existence only.

`Integrität prüfen` reports `OK`, `Geändert`, `Fehlt` or `Fehler` without altering the referenced
artifact. Removing an item from the project removes only the reference; the file or directory on
disk is never deleted.

## Discovery

`Projektordner scannen` finds known DroneDash control/processing artifacts recursively, but does
not automatically register thousands of raw JPG/TIFF source images. Raw datasets should normally
be linked as a source-data directory, keeping the `.ddproj` compact.

## Active project session and automatic registration

Opening or creating a `.ddproj` now activates a process-wide DroneDash project session. Workflow
modules can register successful outputs through this single serialized session rather than writing
the project file independently. Re-registering the same resolved path refreshes its integrity
snapshot while preserving the artifact ID and original `addedAtUtc`; it does not create a duplicate.

Automatic registrations currently include:

- Flight Planning: saved `.ddplan` files and KMZ exports that pass DroneDash KMZ validation;
- Photogrammetry: `photogrammetry-manifest.json` plus the source dataset directory;
- PV Analysis: `pv-analysis.json` plus the source dataset directory;
- Smart Farming: dataset manifests, local processing plans/workspaces, NodeODM result ZIPs and
  successful ODM vegetation field products.

Registration is best-effort from the producing module: failure to update `.ddproj` is surfaced in
the module status but does not retroactively invalidate an already successful file export.

## Workflow dashboard

The Project tab derives four descriptive stages from the artifacts currently linked to the project:

1. Planning — `.ddplan` and/or validated DJI KMZ;
2. Dataset — photogrammetry/Smart-Farming manifests, source folders or explicitly linked source imagery;
3. Processing — local processing plans/workspaces and NodeODM result archives;
4. Analysis / field product — PV analyses, Smart-Farming field-product manifests and vegetation rasters.

The dashboard reports whether each stage has referenced artifacts and how many. It is a project
organization view, not a scientific quality score: integrity and domain-specific QA remain separate.
