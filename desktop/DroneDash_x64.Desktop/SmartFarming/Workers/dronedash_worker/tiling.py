"""Tile windows, read-ahead prefetching, raster statistics and option parsing."""


def _update_stats(values, np, stats):
    finite = np.isfinite(values)

    if not finite.any():
        return

    selected = values[finite].astype(
        np.float64,
        copy=False,
    )

    count = int(selected.size)
    tile_sum = float(np.sum(selected))
    tile_sum_squares = float(
        np.sum(selected * selected)
    )
    tile_min = float(np.min(selected))
    tile_max = float(np.max(selected))

    stats["count"] += count
    stats["sum"] += tile_sum
    stats["sumSquares"] += tile_sum_squares
    stats["minimum"] = (
        tile_min
        if stats["minimum"] is None
        else min(stats["minimum"], tile_min)
    )
    stats["maximum"] = (
        tile_max
        if stats["maximum"] is None
        else max(stats["maximum"], tile_max)
    )


def _final_stats(stats):
    count = stats["count"]

    if count <= 0:
        raise RuntimeError(
            "geospatial raster contains no finite output pixels"
        )

    average = stats["sum"] / count
    variance = max(
        0.0,
        stats["sumSquares"] / count
        - average * average,
    )

    return {
        "validPixels": count,
        "minimum": stats["minimum"],
        "maximum": stats["maximum"],
        "average": average,
        "standardDeviation": variance ** 0.5,
    }


def _tile_windows(width, height, tile_size):
    for y in range(0, height, tile_size):
        tile_height = min(tile_size, height - y)

        for x in range(0, width, tile_size):
            tile_width = min(tile_size, width - x)
            yield (x, y, tile_width, tile_height)


def _prefetched_tiles(windows, reader, pipeline_depth):
    from concurrent.futures import ThreadPoolExecutor

    depth = max(1, min(int(pipeline_depth), 4))
    iterator = iter(windows)
    pending = []

    with ThreadPoolExecutor(
        max_workers=1,
        thread_name_prefix="dronedash-gdal-read",
    ) as executor:
        for _ in range(depth):
            try:
                window = next(iterator)
            except StopIteration:
                break

            pending.append(
                (
                    window,
                    executor.submit(reader, window),
                )
            )

        while pending:
            window, future = pending.pop(0)
            payload = future.result()

            try:
                next_window = next(iterator)
            except StopIteration:
                next_window = None

            if next_window is not None:
                pending.append(
                    (
                        next_window,
                        executor.submit(reader, next_window),
                    )
                )

            yield (window, payload)


def _parse_auto_or_int(value, minimum, maximum, name):
    text = str(value).strip().lower()

    if text == "auto":
        return None

    try:
        parsed = int(text)
    except (TypeError, ValueError):
        raise RuntimeError(
            f"{name} must be 'auto' or an integer"
        )

    if parsed < minimum or parsed > maximum:
        raise RuntimeError(
            f"{name} must be within {minimum}..{maximum}"
        )

    return parsed
