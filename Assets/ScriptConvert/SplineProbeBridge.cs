using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

public class SplineProbeBridge : MonoBehaviour
{
    private SplineContainer splineContainer;

    [Header("Debug")]
    public bool showDebug = true;
    public float debugLineLength = 5f;

    void Awake()
    {
        GameObject splineObj = GameObject.FindGameObjectWithTag("TrackSpline");

        if (splineObj != null)
        {
            splineContainer = splineObj.GetComponent<SplineContainer>();
        }
    }

    public Vector3 GetClosestPoint(Vector3 worldPosition)
    {
        if (splineContainer == null)
            return worldPosition;

        SplineUtility.GetNearestPoint(
            splineContainer.Spline,
            worldPosition,
            out float3 nearestPoint,
            out float t
        );

        return (Vector3)nearestPoint;
    }

    public Vector3 GetTangent(Vector3 worldPosition)
    {
        if (splineContainer == null)
            return transform.forward;

        SplineUtility.GetNearestPoint(
            splineContainer.Spline,
            worldPosition,
            out float3 nearestPoint,
            out float t
        );

        float3 tangent = splineContainer.Spline.EvaluateTangent(t);

        return (Vector3)math.normalize(tangent);
    }

    void Update()
    {
        if (!showDebug || splineContainer == null)
            return;

        Vector3 pos = transform.position;

        Vector3 closest = GetClosestPoint(pos);
        Vector3 tangent = GetTangent(pos);

        // 🔴 Debug: closest point
        Debug.DrawLine(pos, closest, Color.yellow);

        // 🟢 Debug: forward direction (tangent)
        Debug.DrawRay(closest, tangent * debugLineLength, Color.green);

        // 🔵 Debug: object forward
        Debug.DrawRay(pos, transform.forward * 2f, Color.blue);
    }

    void OnGUI()
    {
        if (!showDebug)
            return;

        string status = splineContainer != null
            ? "Spline: FOUND"
            : "Spline: NOT FOUND";

        GUI.Label(new Rect(10, 10, 300, 20), status);

        GUI.Label(new Rect(10, 30, 400, 20),
            $"Position: {transform.position}");

        if (splineContainer != null)
        {
            Vector3 closest = GetClosestPoint(transform.position);
            GUI.Label(new Rect(10, 50, 500, 20),
                $"Closest Point: {closest}");
        }
    }
}