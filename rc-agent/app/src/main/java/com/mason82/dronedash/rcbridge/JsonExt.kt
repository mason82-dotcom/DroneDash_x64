package com.mason82.dronedash.rcbridge

import org.json.JSONObject

fun JSONObject.putNullable(name: String, value: Any?): JSONObject =
    put(name, value ?: JSONObject.NULL)
