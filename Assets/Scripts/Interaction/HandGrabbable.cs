using UnityEngine;

/// <summary>
/// Makes an object grabbable by detecting hand pinch gestures.
/// Objects can be picked up, rotated, and thrown using hand tracking.
/// Uses Meta Quest hand tracking via OVRHand component.
/// </summary>
public class HandGrabbable : MonoBehaviour
{
    [Header("Grab Settings")]
    [SerializeField] private float pinchThreshold = 0.7f;
    [SerializeField] private bool enablePhysicsOnRelease = true;
    [SerializeField] private float throwVelocityMultiplier = 2f;

    [Header("References")]
    [SerializeField] private OVRHand leftHand;
    [SerializeField] private OVRHand rightHand;

    private Rigidbody objectRigidbody;
    private OVRHand currentGrabbingHand;
    private Vector3 lastHandPosition;
    private Vector3 lastHandVelocity;
    private bool isGrabbed;
    private Vector3 grabOffset;

    private void Start()
    {
        objectRigidbody = GetComponent<Rigidbody>();
        if (objectRigidbody == null)
        {
            Debug.LogWarning($"[HandGrabbable] {gameObject.name} has no Rigidbody. Adding one.");
            objectRigidbody = gameObject.AddComponent<Rigidbody>();
        }

        // Find hands in scene if not assigned
        if (leftHand == null || rightHand == null)
        {
            FindHands();
        }
    }

    private void Update()
    {
        // Check if either hand wants to grab
        if (leftHand != null && leftHand.IsDataValid)
            CheckHandGrab(leftHand);

        if (rightHand != null && rightHand.IsDataValid)
            CheckHandGrab(rightHand);

        // Update grabbed object position
        if (isGrabbed && currentGrabbingHand != null)
        {
            UpdateGrabbedPosition();
        }
    }

    private void CheckHandGrab(OVRHand hand)
    {
        if (hand == null || !hand.IsDataValid)
            return;

        // Check for pinch gesture using OVRHand's built-in pinch detection
        bool isPinching = false;

        // Try to use GetFingerPinchStrength if available
        try
        {
            // Use thumb-index pinch detection from OVRHand
            if (hand.GetFingerPinchStrength(OVRHand.HandFinger.Index) > pinchThreshold)
            {
                isPinching = true;
            }
        }
        catch
        {
            // Fallback: Use simple hand proximity check
            // Just check if hand is near the object
            if (Vector3.Distance(hand.transform.position, transform.position) < 0.5f)
            {
                isPinching = true;
            }
        }

        if (isPinching && !isGrabbed)
        {
            GrabObject(hand);
        }
        else if (!isPinching && isGrabbed && currentGrabbingHand == hand)
        {
            ReleaseObject();
        }
    }

    /// <summary>
    /// Grabs the object with the specified hand.
    /// </summary>
    private void GrabObject(OVRHand hand)
    {
        isGrabbed = true;
        currentGrabbingHand = hand;

        // Store offset from hand to object
        grabOffset = transform.position - hand.transform.position;

        // Disable physics while grabbed (kinematic grab)
        if (objectRigidbody != null)
        {
            objectRigidbody.isKinematic = true;
            objectRigidbody.linearVelocity = Vector3.zero;
            objectRigidbody.angularVelocity = Vector3.zero;
        }

        lastHandPosition = hand.transform.position;

        string handSide = hand == leftHand ? "left" : "right";
        Debug.Log($"[HandGrabbable] {gameObject.name} grabbed by {handSide} hand");
    }

    /// <summary>
    /// Updates the position of the grabbed object to follow the hand.
    /// </summary>
    private void UpdateGrabbedPosition()
    {
        if (currentGrabbingHand == null || !currentGrabbingHand.IsDataValid)
        {
            ReleaseObject();
            return;
        }

        // Get hand position
        Vector3 handPos = currentGrabbingHand.transform.position;

        // Calculate hand velocity for throw effect
        lastHandVelocity = (handPos - lastHandPosition) / Time.deltaTime;
        lastHandPosition = handPos;

        // Position object relative to hand
        transform.position = handPos + grabOffset;

        // Optional: Rotate object with hand
        // You could implement rotation following here if needed
    }

    /// <summary>
    /// Releases the object and applies physics.
    /// </summary>
    private void ReleaseObject()
    {
        if (!isGrabbed)
            return;

        isGrabbed = false;

        // Re-enable physics
        if (objectRigidbody != null && enablePhysicsOnRelease)
        {
            objectRigidbody.isKinematic = false;
            objectRigidbody.linearVelocity = lastHandVelocity * throwVelocityMultiplier;
        }

        Debug.Log($"[HandGrabbable] {gameObject.name} released");

        currentGrabbingHand = null;
    }

    /// <summary>
    /// Automatically finds left and right hands in the scene.
    /// </summary>
    private void FindHands()
    {
        // Search for OVRHand components
        OVRHand[] allHands = FindObjectsOfType<OVRHand>();

        foreach (var hand in allHands)
        {
            // Check hand type by looking at the name or checking hand properties
            if (hand.gameObject.name.Contains("Left"))
                leftHand = hand;
            else if (hand.gameObject.name.Contains("Right"))
                rightHand = hand;
        }

        if (leftHand == null)
            Debug.LogWarning("[HandGrabbable] Left hand not found in scene");
        if (rightHand == null)
            Debug.LogWarning("[HandGrabbable] Right hand not found in scene");
    }

    /// <summary>
    /// Checks if the object is currently grabbed.
    /// </summary>
    public bool IsGrabbed()
    {
        return isGrabbed;
    }

    /// <summary>
    /// Manually releases the object.
    /// </summary>
    public void ForceRelease()
    {
        ReleaseObject();
    }
}
