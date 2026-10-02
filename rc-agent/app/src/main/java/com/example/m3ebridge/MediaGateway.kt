package com.example.m3ebridge

import android.content.Context
import dji.sdk.keyvalue.value.camera.CameraStorageLocation
import dji.v5.common.callback.CommonCallbacks
import dji.v5.common.error.IDJIError
import dji.v5.manager.datacenter.MediaDataCenter
import dji.v5.manager.datacenter.media.MediaFile
import dji.v5.manager.datacenter.media.MediaFileDownloadListener
import dji.v5.manager.datacenter.media.MediaFileFilter
import dji.v5.manager.datacenter.media.MediaFileListDataSource
import dji.v5.manager.datacenter.media.MediaFileListState
import dji.v5.manager.datacenter.media.MediaFileListStateListener
import dji.v5.manager.datacenter.media.PullMediaFileListParam
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.RandomAccessFile
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

class MediaGateway {
    private val lock = Any()
    private val manager get() = MediaDataCenter.getInstance().mediaManager

    fun listMedia(): JSONArray = synchronized(lock) {
        withMediaMode {
            val files = refreshList()
            JSONArray().apply {
                files.forEach { file ->
                    put(
                        JSONObject()
                            .put("index", file.fileIndex)
                            .put("name", file.fileName ?: "DJI_MEDIA_${file.fileIndex}")
                            .put("type", file.fileType?.toString() ?: "")
                            .put("sizeBytes", file.fileSize)
                            .put("date", file.date?.toString() ?: "")
                    )
                }
            }
        }
    }

    fun downloadToTemp(context: Context, index: Int): DownloadedFile = synchronized(lock) {
        withMediaMode {
            val mediaFile = refreshList().firstOrNull { it.fileIndex == index }
                ?: throw IllegalArgumentException("Media index $index not found")

            val safeName = sanitizeFileName(mediaFile.fileName ?: "DJI_MEDIA_$index")
            val target = File(context.cacheDir, "m3e_${System.nanoTime()}_$safeName")
            download(mediaFile, target)
            DownloadedFile(target, safeName, mediaFile.fileSize)
        }
    }

    private fun refreshList(): List<MediaFile> {
        val source = MediaFileListDataSource.Builder()
            .setLocation(CameraStorageLocation.SDCARD)
            .build()
        manager.setMediaFileDataSource(source)

        val upToDate = CountDownLatch(1)
        val listener = object : MediaFileListStateListener {
            override fun onUpdate(state: MediaFileListState) {
                if (state == MediaFileListState.UP_TO_DATE) {
                    upToDate.countDown()
                }
            }
        }

        manager.addMediaFileListStateListener(listener)
        try {
            awaitCompletion("pull media list", 30) { callback ->
                val param = PullMediaFileListParam.Builder()
                    .mediaFileIndex(-1)
                    .count(-1)
                    .filter(MediaFileFilter.ALL)
                    .build()
                manager.pullMediaFileListFromCamera(param, callback)
            }

            if (manager.mediaFileListState != MediaFileListState.UP_TO_DATE &&
                !upToDate.await(30, TimeUnit.SECONDS)
            ) {
                throw IllegalStateException("Timeout waiting for media list to become UP_TO_DATE")
            }

            return manager.mediaFileListData?.data?.toList() ?: emptyList()
        } finally {
            manager.removeMediaFileListStateListener(listener)
        }
    }

    private fun download(mediaFile: MediaFile, target: File) {
        target.parentFile?.mkdirs()
        if (target.exists()) {
            target.delete()
        }

        val latch = CountDownLatch(1)
        val failure = AtomicReference<String?>(null)
        val raf = RandomAccessFile(target, "rw")

        try {
            mediaFile.pullOriginalMediaFileFromCamera(0, object : MediaFileDownloadListener {
                override fun onStart() = Unit

                override fun onProgress(total: Long, current: Long) = Unit

                override fun onRealtimeDataUpdate(data: ByteArray, position: Long) {
                    synchronized(raf) {
                        raf.seek(position)
                        raf.write(data)
                    }
                }

                override fun onFinish() {
                    latch.countDown()
                }

                override fun onFailure(error: IDJIError) {
                    failure.set(error.description())
                    latch.countDown()
                }
            })

            if (!latch.await(20, TimeUnit.MINUTES)) {
                throw IllegalStateException("Timeout downloading ${mediaFile.fileName}")
            }

            failure.get()?.let { throw IllegalStateException("DJI media download failed: $it") }
        } finally {
            raf.close()
        }
    }

    private fun <T> withMediaMode(block: () -> T): T {
        awaitCompletion("enable media manager", 15) { manager.enable(it) }
        try {
            return block()
        } finally {
            runCatching {
                awaitCompletion("disable media manager", 15) { manager.disable(it) }
            }
        }
    }

    private fun awaitCompletion(
        operation: String,
        timeoutSeconds: Long,
        start: (CommonCallbacks.CompletionCallback) -> Unit
    ) {
        val latch = CountDownLatch(1)
        val failure = AtomicReference<String?>(null)

        start(object : CommonCallbacks.CompletionCallback {
            override fun onSuccess() {
                latch.countDown()
            }

            override fun onFailure(error: IDJIError) {
                failure.set(error.description())
                latch.countDown()
            }
        })

        if (!latch.await(timeoutSeconds, TimeUnit.SECONDS)) {
            throw IllegalStateException("Timeout: $operation")
        }

        failure.get()?.let { throw IllegalStateException("$operation: $it") }
    }

    private fun sanitizeFileName(name: String): String =
        name.replace(Regex("[\\\\/:*?\\\"<>|]"), "_")

    data class DownloadedFile(
        val file: File,
        val downloadName: String,
        val expectedSize: Long
    )
}
