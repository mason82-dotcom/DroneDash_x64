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
