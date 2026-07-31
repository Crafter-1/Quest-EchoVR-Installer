using UnityEngine;
using TMPro;

[RequireComponent(typeof(TMP_Text))]
public class DebugInfo : MonoBehaviour
{
    // TextMeshPro component
    private TMP_Text _tmpInput;

    // Device
    private string _hw;

    // Device version
    private string _hwV;

    // Installer version
    public string V;

    public void Start()
    {
        _tmpInput = GetComponent<TMP_Text>();

        _hw = OVRPlugin.GetSystemHeadsetType().ToString().Replace("_", " ");
        _hwV = new AndroidJavaClass("android.os.SystemProperties").CallStatic<string>("get",
            "ro.build.version.incremental");

        _tmpInput.text = _tmpInput.text.Replace("${hw}", _hw);
        _tmpInput.text = _tmpInput.text.Replace("${hwV}", _hwV);
        _tmpInput.text = _tmpInput.text.Replace("${v}", V);
    }
}