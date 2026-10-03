package com.mason82.dronedash.rcbridge

import android.os.Bundle
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import java.net.NetworkInterface
import java.util.Collections
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

class MainActivity : AppCompatActivity() {
    private val scheduler = Executors.newSingleThreadScheduledExecutor()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        scheduler.scheduleAtFixedRate({
            val app = application as BridgeApplication
            val status = buildString {
                append("SDK: ")
                append(app.djiRuntime.sdkVersion())
                append("\nPhase: ")
                append(app.djiRuntime.phase.get())
                append("\nRegistered: ")
                append(app.djiRuntime.registered.get())
                append("\nAircraft connected: ")
                append(app.djiRuntime.productConnected.get())
                app.djiRuntime.lastError.get()?.let {
                    append("\nDJI error: ")
                    append(it)
                }
            }

            val bridgeStatus = buildString {
                append(if (app.bridgeServer.isRunning()) "Bridge aktiv" else "Bridge inaktiv")
                append(" · ")
                append(app.bridgeServer.bindAddress)
                append(":49152 · Token: ")
                append(if (BuildConfig.BRIDGE_TOKEN == "change-me-now") "DEFAULT (ändern!)" else "konfiguriert")
                app.bridgeServer.lastError.get()?.let {
                    append("\nBridge error: ")
                    append(it)
                }
            }

            val networkInfo = if (app.bridgeServer.isLoopbackBinding()) {
                "USB: adb forward tcp:49152 tcp:49152\n\nLAN-Zugriff ist standardmäßig deaktiviert."
            } else {
                "USB: adb forward tcp:49152 tcp:49152\n\nLAN-Adressen:\n${localAddresses().joinToString("\n")}"
            }

            runOnUiThread {
                findViewById<TextView>(R.id.sdk_status).text = status
                findViewById<TextView>(R.id.bridge_status).text = bridgeStatus
                findViewById<TextView>(R.id.network_info).text = networkInfo
            }
        }, 0, 1, TimeUnit.SECONDS)
    }

    override fun onDestroy() {
        scheduler.shutdownNow()
        super.onDestroy()
    }

    private fun localAddresses(): List<String> =
        runCatching {
            Collections.list(NetworkInterface.getNetworkInterfaces())
                .flatMap { iface ->
                    Collections.list(iface.inetAddresses)
                        .filter { !it.isLoopbackAddress && !it.isLinkLocalAddress }
                        .map { "${it.hostAddress}:49152" }
                }
        }.getOrDefault(emptyList())
}
