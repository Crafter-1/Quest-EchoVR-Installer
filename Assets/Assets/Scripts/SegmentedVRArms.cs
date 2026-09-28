using UnityEngine;

/// <summary>
/// VR Arms with 3 segments: Upper Arm, Lower Arm, and Hand
/// This creates natural elbow bending using simple 2-bone IK
/// </summary>
public class SegmentedVRArms : MonoBehaviour
{
    [System.Serializable]
    public class ArmSegments
    {
        [Header("Arm Pieces")]
        public GameObject upperArm;  // Shoulder to elbow
        public GameObject lowerArm;  // Elbow to wrist
        public GameObject hand;      // Hand model
        
        [Header("Bone Lengths (auto-calculated if 0)")]
        public float upperArmLength = 0.3f;  // Shoulder to elbow
        public float lowerArmLength = 0.25f; // Elbow to wrist
        
        [Header("Elbow Settings")]
        public Vector3 elbowHintOffset = new Vector3(0, 0, -0.1f); // Which direction elbow should bend
        
        // Internal transforms
        [HideInInspector] public Transform shoulder;
        [HideInInspector] public Transform elbow;
        [HideInInspector] public Transform wrist;
    }
    
    [Header("Arm Configuration")]
    public ArmSegments leftArm;
    public ArmSegments rightArm;
    
    [Header("Shoulder Settings")]
    public Vector3 leftShoulderOffset = new Vector3(-0.15f, -0.2f, 0.05f);
    public Vector3 rightShoulderOffset = new Vector3(0.15f, -0.2f, 0.05f);
    public bool shouldersFollowHeadYawOnly = true;
    
    [Header("Smoothing")]
    [Range(1f, 30f)]
    public float handSmoothSpeed = 15f;
    [Range(1f, 30f)]
    public float shoulderSmoothSpeed = 8f;
    [Range(1f, 30f)]
    public float elbowSmoothSpeed = 12f;
    
    // Private references
    private Transform headTransform;
    private Transform leftControllerTransform;
    private Transform rightControllerTransform;
    
    // Smooth tracking
    private Vector3 leftHandTargetPos;
    private Vector3 rightHandTargetPos;
    private Quaternion leftHandTargetRot;
    private Quaternion rightHandTargetRot;

    void Start()
    {
        // Find OVR Camera Rig
        OVRCameraRig rig = FindObjectOfType<OVRCameraRig>();
        if (rig == null)
        {
            Debug.LogError("SegmentedVRArms: No OVRCameraRig found!");
            enabled = false;
            return;
        }
        
        headTransform = rig.centerEyeAnchor;
        leftControllerTransform = rig.leftHandAnchor;
        rightControllerTransform = rig.rightHandAnchor;
        
        // Setup arm hierarchies
        SetupArm(leftArm, "Left", leftShoulderOffset);
        SetupArm(rightArm, "Right", rightShoulderOffset);
        
        // Initialize smooth positions
        if (leftControllerTransform != null)
        {
            leftHandTargetPos = leftControllerTransform.position;
            leftHandTargetRot = leftControllerTransform.rotation;
        }
        
        if (rightControllerTransform != null)
        {
            rightHandTargetPos = rightControllerTransform.position;
            rightHandTargetRot = rightControllerTransform.rotation;
        }
    }
    
