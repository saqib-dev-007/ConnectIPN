package com.connectipn;

import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioPlaybackCaptureConfiguration;
import android.media.AudioRecord;
import android.media.projection.MediaProjection;
import android.os.Build;
import android.util.Log;

/**
 * Captures the phone's speaker mix with Android's playback-capture API and pushes it
 * to the PC, so mirrored video has sound. Needs Android 10 and the RECORD_AUDIO
 * permission; anything the source app marks as non-capturable stays silent, which is
 * the platform rule and not something an app can override.
 */
public final class ScreenAudioCapture {

    private static final String TAG = "ConnectIPN-audio";
    private static final int SAMPLE_RATE = 48000;
    private static final int CHUNK = 3840; // 20 ms of stereo 16-bit

    private AudioRecord record;
    private Thread thread;
    private volatile boolean running;

    public boolean start(MediaProjection projection) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q || projection == null) {
            return false;
        }
        try {
            AudioPlaybackCaptureConfiguration config =
                    new AudioPlaybackCaptureConfiguration.Builder(projection)
                            .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                            .addMatchingUsage(AudioAttributes.USAGE_GAME)
                            .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                            .build();

            AudioFormat format = new AudioFormat.Builder()
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setSampleRate(SAMPLE_RATE)
                    .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
                    .build();

            int minimum = AudioRecord.getMinBufferSize(SAMPLE_RATE,
                    AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT);
            int bufferSize = Math.max(minimum > 0 ? minimum : 0, CHUNK * 4);

            record = new AudioRecord.Builder()
                    .setAudioFormat(format)
                    .setBufferSizeInBytes(bufferSize)
                    .setAudioPlaybackCaptureConfig(config)
                    .build();

            if (record.getState() != AudioRecord.STATE_INITIALIZED) {
                Log.w(TAG, "AudioRecord did not initialise");
                release();
                return false;
            }

            record.startRecording();
            running = true;
            thread = new Thread(this::loop, "connectipn-audio");
            thread.setDaemon(true);
            thread.start();
            return true;
        } catch (Throwable t) {
            Log.w(TAG, "playback capture unavailable: " + t);
            release();
            return false;
        }
    }

    private void loop() {
        byte[] buffer = new byte[CHUNK];
        while (running) {
            int read;
            try {
                read = record.read(buffer, 0, buffer.length);
            } catch (Throwable t) {
                break;
            }
            if (read > 0) {
                LinkManager.get().sendAudio(buffer, read);
            } else if (read < 0) {
                break;
            }
        }
    }

    public boolean isRunning() {
        return running;
    }

    public void stop() {
        running = false;
        Thread current = thread;
        thread = null;
        if (current != null) {
            try {
                current.join(300);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
            }
        }
        release();
    }

    private void release() {
        AudioRecord value = record;
        record = null;
        if (value == null) {
            return;
        }
        try {
            if (value.getRecordingState() == AudioRecord.RECORDSTATE_RECORDING) {
                value.stop();
            }
        } catch (Throwable ignored) {
            // already stopped
        }
        try {
            value.release();
        } catch (Throwable ignored) {
            // already released
        }
    }
}
