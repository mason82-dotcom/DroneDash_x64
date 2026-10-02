package com.example.m3ebridge

import dji.sdk.keyvalue.key.BatteryKey
import dji.sdk.keyvalue.key.CameraKey
import dji.sdk.keyvalue.key.FlightControllerKey
import dji.sdk.keyvalue.key.GimbalKey
import dji.sdk.keyvalue.key.KeyTools
import dji.sdk.keyvalue.key.ProductKey
import dji.sdk.keyvalue.key.RemoteControllerKey
import dji.sdk.keyvalue.value.camera.CameraStorageLocation
import dji.sdk.keyvalue.value.common.ComponentIndexType
import dji.v5.common.callback.CommonCallbacks
import dji.v5.common.error.IDJIError
import dji.v5.manager.KeyManager
import org.json.JSONObject
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference
import kotlin.math.sqrt

class DjiGateway {
    private val keys get() = KeyManager.getInstance()

    fun status(runtime: DjiRuntime): JSONObject {
        val location = keys.getValue(KeyTools.createKey(FlightControllerKey.KeyAircraftLocation3D))
        val attitude = keys.getValue(KeyTools.createKey(FlightControllerKey.KeyAircraftAttitude))
        val velocity = keys.getValue(KeyTools.createKey(FlightControllerKey.KeyAircraftVelocity))
        val homeLocation = keys.getValue(KeyTools.createKey(FlightControllerKey.KeyHomeLocation))
        val rcBattery = keys.getValue(KeyTools.createKey(RemoteControllerKey.KeyBatteryInfo))

        val velocityNorth = velocity?.x
        val velocityEast = velocity?.y
        val velocityDown = velocity?.z
        val groundSpeed = if (velocityNorth != null && velocityEast != null) {
            sqrt(velocityNorth * velocityNorth + velocityEast * velocityEast)
        } else {
            null
        }
        val verticalSpeed = velocityDown?.let { -it }

        val batteryVoltageMv = keys.getValue(KeyTools.createKey(BatteryKey.KeyVoltage))
        val batteryCurrentMa = keys.getValue(KeyTools.createKey(BatteryKey.KeyCurrent))
        val batteryTemperatureC = keys.getValue(KeyTools.createKey(BatteryKey.KeyBatteryTemperature))
        val batteryRemainingMah = keys.getValue(KeyTools.createKey(BatteryKey.KeyChargeRemaining))
        val batteryFullChargeMah = keys.getValue(KeyTools.createKey(BatteryKey.KeyFullChargeCapacity))

        val windSpeedDmPs = keys.getValue(KeyTools.createKey(FlightControllerKey.KeyWindSpeed))
        val rtk = RtkTelemetrySource.snapshot

        val mainComponent = ComponentIndexType.LEFT_OR_MAIN
        val cameraType = keys.getValue(KeyTools.createKey(CameraKey.KeyCameraType, mainComponent))
        val cameraFirmware = keys.getValue(KeyTools.createKey(CameraKey.KeyFirmwareVersion, mainComponent))
        val cameraMode = keys.getValue(KeyTools.createKey(CameraKey.KeyCameraMode, mainComponent))
        val cameraIsShootingPhoto = keys.getValue(KeyTools.createKey(CameraKey.KeyIsShootingPhoto, mainComponent))
        val cameraIsRecording = keys.getValue(KeyTools.createKey(CameraKey.KeyIsRecording, mainComponent))
        val cameraStorageInfos = keys.getValue(KeyTools.createKey(CameraKey.KeyCameraStorageInfos, mainComponent))
        val sdStorage = cameraStorageInfos?.getCameraStorageInfoByLocation(CameraStorageLocation.SDCARD)
        val internalStorage = cameraStorageInfos?.getCameraStorageInfoByLocation(CameraStorageLocation.INTERNAL)

        val gimbalAttitude = keys.getValue(KeyTools.createKey(GimbalKey.KeyGimbalAttitude, mainComponent))
        val gimbalMode = keys.getValue(KeyTools.createKey(GimbalKey.KeyGimbalMode, mainComponent))

        return JSONObject()
            .put("sdkPhase", runtime.phase.get())
            .put("sdkVersion", runtime.sdkVersion())
            .put("productConnected", runtime.productConnected.get())
            .put("productType", enumText(keys.getValue(KeyTools.createKey(ProductKey.KeyProductType))))
            .put("aircraftFirmware", stringOrBlank(keys.getValue(KeyTools.createKey(ProductKey.KeyFirmwareVersion))))
            .put("remoteControllerType", enumText(keys.getValue(KeyTools.createKey(RemoteControllerKey.KeyRemoteControllerType))))
            .put("remoteControllerFirmware", stringOrBlank(keys.getValue(KeyTools.createKey(RemoteControllerKey.KeyFirmwareVersion))))
            .putNullable("aircraftBatteryPercent", keys.getValue(KeyTools.createKey(BatteryKey.KeyChargeRemainingInPercent)))
            .putNullable("aircraftBatteryVoltageMv", batteryVoltageMv)
            .putNullable("aircraftBatteryCurrentMa", batteryCurrentMa)
            .putNullable("aircraftBatteryTemperatureC", batteryTemperatureC)
            .putNullable("aircraftBatteryRemainingMah", batteryRemainingMah)
            .putNullable("aircraftBatteryFullChargeMah", batteryFullChargeMah)
            .putNullable("remoteControllerBatteryPercent", rcBattery?.batteryPercent)
            .putNullable("satelliteCount", keys.getValue(KeyTools.createKey(FlightControllerKey.KeyGPSSatelliteCount)))
            .put("gpsSignalLevel", enumText(keys.getValue(KeyTools.createKey(FlightControllerKey.KeyGPSSignalLevel))))
            .put("flightMode", enumText(keys.getValue(KeyTools.createKey(FlightControllerKey.KeyFlightMode))))
            .putNullable("isFlying", keys.getValue(KeyTools.createKey(FlightControllerKey.KeyIsFlying)))
            .putNullable("areMotorsOn", keys.getValue(KeyTools.createKey(FlightControllerKey.KeyAreMotorsOn)))
            .putNullable("latitude", location?.latitude)
            .putNullable("longitude", location?.longitude)
            .putNullable("altitudeMeters", location?.altitude)
            .putNullable("takeoffAltitudeMeters", keys.getValue(KeyTools.createKey(FlightControllerKey.KeyTakeoffLocationAltitude)))
            .putNullable("velocityNorthMs", velocityNorth)
            .putNullable("velocityEastMs", velocityEast)
            .putNullable("velocityDownMs", velocityDown)
            .putNullable("groundSpeedMs", groundSpeed)
            .putNullable("verticalSpeedMs", verticalSpeed)
            .putNullable("pitchDegrees", attitude?.pitch)
            .putNullable("rollDegrees", attitude?.roll)
            .putNullable("yawDegrees", attitude?.yaw)
            .putNullable("compassHeadingDegrees", keys.getValue(KeyTools.createKey(FlightControllerKey.KeyCompassHeading)))
            .putNullable("compassHasError", keys.getValue(KeyTools.createKey(FlightControllerKey.KeyCompassHasError)))
            .putNullable("homeLocationSet", keys.getValue(KeyTools.createKey(FlightControllerKey.KeyIsHomeLocationSet)))
            .putNullable("homeLatitude", homeLocation?.latitude)
            .putNullable("homeLongitude", homeLocation?.longitude)
            .putNullable("windSpeedMs", windSpeedDmPs?.div(10.0))
            .put("windWarning", enumText(keys.getValue(KeyTools.createKey(FlightControllerKey.KeyWindWarning))))
            .put("windDirection", enumText(keys.getValue(KeyTools.createKey(FlightControllerKey.KeyWindDirection))))
            .putNullable("rtkEnabled", rtk.enabled)
            .putNullable("rtkHealthy", rtk.healthy)
            .putNullable("rtkMaintainAccuracyEnabled", rtk.maintainAccuracyEnabled)
            .putNullable("rtkReferenceStationSource", rtk.referenceStationSource)
            .putNullable("rtkPositioningSolution", rtk.positioningSolution)
            .putNullable("rtkMobileLatitude", rtk.mobileLatitude)
            .putNullable("rtkMobileLongitude", rtk.mobileLongitude)
            .putNullable("rtkMobileAltitudeMeters", rtk.mobileAltitudeM)
            .putNullable("rtkBaseLatitude", rtk.baseLatitude)
            .putNullable("rtkBaseLongitude", rtk.baseLongitude)
            .putNullable("rtkBaseAltitudeMeters", rtk.baseAltitudeM)
            .putNullable("rtkStdLongitudeMeters", rtk.stdLongitude)
            .putNullable("rtkStdLatitudeMeters", rtk.stdLatitude)
            .putNullable("rtkStdAltitudeMeters", rtk.stdAltitude)
            .putNullable("rtkHeading", rtk.rtkHeading)
            .putNullable("rtkRealHeading", rtk.realHeading)
            .put("rtkSatelliteCounts", JSONObject(rtk.satelliteCounts))
            .putNullable("rtkError", rtk.error)
            .put("cameraType", enumText(cameraType))
            .put("cameraFirmware", stringOrBlank(cameraFirmware))
            .put("cameraMode", enumText(cameraMode))
            .putNullable("cameraIsShootingPhoto", cameraIsShootingPhoto)
            .putNullable("cameraIsRecording", cameraIsRecording)
            .put("cameraCurrentStorage", enumText(cameraStorageInfos?.currentStorageType))
            .put("sdStorageState", enumText(sdStorage?.storageState))
            .putNullable("sdStorageCapacityMb", sdStorage?.storageCapacity)
            .putNullable("sdStorageLeftMb", sdStorage?.storageLeftCapacity)
            .putNullable("sdAvailablePhotoCount", sdStorage?.availablePhotoCount)
            .putNullable("sdAvailableVideoSeconds", sdStorage?.availableVideoDuration)
            .put("internalStorageState", enumText(internalStorage?.storageState))
            .putNullable("internalStorageCapacityMb", internalStorage?.storageCapacity)
            .putNullable("internalStorageLeftMb", internalStorage?.storageLeftCapacity)
            .putNullable("internalAvailablePhotoCount", internalStorage?.availablePhotoCount)
            .putNullable("internalAvailableVideoSeconds", internalStorage?.availableVideoDuration)
            .put("gimbalMode", enumText(gimbalMode))
            .putNullable("gimbalPitchDegrees", gimbalAttitude?.pitch)
            .putNullable("gimbalRollDegrees", gimbalAttitude?.roll)
            .putNullable("gimbalYawDegrees", gimbalAttitude?.yaw)
            .put("timestamp", isoNow())
    }

