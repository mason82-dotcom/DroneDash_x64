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
