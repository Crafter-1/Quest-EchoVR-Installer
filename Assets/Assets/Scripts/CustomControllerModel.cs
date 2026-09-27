using UnityEngine;

public class CustomControllerModel : MonoBehaviour
{
    [Header("Controller Model Settings")]
    [Tooltip("The custom model to use for controllers")]
    public GameObject customModelPrefab;
    
    [Header("Controller Anchors")]
    public Transform leftHandAnchor;
    public Transform rightHandAnchor;
    
    [Header("Model Transform")]
    public Vector3 modelPositionOffset = Vector3.zero;
    public Vector3 modelRotationOffset = Vector3.zero;
    public Vector3 modelScale = Vector3.one;
    
    private GameObject leftControllerModel;
    private GameObject rightControllerModel;

    void Start()
    {
        LoadControllerModels();
    }

    void LoadControllerModels()
    {
        // Load from Resources if not assigned
        if (customModelPrefab == null)
        {
            // Load the menu arms model from Resources folder
            customModelPrefab = Resources.Load<GameObject>("menu arms");
            
            if (customModelPrefab == null)
            {
                Debug.LogError("Could not load 'menu arms' from Resources folder!");
                return;
            }
        }

        // Auto-find hand anchors if not assigned
        if (leftHandAnchor == null || rightHandAnchor == null)
        {
            FindHandAnchors();
        }

        // Instantiate left controller model
        if (leftHandAnchor != null)
        {
            leftControllerModel = Instantiate(customModelPrefab, leftHandAnchor);
            SetupModelTransform(leftControllerModel.transform, false);
        }

        // Instantiate right controller model
        if (rightHandAnchor != null)
        {
            rightControllerModel = Instantiate(customModelPrefab, rightHandAnchor);
            SetupModelTransform(rightControllerModel.transform, true);
        }
    }

    void FindHandAnchors()
    {
        // Try to find OVRCameraRig in scene
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig != null)
        {
            // Find the controller anchors
            Transform trackingSpace = cameraRig.trackingSpace;
            
            if (trackingSpace != null)
            {
                // Look for LeftControllerAnchor - this is where controller models go
                Transform leftController = trackingSpace.Find("LeftHandAnchor/LeftControllerAnchor");
                if (leftController == null)
                {
                    // Alternative path if using different hierarchy
                    leftController = trackingSpace.Find("LeftHandAnchor/LeftControllerInHandAnchor");
                }
                if (leftController != null)
                {
                    leftHandAnchor = leftController;
                    Debug.Log("Found LeftControllerAnchor at: " + leftController.name);
                }
                else
                {
                    Debug.LogWarning("Could not find LeftControllerAnchor!");
                }
                
                // Look for RightControllerAnchor - this is where controller models go
                Transform rightController = trackingSpace.Find("RightHandAnchor/RightControllerAnchor");
                if (rightController == null)
                {
                    // Alternative path if using different hierarchy
                    rightController = trackingSpace.Find("RightHandAnchor/RightControllerInHandAnchor");
                }
                if (rightController != null)
                {
                    rightHandAnchor = rightController;
                    Debug.Log("Found RightControllerAnchor at: " + rightController.name);
                }
                else
                {
                    Debug.LogWarning("Could not find RightControllerAnchor!");
                }
            }
        }
        else
        {
            Debug.LogWarning("OVRCameraRig not found in scene!");
        }
    }

    void SetupModelTransform(Transform modelTransform, bool isRightHand)
    {
        // Apply position offset
        modelTransform.localPosition = modelPositionOffset;
        
        // Apply rotation offset (mirror for right hand if needed)
        Vector3 rotation = modelRotationOffset;
        if (isRightHand)
        {
            // You may need to flip certain axes for the right hand
            // rotation.y *= -1; // Uncomment if needed
        }
        modelTransform.localEulerAngles = rotation;
        
        // Apply scale
        modelTransform.localScale = modelScale;
    }
}
