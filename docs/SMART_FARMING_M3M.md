# Smart Farming for DJI Mavic 3 Multispectral

DroneDash Smart Farming is built specifically around the DJI Mavic 3 Multispectral (M3M).

## Sensor basis

Official DJI M3M specifications used by this module:

- RGB: 20 MP 4/3 CMOS.
- Multispectral: four 5 MP / 2592×1944 TIFF cameras.
- Green: 560 ± 16 nm.
- Red: 650 ± 16 nm.
- Red Edge: 730 ± 16 nm.
- NIR: 860 ± 26 nm.
- Integrated sunlight sensor records irradiance information.
- M3M images store DN values; they are not already reflectance rasters.
- The five cameras are time synchronized and RTK/camera timing is synchronized at microsecond scale.

Official references:

- https://enterprise.dji.com/mavic-3-m/specs
- https://www.dji.com/support/product/mavic-3-m
- https://ag.dji.com/mavic-3-m
- https://dl.djicdn.com/downloads/DJI_Mavic_3_Enterprise/20230829/Mavic_3M_Image_Processing_Guide_EN.pdf

## Vegetation indices

- NDVI = (NIR - Red) / (NIR + Red)
- NDRE = (NIR - RedEdge) / (NIR + RedEdge)
- GNDVI = (NIR - Green) / (NIR + Green)

The quicklook applies DJI metadata factors BlackLevel, SensorGain, ExposureTime, SensorGainAdjustment and Irradiance before the band ratio is calculated.

This is deliberately labelled a single-capture quicklook. Full vignetting correction, distortion correction, sub-pixel band co-registration, orthorectification and reflectance-panel calibration are separate processing stages.

## Agriculture workflows included

1. crop-vigor and stress scouting;
2. management zones / variable-rate workflows;
3. plant count, emergence and stand-uniformity analysis;
4. weed scouting and targeted field inspection;
5. water, drainage and soil-variability investigation;
6. repeated RTK monitoring over time.

These are agronomic use cases, not automatic diagnoses.

## Practice references supplied for this module

- Avary Drone: https://avarydrone.com/blogs/learn/how-to-perform-ndvi-analysis-with-the-dji-mavic-3-multispectral-a-complete-practical-guide
- NineTenths: https://9tenthsco.com/need-an-agricultural-drone-solutions-its-dji-mavic-3-enterprise-multispectral-drone/
- Talos Drones: https://talosdrones.com/blogs/blog/top-5-uses-of-multispectral-drones-in-agriculture-a-deep-dive-into-dji-mavic-3m

The third-party articles are used as workflow/use-case references. Sensor facts and processing constraints are grounded in DJI's own specifications and image-processing guide.


## Local open-source processing toolchain

DroneDash can probe and stage a local processing workspace around three optional external
engines. None of their native binaries are committed to the repository.

- GDAL: gdalinfo is used for probing and gdalbuildvrt -separate builds the corrected
  Green/Red/Red-Edge/NIR stack.
- Orfeo ToolBox (OTB): otbcli_BandMath applies the per-band DJI DN compensation and
  otbcli_BandMathX calculates NDVI, NDRE and GNDVI from the corrected four-band stack.
- Python + OpenCV: the bundled opencv_m3m.py worker uses ECC affine registration to align
  Green, Red and Red Edge to the NIR band. It writes both the registered TIFF and a JSON file
  containing the ECC score and transform matrix.

The worker is copied into the Windows output as smart-farming/opencv_m3m.py. It requires a local
Python environment with opencv-python and numpy; DroneDash does not download packages or modify
the user's Python installation.

Optional environment overrides:

- DRONEDASH_GDAL_BIN — directory containing GDAL command-line programs.
- DRONEDASH_OTB_BIN — directory containing OTB CLI applications.
- DRONEDASH_PYTHON — full path to the desired Python executable.

The Smart Farming UI has a Lokales Processing tab that probes all three engines and, for a
selected complete M3M capture, creates a self-contained workspace with
local-processing-plan.json, run-smart-farming-processing.ps1, registration transforms,
registered-band outputs, corrected-band outputs and an indices directory.

