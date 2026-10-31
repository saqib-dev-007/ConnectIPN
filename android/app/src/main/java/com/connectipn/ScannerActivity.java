package com.connectipn;

import android.os.Bundle;
import android.view.ViewGroup;
import android.widget.ImageButton;
import android.widget.RelativeLayout;
import android.widget.TextView;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.appcompat.app.AppCompatActivity;

import com.journeyapps.barcodescanner.CaptureManager;
import com.journeyapps.barcodescanner.DecoratedBarcodeView;
import com.journeyapps.barcodescanner.ViewfinderView;

/**
 * My own capture activity: the stock zxing laser is replaced by the animated
 * ScanOverlay, the chrome is reduced to a back control, and a torch toggle is
 * offered where the code you need to scan is on a PC in a lit room.
 */
public class ScannerActivity extends AppCompatActivity {

    private CaptureManager manager;
    private DecoratedBarcodeView scanner;
    private ScanOverlay overlay;
    private boolean torchOn;

    @Override
    protected void onCreate(@Nullable Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_scanner);

        scanner = findViewById(R.id.scanner);
        manager = new CaptureManager(this, scanner);
        manager.initializeFromIntent(getIntent(), savedInstanceState);
        manager.decode();

        hideStockViewfinder();
        overlay = new ScanOverlay(this);
        overlay.setClickable(false);
        RelativeLayout host = findViewById(R.id.overlays);
        host.addView(overlay, new RelativeLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        TextView title = findViewById(R.id.scannerTitle);
        title.setText(R.string.scan_title);
        ImageButton back = findViewById(R.id.backButton);
        back.setOnClickListener(v -> finish());
        ImageButton torch = findViewById(R.id.torchButton);
        torch.setOnClickListener(v -> {
            torchOn = !torchOn;
            if (torchOn) {
                scanner.setTorchOn();
            } else {
                scanner.setTorchOff();
            }
            torch.setAlpha(torchOn ? 1f : 0.6f);
        });
    }

    @Override
    protected void onResume() {
        super.onResume();
        manager.onResume();
    }

    @Override
    protected void onPause() {
        manager.onPause();
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        manager.onDestroy();
        super.onDestroy();
    }

    @Override
    public void onSaveInstanceState(@NonNull Bundle outState) {
        super.onSaveInstanceState(outState);
        manager.onSaveInstanceState(outState);
    }

    @Override
    public boolean onSupportNavigateUp() {
        finish();
        return true;
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, @NonNull String[] permissions,
            @NonNull int[] grantResults) {
        manager.onRequestPermissionsResult(requestCode, permissions, grantResults);
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
    }

    /** The stock ViewfinderView's laser adds noise next to the ScanOverlay. */
    private void hideStockViewfinder() {
        hideViewfinder(scanner);
    }

    private void hideViewfinder(ViewGroup group) {
        for (int i = 0; i < group.getChildCount(); i++) {
            if (group.getChildAt(i) instanceof ViewfinderView) {
                ((ViewfinderView) group.getChildAt(i)).setAlpha(0f);
                return;
            } else if (group.getChildAt(i) instanceof ViewGroup) {
                hideViewfinder((ViewGroup) group.getChildAt(i));
            }
        }
    }
}
