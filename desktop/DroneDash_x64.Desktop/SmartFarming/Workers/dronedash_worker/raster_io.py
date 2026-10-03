"""GDAL GeoTIFF helpers and the asynchronous tile writer."""

import json
import os


def _prepare_source_tile(array, nodata, np):
    values = array.astype(np.float32, copy=False)

    if nodata is not None and np.isfinite(nodata):
        values = values.copy()
        values[values == np.float32(nodata)] = np.nan

    return values


def _write_json(path, payload):
    if not path:
        return

    os.makedirs(
        os.path.dirname(
            os.path.abspath(path)
        ),
        exist_ok=True,
    )

    temp = (
        os.path.abspath(path)
        + "."
        + os.urandom(8).hex()
        + ".tmp"
    )

    try:
        with open(
            temp,
            "w",
            encoding="utf-8",
        ) as handle:
            json.dump(
                payload,
                handle,
                indent=2,
            )

        os.replace(
            temp,
            os.path.abspath(path),
        )
    finally:
        try:
            if os.path.exists(temp):
                os.remove(temp)
        except Exception:
            pass


def _gdal_profile(source):
    try:
        transform = source.GetGeoTransform(
            can_return_null=True
        )
    except TypeError:
        try:
            transform = source.GetGeoTransform()
        except Exception:
            transform = None

    projection = source.GetProjection()

    return {
        "width": int(source.RasterXSize),
        "height": int(source.RasterYSize),
        "transform": transform,
        "projection": projection or None,
    }


def _create_geotiff_from_profile(
    profile,
    output,
    data_type,
    nodata,
    description,
):
    from osgeo import gdal

    driver = gdal.GetDriverByName("GTiff")
    if driver is None:
        raise RuntimeError("GDAL GTiff driver is unavailable")

    absolute_output = os.path.abspath(output)
    os.makedirs(
        os.path.dirname(absolute_output),
        exist_ok=True,
    )

    temp = (
        absolute_output
        + "."
        + os.urandom(8).hex()
        + ".tmp.tif"
    )

    target = driver.Create(
        temp,
        profile["width"],
        profile["height"],
        1,
        data_type,
        options=[
            "TILED=YES",
            "COMPRESS=DEFLATE",
            "BIGTIFF=IF_SAFER",
            "BLOCKXSIZE=512",
            "BLOCKYSIZE=512",
        ],
    )

    if target is None:
        raise RuntimeError(
            f"GDAL failed to create output: {temp}"
        )

    if profile["transform"] is not None:
        target.SetGeoTransform(
            profile["transform"]
        )

    if profile["projection"]:
        target.SetProjection(
            profile["projection"]
        )

    band = target.GetRasterBand(1)
    band.SetNoDataValue(nodata)
    band.SetDescription(description)

    return absolute_output, temp, target, band


def _benchmark_geotiff_write(
    values,
    output,
    data_type,
    nodata,
    description,
):
    import time

    height, width = values.shape[:2]
    directory = os.path.dirname(
        os.path.abspath(output)
    )
    os.makedirs(
        directory,
        exist_ok=True,
    )

    benchmark_output = os.path.join(
        directory,
        ".dronedash-write-benchmark-" +
        os.urandom(8).hex() +
        ".tif",
    )

    profile = {
        "width": int(width),
        "height": int(height),
        "transform": None,
        "projection": None,
    }

    absolute_output = None
    temp = None
    target = None
    band = None

    try:
        (
            absolute_output,
            temp,
            target,
            band,
        ) = _create_geotiff_from_profile(
            profile,
            benchmark_output,
            data_type,
            nodata,
            description,
        )

        started = time.perf_counter()

        band.WriteArray(
            values,
            0,
            0,
        )
        band = None
        target.FlushCache()

        return max(
            time.perf_counter() - started,
            0.0,
        )
    finally:
        band = None
        target = None
        _cleanup_temp(temp)
        _cleanup_temp(absolute_output)


class _AsyncGeoTiffWriter:
    def __init__(
        self,
        profile,
        output,
        data_type,
        nodata,
        description,
        pipeline_depth,
    ):
        from concurrent.futures import ThreadPoolExecutor

        self._profile = profile
        self._output = output
        self._data_type = data_type
        self._nodata = nodata
        self._description = description
        self._depth = max(
            1,
            min(
                int(pipeline_depth),
                4,
            ),
        )
        self._executor = ThreadPoolExecutor(
            max_workers=1,
            thread_name_prefix="dronedash-gdal-write",
        )
        self._pending = []
        self._state = {}
        self._finished = False

    def _ensure_open(self):
        if "target" in self._state:
            return

        (
            absolute_output,
            temp,
            target,
            band,
        ) = _create_geotiff_from_profile(
            self._profile,
            self._output,
            self._data_type,
            self._nodata,
            self._description,
        )

        self._state.update(
            {
                "absoluteOutput": absolute_output,
                "temp": temp,
                "target": target,
                "band": band,
            }
        )

    def _write(self, window, values):
        self._ensure_open()

        x, y, _, _ = window
        self._state["band"].WriteArray(
            values,
            x,
            y,
        )

    def submit(self, window, values):
        if self._finished:
            raise RuntimeError(
                "cannot submit a tile after writer finalization"
            )

        if len(self._pending) >= self._depth:
            self._pending.pop(0).result()

        self._pending.append(
            self._executor.submit(
                self._write,
                window,
                values,
            )
        )

    def _finish_on_writer(self, statistics):
        self._ensure_open()

        if statistics is not None:
            self._state["band"].SetStatistics(
                statistics["minimum"],
                statistics["maximum"],
                statistics["average"],
                statistics["standardDeviation"],
            )

        self._state["band"] = None
        self._state["target"].FlushCache()
        self._state["target"] = None

        os.replace(
            self._state["temp"],
            self._state["absoluteOutput"],
        )

        self._state["temp"] = None
        return self._state["absoluteOutput"]

    def finish(self, statistics=None):
        if self._finished:
            return self._state.get(
                "absoluteOutput"
            )

        try:
            for future in self._pending:
                future.result()

            self._pending.clear()

            result = self._executor.submit(
                self._finish_on_writer,
                statistics,
            ).result()

            self._finished = True
            return result
        finally:
            if self._finished:
                self._executor.shutdown(
                    wait=True,
                    cancel_futures=True,
                )

    def _abort_on_writer(self):
        self._state["band"] = None
        self._state["target"] = None
        _cleanup_temp(
            self._state.get("temp")
        )
        self._state["temp"] = None

    def abort(self):
        if self._finished:
            return

        for future in self._pending:
            future.cancel()

        self._pending.clear()

        try:
            self._executor.submit(
                self._abort_on_writer
            ).result()
        except Exception:
            _cleanup_temp(
                self._state.get("temp")
            )
        finally:
            self._executor.shutdown(
                wait=True,
                cancel_futures=True,
            )
            self._finished = True


def _cleanup_temp(path):
    try:
        if path and os.path.exists(path):
            os.remove(path)
    except Exception:
        pass
