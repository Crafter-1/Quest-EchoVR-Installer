using UnityEngine;

/// <summary>
/// Simple script to make VR arms follow controllers smoothly
/// Attach this to your OVRCameraRig or create an empty GameObject for it
/// </summary>
public class SmoothVRArms : MonoBehaviour
{
    [Header("Arm Model References")]
    [Tooltip("The left arm model GameObject")]
    public GameObject leftArmModel;
    
    [Tooltip("The right arm model GameObject")]
    public GameObject rightArmModel;
    
    [Header("Shoulder Setup")]
    [Tooltip("Parent for the left shoulder (will be created if null)")]
    public Transform leftShoulderTransform;
    
    [Tooltip("Parent for the right shoulder (will be created if null)")]
    public Transform rightShoulderTransform;
    
    [Tooltip("Offset of shoulders from head")]
    public Vector3 leftShoulderOffset = new Vector3(-0.15f, -0.2f, 0.05f);
    public Vector3 rightShoulderOffset = new Vector3(0.15f, -0.2f, 0.05f);
    
    [Header("Smoothing Settings")]
    [Tooltip("How smooth the arm movement is (lower = smoother, higher = more responsive)")]
    [Range(1f, 30f)]
    public float handSmoothSpeed = 12f;
    
    [Tooltip("How smooth the shoulder movement is")]
    [Range(1f, 30f)]
    public float shoulderSmoothSpeed = 8f;
    
    [Tooltip("Only use head Y rotation (yaw) for shoulders - usually more natural")]
    public bool shouldersFollowYawOnly = true;
    
    // Private references
    private Transform headTransform;
    private Transform leftControllerAnchor;
    private Transform rightControllerAnchor;
    
    // Smooth position tracking
    private Vector3 leftHandTargetPos;
    private Vector3 rightHandTargetPos;
    private Quaternion leftHandTargetRot;
    private Quaternion rightHandTargetRot;

    void Start()
    {
        // Find the OVRCameraRig
        OVRCameraRig cameraRig = FindObjectOfType<OVRCameraRig>();
        
        if (cameraRig == null)
        {
            Debug.LogError("SmoothVRArms: Could not find OVRCameraRig!");
            enabled = false;
            return;
        }
        
        // Get references
        headTransform = cameraRig.centerEyeAnchor;
        leftControllerAnchor = cameraRig.leftHandAnchor;
        rightControllerAnchor = cameraRig.rightHandAnchor;
        
        // Setup shoulders
        SetupShoulders();
        
        // Reparent arm models to shoulders
        ReparentArmsToShoulders();
        
        // Initialize smooth positions
        if (leftArmModel != null)
        {
            leftHandTargetPos = leftArmModel.transform.position;
            leftHandTargetRot = leftArmModel.transform.rotation;
        }
        
        if (rightArmModel != null)
        {
            rightHandTargetPos = rightArmModel.transform.position;
            rightHandTargetRot = rightArmModel.transform.rotation;
        }
    }
    
    void SetupShoulders()
    {
        // Create shoulder transforms if they don't exist
        if (leftShoulderTransform == null)
        {
            GameObject leftShoulder = new GameObject("LeftShoulder");
            leftShoulderTransform = leftShoulder.transform;
            leftShoulderTransform.SetParent(headTransform);
        }
        
        if (rightShoulderTransform == null)
        {
            GameObject rightShoulder = new GameObject("RightShoulder");
            rightShoulderTransform = rightShoulder.transform;
            rightShoulderTransform.SetParent(headTransform);
        }
        
        // Set initial positions
        leftShoulderTransform.localPosition = leftShoulderOffset;
        rightShoulderTransform.localPosition = rightShoulderOffset;
        leftShoulderTransform.localRotation = Quaternion.identity;
        rightShoulderTransform.localRotation = Quaternion.identity;
    }
    
    void ReparentArmsToShoulders()
    {
        // Move arm models from controller anchors to shoulder transforms
        if (leftArmModel != null)
        {
            // Store original local values if parented to controller
            Vector3 origLocalPos = leftArmModel.transform.localPosition;
            Quaternion origLocalRot = leftArmModel.transform.localRotation;
            
            // Reparent to shoulder
            leftArmModel.transform.SetParent(leftShoulderTransform);
            
            // You might want to reset or keep the local position
            // For now, let's keep it at the shoulder origin
            leftArmModel.transform.localPosition = Vector3.zero;
            leftArmModel.transform.localRotation = Quaternion.identity;
        }
        
        if (rightArmModel != null)
        {
            Vector3 origLocalPos = rightArmModel.transform.localPosition;
            Quaternion origLocalRot = rightArmModel.transform.localRotation;
            
            rightArmModel.transform.SetParent(rightShoulderTransform);
            rightArmModel.transform.localPosition = Vector3.zero;
            rightArmModel.transform.localRotation = Quaternion.identity;
        }
    }

