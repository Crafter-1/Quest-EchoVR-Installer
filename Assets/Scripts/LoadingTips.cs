using UnityEngine;
using TMPro;
using System.Collections;

[System.Serializable]
public class TipsData
{
    public string[] Tips;
    public string[] Facts;
    public string[] Jokes;
}

[RequireComponent(typeof(TMP_Text))]
public class LoadingTips : MonoBehaviour
{
    private TMP_Text _tipUi;
    private string _originalText;
    public TextAsset TipsFile;

    public float SwitchTime = 5f;

    public string[] Tips = new string[0];
    public string[] Fact = new string[0];
    public string[] Joke = new string[0];

    public void Start()
    {
        if (gameObject.TryGetComponent<TMP_Text>(out TMP_Text tipUI))
        {
            _tipUi = tipUI;
        }
        else
        {
            Debug.LogError("[LoadingTips] Missing Tip UI Text Object!");
            return;
        }

        _originalText = _tipUi.text;

        if (TipsFile == null)
            TipsFile = Resources.Load<TextAsset>("loadingTips");

        if (TipsFile == null)
        {
            Debug.LogError("[LoadingTips] No TipsFile assigned and none found at Resources/loadingTips.json - using empty tip lists.");
        }
        else
        {
            try
            {
                var data = JsonUtility.FromJson<TipsData>(TipsFile.text);

                // JsonUtility silently leaves fields null if JSON keys don't match
                // the class's field names exactly (case-sensitive) - guard against that
                // instead of trusting the parse blindly.
                Tips = data?.Tips ?? new string[0];
                Fact = data?.Facts ?? new string[0];
                Joke = data?.Jokes ?? new string[0];

                if (Tips.Length == 0 && Fact.Length == 0 && Joke.Length == 0)
                {
                    Debug.LogWarning("[LoadingTips] TipsFile parsed but all three arrays are empty. " +
                                      "Check that JSON keys are exactly 'Tips', 'Facts', 'Jokes' (case-sensitive).");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[LoadingTips] Failed to parse TipsFile JSON: {e.Message}");
            }
        }

        StartCoroutine(ChangeTip());
    }

    private IEnumerator ChangeTip()
    {
        while (true)
        {
            // Only consider categories that actually have content, so we never
            // roll a category with an empty array and crash/show a blank tip.
            var available = new System.Collections.Generic.List<int>();
            if (Tips.Length > 0) available.Add(0);
            if (Fact.Length > 0) available.Add(1);
            if (Joke.Length > 0) available.Add(2);

            if (available.Count == 0)
            {
                Debug.LogWarning("[LoadingTips] No tips/facts/jokes available to display.");
                yield return new WaitForSeconds(SwitchTime);
                continue;
            }

            var randomCategory = available[Random.Range(0, available.Count)];

            var categoryTitle = "";
            var selectedText = "";

            switch (randomCategory)
            {
                case 0:
                    categoryTitle = "TIP";
                    selectedText = Tips[Random.Range(0, Tips.Length)];
                    break;

                case 1:
                    categoryTitle = "FUN FACT";
                    selectedText = Fact[Random.Range(0, Fact.Length)];
                    break;

                case 2:
                    categoryTitle = "JOKE";
                    selectedText = Joke[Random.Range(0, Joke.Length)];
                    break;
            }

            _tipUi.text = _originalText
                .Replace("${tipTitle}", categoryTitle)
                .Replace("${tip}", selectedText);

            yield return new WaitForSeconds(SwitchTime);
        }
    }
}
