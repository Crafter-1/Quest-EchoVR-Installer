package com.crafter.evrinstaller.bridge;

import android.app.Activity;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;
import android.provider.Settings;
import android.util.Log;

/**
 * Opens Echo VR's standard Android app-details page from a non-VR Activity.
 * Horizon OS can hide or redirect this screen when it is started directly
 * from Unity's immersive GameActivity.
 */
public final class EchoPermissionBridgeActivity extends Activity {
    private static final String TAG = "EchoPermissionBridge";
    private static final String ECHO_PACKAGE = "com.readyatdawn.r15";

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        try {
            Intent intent = new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS);
            intent.setData(Uri.parse("package:" + ECHO_PACKAGE));
            startActivity(intent);
        } catch (Exception exception) {
            Log.e(TAG, "Could not open Echo VR app settings", exception);
        } finally {
            finish();
        }
    }
}