    void LateUpdate()
    {
        // Update shoulder positions to follow head smoothly
        UpdateShoulders();
        
        // Update arm positions to follow controllers smoothly
        UpdateArmPositions();
    }
    
    void UpdateShoulders()
    {
        if (headTransform == null) return;
        
        // Get head rotation
        Quaternion headRot = headTransform.rotation;
        
        // If only using yaw (Y rotation), more natural for shoulders
        if (shouldersFollowYawOnly)
        {
            Vector3 euler = headRot.eulerAngles;
            headRot = Quaternion.Euler(0, euler.y, 0);
        }
        
        // Smoothly rotate shoulders
        if (leftShoulderTransform != null)
        {
            leftShoulderTransform.rotation = Quaternion.Slerp(
                leftShoulderTransform.rotation,
                headRot,
                Time.deltaTime * shoulderSmoothSpeed
            );
        }
        
        if (rightShoulderTransform != null)
        {
            rightShoulderTransform.rotation = Quaternion.Slerp(
                rightShoulderTransform.rotation,
                headRot,
                Time.deltaTime * shoulderSmoothSpeed
            );
        }
    }
    
    void UpdateArmPositions()
    {
        // Update left arm
        if (leftArmModel != null && leftControllerAnchor != null)
        {
            // Get controller position in world space
            Vector3 controllerWorldPos = leftControllerAnchor.position;
            Quaternion controllerWorldRot = leftControllerAnchor.rotation;
            
            // Smooth towards controller position
            leftHandTargetPos = Vector3.Lerp(
                leftHandTargetPos,
                controllerWorldPos,
                Time.deltaTime * handSmoothSpeed
            );
            
            leftHandTargetRot = Quaternion.Slerp(
                leftHandTargetRot,
                controllerWorldRot,
                Time.deltaTime * handSmoothSpeed
            );
            
            // Make the arm point towards the smoothed controller position
            UpdateArmOrientation(leftArmModel.transform, leftShoulderTransform, leftHandTargetPos, leftHandTargetRot);
        }
        
        // Update right arm
        if (rightArmModel != null && rightControllerAnchor != null)
        {
            Vector3 controllerWorldPos = rightControllerAnchor.position;
            Quaternion controllerWorldRot = rightControllerAnchor.rotation;
            
            rightHandTargetPos = Vector3.Lerp(
                rightHandTargetPos,
                controllerWorldPos,
                Time.deltaTime * handSmoothSpeed
            );
            
            rightHandTargetRot = Quaternion.Slerp(
                rightHandTargetRot,
                controllerWorldRot,
                Time.deltaTime * handSmoothSpeed
            );
            
            UpdateArmOrientation(rightArmModel.transform, rightShoulderTransform, rightHandTargetPos, rightHandTargetRot);
        }
    }
    
    void UpdateArmOrientation(Transform armTransform, Transform shoulderTransform, Vector3 handWorldPos, Quaternion handWorldRot)
    {
        if (armTransform == null || shoulderTransform == null) return;
        
        // Calculate direction from shoulder to hand
        Vector3 shoulderPos = shoulderTransform.position;
        Vector3 toHand = handWorldPos - shoulderPos;
        
        if (toHand.magnitude > 0.001f)
        {
            // Make the arm point towards the hand
            // Note: You may need to adjust the rotation depending on how your arm model is oriented
            Quaternion lookRot = Quaternion.LookRotation(toHand);
            
            // Apply rotation smoothly
            armTransform.rotation = Quaternion.Slerp(
                armTransform.rotation,
                lookRot * handWorldRot,
                Time.deltaTime * handSmoothSpeed
            );
        }
    }
    
    // Gizmos for debugging
    void OnDrawGizmos()
    {
        if (!Application.isPlaying) return;
        
        // Draw shoulders
        if (leftShoulderTransform != null)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(leftShoulderTransform.position, 0.03f);
        }
        
        if (rightShoulderTransform != null)
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawWireSphere(rightShoulderTransform.position, 0.03f);
        }
        
        // Draw lines from shoulders to target hand positions
        if (leftShoulderTransform != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawLine(leftShoulderTransform.position, leftHandTargetPos);
        }
        
        if (rightShoulderTransform != null)
        {
            Gizmos.color = Color.blue;
            Gizmos.DrawLine(rightShoulderTransform.position, rightHandTargetPos);
        }
    }
}
