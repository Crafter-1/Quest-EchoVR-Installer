using UnityEngine;
using TMPro;

public class HeadsetTypeTest : MonoBehaviour

{
    private TextMeshPro _text;

    void Start()
    {
        // Create a floating text object in front of the player
        GameObject go = new GameObject("HeadsetTypeDebugText");
        _text = go.AddComponent<TextMeshPro>();

        _text.fontSize = 6;
        _text.color = Color.white;
        _text.alignment = TextAlignmentOptions.Center;

        RectTransform rt = go.GetComponent<RectTransform>();
        rt.position = new Vector3(0f, 1.5f, 1.5f); // 1.5m in front, roughly head height
        rt.localScale = new Vector3(0.02f, 0.02f, 0.02f);
        rt.sizeDelta = new Vector2(100, 20);
    }

    void Update()
    {
        if (_text == null) return;

        OVRPlugin.SystemHeadset headset = OVRPlugin.GetSystemHeadsetType();

        _text.text =
            $"Headset: {headset}\n" +
            $"Product: {OVRPlugin.productName}\n" +
            $"Backend: {(UnityEngine.XR.XRSettings.loadedDeviceName)}";
    }
}
