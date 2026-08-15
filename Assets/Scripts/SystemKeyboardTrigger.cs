using UnityEngine;
using TMPro;

/// Opens Quest's System Keyboard Overlay when the input field gains focus.
/// Requires "Require System Keyboard" enabled on OVRManager (Camera Rig).
[RequireComponent(typeof(TMP_InputField))]
public class SystemKeyboardTrigger : MonoBehaviour
{
    private TMP_InputField _field;
    private TouchScreenKeyboard _keyboard;

    private void Awake()
    {
        _field = GetComponent<TMP_InputField>();
        _field.onSelect.AddListener(OnFieldSelected);
    }

    private void OnFieldSelected(string currentText)
    {
        _keyboard = TouchScreenKeyboard.Open(currentText, TouchScreenKeyboardType.Default);
    }

    private void Update()
    {
        if (_keyboard == null) return;

        // Keep the input field in sync with what's typed on the overlay keyboard
        if (_field.text != _keyboard.text)
            _field.text = _keyboard.text;

        if (_keyboard.status == TouchScreenKeyboard.Status.Done ||
            _keyboard.status == TouchScreenKeyboard.Status.Canceled ||
            _keyboard.status == TouchScreenKeyboard.Status.LostFocus)
        {
            _keyboard = null;
        }
    }
}
