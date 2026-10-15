using System;
namespace UnityEngine
{
    public static class Time { public static int frameCount; public static float fixedTime, fixedDeltaTime = .02f, time, deltaTime = .016f; }
    public static class Application { public static string unityVersion = "fixture-unity"; }
    public enum HideFlags { HideAndDontSave }
    [Flags] public enum RigidbodyConstraints { None = 0, FreezeRotation = 112 }
    public struct Vector3
    {
        public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero { get { return new Vector3(); } }
        public static Vector3 operator +(Vector3 a, Vector3 b) { return new Vector3(a.x + b.x, a.y + b.y, a.z + b.z); }
        public static Vector3 operator *(Vector3 a, float b) { return new Vector3(a.x * b, a.y * b, a.z * b); }
    }
    public struct Quaternion { public float x, y, z, w; public static Quaternion identity { get { return new Quaternion { w = 1 }; } } }
    public class Object { public static void Destroy(Object value) { if (value is GameObject) ((GameObject)value).Destroyed = true; } }
    public class GameObject : Object
    {
        public static readonly System.Collections.Generic.List<Rigidbody> Bodies = new System.Collections.Generic.List<Rigidbody>();
        public bool Destroyed; public HideFlags hideFlags; public GameObject(string name) { }
        public T AddComponent<T>() where T : new() { var value = new T(); if (value is Rigidbody) Bodies.Add((Rigidbody)(object)value); return value; }
    }
    public class Rigidbody
    {
        public bool useGravity, detectCollisions; public float drag, angularDrag; public RigidbodyConstraints constraints;
        public Vector3 position, velocity, angularVelocity; public Quaternion rotation;
        public static void Integrate() { foreach (var body in GameObject.Bodies) body.position += body.velocity * Time.fixedDeltaTime; }
    }
}
namespace UnityEngine.PlayerLoop
{
    public struct Initialization { }
    public struct EarlyUpdate { }
    public struct PreUpdate { }
    public struct FixedUpdate { public struct PhysicsFixedUpdate {} public struct ScriptRunBehaviourFixedUpdate {} }
    public struct Update { public struct ScriptRunBehaviourUpdate {} public struct ScriptRunDelayedDynamicFrameRate {} public struct ScriptRunDelayedTasks {} }
    public struct PreLateUpdate { public struct ScriptRunBehaviourLateUpdate {} }
    public struct PostLateUpdate { public struct UpdateAllRenderers {} public struct UpdateAllSkinnedMeshes {} public struct PlayerUpdateCanvases {} public struct FinishFrameRendering {} public struct PresentAfterDraw {} }
}
public static class Versioning { public static int version_major = 1, version_minor = 12, Revision = 5; }
namespace UnityEngine.LowLevel
{
    public struct PlayerLoopSystem
    {
        public delegate void UpdateFunction();
        public Type type;
        public PlayerLoopSystem[] subSystemList;
        public UpdateFunction updateDelegate;
        public IntPtr updateFunction, loopConditionFunction;
    }
    public static class PlayerLoop
    {
        public static PlayerLoopSystem Current;
        public static int Writes;
        public static bool FailAfterWrite, FailBeforeWrite;
        public static PlayerLoopSystem GetCurrentPlayerLoop() { return Current; }
        public static void SetPlayerLoop(PlayerLoopSystem value) { Writes++; if (FailBeforeWrite) { FailBeforeWrite = false; throw new InvalidOperationException("Native setter failure before write"); } Current = value; if (FailAfterWrite) { FailAfterWrite = false; throw new InvalidOperationException("Native setter failure"); } }
    }
}