    void SetupArm(ArmSegments arm, string side, Vector3 shoulderOffset)
    {
        if (arm.upperArm == null || arm.lowerArm == null || arm.hand == null)
        {
            Debug.LogWarning($"SegmentedVRArms: {side} arm segments not assigned!");
            return;
        }
        
        // Create shoulder joint
        GameObject shoulderObj = new GameObject($"{side}Shoulder");
        arm.shoulder = shoulderObj.transform;
        arm.shoulder.SetParent(headTransform);
        arm.shoulder.localPosition = shoulderOffset;
        arm.shoulder.localRotation = Quaternion.identity;
        
        // Create elbow joint
        GameObject elbowObj = new GameObject($"{side}Elbow");
        arm.elbow = elbowObj.transform;
        arm.elbow.SetParent(arm.shoulder);
        arm.elbow.localPosition = Vector3.forward * arm.upperArmLength;
        
        // Create wrist joint
        GameObject wristObj = new GameObject($"{side}Wrist");
        arm.wrist = wristObj.transform;
        arm.wrist.SetParent(arm.elbow);
        arm.wrist.localPosition = Vector3.forward * arm.lowerArmLength;
        
        // Parent the visual models to the joints
        arm.upperArm.transform.SetParent(arm.shoulder);
        arm.upperArm.transform.localPosition = Vector3.zero;
        arm.upperArm.transform.localRotation = Quaternion.identity;
        arm.upperArm.transform.localScale = Vector3.one;
        
        arm.lowerArm.transform.SetParent(arm.elbow);
        arm.lowerArm.transform.localPosition = Vector3.zero;
        arm.lowerArm.transform.localRotation = Quaternion.identity;
        arm.lowerArm.transform.localScale = Vector3.one;
        
        arm.hand.transform.SetParent(arm.wrist);
        arm.hand.transform.localPosition = Vector3.zero;
        arm.hand.transform.localRotation = Quaternion.identity;
        arm.hand.transform.localScale = Vector3.one;
        
        Debug.Log($"SegmentedVRArms: {side} arm setup complete");
    }

    void LateUpdate()
    {
        UpdateShoulders();
        UpdateArm(leftArm, leftControllerTransform, ref leftHandTargetPos, ref leftHandTargetRot, true);
        UpdateArm(rightArm, rightControllerTransform, ref rightHandTargetPos, ref rightHandTargetRot, false);
    }
    
    void UpdateShoulders()
    {
        if (headTransform == null) return;
        
        Quaternion targetRot = headTransform.rotation;
        
        if (shouldersFollowHeadYawOnly)
        {
            Vector3 euler = targetRot.eulerAngles;
            targetRot = Quaternion.Euler(0, euler.y, 0);
        }
        
        // Smooth shoulder rotation
        if (leftArm.shoulder != null)
        {
            leftArm.shoulder.rotation = Quaternion.Slerp(
                leftArm.shoulder.rotation,
                targetRot,
                Time.deltaTime * shoulderSmoothSpeed
            );
        }
        
        if (rightArm.shoulder != null)
        {
            rightArm.shoulder.rotation = Quaternion.Slerp(
                rightArm.shoulder.rotation,
                targetRot,
                Time.deltaTime * shoulderSmoothSpeed
            );
        }
    }
    
    void UpdateArm(ArmSegments arm, Transform controller, ref Vector3 targetPos, ref Quaternion targetRot, bool isLeft)
    {
        if (arm.shoulder == null || arm.elbow == null || arm.wrist == null || controller == null)
            return;
        
        // Smooth the hand target position
        targetPos = Vector3.Lerp(targetPos, controller.position, Time.deltaTime * handSmoothSpeed);
        targetRot = Quaternion.Slerp(targetRot, controller.rotation, Time.deltaTime * handSmoothSpeed);
        
        // Simple 2-bone IK
        SolveIK(arm, targetPos, targetRot, isLeft);
    }
    