The generated script is intentionally not executed automatically. The operator can inspect it
before running native third-party tools. This first toolchain remains a pixel-space,
single-capture processing workflow, not a georeferenced field product. OpenCV intermediate TIFFs
must not be treated as orthorectified survey rasters. ODM/photogrammetry is the next stage for
full-field orthomosaics and map products.

## Direct local execution

The generated local processing plan can now be executed directly from DroneDash after explicit
operator confirmation. `LocalProcessingRunner` starts every planned executable without a shell
where possible, captures stdout/stderr into `local-processing.log`, reports step progress in the
UI and can terminate the active process tree when the operator presses **Abbrechen**. Expected
output files are checked after each successful exit code.

This keeps the reviewable JSON/PowerShell artifacts while also supporting an interactive
one-click execution path. Source M3M files remain read-only inputs.

## NodeODM / ODM orthomosaic worker

Smart Farming now includes an optional NodeODM client. The default endpoint is
`http://127.0.0.1:3000/`, configurable in the UI or with `DRONEDASH_NODEODM_URL`.
Authentication tokens can be supplied through the masked UI field or
`DRONEDASH_NODEODM_TOKEN`.

DroneDash probes `/info` and requires an ODM engine version of at least 3.5.3 before enabling
Mavic 3M task submission. A task uploads only the four multispectral TIFF bands from complete
captures (Green, Red, Red Edge and NIR), then uses the NodeODM init/upload/commit workflow so
large datasets are streamed file-by-file rather than buffered as one giant multipart request.

Default M3M options are:

- `radiometric-calibration = camera+sun` (operator can switch to `camera`; ODM documents
  `camera+sun` as experimental);
- `primary-band = NIR`.

DroneDash monitors the NodeODM task status and progress, can request cancellation and can download
the completed `all.zip` result. The RGB `_D.JPG` frames are intentionally not uploaded by this
M3M orthomosaic path; current ODM has explicit Mavic 3M multispectral handling and discards the
very different RGB camera when the multispectral camera set is present.

OpenDroneMap's current multispectral documentation lists DJI Mavic 3 Multispectral as supported
starting with ODM 3.5.3. It recommends processing all multispectral bands together and supports
radiometric calibration modes `camera` and `camera+sun`.

## ODM field products

Completed NodeODM `all.zip` results can be imported back into DroneDash. ZIP extraction is
path-normalized and rejects path traversal and Unix symlink entries before writing to disk.
DroneDash locates `odm_orthophoto/odm_orthophoto.tif` (or one unambiguous equivalent in the
archive), then calls local `gdalinfo -json` to inspect raster dimensions, CRS and band metadata.

Band mapping is conservative. If GDAL band descriptions identify Red, Green, NIR and Red Edge,
those descriptions are authoritative. For a four-band orthophoto produced by the DroneDash
M3M-only NodeODM path, missing descriptions may fall back to ODM's current normalized
multispectral order: Red, Green, NIR, Red Edge. The UI surfaces this fallback as a warning.

From the georeferenced ODM orthophoto DroneDash builds a four-step OTB field-product plan:

1. NDVI GeoTIFF from NIR and Red;
2. NDRE GeoTIFF from NIR and Red Edge;
3. GNDVI GeoTIFF from NIR and Green;
4. a five-class NDVI scouting-zone GeoTIFF.

The default scouting thresholds are 0.20, 0.40, 0.60 and 0.80. They are operator-editable and
are intentionally described as scouting classes, not crop diagnosis, fertilizer rates,
irrigation prescriptions or pesticide recommendations. The generated `field-products.json`
records the source orthophoto, resolved band mapping, thresholds and output paths.

Current ODM source normalizes identifiable multispectral bands with Red before Green, then NIR
and Red Edge (when RGB/Blue are absent), while the standard ODM output path remains
`odm_orthophoto/odm_orthophoto.tif`.
