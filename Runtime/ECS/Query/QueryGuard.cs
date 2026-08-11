using System;
using System.Diagnostics;

namespace Strada.Core.ECS.Query
{
    /// <summary>
    /// Detects structural changes made from inside a query callback.
    /// </summary>
    /// <remarks>
    /// Query iteration hoists raw pointers to the component storages' native arrays out of
    /// the loop for speed. If a callback adds or removes a component of an iterated type, the
    /// storage may grow (reallocating those arrays) or swap-remove (reordering them) — the
    /// hoisted pointers then reference freed or shuffled memory, and every subsequent
    /// iteration reads and writes it. That is silent heap corruption, not an exception.
    ///
    /// The check is compiled out entirely in non-development player builds: because
    /// <see cref="ConditionalAttribute"/> removes the call AND its argument expressions, the
    /// version reads disappear too, so release iteration pays nothing. This mirrors how
    /// Unity's own collections do safety checks (ENABLE_UNITY_COLLECTIONS_CHECKS).
    ///
    /// Callers must defer structural changes with an
    /// <see cref="Strada.Core.ECS.Jobs.EntityCommandBuffer"/> instead.
    /// </remarks>
    internal static class QueryGuard
    {
        [Conditional("UNITY_EDITOR")]
        [Conditional("DEVELOPMENT_BUILD")]
        public static void Check(int expected, int actual)
        {
            if (expected != actual)
                Throw();
        }

        private static void Throw()
        {
            throw new InvalidOperationException(
                "A structural change (AddComponent/RemoveComponent/Clear/DestroyEntity) was made " +
                "during query iteration. The query holds direct pointers into the component " +
                "storage, so this corrupts memory. Record the change into an EntityCommandBuffer " +
                "and play it back after the loop instead.");
        }
    }
}
