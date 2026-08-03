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

    public string[] Tips;
    public string[] Fact;
    public string[] Joke;

    public void Start()
    {
        if(gameObject.TryGetComponent<TMP_Text>(out TMP_Text tipUI))
        {
            _tipUi = tipUI;
        }
        else
        {
            Debug.LogError("Missing Tip UI Text Object!");
            return;
        }
        
        _originalText = _tipUi.text;

        try
        {
            if (TipsFile == null) TipsFile = Resources.Load<TextAsset>("loadingTips");
            var data = JsonUtility.FromJson<TipsData>(TipsFile.text);

            Tips = data.Tips;
            Fact = data.Facts;
            Joke = data.Jokes;
        } catch {
            Debug.Log("Failed to get find file, using Unity data.");
        }

        StartCoroutine(ChangeTip());
    }

    private IEnumerator ChangeTip()
    {
        while (true)
        {
            var randomCategory = Random.Range(0, 3);

            var categoryTitle = "";
            var selectedText = "";

            switch (randomCategory)
            {
                case 0:
                    categoryTitle = "TIP";
                    if(Tips.Length > 0) selectedText = Tips[Random.Range(0, Tips.Length)];
                    break;

                case 1:
                    categoryTitle = "FUN FACT";
                    if(Fact.Length > 0) selectedText = Fact[Random.Range(0, Fact.Length)];
                    break;

                case 2:
                    categoryTitle = "JOKE";
                    if(Joke.Length > 0) selectedText = Joke[Random.Range(0, Joke.Length)];
                    break;
            }

            _tipUi.text = _originalText
                .Replace("${tipTitle}", categoryTitle)
                .Replace("${tip}", selectedText);

            // Debug.Log($"Waitig for ${switchTime} seconds.");
            yield return new WaitForSeconds(SwitchTime);
        }
    }
}
