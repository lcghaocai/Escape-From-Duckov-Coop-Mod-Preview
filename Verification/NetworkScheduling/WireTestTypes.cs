// Only value types used by the real snapshot serializer are stand-ins here.
// Nested item serialization requires Unity and is explicitly outside this test.
namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }
    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new(0, 0, 0, 1);
    }
    public static class Mathf
    {
        public static float Sqrt(float value) => MathF.Sqrt(value);
    }
}

namespace EscapeFromDuckovCoopMod
{
    public enum Teams : byte { neutral }
    public enum AIStatus : byte { Dormant, Active, Dead }
    internal static class ItemTool
    {
        internal static void WriteItemSnapshot(NetDataWriter writer, ItemSnapshot item) =>
            throw new NotSupportedException("Nested Unity items are outside the wire-tail test");
        internal static ItemSnapshot ReadItemSnapshot(NetPacketReader reader) =>
            throw new NotSupportedException("Nested Unity items are outside the wire-tail test");
    }
}
