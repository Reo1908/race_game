using UnityEngine;

namespace RevAudio
{
    /// <summary>
    /// Moves the listener around the car so the directional mix is audible. 0 degrees is
    /// straight in front of the nose (engine side), 180 is behind it (exhaust side).
    /// </summary>
    [AddComponentMenu("Audio/REV Listener Orbit")]
    public class RevListenerOrbit : MonoBehaviour
    {
        public Transform target;
        [Range(0.5f, 60f)] public float distance = 5f;
        public float height = 1.3f;
        [Range(-180f, 180f)] public float angleDegrees = 0f;
        public bool autoOrbit = false;
        [Range(5f, 180f)] public float orbitSpeed = 35f;
        public bool lookAtTarget = true;
        public bool showPanel = true;

        void LateUpdate()
        {
            if (target == null) return;

            if (autoOrbit)
            {
                angleDegrees += orbitSpeed * Time.deltaTime;
                while (angleDegrees > 180f) angleDegrees -= 360f;
            }

            Vector3 forward = target.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            forward.Normalize();

            Vector3 offset = Quaternion.AngleAxis(angleDegrees, Vector3.up) * forward * distance;
            transform.position = target.position + offset + Vector3.up * height;
            if (lookAtTarget) transform.rotation = Quaternion.LookRotation(target.position + Vector3.up * 0.5f - transform.position);
        }

        void OnGUI()
        {
            if (!showPanel) return;

            GUILayout.BeginArea(new Rect(Screen.width - 260f, 10f, 250f, 190f), GUI.skin.box);
            GUILayout.Label("Listener position");

            GUILayout.Label(string.Format("Angle {0:0} deg   ({1})", angleDegrees, DescribeAngle(angleDegrees)));
            angleDegrees = GUILayout.HorizontalSlider(angleDegrees, -180f, 180f);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Front")) angleDegrees = 0f;
            if (GUILayout.Button("Side")) angleDegrees = 90f;
            if (GUILayout.Button("Rear")) angleDegrees = 180f;
            GUILayout.EndHorizontal();

            GUILayout.Label(string.Format("Distance {0:0.0} m", distance));
            distance = GUILayout.HorizontalSlider(distance, 1f, 40f);

            autoOrbit = GUILayout.Toggle(autoOrbit, "Orbit automatically");
            GUILayout.EndArea();
        }

        static string DescribeAngle(float angle)
        {
            float a = Mathf.Abs(angle);
            if (a < 45f) return "engine side";
            if (a > 135f) return "exhaust side";
            return "beside the car";
        }
    }
}
