package com.example.m3ebridge

import org.json.JSONObject

fun JSONObject.putNullable(name: String, value: Any?): JSONObject =
    put(name, value ?: JSONObject.NULL)
