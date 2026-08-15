using UnityEngine;
using TMPro;
using UnityEngine.UI;

/// Pastes clipboard content into the input field with one tap -
/// avoids typing a long server message on the VR keyboard.
public class PasteButtonHandler : MonoBehaviour
{
    public TMP_InputField targetField;
    public Button pasteButton;

    private void Start()
    {
        if (pasteButton != null)
            pasteButton.onClick.AddListener(OnPasteClicked);
    }

    public void OnPasteClicked()
    {
        string clipboardText = GUIUtility.systemCopyBuffer;

        if (string.IsNullOrEmpty(clipboardText))
        {
            Debug.LogWarning("[PasteButtonHandler] Clipboard is empty.");
            return;
        }

        targetField.text = clipboardText;
    }
}
