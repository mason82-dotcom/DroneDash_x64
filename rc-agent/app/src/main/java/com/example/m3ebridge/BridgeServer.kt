package com.example.m3ebridge

import android.app.Application
import org.json.JSONObject
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.ByteArrayOutputStream
import java.io.EOFException
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.URLDecoder
import java.nio.charset.StandardCharsets
import java.util.Locale
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

class BridgeServer(
    private val application: Application,
    private val djiRuntime: DjiRuntime,
    private val token: String,
    private val port: Int
) {
    private val running = AtomicBoolean(false)
    private val workers = Executors.newFixedThreadPool(4)
    private var serverSocket: ServerSocket? = null
    private var acceptThread: Thread? = null

    fun start() {
        if (!running.compareAndSet(false, true)) return

        acceptThread = Thread({
            try {
                ServerSocket(port, 16, InetAddress.getByName("0.0.0.0")).use { server ->
                    serverSocket = server
                    while (running.get()) {
                        val socket = runCatching { server.accept() }.getOrNull() ?: break
                        workers.execute { handle(socket) }
                    }
                }
            } finally {
                running.set(false)
            }
        }, "m3e-bridge-accept").apply {
            isDaemon = true
            start()
        }
    }

    fun stop() {
        running.set(false)
        runCatching { serverSocket?.close() }
        workers.shutdownNow()
    }

    private fun handle(socket: Socket) {
        socket.use {
            it.soTimeout = 30_000
            val input = BufferedInputStream(it.getInputStream())
            val output = BufferedOutputStream(it.getOutputStream())

            try {
                val request = readRequest(input)

                if (request.path != "/api/v1/health" &&
                    !constantTimeEquals(request.headers["x-bridge-token"].orEmpty(), token)
                ) {
                    writeJson(output, 401, JSONObject().put("error", "invalid bridge token"))
                    return
                }

                route(request, output)
            } catch (e: Throwable) {
                runCatching {
                    writeJson(
                        output,
                        500,
                        JSONObject()
                            .put("error", e.message ?: e.javaClass.simpleName)
                    )
                }
            } finally {
                runCatching { output.flush() }
            }
        }
    }

    private fun route(request: Request, output: BufferedOutputStream) {
        when {
            request.method == "GET" && request.path == "/api/v1/health" -> {
                writeJson(
                    output,
                    200,
                    JSONObject()
                        .put("ok", true)
                        .put("sdkPhase", djiRuntime.phase.get())
                        .put("sdkVersion", djiRuntime.sdkVersion())
                        .put("registered", djiRuntime.registered.get())
                        .put("productConnected", djiRuntime.productConnected.get())
                        .putNullable("error", djiRuntime.lastError.get())
                )
            }

            request.method == "GET" && request.path == "/api/v1/status" -> {
                writeJson(output, 200, djiRuntime.gateway.status(djiRuntime))
            }

            request.method == "GET" && request.path == "/api/v1/config" -> {
                writeJson(output, 200, djiRuntime.gateway.configuration())
            }

            request.method == "PUT" && request.path == "/api/v1/config" -> {
                if (!djiRuntime.productConnected.get()) {
                    writeJson(output, 409, JSONObject().put("error", "aircraft not connected"))
                    return
                }
                val payload = JSONObject(request.body.toString(StandardCharsets.UTF_8))
                val result = djiRuntime.gateway.updateConfiguration(payload)
                writeJson(output, 200, result)
            }

            request.method == "GET" && request.path == "/api/v1/media" -> {
                if (!djiRuntime.productConnected.get()) {
                    writeJson(output, 409, JSONObject().put("error", "aircraft not connected"))
                    return
                }
                writeJsonArray(output, 200, djiRuntime.mediaGateway.listMedia().toString())
            }

            request.method == "GET" &&
                request.path.matches(Regex("/api/v1/media/\\d+/download")) -> {
                if (!djiRuntime.productConnected.get()) {
                    writeJson(output, 409, JSONObject().put("error", "aircraft not connected"))
                    return
                }

                val index = request.path
                    .substringAfter("/api/v1/media/")
                    .substringBefore("/")
                    .toInt()

                val downloaded = djiRuntime.mediaGateway.downloadToTemp(application, index)
                try {
                    writeFile(
                        output,
                        downloaded.file,
                        downloaded.downloadName
                    )
                } finally {
                    downloaded.file.delete()
                }
            }

            else -> writeJson(output, 404, JSONObject().put("error", "route not found"))
        }
    }

    private fun readRequest(input: BufferedInputStream): Request {
        val requestLine = readLine(input) ?: throw EOFException("empty request")
        val parts = requestLine.split(' ')
        if (parts.size < 2) throw IllegalArgumentException("invalid request line")

        val method = parts[0].uppercase(Locale.ROOT)
        val rawPath = parts[1].substringBefore('?')
        val path = URLDecoder.decode(rawPath, StandardCharsets.UTF_8.name())

        val headers = mutableMapOf<String, String>()
        while (true) {
            val line = readLine(input) ?: break
            if (line.isEmpty()) break
            val separator = line.indexOf(':')
            if (separator > 0) {
                headers[line.substring(0, separator).trim().lowercase(Locale.ROOT)] =
                    line.substring(separator + 1).trim()
            }
        }

        val contentLength = headers["content-length"]?.toIntOrNull() ?: 0
        if (contentLength !in 0..65_536) {
            throw IllegalArgumentException("request body too large")
        }

        val body = ByteArray(contentLength)
        var offset = 0
        while (offset < contentLength) {
            val read = input.read(body, offset, contentLength - offset)
            if (read < 0) throw EOFException("unexpected EOF")
            offset += read
        }

        return Request(method, path, headers, body)
    }

    private fun readLine(input: BufferedInputStream): String? {
        val buffer = ByteArrayOutputStream()
        var previous = -1

        while (true) {
            val current = input.read()
            if (current == -1) {
                return if (buffer.size() == 0) null else buffer.toString(StandardCharsets.UTF_8.name())
            }

            if (previous == '\r'.code && current == '\n'.code) {
                val bytes = buffer.toByteArray()
                return String(bytes, 0, (bytes.size - 1).coerceAtLeast(0), StandardCharsets.UTF_8)
            }

            buffer.write(current)
            previous = current

            if (buffer.size() > 16_384) {
                throw IllegalArgumentException("HTTP line too long")
            }
        }
    }

    private fun writeJson(output: BufferedOutputStream, status: Int, body: JSONObject) {
        writeJsonArray(output, status, body.toString())
    }

    private fun writeJsonArray(output: BufferedOutputStream, status: Int, body: String) {
        val bytes = body.toByteArray(StandardCharsets.UTF_8)
        writeHeaders(
            output,
            status,
            "application/json; charset=utf-8",
            bytes.size.toLong(),
            null
        )
        output.write(bytes)
        output.flush()
    }

    private fun writeFile(output: BufferedOutputStream, file: java.io.File, downloadName: String) {
        val safeHeaderName = downloadName.replace("\"", "_").replace("\r", "_").replace("\n", "_")
        writeHeaders(
            output,
            200,
            "application/octet-stream",
            file.length(),
            "attachment; filename=\"$safeHeaderName\""
        )

        file.inputStream().buffered(1024 * 1024).use { input ->
            input.copyTo(output, 1024 * 1024)
        }
        output.flush()
    }

    private fun writeHeaders(
        output: BufferedOutputStream,
        status: Int,
        contentType: String,
        contentLength: Long,
        contentDisposition: String?
    ) {
        val reason = when (status) {
            200 -> "OK"
            400 -> "Bad Request"
            401 -> "Unauthorized"
            404 -> "Not Found"
            409 -> "Conflict"
            else -> "Internal Server Error"
        }

        val text = buildString {
            append("HTTP/1.1 $status $reason\r\n")
            append("Content-Type: $contentType\r\n")
            append("Content-Length: $contentLength\r\n")
            append("Connection: close\r\n")
            append("Cache-Control: no-store\r\n")
            if (contentDisposition != null) {
                append("Content-Disposition: $contentDisposition\r\n")
            }
            append("\r\n")
        }

        output.write(text.toByteArray(StandardCharsets.US_ASCII))
    }

    private fun constantTimeEquals(a: String, b: String): Boolean {
        val left = a.toByteArray(StandardCharsets.UTF_8)
        val right = b.toByteArray(StandardCharsets.UTF_8)
        var diff = left.size xor right.size
        val max = maxOf(left.size, right.size)
        for (i in 0 until max) {
            val x = if (i < left.size) left[i].toInt() else 0
            val y = if (i < right.size) right[i].toInt() else 0
            diff = diff or (x xor y)
        }
        return diff == 0
    }

    private data class Request(
        val method: String,
        val path: String,
        val headers: Map<String, String>,
        val body: ByteArray
    )
}
