using System;
using NUnit.Framework;
using Strada.Core.ECS;
using Strada.Core.ECS.Core;
using Strada.Core.ECS.Jobs;
using Strada.Core.ECS.Query;
using Unity.Collections;

namespace Strada.Core.Tests.Tests.Runtime.ECS
{
    [TestFixture]
    public class ECSIterationSafetyTests
    {
        private EntityManager _manager;

        private struct TestComponent : IComponent { public int Value; }

        [SetUp]
        public void Setup() => _manager = new EntityManager();

        [TearDown]
        public void TearDown() => _manager?.Dispose();

        /// <summary>
        /// Destroying an entity from inside ForEach swap-removes it from the component
        /// storage, reordering the very dense array the loop holds a raw pointer into. The
        /// loop previously kept going over stale indices and counted them as processed —
        /// reading memory that no longer belongs to the entity it thinks it is visiting.
        /// In Editor and Development builds that is now a hard error instead of silent
        /// corruption. Release player builds keep the old unchecked behaviour (the guard is
        /// [Conditional]), which is why the ECB path below is the only supported pattern.
        /// </summary>
        [Test]
        public void ForEach_DestroyEntity_ThrowsStructuralChangeGuard()
        {
            var e1 = _manager.CreateEntity(); _manager.AddComponent(e1, new TestComponent { Value = 1 });
            var e2 = _manager.CreateEntity(); _manager.AddComponent(e2, new TestComponent { Value = 2 });
            var e3 = _manager.CreateEntity(); _manager.AddComponent(e3, new TestComponent { Value = 3 });

            var ex = Assert.Throws<InvalidOperationException>(() =>
                _manager.ForEach((int entityIndex, ref TestComponent c) =>
                {
                    if (c.Value == 1)
                        _manager.DestroyEntity(_manager.GetEntity(entityIndex));
                }));

            Assert.That(ex.Message, Does.Contain("EntityCommandBuffer"),
                "The guard should point the caller at the supported alternative.");
        }

        [Test]
        public void ForEach_WithECB_IsSafe()
        {
            var e1 = _manager.CreateEntity(); _manager.AddComponent(e1, new TestComponent { Value = 1 });
            var e2 = _manager.CreateEntity(); _manager.AddComponent(e2, new TestComponent { Value = 2 });
            var e3 = _manager.CreateEntity(); _manager.AddComponent(e3, new TestComponent { Value = 3 });

            int processedCount = 0;
            var ecb = new EntityCommandBuffer(Allocator.TempJob);

            _manager.ForEach((int entityIndex, ref TestComponent c) =>
            {
                processedCount++;
                if (c.Value == 1)
                {
                    var entity = _manager.GetEntity(entityIndex);
                    ecb.DestroyEntity(entity);
                }
            });

            ecb.Playback(_manager);
            ecb.Dispose();

            Assert.AreEqual(3, processedCount);
            Assert.IsFalse(_manager.Exists(e1));
            Assert.IsTrue(_manager.Exists(e2));
            Assert.IsTrue(_manager.Exists(e3));
        }
    }
}