    fun configuration(): JSONObject =
        JSONObject()
            .putNullable("heightLimitMeters", keys.getValue(KeyTools.createKey(FlightControllerKey.KeyHeightLimit)))
            .putNullable("goHomeHeightMeters", keys.getValue(KeyTools.createKey(FlightControllerKey.KeyGoHomeHeight)))

    fun isFlying(): Boolean =
        keys.getValue(KeyTools.createKey(FlightControllerKey.KeyIsFlying)) == true

    fun updateConfiguration(payload: JSONObject): JSONObject {
        val heightLimit = readOptionalAltitude(payload, "heightLimitMeters")
        val goHomeHeight = readOptionalAltitude(payload, "goHomeHeightMeters")

        if (heightLimit != null) {
            setIntKey(
                KeyTools.createKey(FlightControllerKey.KeyHeightLimit),
                heightLimit,
                "heightLimitMeters"
            )
        }

        if (goHomeHeight != null) {
            setIntKey(
                KeyTools.createKey(FlightControllerKey.KeyGoHomeHeight),
                goHomeHeight,
                "goHomeHeightMeters"
            )
        }

        return configuration()
    }

    private fun readOptionalAltitude(payload: JSONObject, name: String): Int? {
        if (!payload.has(name) || payload.isNull(name)) {
            return null
        }

        val value = runCatching { payload.getInt(name) }
            .getOrElse { throw IllegalArgumentException("$name must be an integer") }

        if (value !in 20..500) {
            throw IllegalArgumentException("$name must be between 20 and 500 metres")
        }

        return value
    }

    private fun setIntKey(key: dji.sdk.keyvalue.key.DJIKey<Int>, value: Int, label: String) {
        val latch = CountDownLatch(1)
        val error = AtomicReference<String?>(null)

        keys.setValue(key, value, object : CommonCallbacks.CompletionCallback {
            override fun onSuccess() {
                latch.countDown()
            }

            override fun onFailure(djiError: IDJIError) {
                error.set(djiError.description())
                latch.countDown()
            }
        })

        if (!latch.await(10, TimeUnit.SECONDS)) {
            throw IllegalStateException("Timeout while setting $label")
        }
        error.get()?.let { throw IllegalStateException("$label: $it") }
    }

    private fun isoNow(): String =
        SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSSXXX", Locale.US).format(Date())

    private fun enumText(value: Any?): String = value?.toString() ?: ""
    private fun stringOrBlank(value: String?): String = value ?: ""
}
