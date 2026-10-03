using UnityEngine;

[System.Serializable]
public class VRMap
{
    public Transform vrTarget;
    public Transform ikTarget;
    public Vector3 trackingPositionOffset;
    public Vector3 trackingRotationOffset;

    // Hand rotation of the model in its rest pose, relative to the body root. Not serialized.
    Quaternion restLocalRotation = Quaternion.identity;

    public bool IsValid => vrTarget != null && ikTarget != null;

    public void Map()
    {
        ikTarget.position = vrTarget.TransformPoint(trackingPositionOffset);
        ikTarget.rotation = vrTarget.rotation * Quaternion.Euler(trackingRotationOffset);
    }

    public void CaptureRest(Transform root)
    {
        restLocalRotation = Quaternion.Inverse(root.rotation) * ikTarget.rotation;
    }

    // Chooses the rotation offset so that, with the controller in its current orientation,
    // the hand bone is in the model's rest orientation.
    public void CalibrateRotation(Transform root)
    {
        Quaternion offset = Quaternion.Inverse(vrTarget.rotation) * (root.rotation * restLocalRotation);
        trackingRotationOffset = offset.eulerAngles;
    }
}

// Runs after OVRCameraRig has refreshed its anchors, and in Update (not LateUpdate) so the
// Animation Rigging jobs, which are evaluated between Update and LateUpdate, read this frame's targets.
[DefaultExecutionOrder(10000)]
public class IKTargetFollowVRRig : MonoBehaviour
{
    [Range(0, 1)]
    [Tooltip("Body yaw follow factor per frame at 60 fps. Converted to be frame-rate independent.")]
    public float turnSmoothness = 0.1f;

    [Tooltip("The avatar root that gets moved/rotated. If empty, the Animator found on this object or its parents is used.")]
    public Transform bodyRoot;

    public VRMap head;
    public VRMap leftHand;
    public VRMap rightHand;

    [Tooltip("Extra offset on top of the automatic one, in the head's yaw frame (metres). The automatic offset " +
             "places the body so the rig's HeadTarget (as saved in the scene) lands on your head. " +
             "Use negative Z to move the arms back from your face.")]
    public Vector3 headBodyPositionOffset;
    public float headBodyYawOffset;

    [Tooltip("Off: body follows head height (works for both Eye Level and Floor Level tracking origins). " +
             "On: body root keeps its starting height.")]
    public bool lockRootHeight = false;

    [Header("Shoulder placement")]
    [Tooltip("Places the body so the shoulders (EXP_L1_Arm1 / EXP_R1_Arm1 midpoint) sit at the distances below. " +
             "Off: the old behaviour, which lines up the rig's HeadTarget with your head and puts the shoulders wherever that lands.")]
    public bool anchorShouldersToHead = true;
    [Tooltip("Metres from the eyes down to the shoulder joints. About 0.2-0.3 for an adult. Raise it to drop the shoulders further.")]
    public float shouldersBelowEyes = 0.25f;
    [Tooltip("Metres from the eyes back to the shoulder joints. About 0.05-0.12. Raise it to move the shoulders back, away from your face.")]
    public float shouldersBehindEyes = 0.08f;

    [Header("Rendering")]
    [Tooltip("Near clip plane applied to the OVRCameraRig eye cameras. 0 = leave as is. " +
             "The scene had 0.3, which clips anything closer than 30 cm to your eyes.")]
    public float eyeNearClip = 0.05f;

    [Tooltip("Stops the skinned meshes being culled when IK pulls bones outside their stored bounds.")]
    public bool forceRenderWhenOffscreen = true;

    [Header("Hand rotation calibration")]
    [Tooltip("Hold X (left) and A (right) together while standing with arms straight out " +
             "to the sides, palms down (the model's rest pose). The resulting offsets are applied immediately and printed to the Console.")]
    public bool enableCalibration = true;

    [Header("Interaction pointer")]
    [Tooltip("Moves the origin of the Interaction SDK controller ray to the index fingertip of each hand. " +
             "The ray direction still comes from the controller.")]
    public bool pointerFromIndexFingertip = true;
    [Tooltip("Left index fingertip. Found automatically (EXP_L1_Index3_end) if empty.")]
    public Transform leftIndexTip;
    [Tooltip("Right index fingertip. Found automatically (EXP_R1_Index3_end) if empty.")]
    public Transform rightIndexTip;
    [Tooltip("Logs the pointer origin vs fingertip distance once per second. Turn off when done.")]
    public bool logPointerDebug = true;

    [Tooltip("Also move the poke and distance-grab origins to the fingertip. Off: only the ray interactor is changed.")]
    public bool alsoMoveOtherPointers = false;

    readonly System.Collections.Generic.List<Oculus.Interaction.ControllerPointerPose> pointerPoses =
        new System.Collections.Generic.List<Oculus.Interaction.ControllerPointerPose>();
    float nextPointerSearch;
    float nextPointerLog;

