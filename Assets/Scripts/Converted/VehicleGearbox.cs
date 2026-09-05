using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Converted from the "Gearbox" visual scripting graph. Shifting is a timed sequence
/// (matches the graph's coroutine + WaitForSeconds pattern): on a valid shift request,
/// clutch/ignition cut immediately, the gear actually changes at the halfway point of
/// ShiftTime, and clutch/ignition come back at the end. Gear 0 is reverse — named it
/// "VehicleGearbox" up front since your other two conversions both hit a name clash
/// with an old script called the same thing as the graph.
/// </summary>
public class VehicleGearbox : MonoBehaviour
{
    [Header("Input")]
    [SerializeField] private InputActionReference shiftUpAction;
    [SerializeField] private InputActionReference shiftDownAction;

    [Header("Gears (no defaults were given for these — fill in your real numbers)")]
    [Tooltip("The graph kept this as index 0 of one combined list. Split out here into its own field per your request.")]
    [SerializeField] private float reverseGearRatio = 1f;
    [Tooltip("Forward gears only, in order (index 0 = 1st gear). Reverse is the separate field above.")]
    [SerializeField] private List<float> gearRatios = new List<float>();

    [Header("Shifting")]
    [Tooltip("Total time for one shift. Half spent with ignition/clutch cut before the gear changes, half after (matches the graph's two WaitForSeconds calls at ShiftTime * 0.5 each).")]
    [SerializeField] private float shiftTime = 0.3f;

    [Header("Debug")]
    [Tooltip("Show gearbox status top-left of the screen in Play Mode. Drawn below VehicleSuspension's debug overlay so the two don't overlap.")]
    [SerializeField] private bool showDebugOverlay = false;

    // Outputs for other scripts to read.
    public int Ignition { get; private set; }
    public int Clutch { get; private set; }
    public float CurrentGearRatio { get; private set; }
    public int DrivetrainDirection { get; private set; }

    // currentGear: 0 = reverse, 1 = 1st gear, 2 = 2nd, etc. — matches the original index convention.
    private int currentGear;
    private int gearCount; // reverse + all forward gears
    private bool canShift = true;

    private void Start()
    {
        currentGear = 1;
        gearCount = gearRatios.Count + 1; // +1 for reverse, which isn't in the list anymore
    }

    private void OnEnable()
    {
        if (shiftUpAction != null && shiftUpAction.action != null)
        {
            shiftUpAction.action.Enable();
            shiftUpAction.action.performed += OnShiftUpPerformed;
        }
        if (shiftDownAction != null && shiftDownAction.action != null)
        {
            shiftDownAction.action.Enable();
            shiftDownAction.action.performed += OnShiftDownPerformed;
        }
    }

    private void OnDisable()
    {
        if (shiftUpAction != null && shiftUpAction.action != null)
        {
            shiftUpAction.action.performed -= OnShiftUpPerformed;
            shiftUpAction.action.Disable();
        }
        if (shiftDownAction != null && shiftDownAction.action != null)
        {
            shiftDownAction.action.performed -= OnShiftDownPerformed;
            shiftDownAction.action.Disable();
        }
    }

    private void FixedUpdate()
    {
        DrivetrainDirection = currentGear == 0 ? -1 : 1;
        CurrentGearRatio = GetGearRatio(currentGear);
    }

    private void OnShiftUpPerformed(InputAction.CallbackContext ctx)
    {
        if (currentGear < gearCount - 1 && canShift)
        {
            StartCoroutine(ShiftRoutine(currentGear + 1));
        }
    }

    private void OnShiftDownPerformed(InputAction.CallbackContext ctx)
    {
        if (currentGear > 0 && canShift)
        {
            StartCoroutine(ShiftRoutine(currentGear - 1));
        }
    }

    private IEnumerator ShiftRoutine(int targetGear)
    {
        canShift = false;
        Clutch = 0;
        Ignition = 0;

        yield return new WaitForSeconds(shiftTime * 0.5f);

        currentGear = targetGear;

        yield return new WaitForSeconds(shiftTime * 0.5f);

        Clutch = 1;
        Ignition = 1;
        canShift = true;
    }

    private float GetGearRatio(int gear)
    {
        if (gear <= 0) return reverseGearRatio;
        int i = gear - 1;
        return i < gearRatios.Count ? gearRatios[i] : 0f;
    }

    private void OnGUI()
    {
        if (!showDebugOverlay || !Application.isPlaying) return;

        bool canShiftUp = currentGear < gearCount - 1 && canShift;
        bool canShiftDown = currentGear > 0 && canShift;

        // Starts at y=70 so it sits below VehicleSuspension's overlay (which uses 10/28/46).
        GUI.Label(new Rect(10, 70, 320, 20), $"Current Gear: {currentGear}");
        GUI.Label(new Rect(10, 88, 320, 20), $"Current Gear Ratio: {CurrentGearRatio:F3}");
        GUI.Label(new Rect(10, 106, 320, 20), $"Ignition: {Ignition}");
        GUI.Label(new Rect(10, 124, 320, 20), $"Drivetrain Direction: {DrivetrainDirection}");
        GUI.Label(new Rect(10, 142, 320, 20), $"Can Shift Up: {canShiftUp}   Can Shift Down: {canShiftDown}");
    }
}
