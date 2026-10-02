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

    fun updateConfiguration(payload: JSONObject): JSONObject {
        if (payload.has("heightLimitMeters") && !payload.isNull("heightLimitMeters")) {
            setIntKey(
                KeyTools.createKey(FlightControllerKey.KeyHeightLimit),
                payload.getInt("heightLimitMeters"),
                "heightLimitMeters"
            )
        }

        if (payload.has("goHomeHeightMeters") && !payload.isNull("goHomeHeightMeters")) {
            setIntKey(
                KeyTools.createKey(FlightControllerKey.KeyGoHomeHeight),
                payload.getInt("goHomeHeightMeters"),
                "goHomeHeightMeters"
            )
        }

        return configuration()
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
