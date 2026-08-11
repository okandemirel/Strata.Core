using NUnit.Framework;
using Strada.Core.ECS;
using Strada.Core.ECS.Core;
using Strada.Core.ECS.Query;

namespace Strada.Core.Tests.Tests.Runtime.ECS.Query
{
    /// <summary>
    /// Covers the 9-16 component query surface produced by EntityQueryGenerator.
    ///
    /// This surface is advertised in the README but had never actually reached a consumer:
    /// the generator was missing from the shipped analyzer assembly, the code it emitted did
    /// not compile (EntityManager lives in a sibling namespace that was never imported), and
    /// it emitted into every assembly rather than only the one that owns the namespace.
    /// Each of those failed silently, so these tests exist to make a regression loud.
    /// </summary>
    [TestFixture]
    public class GeneratedQueryTests
    {
        private EntityManager _manager;

        [SetUp]
        public void Setup() => _manager = new EntityManager();

        [TearDown]
        public void TearDown() => _manager?.Dispose();

        private struct C1 : IComponent { public int V; }
        private struct C2 : IComponent { public int V; }
        private struct C3 : IComponent { public int V; }
        private struct C4 : IComponent { public int V; }
        private struct C5 : IComponent { public int V; }
        private struct C6 : IComponent { public int V; }
        private struct C7 : IComponent { public int V; }
        private struct C8 : IComponent { public int V; }
        private struct C9 : IComponent { public int V; }

        private Entity CreateFullEntity(int seed)
        {
            var e = _manager.CreateEntity();
            _manager.AddComponent(e, new C1 { V = seed });
            _manager.AddComponent(e, new C2 { V = seed });
            _manager.AddComponent(e, new C3 { V = seed });
            _manager.AddComponent(e, new C4 { V = seed });
            _manager.AddComponent(e, new C5 { V = seed });
            _manager.AddComponent(e, new C6 { V = seed });
            _manager.AddComponent(e, new C7 { V = seed });
            _manager.AddComponent(e, new C8 { V = seed });
            _manager.AddComponent(e, new C9 { V = seed });
            return e;
        }

        [Test]
        public void NineComponentQuery_VisitsMatchingEntities_AndWritesBack()
        {
            CreateFullEntity(1);
            CreateFullEntity(2);

            // An entity missing the ninth component must not be visited.
            var partial = _manager.CreateEntity();
            _manager.AddComponent(partial, new C1 { V = 99 });

            int visited = 0;
            _manager.Query().Select<C1, C2, C3, C4, C5, C6, C7, C8, C9>().ForEach(
                (int entity, ref C1 c1, ref C2 c2, ref C3 c3, ref C4 c4, ref C5 c5,
                 ref C6 c6, ref C7 c7, ref C8 c8, ref C9 c9) =>
                {
                    visited++;
                    c1.V *= 10;
                });

            Assert.AreEqual(2, visited, "Only entities holding all nine components should be visited.");
            Assert.AreEqual(99, _manager.GetComponent<C1>(partial).V, "The partial entity must be untouched.");
        }

        [Test]
        public void NineComponentQuery_EntityManagerExtension_IsGenerated()
        {
            CreateFullEntity(7);

            int visited = 0;
            _manager.ForEach((int entity, ref C1 c1, ref C2 c2, ref C3 c3, ref C4 c4, ref C5 c5,
                              ref C6 c6, ref C7 c7, ref C8 c8, ref C9 c9) => visited++);

            Assert.AreEqual(1, visited);
        }
    }
}
