using NUnit.Framework;
using Strada.Core.Sync;

// Tests intentionally exercise the legacy Unsubscribe API on
// IReadOnlyReactiveProperty<T> during the deprecation period.
#pragma warning disable CS0618

namespace Strada.Core.Tests.Tests.Runtime.Sync
{
    [TestFixture]
    public class ReactivePropertyTests
    {
        [Test]
        public void Value_Get_ReturnsCurrentValue()
        {
            var property = new ReactiveProperty<int>(42);

            Assert.AreEqual(42, property.Value);
        }

        [Test]
        public void Value_Set_UpdatesValue()
        {
            var property = new ReactiveProperty<int>(0);

            property.Value = 100;

            Assert.AreEqual(100, property.Value);
        }

        [Test]
        public void Subscribe_NotifiesOnValueChange()
        {
            var property = new ReactiveProperty<int>(0);
            int notifiedValue = 0;

            property.Subscribe(v => notifiedValue = v);
            property.Value = 50;

            Assert.AreEqual(50, notifiedValue);
        }

        [Test]
        public void Subscribe_DoesNotNotify_WhenValueUnchanged()
        {
            var property = new ReactiveProperty<int>(10);
            int notifyCount = 0;

            property.Subscribe(_ => notifyCount++);
            property.Value = 10;

            Assert.AreEqual(0, notifyCount);
        }

        [Test]
        public void SubscribeAndInvoke_ImmediatelyCallsHandler()
        {
            var property = new ReactiveProperty<int>(25);
            int receivedValue = 0;

            property.SubscribeAndInvoke(v => receivedValue = v);

            Assert.AreEqual(25, receivedValue);
        }

        [Test]
        public void Unsubscribe_StopsNotifications()
        {
            var property = new ReactiveProperty<int>(0);
            int notifyCount = 0;
            System.Action<int> handler = _ => notifyCount++;

            property.Subscribe(handler);
            property.Value = 1;
            Assert.AreEqual(1, notifyCount);

            property.Unsubscribe(handler);
            property.Value = 2;
            Assert.AreEqual(1, notifyCount);
        }

        [Test]
        public void SetWithoutNotify_DoesNotTriggerSubscribers()
        {
            var property = new ReactiveProperty<int>(0);
            int notifyCount = 0;

            property.Subscribe(_ => notifyCount++);
            property.SetWithoutNotify(100);

            Assert.AreEqual(0, notifyCount);
            Assert.AreEqual(100, property.Value);
        }

        [Test]
        public void ImplicitConversion_ReturnsValue()
        {
            var property = new ReactiveProperty<int>(42);

            int value = property;

            Assert.AreEqual(42, value);
        }

        [Test]
        public void Clear_RemovesAllSubscribers()
        {
            var property = new ReactiveProperty<int>(0);
            int notifyCount = 0;

            property.Subscribe(_ => notifyCount++);
            property.Subscribe(_ => notifyCount++);
            property.Clear();
            property.Value = 1;

            Assert.AreEqual(0, notifyCount);
        }

        [Test]
        public void MultipleSubscribers_AllNotified()
        {
            var property = new ReactiveProperty<int>(0);
            int count1 = 0, count2 = 0, count3 = 0;

            property.Subscribe(_ => count1++);
            property.Subscribe(_ => count2++);
            property.Subscribe(_ => count3++);

            property.Value = 1;

            Assert.AreEqual(1, count1);
            Assert.AreEqual(1, count2);
            Assert.AreEqual(1, count3);
        }
    }

    [TestFixture]
    public class ReactiveCollectionTests
    {
        [Test]
        public void Add_IncreasesCount()
        {
            var collection = new ReactiveCollection<int>();

            collection.Add(1);
            collection.Add(2);

            Assert.AreEqual(2, collection.Count);
        }

        [Test]
        public void Add_NotifiesOnAddHandler()
        {
            var collection = new ReactiveCollection<int>();
            int addedItem = 0;

            collection.OnAdd(item => addedItem = item);
            collection.Add(42);

            Assert.AreEqual(42, addedItem);
        }

        [Test]
        public void Remove_DecreasesCount()
        {
            var collection = new ReactiveCollection<int>();
            collection.Add(1);
            collection.Add(2);

            collection.Remove(1);

            Assert.AreEqual(1, collection.Count);
        }

        [Test]
        public void Remove_NotifiesOnRemoveHandler()
        {
            var collection = new ReactiveCollection<int>();
            int removedItem = 0;

            collection.OnRemove(item => removedItem = item);
            collection.Add(42);
            collection.Remove(42);

            Assert.AreEqual(42, removedItem);
        }

        [Test]
        public void Clear_NotifiesOnClearHandler()
        {
            var collection = new ReactiveCollection<int>();
            bool cleared = false;

            collection.OnClear(() => cleared = true);
            collection.Add(1);
            collection.Clear();

            Assert.IsTrue(cleared);
            Assert.AreEqual(0, collection.Count);
        }

        [Test]
        public void Indexer_ReturnsCorrectItem()
        {
            var collection = new ReactiveCollection<string>();
            collection.Add("first");
            collection.Add("second");

            Assert.AreEqual("first", collection[0]);
            Assert.AreEqual("second", collection[1]);
        }

        [Test]
        public void RemoveAt_RemovesAtIndex()
        {
            var collection = new ReactiveCollection<int>();
            collection.Add(10);
            collection.Add(20);
            collection.Add(30);

            collection.RemoveAt(1);

            Assert.AreEqual(2, collection.Count);
            Assert.AreEqual(10, collection[0]);
            Assert.AreEqual(30, collection[1]);
        }
    }
}
