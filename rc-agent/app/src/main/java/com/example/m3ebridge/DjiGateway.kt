package com.example.m3ebridge

import dji.sdk.keyvalue.key.BatteryKey
import dji.sdk.keyvalue.key.FlightControllerKey
import dji.sdk.keyvalue.key.KeyTools
import dji.sdk.keyvalue.key.ProductKey
import dji.sdk.keyvalue.key.RemoteControllerKey
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

class DjiGateway {
    private val keys get() = KeyManager.getInstance()

    fun status(runtime: DjiRuntime): JSONObject {
        val location = keys.getValue(KeyTools.createKey(FlightControllerKey.KeyAircraftLocation3D))
        val attitude = keys.getValue(KeyTools.createKey(FlightControllerKey.KeyAircraftAttitude))
        val rcBattery = keys.getValue(KeyTools.createKey(RemoteControllerKey.KeyBatteryInfo))

        return JSONObject()
            .put("sdkPhase", runtime.phase.get())
            .put("sdkVersion", runtime.sdkVersion())
            .put("productConnected", runtime.productConnected.get())
            .put("productType", enumText(keys.getValue(KeyTools.createKey(ProductKey.KeyProductType))))
            .put("aircraftFirmware", stringOrBlank(keys.getValue(KeyTools.createKey(ProductKey.KeyFirmwareVersion))))
            .put("remoteControllerType", enumText(keys.getValue(KeyTools.createKey(RemoteControllerKey.KeyRemoteControllerType))))
            .put("remoteControllerFirmware", stringOrBlank(keys.getValue(KeyTools.createKey(RemoteControllerKey.KeyFirmwareVersion))))
            .putNullable("aircraftBatteryPercent", keys.getValue(KeyTools.createKey(BatteryKey.KeyChargeRemainingInPercent)))
            .putNullable("remoteControllerBatteryPercent", rcBattery?.batteryPercent)
            .putNullable("satelliteCount", keys.getValue(KeyTools.createKey(FlightControllerKey.KeyGPSSatelliteCount)))
            .put("flightMode", enumText(keys.getValue(KeyTools.createKey(FlightControllerKey.KeyFlightMode))))
            .putNullable("isFlying", keys.getValue(KeyTools.createKey(FlightControllerKey.KeyIsFlying)))
            .putNullable("latitude", location?.latitude)
            .putNullable("longitude", location?.longitude)
            .putNullable("altitudeMeters", location?.altitude)
            .putNullable("pitchDegrees", attitude?.pitch)
            .putNullable("rollDegrees", attitude?.roll)
            .putNullable("yawDegrees", attitude?.yaw)
            .put("timestamp", isoNow())
    }

    fun configuration(): JSONObject =
        JSONObject()
            .putNullable("heightLimitMeters", keys.getValue(KeyTools.createKey(FlightControllerKey.KeyHeightLimit)))
            .putNullable("goHomeHeightMeters", keys.getValue(KeyTools.createKey(FlightControllerKey.KeyGoHomeHeight)))

    fun isFlying(): Boolean =
        keys.getValue(KeyTools.createKey(FlightControllerKey.KeyIsFlying)) == true

    fun updateConfiguration(payload: JSONObject): JSONObject {
        // Validate the complete request before the first write. This avoids a partial
        // configuration update when one of two supplied values is invalid.
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
