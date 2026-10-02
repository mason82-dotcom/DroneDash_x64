# Bridge API v1

The Windows application and RC Pro Enterprise agent communicate over a deliberately small HTTP/JSON contract.

Default endpoint: `http://127.0.0.1:49152/` when using `adb forward`.
Authentication: `X-Bridge-Token: <token>`.

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/health` | Agent health and SDK registration phase |
| GET | `/api/v1/status` | Aircraft, RC, batteries, GPS, attitude, flight mode |
| GET | `/api/v1/config` | Read max altitude and RTH altitude |
| PUT | `/api/v1/config` | Change max altitude and/or RTH altitude |
| GET | `/api/v1/media` | Read the current aircraft SD-card media list |
| GET | `/api/v1/media/{index}/download` | Download the original file |

Example configuration payload:

```json
{
  "heightLimitMeters": 120,
  "goHomeHeightMeters": 60
}
```

The RC agent intentionally does not provide a generic media-upload endpoint. DJI MSDK V5 exposes
aircraft-media enumeration and download, but not arbitrary upload of files back into the aircraft
camera storage. Uploading files from the Windows PC *to the RC controller* can be added as a separate
controller-storage endpoint without pretending that it writes to the aircraft SD card.

## Validation and error semantics

For the Mavic 3 Enterprise family, both `heightLimitMeters` and `goHomeHeightMeters` are
validated in the RC agent before any DJI KeyManager write is attempted. Values must be integers in
the range **20..500 m**. If a request contains multiple settings, all supplied values are validated
first so an invalid second value cannot leave the first one partially applied.

Relevant HTTP responses:

- `400 Bad Request`: malformed JSON, invalid parameter type, or value outside the supported range.
- `401 Unauthorized`: missing or invalid bridge token.
- `404 Not Found`: unknown route/media index.
- `409 Conflict`: operation requires an aircraft connection, or a configuration write was requested while the aircraft is flying.
- `500 Internal Server Error`: DJI SDK failure, timeout, or unexpected bridge error.

## Flight-state write interlock

`PUT /api/v1/config` is rejected with `409 Conflict` while DJI reports
`KeyIsFlying=true`. Reading status/configuration and downloading media remain separate operations.
This interlock is enforced in the RC agent, so it cannot be bypassed by a desktop UI mistake.

## Extended live telemetry

`GET /api/v1/status` now also exposes:

- aircraft N/E/D velocity, horizontal ground speed and climb/descent speed;
- GPS signal level, Home Point, compass heading/error, wind speed/warning/direction;
- aircraft battery voltage, current, temperature, remaining/full capacity;
- RTK enable/health state, maintain-accuracy state, reference-station source and positioning solution;
- RTK mobile/base coordinates, standard deviations, heading/fused heading and per-receiver GNSS satellite counts.

The RTK integration is **read-only**. DroneDash registers DJI RTK listeners but does not enable/disable
RTK, change the RTK source, configure NTRIP credentials, or modify base-station settings.

## Camera, gimbal and storage telemetry

The primary payload is read from DJI component index `LEFT_OR_MAIN`. The status response includes
camera type/firmware/mode, photo/recording activity, current storage location, SD/internal storage
state, capacity/free space, remaining photo count/video duration, and main-gimbal mode plus
pitch/roll/yaw attitude.

These fields are observational only. DroneDash does not start/stop recording, trigger photos,
rotate the gimbal, format storage, or change camera settings through this status path.
