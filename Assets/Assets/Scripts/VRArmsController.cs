using UnityEngine;

public class VRArmsController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The camera/head transform")]
    public Transform headTransform;
    
    [Tooltip("Left and Right controller transforms")]
    public Transform leftControllerTransform;
    public Transform rightControllerTransform;
    
    [Header("Arm Models")]
    [Tooltip("The left arm model (should have shoulder, elbow, hand bones)")]
    public Transform leftArmRoot;
    
    [Tooltip("The right arm model (should have shoulder, elbow, hand bones)")]
    public Transform rightArmRoot;
    
    [Header("Shoulder Settings")]
    [Tooltip("Offset of shoulders from head position")]
    public Vector3 leftShoulderOffset = new Vector3(-0.2f, -0.15f, 0.1f);
    public Vector3 rightShoulderOffset = new Vector3(0.2f, -0.15f, 0.1f);
    
    [Tooltip("Should shoulders rotate with head?")]
    public bool shouldersFollowHeadRotation = true;
    
    [Tooltip("Only rotate shoulders on Y axis (yaw)")]
    public bool onlyFollowYRotation = true;
    
    [Header("Smoothing")]
    [Tooltip("How smoothly the arm follows the controller (lower = smoother but more lag)")]
    [Range(1f, 30f)]
    public float armFollowSpeed = 15f;
    
    [Tooltip("How smoothly the shoulder follows the head")]
    [Range(1f, 30f)]
    public float shoulderFollowSpeed = 10f;
    
    [Header("Advanced")]
    [Tooltip("Enable simple IK for more natural arm bending")]
    public bool useSimpleIK = false;
    
    [Tooltip("Arm length (auto-calculated if 0)")]
    public float armLength = 0f;
    
    // Private variables for smooth movement
    private Vector3 leftShoulderTargetPos;
    private Vector3 rightShoulderTargetPos;
    private Quaternion leftShoulderTargetRot;
    private Quaternion rightShoulderTargetRot;
    
    private Vector3 leftHandSmoothPos;
    private Vector3 rightHandSmoothPos;
    private Quaternion leftHandSmoothRot;
    private Quaternion rightHandSmoothRot;

    void Start()
    {
        // Auto-find head transform if not assigned
        if (headTransform == null)
        {
            // Try to find the center eye anchor
            var cameraRig = FindObjectOfType<OVRCameraRig>();
            if (cameraRig != null)
            {
                headTransform = cameraRig.centerEyeAnchor;
            }
            else
            {
                // Fallback to main camera
                headTransform = Camera.main.transform;
            }
        }
        
        // Auto-find controller transforms if not assigned
        if (leftControllerTransform == null || rightControllerTransform == null)
        {
            var cameraRig = FindObjectOfType<OVRCameraRig>();
            if (cameraRig != null)
            {
                if (leftControllerTransform == null)
                {
                    leftControllerTransform = cameraRig.leftHandAnchor;
                }
                if (rightControllerTransform == null)
                {
                    rightControllerTransform = cameraRig.rightHandAnchor;
                }
            }
        }
        
        // Initialize smooth positions
        if (leftControllerTransform != null)
        {
            leftHandSmoothPos = leftControllerTransform.position;
            leftHandSmoothRot = leftControllerTransform.rotation;
        }
        
        if (rightControllerTransform != null)
        {
            rightHandSmoothPos = rightControllerTransform.position;
            rightHandSmoothRot = rightControllerTransform.rotation;
        }
    }

    void LateUpdate()
    {
        if (headTransform == null) return;
        
        // Update shoulders to follow head
        UpdateShoulders();
        
        // Update arms to follow controllers smoothly
        UpdateArms();
    }
    
    void UpdateShoulders()
    {
        // Calculate shoulder target positions based on head
        Vector3 headPos = headTransform.position;
        Quaternion headRot = headTransform.rotation;
        
        // If we only want Y rotation (most natural for shoulders)
        if (onlyFollowYRotation)
        {
            Vector3 euler = headRot.eulerAngles;
            headRot = Quaternion.Euler(0, euler.y, 0);
        }
        
        // Calculate world positions for shoulders
        leftShoulderTargetPos = headPos + headRot * leftShoulderOffset;
        rightShoulderTargetPos = headPos + headRot * rightShoulderOffset;
        
        // Calculate shoulder rotations
        if (shouldersFollowHeadRotation)
        {
            leftShoulderTargetRot = headRot;
            rightShoulderTargetRot = headRot;
        }
        else
        {
            leftShoulderTargetRot = Quaternion.identity;
            rightShoulderTargetRot = Quaternion.identity;
        }
        
        // Smoothly move shoulders to target positions
        if (leftArmRoot != null)
        {
            leftArmRoot.position = Vector3.Lerp(
                leftArmRoot.position, 
                leftShoulderTargetPos, 
                Time.deltaTime * shoulderFollowSpeed
            );
            
            leftArmRoot.rotation = Quaternion.Slerp(
                leftArmRoot.rotation,
                leftShoulderTargetRot,
                Time.deltaTime * shoulderFollowSpeed
            );
        }
        
        if (rightArmRoot != null)
        {
            rightArmRoot.position = Vector3.Lerp(
                rightArmRoot.position,
                rightShoulderTargetPos,
                Time.deltaTime * shoulderFollowSpeed
            );
            
            rightArmRoot.rotation = Quaternion.Slerp(
                rightArmRoot.rotation,
                rightShoulderTargetRot,
                Time.deltaTime * shoulderFollowSpeed
            );
        }
    }
    
    void UpdateArms()
    {
        // Left arm
        if (leftControllerTransform != null && leftArmRoot != null)
        {
            // Smooth the hand position
            leftHandSmoothPos = Vector3.Lerp(
                leftHandSmoothPos,
                leftControllerTransform.position,
                Time.deltaTime * armFollowSpeed
            );
            
            leftHandSmoothRot = Quaternion.Slerp(
                leftHandSmoothRot,
                leftControllerTransform.rotation,
                Time.deltaTime * armFollowSpeed
            );
            
            // Update arm to point towards hand
            UpdateArmTransform(leftArmRoot, leftHandSmoothPos, leftHandSmoothRot, true);
        }
        
        // Right arm
        if (rightControllerTransform != null && rightArmRoot != null)
        {
            // Smooth the hand position
            rightHandSmoothPos = Vector3.Lerp(
                rightHandSmoothPos,
                rightControllerTransform.position,
                Time.deltaTime * armFollowSpeed
            );
            
            rightHandSmoothRot = Quaternion.Slerp(
                rightHandSmoothRot,
                rightControllerTransform.rotation,
                Time.deltaTime * armFollowSpeed
            );
            
            // Update arm to point towards hand
            UpdateArmTransform(rightArmRoot, rightHandSmoothPos, rightHandSmoothRot, false);
        }
    }
    
    void UpdateArmTransform(Transform armRoot, Vector3 handPos, Quaternion handRot, bool isLeftArm)
    {
        if (armRoot == null) return;
        
        // Simple approach: make the arm point towards the hand
        // The arm mesh should be oriented along its local Z or Y axis
        
        Vector3 direction = handPos - armRoot.position;
        if (direction.magnitude > 0.01f)
        {
            // Make the arm point towards the hand
            Quaternion targetRotation = Quaternion.LookRotation(direction);
            
            // You might need to adjust this offset depending on how your arm model is oriented
            // Common adjustments: Quaternion.Euler(90, 0, 0) or Quaternion.Euler(0, 90, 0)
            armRoot.rotation = Quaternion.Slerp(
                armRoot.rotation,
                targetRotation,
                Time.deltaTime * armFollowSpeed
            );
        }
        
        // If using simple IK, we could add elbow bending here
        // For now, this basic version just stretches the arm towards the hand
    }
    
    // Debug visualization
    void OnDrawGizmos()
    {
        if (!Application.isPlaying || headTransform == null) return;
        
        // Draw shoulder positions
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(leftShoulderTargetPos, 0.05f);
        Gizmos.DrawWireSphere(rightShoulderTargetPos, 0.05f);
        
        // Draw lines from shoulders to hands
        if (leftControllerTransform != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawLine(leftShoulderTargetPos, leftHandSmoothPos);
        }
        
        if (rightControllerTransform != null)
        {
            Gizmos.color = Color.blue;
            Gizmos.DrawLine(rightShoulderTargetPos, rightHandSmoothPos);
        }
    }
}
