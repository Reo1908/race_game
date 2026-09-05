using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RevAudio.EditorTools
{
    /// <summary>
    /// Builds a ready-to-run test scene for the granular engine: a car body you can walk
    /// around, a listener on an orbit rig, and a driver rig with a simulated crankshaft.
    /// Assigns the four clips automatically by name where it can.
    /// </summary>
    public static class RevTestSceneBuilder
    {
        const string Folder = "Assets/TestEngineSounds/Try3";
        const string ScenePath = Folder + "/REV_TestScene.unity";
        const string MaterialFolder = Folder + "/TestSceneMaterials";

        [MenuItem("Tools/REV/Create test scene", false, 10)]
        public static void CreateTestScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            Material bodyMaterial = GetMaterial("Body", new Color(0.24f, 0.25f, 0.28f));
            Material groundMaterial = GetMaterial("Ground", new Color(0.42f, 0.43f, 0.45f));
            Material engineMaterial = GetMaterial("EngineSide", new Color(0.25f, 0.55f, 0.95f));
            Material exhaustMaterial = GetMaterial("ExhaustSide", new Color(0.95f, 0.45f, 0.15f));

            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(6f, 1f, 6f);
            SetMaterial(ground, groundMaterial);

            GameObject car = new GameObject("Car (REV granular engine)");
            car.transform.position = Vector3.zero;

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Body";
            body.transform.SetParent(car.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.6f, 0f);
            body.transform.localScale = new Vector3(1.8f, 0.9f, 4.2f);
            SetMaterial(body, bodyMaterial);

            GameObject nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
            nose.name = "Nose (engine side, +Z)";
            nose.transform.SetParent(car.transform, false);
            nose.transform.localPosition = new Vector3(0f, 0.75f, 2.1f);
            nose.transform.localScale = new Vector3(1.4f, 0.5f, 0.5f);
            SetMaterial(nose, engineMaterial);

            GameObject pipe = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pipe.name = "Tailpipe (exhaust side, -Z)";
            pipe.transform.SetParent(car.transform, false);
            pipe.transform.localPosition = new Vector3(0.45f, 0.35f, -2.3f);
            pipe.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            pipe.transform.localScale = new Vector3(0.22f, 0.35f, 0.22f);
            SetMaterial(pipe, exhaustMaterial);

            AudioSource source = car.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
            source.spatialBlend = 1f;
            source.dopplerLevel = 0f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 3f;
            source.maxDistance = 60f;
            // A point source in 3D collapses the stereo image, which would hide the grain
            // spread entirely. A little spread keeps some width without smearing the position.
            source.spread = 25f;

            RevGranularEngine engine = car.AddComponent<RevGranularEngine>();
            int cylinders = AssignClips(engine);
            engine.cylinders = cylinders;
            engine.rpmRange = new Vector2(900f, 7500f);
            engine.engineDown.rpmAtRangeStart = engine.exhaustDown.rpmAtRangeStart = engine.rpmRange.y;
            engine.engineDown.rpmAtRangeEnd = engine.exhaustDown.rpmAtRangeEnd = engine.rpmRange.x;
            engine.engineUp.rpmAtRangeStart = engine.exhaustUp.rpmAtRangeStart = engine.rpmRange.x;
            engine.engineUp.rpmAtRangeEnd = engine.exhaustUp.rpmAtRangeEnd = engine.rpmRange.y;

            RevTestDriver driver = car.AddComponent<RevTestDriver>();
            driver.engine = engine;
            driver.mode = RevTestDriver.DriveMode.FreeRev;

            GameObject cameraObject = Camera.main != null ? Camera.main.gameObject : new GameObject("Main Camera");
            if (cameraObject.GetComponent<Camera>() == null) cameraObject.AddComponent<Camera>();
            if (cameraObject.GetComponent<AudioListener>() == null) cameraObject.AddComponent<AudioListener>();
            cameraObject.name = "Main Camera (listener)";
            RevListenerOrbit orbit = cameraObject.GetComponent<RevListenerOrbit>();
            if (orbit == null) orbit = cameraObject.AddComponent<RevListenerOrbit>();
            orbit.target = car.transform;
            orbit.distance = 6f;
            orbit.height = 1.4f;
            orbit.angleDegrees = 35f;

            Light light = Object.FindAnyObjectByType<Light>();
            if (light != null) light.transform.rotation = Quaternion.Euler(48f, 35f, 0f);

            Directory.CreateDirectory(Folder);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Selection.activeGameObject = car;
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath));

            ReportAndOfferPreparation(engine, cylinders);
        }

        // ------------------------------------------------------------------ clip matching
        static int AssignClips(RevGranularEngine engine)
        {
            string[] guids = AssetDatabase.FindAssets("t:AudioClip");
            engine.engineUp.clip = PickClip(guids, false, false);
            engine.engineDown.clip = PickClip(guids, false, true);
            engine.exhaustUp.clip = PickClip(guids, true, false);
            engine.exhaustDown.clip = PickClip(guids, true, true);

            int cylinders = 4;
            AudioClip reference = engine.engineUp.clip != null ? engine.engineUp.clip : engine.exhaustUp.clip;
            if (reference != null)
            {
                string n = reference.name.ToLowerInvariant();
                if (n.Contains("v12")) cylinders = 12;
                else if (n.Contains("v10")) cylinders = 10;
                else if (n.Contains("v8")) cylinders = 8;
                else if (n.Contains("v6") || n.Contains("i6") || n.Contains("l6")) cylinders = 6;
                else if (n.Contains("v4") || n.Contains("i4") || n.Contains("l4")) cylinders = 4;
                else if (n.Contains("i3") || n.Contains("l3")) cylinders = 3;
            }
            return cylinders;
        }

        static AudioClip PickClip(string[] guids, bool exhaust, bool down)
        {
            AudioClip best = null;
            float bestScore = 0f;

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

                bool isExhaust = name.Contains("exh");
                bool isEngine = !isExhaust && (name.Contains("eng") || name.Contains("motor"));
                if (exhaust != isExhaust) continue;
                if (!exhaust && !isEngine) continue;

                float score = 1f;

                bool namedDown = name.Contains("down") || name.Contains("_dn") || name.Contains("dec") || name.Contains("off");
                bool namedUp = name.Contains("up") || name.Contains("acc");
                if (namedDown || namedUp)
                {
                    if (namedDown == down) score += 20f;
                    else continue;                       // explicitly the other direction
                }
                else
                {
                    // No up/down in the name: fall back on a trailing index, _1 = up, _2 = down.
                    bool endsOne = name.EndsWith("_1") || name.EndsWith("1");
                    bool endsTwo = name.EndsWith("_2") || name.EndsWith("2") || name.EndsWith("_3") || name.EndsWith("3");
                    if (endsOne && !down) score += 8f;
                    else if (endsTwo && down) score += 8f;
                    else if ((endsOne && down) || (endsTwo && !down)) continue;
                }

                if (path.StartsWith("Assets/TestEngineSounds")) score += 4f;

                AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (clip == null) continue;
                score += Mathf.Min(3f, clip.length * 0.1f);      // a longer sweep covers more RPM

                if (score > bestScore) { bestScore = score; best = clip; }
            }
            return best;
        }

        // ------------------------------------------------------------------ report
        static void ReportAndOfferPreparation(RevGranularEngine engine, int cylinders)
        {
            string report = "REV test scene created at " + ScenePath + "\n\n";
            int assigned = 0;
            for (int i = 0; i < RevEngineCore.LayerCount; i++)
            {
                RevSampleLayer layer = engine.GetLayerSettings(i);
                string clipName = layer != null && layer.clip != null ? layer.clip.name : "<nothing found>";
                if (layer != null && layer.clip != null) assigned++;
                report += "  " + RevGranularEngine.LayerName(i) + ":  " + clipName + "\n";
            }
            report += "\nCylinders guessed from the file name: " + cylinders;
            Debug.Log(report);

            if (assigned == 0)
            {
                EditorUtility.DisplayDialog("REV test scene",
                    "Scene created, but no clips could be matched by name. Assign the four clips on the " +
                    "Car object by hand, then press Detect RPM.", "OK");
                return;
            }

            bool prepare = EditorUtility.DisplayDialog("REV test scene",
                report + "\n\nUp and down are guessed from the file names - check them, and swap the two if " +
                "accel and decel sound the wrong way round.\n\nPrepare the clips now? That sets their Load " +
                "Type to Decompress On Load (the engine cannot read compressed clips) and runs RPM detection " +
                "on all four, which takes a few seconds each.",
                "Prepare clips", "Skip");

            if (!prepare) return;

            for (int i = 0; i < RevEngineCore.LayerCount; i++)
            {
                RevSampleLayer layer = engine.GetLayerSettings(i);
                if (layer == null || layer.clip == null) continue;
                EnsureDecompressOnLoad(layer.clip);
            }

            string messages = "";
            for (int i = 0; i < RevEngineCore.LayerCount; i++)
            {
                RevSampleLayer layer = engine.GetLayerSettings(i);
                if (layer == null || layer.clip == null) continue;
                string message;
                RevSampleAnalyser.Analyse(layer, cylinders, 400f, engine.rpmRange.y * 1.4f, out message);
                messages += RevGranularEngine.LayerName(i) + " - " + message + "\n";
            }
            Debug.Log("REV RPM detection:\n" + messages);

            // Detection fills in the real RPM span, so widen the engine range to match it.
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < RevEngineCore.LayerCount; i++)
            {
                RevSampleLayer layer = engine.GetLayerSettings(i);
                if (layer == null || !layer.hasProfile) continue;
                min = Mathf.Min(min, layer.detectedMinRpm);
                max = Mathf.Max(max, layer.detectedMaxRpm);
            }
            if (min < max)
            {
                engine.rpmRange = new Vector2(Mathf.Round(min / 50f) * 50f, Mathf.Round(max / 50f) * 50f);
                Debug.Log("REV: RPM range set to " + engine.rpmRange.x + " - " + engine.rpmRange.y +
                          " from the detected profiles.");
            }

            engine.Rebuild(true);
            EditorUtility.SetDirty(engine);
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), ScenePath);
        }

        static void EnsureDecompressOnLoad(AudioClip clip)
        {
            string path = AssetDatabase.GetAssetPath(clip);
            AudioImporter importer = AssetImporter.GetAtPath(path) as AudioImporter;
            if (importer == null) return;

            AudioImporterSampleSettings settings = importer.defaultSampleSettings;
            if (settings.loadType == AudioClipLoadType.DecompressOnLoad && !importer.loadInBackground) return;

            settings.loadType = AudioClipLoadType.DecompressOnLoad;
            importer.defaultSampleSettings = settings;
            importer.loadInBackground = false;
            importer.SaveAndReimport();
        }

        // ------------------------------------------------------------------ materials
        static Material GetMaterial(string name, Color colour)
        {
            string path = MaterialFolder + "/REV_" + name + ".mat";
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) return null;

            Material material = new Material(shader);
            material.name = "REV_" + name;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", colour);
            if (material.HasProperty("_Color")) material.SetColor("_Color", colour);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.25f);

            Directory.CreateDirectory(MaterialFolder);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        static void SetMaterial(GameObject target, Material material)
        {
            if (material == null) return;
            Renderer renderer = target.GetComponent<Renderer>();
            if (renderer != null) renderer.sharedMaterial = material;
        }
    }
}
