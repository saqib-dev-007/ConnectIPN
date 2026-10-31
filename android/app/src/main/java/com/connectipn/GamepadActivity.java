package com.connectipn;

import android.hardware.Sensor;
import android.hardware.SensorEvent;
import android.hardware.SensorEventListener;
import android.hardware.SensorManager;
import android.os.Bundle;
import android.view.MotionEvent;
import android.view.View;
import android.widget.TextView;
import android.widget.Toast;

import androidx.appcompat.app.AppCompatActivity;

/**
 * Turns the phone into an Xbox 360 pad. The PC side creates a real XInput device
 * through ViGEmBus, so games see a controller rather than keyboard input.
 *
 * The middle button switches the left stick between the on-screen stick and the
 * accelerometer: tilt the phone like a wheel to steer, or tilt it forward and back
 * to walk. Neutral is captured when the gyro is switched on, so any way of holding
 * the phone works.
 */
public class GamepadActivity extends AppCompatActivity
        implements LinkManager.Listener, SensorEventListener {

    /** Tilt that counts as a full deflection, about 31 degrees. */
    private static final float MAX_TILT_RAD = 0.55f;
    private static final float DEAD_ZONE = 0.06f;
    private static final int TRIGGER_ON = 255;

    private final LinkManager link = LinkManager.get();

    private JoystickView leftStick;
    private JoystickView rightStick;
    private TextView gyroButton;

    private SensorManager sensors;
    private Sensor accelerometer;
    private final float[] gravity = new float[3];
    private boolean haveGravity;
    private boolean calibrated;
    private float neutralSteer;
    private float neutralPitch;
    private int lastSentX;
    private int lastSentY;

    private boolean gyro;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_gamepad);
        SystemBars.apply(this, R.id.root);

        leftStick = findViewById(R.id.leftStick);
        rightStick = findViewById(R.id.rightStick);
        gyroButton = findViewById(R.id.gyroButton);
        sensors = (SensorManager) getSystemService(SENSOR_SERVICE);
        accelerometer = sensors == null ? null : sensors.getDefaultSensor(Sensor.TYPE_ACCELEROMETER);

        leftStick.setListener((x, y) -> {
            if (!gyro) {
                sendStick(Protocol.PAD_LEFT_X, x, y);
            }
        });
        rightStick.setListener((x, y) -> sendStick(Protocol.PAD_RIGHT_X, x, y));

        findViewById(R.id.closeButton).setOnClickListener(v -> finish());
        gyroButton.setOnClickListener(v -> setGyro(!gyro));

        wireButton(R.id.lbButton, Protocol.PAD_LB);
        wireButton(R.id.rbButton, Protocol.PAD_RB);
        wireButton(R.id.buttonA, Protocol.PAD_A);
        wireButton(R.id.buttonB, Protocol.PAD_B);
        wireButton(R.id.buttonX, Protocol.PAD_X);
        wireButton(R.id.buttonY, Protocol.PAD_Y);
        wireButton(R.id.dpadUp, Protocol.PAD_UP);
        wireButton(R.id.dpadDown, Protocol.PAD_DOWN);
        wireButton(R.id.dpadLeft, Protocol.PAD_LEFT);
        wireButton(R.id.dpadRight, Protocol.PAD_RIGHT);
        wireButton(R.id.backButton, Protocol.PAD_BACK);
        wireButton(R.id.startButton, Protocol.PAD_START);

        wireTrigger(R.id.ltButton, Protocol.PAD_LEFT_TRIGGER);
        wireTrigger(R.id.rtButton, Protocol.PAD_RIGHT_TRIGGER);

        link.padConnect(true);
        setGyro(false);
    }

    @Override
    protected void onResume() {
        super.onResume();
        link.setListener(this);
        if (gyro) {
            registerSensor();
        }
    }

    @Override
    protected void onPause() {
        link.clearListener(this);
        if (sensors != null) {
            sensors.unregisterListener(this);
        }
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        link.padConnect(false);
        SystemBars.restore(this);
        super.onDestroy();
    }

    @Override
    public void onLinkState(LinkManager.State state, String message) {
        if (state != LinkManager.State.CONNECTED) {
            Toast.makeText(this, R.string.gamepad_disconnected, Toast.LENGTH_SHORT).show();
            finish();
        }
    }

    private void wireButton(int id, int mask) {
        findViewById(id).setOnTouchListener((v, event) -> {
            switch (event.getActionMasked()) {
                case MotionEvent.ACTION_DOWN:
                    v.setPressed(true);
                    link.padButton(mask, true);
                    return true;
                case MotionEvent.ACTION_UP:
                case MotionEvent.ACTION_CANCEL:
                    v.setPressed(false);
                    link.padButton(mask, false);
                    return true;
                default:
                    return true;
            }
        });
    }

    private void wireTrigger(int id, int axis) {
        findViewById(id).setOnTouchListener((v, event) -> {
            switch (event.getActionMasked()) {
                case MotionEvent.ACTION_DOWN:
                    v.setPressed(true);
                    link.padAxis(axis, TRIGGER_ON);
                    return true;
                case MotionEvent.ACTION_UP:
                case MotionEvent.ACTION_CANCEL:
                    v.setPressed(false);
                    link.padAxis(axis, 0);
                    return true;
                default:
                    return true;
            }
        });
    }

    private void sendStick(int axisX, float x, float y) {
        link.padAxis(axisX, Math.round(x * 32767));
        link.padAxis(axisX + 1, Math.round(y * 32767));
    }

    private void setGyro(boolean on) {
        gyro = on;
        gyroButton.setText(on ? R.string.gamepad_gyro_on : R.string.gamepad_gyro_off);
        gyroButton.setBackgroundResource(on ? R.drawable.bg_key_active : R.drawable.bg_touch_button);

        // The stick stays visible while the gyro drives it: the knob follows the
        // phone, so the two never disagree about where the character is going.
        leftStick.setInteractive(!on);
        leftStick.setPosition(0f, 0f);
        sendStick(Protocol.PAD_LEFT_X, 0f, 0f);

        if (on) {
            calibrated = false;
            haveGravity = false;
            lastSentX = 0;
            lastSentY = 0;
            registerSensor();
        } else if (sensors != null) {
            sensors.unregisterListener(this);
        }
    }

    private void registerSensor() {
        if (sensors == null || accelerometer == null) {
            Toast.makeText(this, R.string.gamepad_no_gyro, Toast.LENGTH_SHORT).show();
            return;
        }
        // Re-capture neutral every time the gyro starts listening, so however the
        // phone is held when you come back to the app is the "straight ahead" pose.
        calibrated = false;
        haveGravity = false;
        sensors.registerListener(this, accelerometer, SensorManager.SENSOR_DELAY_GAME);
    }

    @Override
    public void onSensorChanged(SensorEvent event) {
        if (!gyro || event.values.length < 3) {
            return;
        }

        // Low pass so a shaky hand does not translate into twitchy steering.
        final float alpha = 0.8f;
        if (!haveGravity) {
            gravity[0] = event.values[0];
            gravity[1] = event.values[1];
            gravity[2] = event.values[2];
            haveGravity = true;
            return;
        }
        for (int i = 0; i < 3; i++) {
            gravity[i] = alpha * gravity[i] + (1f - alpha) * event.values[i];
        }

        float gx = gravity[0];
        float gy = gravity[1];
        float gz = gravity[2];

        // Tilt in the plane of the screen, and tilt forwards/backwards.
        float steer = (float) Math.atan2(gx, -gy);
        float pitch = (float) Math.atan2(gz, -gy);

        if (!calibrated) {
            neutralSteer = steer;
            neutralPitch = pitch;
            calibrated = true;
            return;
        }

        float fx = axis(steer - neutralSteer);
        // Tilting the top of the phone away should push the stick up.
        float fy = -axis(pitch - neutralPitch);

        leftStick.setPosition(fx, fy);

        int x = Math.round(fx * 32767);
        int y = Math.round(fy * 32767);

        if (Math.abs(x - lastSentX) < 400 && Math.abs(y - lastSentY) < 400) {
            return;
        }
        lastSentX = x;
        lastSentY = y;
        link.padAxis(Protocol.PAD_LEFT_X, x);
        link.padAxis(Protocol.PAD_LEFT_Y, y);
    }

    @Override
    public void onAccuracyChanged(Sensor sensor, int accuracy) {
        // nothing to do
    }

    /** Normalises a tilt delta to -1..1 with a small dead zone around neutral. */
    private static float axis(float delta) {
        float value = Math.max(-1f, Math.min(1f, delta / MAX_TILT_RAD));
        float magnitude = Math.abs(value);
        if (magnitude < DEAD_ZONE) {
            return 0f;
        }
        return Math.signum(value) * (magnitude - DEAD_ZONE) / (1f - DEAD_ZONE);
    }
}
