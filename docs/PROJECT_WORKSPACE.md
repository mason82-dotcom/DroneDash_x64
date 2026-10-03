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

## Standard project folders

When a `.ddproj` is created, opened or saved under a new path, DroneDash derives and creates a
portable default folder layout next to the project file:

- `01_Planning`;
- `02_Datasets/Photogrammetry`, `02_Datasets/PV`, `02_Datasets/SmartFarming`;
- `03_Processing/Photogrammetry`, `03_Processing/PV`, `03_Processing/SmartFarming`;
- `04_Results/Photogrammetry`, `04_Results/PV`, `04_Results/SmartFarming`;
- `05_Exports`.

The folders are default destinations, not mandatory storage locations. Existing projects remain
schema-compatible and external paths are still supported. If the project directory is read-only,
opening the `.ddproj` is not blocked; affected dialogs fall back to their previous locations.

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

## Clickable workflow and direct flight-plan handoff

The four project stages are clickable. Planning opens the flight planner; Dataset, Processing and
Result/QA select the most relevant downstream module from the current project artifacts. If the
project contains only a flight plan, the aircraft profile is used as a routing hint: M3M selects
Smart Farming, M3T selects PV and other supported profiles select Photogrammetry.

The flight planner also exposes explicit handoff actions for Photogrammetry, PV and Smart Farming.
A handoff first saves the current `.ddplan` (using `01_Planning` when an active project exists),
registers it in the `.ddproj`, switches to the destination module and injects that exact plan path.
Photogrammetry and PV use the plan for route matching; Smart Farming records the plan reference in
its exported dataset-QA manifest so acquisition intent remains traceable.

## Project dashboard and QA

The Project tab still derives four workflow stages from linked artifacts:

1. Planning — `.ddplan` and/or validated DJI KMZ;
2. Dataset — photogrammetry/Smart-Farming manifests, source folders or explicitly linked source imagery;
3. Processing — local processing plans/workspaces and NodeODM result archives;
4. Result / QA — PV analyses, Smart-Farming field-product manifests and vegetation rasters.

In addition, four QA cards summarize project evidence:

- **RTK-QA** reads registered Photogrammetry, PV and Smart-Farming manifests and reports RTK metadata
  coverage; Photogrammetry/PV precision fields are included when available.
- **Dataset-QA** reports image/capture completeness, manifest issue counts and PV processing failures.
- **Processing** reports local processing plans/workspaces and NodeODM result archives.
- **Result-QA** reports registered PV analyses, PV processing failures, field-product manifests and
  vegetation rasters.

These cards summarize recorded project evidence; they do not turn RTK presence, vegetation indices
or thermal candidates into an automatic scientific or engineering acceptance decision.


## Persistent project pipeline

The Project tab can now start a persistent dataset pipeline from a source folder. The structural
probe is deliberately conservative and does not modify source data:

- M3M capture names with multispectral bands select Smart Farming;
- DJI thermal `*_T.JPG` / `*_T.JPEG` files select PV Analysis;
- other supported JPG/JPEG/DNG/TIF/TIFF datasets select Photogrammetry;
- folders that simultaneously contain M3M multispectral bands and thermal files are marked
  ambiguous and are not auto-routed.

The active job is stored next to the `.ddproj` as `project-pipeline.json`. Paths below the project
folder are stored relatively, while external source folders remain absolute. Project `Save As`
copies and rebases the pipeline state so it continues to resolve the same source dataset and flight
plan.

A pipeline handoff carries both the selected source folder and the latest linked flight plan into
the chosen module. Photogrammetry, PV and Smart Farming therefore open with the same project context
without requiring the operator to pick the dataset again.

### Pipeline gates

Five project gates are evaluated from persisted project evidence:

1. **Flight plan** — linked plan exists; absence is a warning rather than an automatic blocker.
2. **Dataset** — the module-specific QA/result manifest has been registered.
3. **RTK** — uses the project RTK-QA evidence from registered manifests.
4. **Processing** — module-specific processing evidence is present.
5. **Results** — module-specific result artifacts are registered and their project QA is readable.

Gate values are `PASS`, `PRÜFEN`, `OFFEN` or `BLOCKIERT`. They are workflow controls, not a
substitute for domain-specific engineering, agronomic or thermographic acceptance criteria.


## Processing job orchestrator

A recognized project pipeline now owns a persistent processing-job ledger in
`project-processing-jobs.json`. DroneDash creates the next concrete worker from the current module
and registered evidence instead of treating "Processing" as a single boolean state.

The current worker chain is:

- Photogrammetry: dataset/XMP analysis -> registered `photogrammetry-manifest.json`;
- PV: radiometric M3T thermal batch -> registered `pv-analysis.json`;
- Smart Farming: M3M dataset QA -> NodeODM -> georeferenced field products.

Each worker records its job ID, pipeline job ID, status, percentage, current step, message,
timestamps, output references and a dedicated log under `03_Processing/_ProjectJobs`. The Project
tab mirrors that state live while the worker is running.

A worker can be `Queued`, `Running`, `AwaitingOutput`, `Succeeded`, `Failed` or `Canceled`.
Process completion alone does not mark a job successful: the expected project artifact must be
registered. For example, a completed NodeODM task remains `AwaitingOutput` until its `all.zip`
has been downloaded and registered.

Registering the expected output reconciles the current job and allows the orchestrator to create the
next Smart-Farming worker automatically. Processing and result gates are therefore released from
project evidence, not only from transient UI state. A failed worker is reflected as a blocked
Processing gate; running, canceled or output-pending work remains visible as a non-pass state.

Project `Save As` copies and rebases both the pipeline state and processing ledger. Existing log
and output references continue to resolve to the same files unless those files are explicitly
moved by the operator.
