package com.example.m3ebridge

import android.app.Application
import android.content.Context

class BridgeApplication : Application() {
    lateinit var djiRuntime: DjiRuntime
        private set

    lateinit var bridgeServer: BridgeServer
        private set

    override fun attachBaseContext(base: Context) {
        super.attachBaseContext(base)
        com.cySdkyc.clx.Helper.install(this)
    }

    override fun onCreate() {
        super.onCreate()

        djiRuntime = DjiRuntime(this)
        djiRuntime.start()

        bridgeServer = BridgeServer(
            application = this,
            djiRuntime = djiRuntime,
            token = BuildConfig.BRIDGE_TOKEN,
            bindAddress = BuildConfig.BRIDGE_BIND_ADDRESS,
            port = 49152
        )
        bridgeServer.start()
    }

    override fun onTerminate() {
        bridgeServer.stop()
        djiRuntime.stop()
        super.onTerminate()
    }
}
