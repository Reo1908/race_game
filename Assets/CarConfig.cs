using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Cars/CarConfig")]
public class CarConfig : ScriptableObject
{
    public Data data;
    public Sounds sounds;
    public Car car;
    public Chassis chassis;
    public Dynamics dynamics;
}

#region DATA

[System.Serializable]
public class Data
{
    [Tooltip("Name of the car")]
    public string carName = "Fireblade";

    public CarClass carClass = CarClass.S;
    public string engineDisplay = "2.3L Flatplane V4";
    [TextArea] public string description = "Hello yes this is a test description";
}

public enum CarClass
{
    C,
    CS,
    S,
    SR,
    R,
    RR,
    EX
}

#endregion

#region SOUNDS

[System.Serializable]
public class Sounds
{
    public AudioClip engineSpoolUp;
    public AudioClip engineSpoolDown;
    public AudioClip engineBurble;

    [Range(0f, 1f)]
    public float transmissionNoiseLevel = 0.2f;
}

#endregion

#region CAR (VISUALS + BODY)

[System.Serializable]
public class Car
{
    public Mesh carMesh;
    public List<Material> carMaterials;
    public Mesh carCollider;

    public Color carColor1 = Color.white;
    public Color carColor2 = Color.white;
    public Color carColor3 = Color.white;

    [Header("Front Wheels")]
    public Mesh frontWheelMesh;
    public Material frontWheelMaterial;

    [Header("Rear Wheels")]
    public Mesh rearWheelMesh;
    public Material rearWheelMaterial;

    [Header("Moving Parts")]
    public List<Mesh> popupHeadlights;
    public List<Vector3> popupMovement;
    public AnimationCurve popupMovementCurve;
    public List<Vector3> popupRotation;
    public AnimationCurve popupRotationCurve;

    public Mesh activeAeroWing;
    public List<Vector3> wingMovement;
    public AnimationCurve wingMovementCurve;
    public List<Vector3> wingRotation;
    public AnimationCurve wingRotationCurve;

    [Header("Body")]
    public float dragCoefficient = 0.31f;

    [Range(0f, 1f)]
    public float weightDistribution = 0.5f;
}

#endregion

#region CHASSIS

[System.Serializable]
public class Chassis
{
    public Transmission transmission;
    public Suspension suspension;
    public Tires tires;
    public Brakes brakes;
    public Engine engine;
}

[System.Serializable]
public class Transmission
{
    public Drivetrain drivetrain = Drivetrain.RWD;
    public float finalDrive = 3.7f;
    public List<float> gearRatios = new List<float> { 1.8f, 2.5f, 1.8f, 1.3f, 1f, 0.8f, 0.58f };

    public float clutchHardness = 25f;
    public float shiftTime = 0.25f;
}

public enum Drivetrain
{
    RWD,
    FWD,
    AWD
}

[System.Serializable]
public class Suspension
{
    public float bodyRollForward = -0.2f;
    public float bodyRollSideways = 0.2f;

    public float frontTrackWidth = 1f;
    public float rearTrackWidth = 1f;

    public float frontAxleOffset = 1f;
    public float rearAxleOffset = 1f;

    public float frontWheelDiameter = 0.55f;
    public float rearWheelDiameter = 0.55f;

    public float suspensionHeight = 0.2f;
    public float suspensionStrengthFront = 3000f;
    public float suspensionStrengthRear = 3000f;
    public float suspensionDamping = 500f;

    public float FrontCamber = 1f;
    public float RearCamber = 1f;
}

[System.Serializable]
public class Tires
{
    public AnimationCurve gripCurve;

    public float grip = 6f;
    public float driftGrip = 4f;
    public float rearGrip = 0.5f;

    public float transitionTime = 5f;
    public float compressionGripMultiplier = 0.62f;
}

[System.Serializable]
public class Brakes
{
    public float brakeTorque = 10f;
    public float handbrakeTorque = 5f;

    [Range(0f, 1f)]
    public float brakeRearBalance = 0.9f;
}

[System.Serializable]
public class Engine
{
    public AnimationCurve torqueCurve;

    public float redline = 8000f;
    public float idleRPM = 800f;

    public float braking = 1f;
    public float inertia = 0.04f;
    public float maxTorque = 1700f;

    public float rpmLimiter = 100f;
    public float response = 30f;

    public ForcedInductionType forcedInductionType = ForcedInductionType.None;
    public float forcedInductionPower = 0f;
    public float forcedInductionLag = 1f;
}

public enum ForcedInductionType
{
    None,
    Turbo,
    TwinTurbo,
    Supercharger
}

#endregion

#region DYNAMICS

[System.Serializable]
public class Dynamics
{
    public Steering steering;
    public Drifting drifting;
}

[System.Serializable]
public class Steering
{
    public float turnRate = 2.3f;
    public float stability = 1.7f;

    [Range(0f, 1f)]
    public float throttleMultiplier = 0.7f;

    public float steeringResponse = 100f;
    public float brakeSteer = 0.2f;
    public float handbrakeSteer = 1f;
}

[System.Serializable]
public class Drifting
{
    public float instability = 1.7f;

    public float throttleMultiplier = 0.7f;
    public float steeringResponse = 10f;

    [Range(0f, 1f)]
    public float alignmentStrength = 0.22f;

    public float alignmentRate = 6f;

    [Range(0f, 1f)]
    public float forceDirectionAlignment = 1f;
}

#endregion