    void SolveIK(ArmSegments arm, Vector3 targetPos, Quaternion targetRot, bool isLeft)
    {
        Vector3 shoulderPos = arm.shoulder.position;
        Vector3 targetDir = targetPos - shoulderPos;
        float targetDistance = targetDir.magnitude;
        
        // Total arm length
        float totalLength = arm.upperArmLength + arm.lowerArmLength;
        
        // Clamp target to reachable distance (with slight compression)
        float maxReach = totalLength * 0.95f; // 95% to avoid full extension
        if (targetDistance > maxReach)
        {
            targetPos = shoulderPos + targetDir.normalized * maxReach;
            targetDistance = maxReach;
        }
        
        // If too close, push out a bit
        float minReach = totalLength * 0.3f;
        if (targetDistance < minReach)
        {
            targetPos = shoulderPos + targetDir.normalized * minReach;
            targetDistance = minReach;
        }
        
        // Calculate elbow position using law of cosines
        float upperSqr = arm.upperArmLength * arm.upperArmLength;
        float lowerSqr = arm.lowerArmLength * arm.lowerArmLength;
        float targetSqr = targetDistance * targetDistance;
        
        // Angle at shoulder
        float cosAngle = (upperSqr + targetSqr - lowerSqr) / (2 * arm.upperArmLength * targetDistance);
        cosAngle = Mathf.Clamp(cosAngle, -1f, 1f);
        float shoulderAngle = Mathf.Acos(cosAngle);
        
        // Elbow hint direction (which way the elbow should point)
        Vector3 elbowHint = arm.shoulder.TransformDirection(arm.elbowHintOffset);
        
        // Calculate elbow position
        Vector3 shoulderToTarget = (targetPos - shoulderPos).normalized;
        Vector3 perpendicular = Vector3.Cross(shoulderToTarget, elbowHint).normalized;
        Vector3 elbowPlaneNormal = Vector3.Cross(perpendicular, shoulderToTarget).normalized;
        
        Vector3 elbowDir = Quaternion.AngleAxis(shoulderAngle * Mathf.Rad2Deg, perpendicular) * shoulderToTarget;
        Vector3 elbowTargetPos = shoulderPos + elbowDir * arm.upperArmLength;
        
        // Smooth elbow movement
        arm.elbow.position = Vector3.Lerp(
            arm.elbow.position,
            elbowTargetPos,
            Time.deltaTime * elbowSmoothSpeed
        );
        
        // Point upper arm at elbow
        Vector3 shoulderToElbow = arm.elbow.position - shoulderPos;
        if (shoulderToElbow.magnitude > 0.01f)
        {
            arm.shoulder.rotation = Quaternion.Slerp(
                arm.shoulder.rotation,
                Quaternion.LookRotation(shoulderToElbow, arm.shoulder.up),
                Time.deltaTime * elbowSmoothSpeed
            );
        }
        
        // Point lower arm at wrist/hand
        Vector3 elbowToTarget = targetPos - arm.elbow.position;
        if (elbowToTarget.magnitude > 0.01f)
        {
            arm.elbow.rotation = Quaternion.Slerp(
                arm.elbow.rotation,
                Quaternion.LookRotation(elbowToTarget, arm.elbow.up),
                Time.deltaTime * elbowSmoothSpeed
            );
        }
        
        // Set wrist position and rotation
        arm.wrist.position = Vector3.Lerp(arm.wrist.position, targetPos, Time.deltaTime * handSmoothSpeed);
        arm.wrist.rotation = Quaternion.Slerp(arm.wrist.rotation, targetRot, Time.deltaTime * handSmoothSpeed);
    }
    
    void OnDrawGizmos()
    {
        if (!Application.isPlaying) return;
        
        DrawArmGizmos(leftArm, Color.green);
        DrawArmGizmos(rightArm, Color.blue);
    }
    
    void DrawArmGizmos(ArmSegments arm, Color color)
    {
        if (arm.shoulder == null || arm.elbow == null || arm.wrist == null) return;
        
        Gizmos.color = color;
        
        // Draw joints
        Gizmos.DrawWireSphere(arm.shoulder.position, 0.03f);
        Gizmos.DrawWireSphere(arm.elbow.position, 0.025f);
        Gizmos.DrawWireSphere(arm.wrist.position, 0.02f);
        
        // Draw bones
        Gizmos.DrawLine(arm.shoulder.position, arm.elbow.position);
        Gizmos.DrawLine(arm.elbow.position, arm.wrist.position);
    }
}
