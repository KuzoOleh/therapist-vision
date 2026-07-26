using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// One-off editor tool: clones the existing Date field / FetchData button to build
// a save-folder picker, since duplicating TMP_InputField's nested view/placeholder
// hierarchy by hand in the scene file is error-prone. Run once from the menu below.
public static class TherapistVisionUISetup
{
    [MenuItem("Tools/Therapist Vision/Add Save Folder Picker")]
    public static void AddSaveFolderPicker()
    {
        GameObject dateField = GameObject.Find("Date");
        GameObject uiControllerGO = GameObject.Find("UIController");
        GameObject fetchButton = GameObject.Find("FetchData");

        if (dateField == null || uiControllerGO == null || fetchButton == null)
        {
            Debug.LogError("[TherapistVisionUISetup] Could not find Date, UIController, or FetchData in the open scene. Open SampleScene first.");
            return;
        }

        var controller = uiControllerGO.GetComponent<TherapistUIController>();
        if (controller == null)
        {
            Debug.LogError("[TherapistVisionUISetup] UIController has no TherapistUIController component.");
            return;
        }

        var so = new SerializedObject(controller);
        var fieldProp = so.FindProperty("saveFolderPathField");
        var buttonProp = so.FindProperty("browseFolderButton");

        if (fieldProp.objectReferenceValue != null || buttonProp.objectReferenceValue != null)
        {
            Debug.Log("[TherapistVisionUISetup] Save folder field/button already wired up — nothing to do.");
            return;
        }

        GameObject newField = Object.Instantiate(dateField, dateField.transform.parent);
        newField.name = "SaveFolderPath";
        var fieldRect = newField.GetComponent<RectTransform>();
        fieldRect.anchoredPosition = new Vector2(40, 165.8f);
        fieldRect.sizeDelta = new Vector2(220, 55.4858f);

        var newInputField = newField.GetComponent<TMP_InputField>();
        var placeholderText = newInputField.placeholder as TMP_Text;
        if (placeholderText != null)
            placeholderText.text = "Save folder path...";

        GameObject newButton = Object.Instantiate(fetchButton, fetchButton.transform.parent);
        newButton.name = "BrowseFolder";
        var buttonRect = newButton.GetComponent<RectTransform>();
        buttonRect.anchoredPosition = new Vector2(205, 165.8f);
        buttonRect.sizeDelta = new Vector2(90, 55.4858f);

        var label = newButton.GetComponentInChildren<TMP_Text>();
        if (label != null)
        {
            label.text = "Browse...";
            label.fontSize = 18;
        }

        fieldProp.objectReferenceValue = newInputField;
        buttonProp.objectReferenceValue = newButton.GetComponent<Button>();
        so.ApplyModifiedProperties();

        EditorUtility.SetDirty(newField);
        EditorUtility.SetDirty(newButton);
        EditorSceneManager.MarkSceneDirty(uiControllerGO.scene);

        Selection.activeGameObject = newField;
        Debug.Log("[TherapistVisionUISetup] Added SaveFolderPath field and BrowseFolder button, wired into TherapistUIController. Save the scene (Ctrl+S) to keep it.");
    }
}