    Vector3 headRestOffset;
    float rootStartY;
    Vector3 lastFlatForward = Vector3.forward;
    bool valid;
    bool calibrationHeld;

    void Start()
    {
        if (bodyRoot == null)
        {
            Animator animator = GetComponentInParent<Animator>();
            bodyRoot = animator != null ? animator.transform : transform;
        }

        valid = true;
        valid &= Check(head, nameof(head));
        valid &= Check(leftHand, nameof(leftHand));
        valid &= Check(rightHand, nameof(rightHand));

        if (valid && leftHand.ikTarget == rightHand.ikTarget)
        {
            Debug.LogError("leftHand and rightHand use the same IK target.", this);
            valid = false;
        }

        if (!valid) return;

        // Everything below must happen before the first Map(), while the IK targets still hold their saved rest pose.
        headRestOffset = -bodyRoot.InverseTransformPoint(head.ikTarget.position);

        if (anchorShouldersToHead)
        {
            Transform leftShoulder = null, rightShoulder = null;
            foreach (Transform tr in bodyRoot.GetComponentsInChildren<Transform>(true))
            {
                if (tr.name == "EXP_L1_Arm1") leftShoulder = tr;
                else if (tr.name == "EXP_R1_Arm1") rightShoulder = tr;
            }
            if (leftShoulder != null && rightShoulder != null)
            {
                Vector3 shoulderLocal = bodyRoot.InverseTransformPoint((leftShoulder.position + rightShoulder.position) * 0.5f);
                // Root position = eyes + yawFrame * headRestOffset, so pick headRestOffset to put the shoulders at the wanted spot.
                headRestOffset = new Vector3(0f, -shouldersBelowEyes, -shouldersBehindEyes) - shoulderLocal;
                if (logPointerDebug)
                    Debug.Log($"[ShoulderDebug] shoulder midpoint in body space = {shoulderLocal:F3}, body offset from eyes = {headRestOffset:F3}", this);
            }
            else
            {
                Debug.LogWarning("IKTargetFollowVRRig: EXP_L1_Arm1 / EXP_R1_Arm1 not found under the body root, " +
                                 "using the HeadTarget-based placement instead.", this);
            }
        }
        leftHand.CaptureRest(bodyRoot);
        rightHand.CaptureRest(bodyRoot);

        rootStartY = bodyRoot.position.y;

        if (pointerFromIndexFingertip)
        {
            foreach (Transform tr in bodyRoot.GetComponentsInChildren<Transform>(true))
            {
                if (leftIndexTip == null && tr.name == "EXP_L1_Index3_end") leftIndexTip = tr;
                if (rightIndexTip == null && tr.name == "EXP_R1_Index3_end") rightIndexTip = tr;
            }
            if (leftIndexTip == null || rightIndexTip == null)
                Debug.LogWarning("IKTargetFollowVRRig: index fingertip not found for " +
                                 (leftIndexTip == null ? "left " : "") + (rightIndexTip == null ? "right " : "") +
                                 "hand. Assign it manually.", this);
        }
        lastFlatForward = bodyRoot.forward;

        if (forceRenderWhenOffscreen)
        {
            foreach (SkinnedMeshRenderer smr in bodyRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                smr.updateWhenOffscreen = true;
        }

        if (eyeNearClip > 0f)
        {
            OVRCameraRig rig = head.vrTarget.GetComponentInParent<OVRCameraRig>();
            if (rig != null)
            {
                if (rig.leftEyeCamera != null) rig.leftEyeCamera.nearClipPlane = eyeNearClip;
                if (rig.rightEyeCamera != null) rig.rightEyeCamera.nearClipPlane = eyeNearClip;
            }
            else
            {
                Debug.LogWarning("IKTargetFollowVRRig: no OVRCameraRig above the head VR target, near clip not changed.", this);
            }
        }
    }

    bool Check(VRMap map, string label)
    {
        if (map != null && map.IsValid) return true;
        Debug.LogError($"{nameof(IKTargetFollowVRRig)}: '{label}' needs both vrTarget and ikTarget assigned.", this);
        return false;
    }

    void Update()
    {
        if (!valid) return;

        // Yaw from the head's forward projected on the horizontal plane. eulerAngles.y becomes
        // unstable when looking straight up/down; if the projection collapses, keep the last heading.
        Vector3 flatForward = Vector3.ProjectOnPlane(head.vrTarget.forward, Vector3.up);
        if (flatForward.sqrMagnitude > 0.0001f)
            lastFlatForward = flatForward.normalized;

        float targetYaw = Quaternion.LookRotation(lastFlatForward, Vector3.up).eulerAngles.y + headBodyYawOffset;
        float t = 1f - Mathf.Pow(1f - turnSmoothness, Time.deltaTime * 60f);
        bodyRoot.rotation = Quaternion.Slerp(bodyRoot.rotation, Quaternion.Euler(0f, targetYaw, 0f), t);

        // Position uses vrTarget directly. The old code read head.ikTarget, which was only updated
        // later in the same frame, so the body was always one frame behind the head.
        Quaternion yawFrame = Quaternion.Euler(0f, bodyRoot.eulerAngles.y, 0f);
        Vector3 pos = head.vrTarget.position + yawFrame * (headRestOffset + headBodyPositionOffset);
        if (lockRootHeight) pos.y = rootStartY;
        bodyRoot.position = pos;

        if (enableCalibration) HandleCalibrationInput();

        head.Map();
        leftHand.Map();
        rightHand.Map();
    }

    // LateUpdate: Animation Rigging has already posed the arms for this frame, so the fingertip is current.
    void LateUpdate()
    {
        if (valid && pointerFromIndexFingertip) UpdatePointerOffsets();
    }

    // ControllerPointerPose places the ray origin at (controller pointer pose + rotation * offset).
    // The hand follows the controller rigidly, so the fingertip is a constant offset in the controller's
    // frame. We solve for that offset and inject it. Doing it this way (instead of moving the pointer
    // transform ourselves) means the SDK already applies it when the interactor reads the pose,
    // so there is no dependence on script execution order. The offset lags by one frame.
    void UpdatePointerOffsets()
    {
        bool log = logPointerDebug && Time.unscaledTime >= nextPointerLog;
        if (log) nextPointerLog = Time.unscaledTime + 1f;

        // Refresh every second, including inactive objects: the SDK enables/disables interactor groups at
        // runtime, so a one-off search can miss the ray interactor.
        if (Time.unscaledTime >= nextPointerSearch)
        {
            nextPointerSearch = Time.unscaledTime + 1f;
            pointerPoses.Clear();
            if (alsoMoveOtherPointers)
            {
                pointerPoses.AddRange(FindObjectsByType<Oculus.Interaction.ControllerPointerPose>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None));
            }
            else
            {
                // The ray origin is the ControllerPointerPose under each RayInteractor
                // (ControllerRayInteractor/ControllerPointerPose in the SDK prefab).
                foreach (Oculus.Interaction.RayInteractor ray in FindObjectsByType<Oculus.Interaction.RayInteractor>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    var pose = ray.GetComponentInChildren<Oculus.Interaction.ControllerPointerPose>(true);
                    if (pose != null) pointerPoses.Add(pose);
                }
            }
        }
        if (pointerPoses.Count == 0)
        {
            if (log) Debug.Log("[PointerDebug] no RayInteractor with a ControllerPointerPose found, so nothing can be moved.", this);
            return;
        }

        foreach (Oculus.Interaction.ControllerPointerPose pp in pointerPoses)
        {
            if (pp == null) continue;
            if (pp.Controller == null)
            {
                if (log) Debug.Log($"[PointerDebug] '{PathOf(pp.transform)}' has no Controller yet.", pp);
                continue;
            }

            var hand = pp.Controller.Handedness;
            Transform tip = hand == Oculus.Interaction.Input.Handedness.Left ? leftIndexTip : rightIndexTip;
            if (tip == null)
            {
                if (log) Debug.Log($"[PointerDebug] {hand}: fingertip transform not assigned.", pp);
                continue;
            }
            if (!pp.Controller.TryGetPointerPose(out Pose basePose))
            {
                if (log) Debug.Log($"[PointerDebug] {hand}: controller has no pointer pose right now (not tracked?).", pp);
                continue;
            }

            float scale = Mathf.Max(pp.Controller.Scale, 0.0001f);
            Vector3 offset = Quaternion.Inverse(basePose.rotation) * (tip.position - basePose.position) / scale;
            pp.InjectOffset(offset);

            // dist = how far the object that drives the ray is from the fingertip. It should be near 0
            // (about one frame of hand movement). rawDist = the same distance with no offset applied.
            if (log)
                Debug.Log($"[PointerDebug] {hand}: pointer object '{PathOf(pp.transform)}' | tip '{PathOf(tip)}' | " +
                          $"dist to tip = {Vector3.Distance(pp.transform.position, tip.position):F3} m | " +
                          $"controller pointer pose to tip = {Vector3.Distance(basePose.position, tip.position):F3} m | " +
                          $"injected offset = {offset:F3} | controller scale = {scale:F2}", pp);
        }
    }

    static string PathOf(Transform t)
    {
        string p = t.name;
        for (int i = 0; i < 4 && t.parent != null; i++) { t = t.parent; p = t.name + "/" + p; }
        return p;
    }
    void HandleCalibrationInput()
    {
        bool both = OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.LTouch)
                 && OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.RTouch);

        if (both && !calibrationHeld)
        {
            leftHand.CalibrateRotation(bodyRoot);
            rightHand.CalibrateRotation(bodyRoot);
            Debug.Log($"IKTargetFollowVRRig calibrated. leftHand.trackingRotationOffset = {leftHand.trackingRotationOffset}, " +
                      $"rightHand.trackingRotationOffset = {rightHand.trackingRotationOffset}. " +
                      "Copy these into the Inspector after leaving Play mode to keep them.", this);
        }
        calibrationHeld = both;
    }
}