using UnityEngine;
using Unity.VisualScripting;

/// <summary>
/// Reads your car's Visual Scripting variables each frame and drives
/// GranularEngineAudio from them. Assumes:
///   - Engine_Redline, EngineThrottlePosition are Object variables (on carObject's Variables component)
///   - Engine_RPM_1, Engine_Idle_RPM, CDI_Ignition are Graph variables (belong to a specific running graph)
///
/// Object variables are read reliably via the standard Variables.Object() API.
/// Graph variables require a reference to the actual running graph instance
/// (the ScriptMachine component), and that API has shifted slightly between
/// Visual Scripting versions — if graphMachine reading doesn't compile or
/// doesn't return values in your version, the simplest fix is to change
/// those three variables from Graph scope to Object scope in the Variables
/// window inside your graph (one dropdown, no logic changes needed) — after
/// that they'll be picked up by the same Object-variable path as the others.
/// </summary>
[RequireComponent(typeof(GranularEngineAudio))]
public class CarEngineDataBridge : MonoBehaviour
{
    [Header("Car object")]
    [Tooltip("The GameObject that holds your car's Visual Scripting graph and its Object variables.")]
    public GameObject carObject;

    [Tooltip("Only needed if Engine_RPM_1 / Engine_Idle_RPM / CDI_Ignition are Graph-scoped. Assign the ScriptMachine that owns them. Leave empty if you've promoted them to Object scope.")]
    public ScriptMachine graphMachine;

    [Header("Variable names (must match your graph exactly)")]
    public string rpmVariableName = "Engine_RPM_1";
    public string idleRpmVariableName = "Engine_Idle_RPM";
    public string redlineVariableName = "Engine_Redline";
    public string throttleVariableName = "EngineThrottlePosition";
    public string cdiIgnitionVariableName = "CDI_Ignition";

    private GranularEngineAudio engine;

    // debug: raw values + whether each variable was actually found or fell back to default
    public float DebugRpm { get; private set; }
    public float DebugIdleRpm { get; private set; }
    public float DebugRedline { get; private set; }
    public float DebugThrottle { get; private set; }
    public float DebugCdi { get; private set; }
    public bool RpmVarFound { get; private set; }
    public bool IdleRpmVarFound { get; private set; }
    public bool RedlineVarFound { get; private set; }
    public bool ThrottleVarFound { get; private set; }
    public bool CdiVarFound { get; private set; }

    void Start()
    {
        engine = GetComponent<GranularEngineAudio>();
        engine.useThrottleBasedMix = true; // this bridge is built around continuous throttle mixing
    }

    void Update()
    {
        if (carObject == null || engine == null) return;

        float rpm = GetFloat(rpmVariableName, 0f, out bool rpmFound);
        float idleRpm = GetFloat(idleRpmVariableName, 0f, out bool idleFound);
        float redline = GetFloat(redlineVariableName, 1f, out bool redlineFound);
        float throttle = GetFloat(throttleVariableName, 0f, out bool throttleFound);
        float cdi = GetFloat(cdiIgnitionVariableName, 1f, out bool cdiFound);

        DebugRpm = rpm; RpmVarFound = rpmFound;
        DebugIdleRpm = idleRpm; IdleRpmVarFound = idleFound;
        DebugRedline = redline; RedlineVarFound = redlineFound;
        DebugThrottle = throttle; ThrottleVarFound = throttleFound;
        DebugCdi = cdi; CdiVarFound = cdiFound;

        float rpmNorm = redline > 0.0001f ? (rpm + idleRpm) / redline : 0f;

        engine.rpmNormalized = Mathf.Clamp01(rpmNorm);
        engine.throttlePosition = Mathf.Clamp01(throttle);
        engine.cdiIgnitionMultiplier = Mathf.Clamp01(cdi);
    }

    float GetFloat(string name, float fallback, out bool found)
    {
        // Object-scoped variable — the reliable, version-stable path.
        var objVars = Variables.Object(carObject);
        if (objVars.IsDefined(name))
        {
            found = true;
            return objVars.Get<float>(name);
        }

        // Graph-scoped variable — needs a live reference to the running graph.
        // Verify this against your installed Visual Scripting version if it
        // doesn't fetch values; see the class comment above for the simpler
        // alternative (promote the variable to Object scope instead).
        if (graphMachine != null)
        {
            var reference = graphMachine.GetReference().AsReference();
            var graphVars = Variables.Graph(reference);
            if (graphVars.IsDefined(name))
            {
                found = true;
                return graphVars.Get<float>(name);
            }
        }

        found = false;
        return fallback;
    }
}
