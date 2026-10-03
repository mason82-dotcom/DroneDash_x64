package com.mason82.dronedash.rcbridge

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

        deleteStaleMediaDownloads()

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

    // Downloads interrupted by a process kill would otherwise stay in the cache forever.
    // Only untouched files are removed so an active download in another process survives.
    private fun deleteStaleMediaDownloads() {
        val cutoff = System.currentTimeMillis() - 30L * 60 * 1000
        cacheDir.listFiles { file ->
            file.isFile && file.name.startsWith("m3e_") && file.lastModified() < cutoff
        }?.forEach { it.delete() }
    }

    override fun onTerminate() {
        bridgeServer.stop()
        djiRuntime.stop()
        super.onTerminate()
    }
}
