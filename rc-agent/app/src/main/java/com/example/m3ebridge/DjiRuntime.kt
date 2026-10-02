package com.example.m3ebridge

import android.app.Application
import android.os.Handler
import android.os.Looper
import dji.v5.common.error.IDJIError
import dji.v5.common.register.DJISDKInitEvent
import dji.v5.manager.SDKManager
import dji.v5.manager.interfaces.SDKManagerCallback
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference

class DjiRuntime(private val application: Application) {
    val phase = AtomicReference("IDLE")
    val lastError = AtomicReference<String?>(null)
    val registered = AtomicBoolean(false)
    val productConnected = AtomicBoolean(false)
    val productId = AtomicReference<Int?>(null)

    val gateway = DjiGateway()
    val mediaGateway = MediaGateway()

    fun start() {
        phase.set("INITIALIZING")
        Handler(Looper.getMainLooper()).post {
            SDKManager.getInstance().init(application, object : SDKManagerCallback {
                override fun onInitProcess(event: DJISDKInitEvent?, totalProcess: Int) {
                    phase.set("INITIALIZING:${event ?: "UNKNOWN"}:$totalProcess")
                    if (event == DJISDKInitEvent.INITIALIZE_COMPLETE) {
                        phase.set("REGISTERING")
                        SDKManager.getInstance().registerApp()
                    }
                }

                override fun onRegisterSuccess() {
                    registered.set(true)
                    lastError.set(null)
                    phase.set(if (productConnected.get()) "READY" else "REGISTERED")
                }

                override fun onRegisterFailure(error: IDJIError?) {
                    registered.set(false)
                    val description = error?.description() ?: "Unknown DJI registration error"
                    lastError.set(description)
                    phase.set("REGISTRATION_FAILED")
                }

                override fun onProductConnect(productId: Int) {
                    this@DjiRuntime.productId.set(productId)
                    productConnected.set(true)
                    phase.set(if (registered.get()) "READY" else "PRODUCT_CONNECTED")
                }

                override fun onProductDisconnect(productId: Int) {
                    this@DjiRuntime.productId.set(productId)
                    productConnected.set(false)
                    phase.set(if (registered.get()) "REGISTERED" else "DISCONNECTED")
                }

                override fun onProductChanged(productId: Int) {
                    this@DjiRuntime.productId.set(productId)
                }

                override fun onDatabaseDownloadProgress(current: Long, total: Long) = Unit
            })
        }
    }

    fun sdkVersion(): String =
        runCatching { SDKManager.getInstance().sdkVersion }.getOrDefault("unknown")

    fun stop() {
        Handler(Looper.getMainLooper()).post {
            runCatching { SDKManager.getInstance().destroy() }
            phase.set("STOPPED")
        }
    }
}
