package com.connectipn;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;

/** The on-screen keymap layout, shared by the phone overlay and the PC editor. */
public final class KeymapModel {

    public static final class Control {
        public String type = "button";
        public String label = "";
        public String key = "";
        public float x = 0.8f;
        public float y = 0.8f;
        public float size = 0.1f;
        public float w = 0.3f;
        public float h = 0.3f;
        public float cx = 0.85f;
        public float cy = 0.85f;
    }

    /** A sensible FPS preset: WASD stick, jump, and a look region. */
    public static final String DEFAULT = "{"
            + "\"name\":\"FPS preset\","
            + "\"controls\":["
            + "{\"type\":\"stick\",\"label\":\"WASD\",\"x\":0.06,\"y\":0.60,\"size\":0.28,\"cx\":0.20,\"cy\":0.80},"
            + "{\"type\":\"button\",\"label\":\"Jump\",\"key\":\"Space\",\"x\":0.82,\"y\":0.78,\"size\":0.13,\"cx\":0.88,\"cy\":0.86},"
            + "{\"type\":\"button\",\"label\":\"Fire\",\"key\":\"Ctrl\",\"x\":0.66,\"y\":0.66,\"size\":0.13,\"cx\":0.72,\"cy\":0.78},"
            + "{\"type\":\"look\",\"label\":\"Look\",\"x\":0.42,\"y\":0.16,\"w\":0.54,\"h\":0.42}"
            + "]}";

    private KeymapModel() {
    }

    public static List<Control> parse(String json) {
        List<Control> controls = new ArrayList<>();
        try {
            JSONObject root = new JSONObject(json);
            JSONArray array = root.optJSONArray("controls");
            if (array == null) {
                return controls;
            }
            for (int i = 0; i < array.length(); i++) {
                JSONObject item = array.getJSONObject(i);
                Control c = new Control();
                c.type = item.optString("type", "button");
                c.label = item.optString("label", "");
                c.key = item.optString("key", "");
                c.x = (float) item.optDouble("x", 0.8);
                c.y = (float) item.optDouble("y", 0.8);
                c.size = (float) item.optDouble("size", 0.1);
                c.w = (float) item.optDouble("w", 0.3);
                c.h = (float) item.optDouble("h", 0.3);
                c.cx = (float) item.optDouble("cx", c.x);
                c.cy = (float) item.optDouble("cy", c.y);
                controls.add(c);
            }
        } catch (Exception e) {
            // A corrupt layout falls back to an empty overlay rather than crashing.
        }
        return controls;
    }

    public static String toJson(List<Control> controls) {
        try {
            JSONArray array = new JSONArray();
            for (Control c : controls) {
                JSONObject item = new JSONObject();
                item.put("type", c.type);
                item.put("label", c.label);
                item.put("key", c.key);
                item.put("x", c.x);
                item.put("y", c.y);
                item.put("size", c.size);
                item.put("w", c.w);
                item.put("h", c.h);
                item.put("cx", c.cx);
                item.put("cy", c.cy);
                array.put(item);
            }
            JSONObject root = new JSONObject();
            root.put("name", "ConnectIPN layout");
            root.put("controls", array);
            return root.toString();
        } catch (Exception e) {
            return DEFAULT;
        }
    }
}
