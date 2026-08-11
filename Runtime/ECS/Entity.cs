using System;
using System.Runtime.InteropServices;

namespace Strada.Core.ECS
{
    /// <summary>
    /// Lightweight entity identifier.
    /// 64-bit value: 32-bit index + 32-bit version for safety.
    /// Designed to be Burst-compatible.
    /// </summary>
    /// <remarks>
    /// <para><b>Runtime-only. Entity handles must not be persisted.</b> The value is meaningful
    /// only against the <see cref="Strada.Core.ECS.Core.EntityManager"/> instance that issued it,
    /// and both fields are <c>readonly</c> — which Unity's serializer skips, along with
    /// <c>static</c> and <c>const</c>. A <c>[Serializable]</c> attribute was therefore advertising
    /// a capability the type does not have: an Entity field on a MonoBehaviour or ScriptableObject
    /// wrote nothing and read back as <c>(0, 0)</c>, i.e. <see cref="Null"/>, which
    /// <c>EntityManager.Exists</c> rejects. The failure surfaced as "my entity reference is gone
    /// after a domain reload" rather than as a serialization error, so the attribute was removed.</para>
    /// <para>To persist world state across a domain reload or a save, use
    /// <c>EntityManager.CaptureState</c> / <c>EntityManager.RestoreState</c>, which round-trip the
    /// index and version arrays together.</para>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct Entity : IEquatable<Entity>
    {
        public readonly int Index;
        public readonly int Version;

        public static readonly Entity Null = new Entity(0, 0);

        public Entity(int index, int version)
        {
            Index = index;
            Version = version;
        }

        public bool IsNull => Index == 0 && Version == 0;

        public bool Equals(Entity other)
        {
            return Index == other.Index && Version == other.Version;
        }

        public override bool Equals(object obj)
        {
            return obj is Entity other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Index * 397) ^ Version;
            }
        }

        public static bool operator ==(Entity left, Entity right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(Entity left, Entity right)
        {
            return !left.Equals(right);
        }

        public override string ToString()
        {
            return $"Entity({Index}, v{Version})";
        }
    }
}
