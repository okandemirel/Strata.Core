using System;
using System.Collections.Generic;
using FsCheck;
using NUnit.Framework;
using Strada.Core.Communication;
using Strada.Core.Tests.Tests.Runtime.Generators;

// Property-based tests intentionally exercise the legacy Unsubscribe API.
// See MessageBusTests.cs for the deprecation rationale.
#pragma warning disable CS0618

namespace Strada.Core.Tests.Tests.Runtime.Communication
{
    /// <summary>
    /// Property-based tests for MessageBus messaging system.
    /// Tests verify correctness properties that must hold across all valid inputs.
    /// </summary>
    [TestFixture]
    public class BusPropertyTests
    {
        [OneTimeSetUp]
        public void Setup()
        {
            StradaArbitraries.RegisterAll();
        }

        /// <summary>
        /// Generator for subscriber count (1-20).
        /// </summary>
        private static Gen<int> SubscriberCountGen => Gen.Choose(1, 20);

        /// <summary>
        /// Generator for event value.
        /// </summary>
        private static Gen<int> EventValueGen => Gen.Choose(1, 10000);

        /// <summary>
        /// Generator for command value.
        /// </summary>
        private static Gen<int> CommandValueGen => Gen.Choose(1, 10000);

        /// <summary>
        /// Generator for query multiplier.
        /// </summary>
        private static Gen<int> QueryMultiplierGen => Gen.Choose(1, 100);

        /// <summary>
        /// Generator for publish count (1-10).
        /// </summary>
        private static Gen<int> PublishCountGen => Gen.Choose(1, 10);

        /// <summary>
        /// **Feature: strada-codebase-audit, Property 10: Event Delivery Completeness**
        /// For any event published to MessageBus with N subscribers,
        /// all N subscribers SHALL receive the event exactly once.
        /// **Validates: Requirements 4.1**
        /// </summary>
        [Test]
        public void EventDeliveryCompleteness_AllSubscribersReceiveEvent()
        {
            var config = PropertyTestConfig.CreateConfig();

            var property = Prop.ForAll(
                SubscriberCountGen.ToArbitrary(),
                EventValueGen.ToArbitrary(),
                (subscriberCount, eventValue) =>
                {
                    using var bus = new EventBus();
                    var receivedValues = new List<int>();
                    var receiveCounts = new int[subscriberCount];

                    for (int i = 0; i < subscriberCount; i++)
                    {
                        int index = i;
                        bus.Subscribe<TestEvent>(e =>
                        {
                            receivedValues.Add(e.Value);
                            receiveCounts[index]++;
                        });
                    }

                    bus.Publish(new TestEvent { Value = eventValue });

                    if (receivedValues.Count != subscriberCount)
                        return false;

                    foreach (var value in receivedValues)
                    {
                        if (value != eventValue)
                            return false;
                    }

                    foreach (var count in receiveCounts)
                    {
                        if (count != 1)
                            return false;
                    }

                    return true;
                });

            property.Check(config);
        }

        /// <summary>
        /// **Feature: strada-codebase-audit, Property 10: Event Delivery Completeness**
        /// Additional test: Multiple events are all delivered to all subscribers.
        /// **Validates: Requirements 4.1**
        /// </summary>
        [Test]
        public void EventDeliveryCompleteness_MultipleEventsAllDelivered()
        {
            var config = PropertyTestConfig.CreateConfig();

            var property = Prop.ForAll(
                SubscriberCountGen.ToArbitrary(),
                PublishCountGen.ToArbitrary(),
                (subscriberCount, publishCount) =>
                {
                    using var bus = new EventBus();
                    var totalReceived = new int[subscriberCount];

                    for (int i = 0; i < subscriberCount; i++)
                    {
                        int index = i;
                        bus.Subscribe<TestEvent>(_ => totalReceived[index]++);
                    }

                    for (int p = 0; p < publishCount; p++)
                    {
                        bus.Publish(new TestEvent { Value = p });
                    }

                    foreach (var count in totalReceived)
                    {
                        if (count != publishCount)
                            return false;
                    }

                    return true;
                });

            property.Check(config);
        }

        /// <summary>
        /// **Feature: strada-codebase-audit, Property 11: Command Handler Invocation**
        /// For any command sent to MessageBus with a registered handler,
        /// the handler SHALL be invoked exactly once with the command data.
        /// **Validates: Requirements 4.2**
        /// </summary>
        [Test]
        public void CommandHandlerInvocation_HandlerInvokedExactlyOnce()
        {
            var config = PropertyTestConfig.CreateConfig();

            var property = Prop.ForAll(
                CommandValueGen.ToArbitrary(),
                (commandValue) =>
                {
                    using var bus = new EventBus();
                    int invokeCount = 0;
                    int receivedValue = 0;

                    bus.RegisterSignalHandler<TestCommand>(cmd =>
                    {
                        invokeCount++;
                        receivedValue = cmd.Value;
                    });

                    bus.Send(new TestCommand { Value = commandValue });

                    return invokeCount == 1 && receivedValue == commandValue;
                });

            property.Check(config);
        }

        /// <summary>
        /// **Feature: strada-codebase-audit, Property 11: Command Handler Invocation**
        /// Additional test: Multiple sends invoke handler multiple times.
        /// **Validates: Requirements 4.2**
        /// </summary>
        [Test]
        public void CommandHandlerInvocation_MultipleSendsInvokeMultipleTimes()
        {
            var config = PropertyTestConfig.CreateConfig();

            var property = Prop.ForAll(
                PublishCountGen.ToArbitrary(),
                (sendCount) =>
                {
                    using var bus = new EventBus();
                    int invokeCount = 0;
                    var receivedValues = new List<int>();

                    bus.RegisterSignalHandler<TestCommand>(cmd =>
                    {
                        invokeCount++;
                        receivedValues.Add(cmd.Value);
                    });

                    for (int i = 0; i < sendCount; i++)
                    {
                        bus.Send(new TestCommand { Value = i + 1 });
                    }

                    if (invokeCount != sendCount)
                        return false;

                    for (int i = 0; i < sendCount; i++)
                    {
                        if (receivedValues[i] != i + 1)
                            return false;
                    }

                    return true;
                });

            property.Check(config);
        }

        /// <summary>
        /// **Feature: strada-codebase-audit, Property 12: Query Result Correctness**
        /// For any query sent to MessageBus, the returned result SHALL equal
        /// the value returned by the registered handler.
        /// **Validates: Requirements 4.3**
        /// </summary>
        [Test]
        public void QueryResultCorrectness_ReturnsHandlerResult()
        {
            var config = PropertyTestConfig.CreateConfig();

            var property = Prop.ForAll(
                QueryMultiplierGen.ToArbitrary(),
                (multiplier) =>
                {
                    using var bus = new EventBus();
                    const int baseValue = 10;
                    int expectedResult = baseValue * multiplier;

                    bus.RegisterQueryHandler<TestQuery, int>(q => baseValue * q.Multiplier);

                    var result = bus.Query<TestQuery, int>(new TestQuery { Multiplier = multiplier });

                    return result == expectedResult;
                });

            property.Check(config);
        }

        /// <summary>
        /// **Feature: strada-codebase-audit, Property 12: Query Result Correctness**
        /// Additional test: Query with string result returns correct value.
        /// **Validates: Requirements 4.3**
        /// </summary>
        [Test]
        public void QueryResultCorrectness_StringResult_ReturnsCorrectValue()
        {
            var config = PropertyTestConfig.CreateConfig();

            var property = Prop.ForAll(
                Gen.Choose(1, 1000).ToArbitrary(),
                (id) =>
                {
                    using var bus = new EventBus();
                    string expectedResult = $"Result_{id}";

                    bus.RegisterQueryHandler<TestStringQuery, string>(q => $"Result_{q.Id}");

                    var result = bus.Query<TestStringQuery, string>(new TestStringQuery { Id = id });

                    return result == expectedResult;
                });

            property.Check(config);
        }

        /// <summary>
        /// **Feature: strada-codebase-audit, Property 12: Query Result Correctness**
        /// Additional test: Multiple queries return consistent results.
        /// **Validates: Requirements 4.3**
        /// </summary>
        [Test]
        public void QueryResultCorrectness_MultipleQueries_ConsistentResults()
        {
            var config = PropertyTestConfig.CreateConfig();

            var property = Prop.ForAll(
                QueryMultiplierGen.ToArbitrary(),
                PublishCountGen.ToArbitrary(),
                (multiplier, queryCount) =>
                {
                    using var bus = new EventBus();
                    bus.RegisterQueryHandler<TestQuery, int>(q => 10 * q.Multiplier);

                    var results = new List<int>();
                    for (int i = 0; i < queryCount; i++)
                    {
                        results.Add(bus.Query<TestQuery, int>(new TestQuery { Multiplier = multiplier }));
                    }

                    int expected = 10 * multiplier;
                    foreach (var result in results)
                    {
                        if (result != expected)
                            return false;
                    }

                    return true;
                });

            property.Check(config);
        }

        /// <summary>
        /// **Feature: strada-codebase-audit, Property 13: Unsubscribe Effectiveness**
        /// For any handler that has been unsubscribed, subsequent event publications
        /// SHALL NOT invoke that handler.
        /// **Validates: Requirements 4.4**
        /// </summary>
        [Test]
        public void UnsubscribeEffectiveness_UnsubscribedHandlerNotInvoked()
        {
            var config = PropertyTestConfig.CreateConfig();

            var property = Prop.ForAll(
                PublishCountGen.ToArbitrary(),
                (publishCount) =>
                {
                    using var bus = new EventBus();
                    int invokeCount = 0;
                    Action<TestEvent> handler = _ => invokeCount++;

                    bus.Subscribe(handler);

                    bus.Publish(new TestEvent { Value = 1 });
                    int countBeforeUnsubscribe = invokeCount;

                    bus.Unsubscribe(handler);

                    for (int i = 0; i < publishCount; i++)
                    {
                        bus.Publish(new TestEvent { Value = i + 2 });
                    }

                    return countBeforeUnsubscribe == 1 && invokeCount == 1;
                });

            property.Check(config);
        }

        /// <summary>
        /// **Feature: strada-codebase-audit, Property 13: Unsubscribe Effectiveness**
        /// Additional test: Unsubscribing one handler doesn't affect others.
        /// **Validates: Requirements 4.4**
        /// </summary>
        [Test]
        public void UnsubscribeEffectiveness_OtherHandlersStillReceive()
        {
            var config = PropertyTestConfig.CreateConfig();

            var property = Prop.ForAll(
                SubscriberCountGen.ToArbitrary(),
                PublishCountGen.ToArbitrary(),
                (subscriberCount, publishCount) =>
                {
                    if (subscriberCount < 2) subscriberCount = 2;

                    using var bus = new EventBus();
                    var invokeCounts = new int[subscriberCount];
                    var handlers = new Action<TestEvent>[subscriberCount];

                    for (int i = 0; i < subscriberCount; i++)
                    {
                        int index = i;
                        handlers[i] = _ => invokeCounts[index]++;
                        bus.Subscribe(handlers[i]);
                    }

                    bus.Unsubscribe(handlers[0]);

                    for (int p = 0; p < publishCount; p++)
                    {
                        bus.Publish(new TestEvent { Value = p });
                    }

                    if (invokeCounts[0] != 0)
                        return false;

                    for (int i = 1; i < subscriberCount; i++)
                    {
                        if (invokeCounts[i] != publishCount)
                            return false;
                    }

                    return true;
                });

            property.Check(config);
        }

        /// <summary>
        /// **Feature: strada-codebase-audit, Property 13: Unsubscribe Effectiveness**
        /// Additional test: Subscriber count decreases after unsubscribe.
        /// **Validates: Requirements 4.4**
        /// </summary>
        [Test]
        public void UnsubscribeEffectiveness_SubscriberCountDecreases()
        {
            var config = PropertyTestConfig.CreateConfig();

            var property = Prop.ForAll(
                SubscriberCountGen.ToArbitrary(),
                (subscriberCount) =>
                {
                    using var bus = new EventBus();
                    var handlers = new Action<TestEvent>[subscriberCount];

                    for (int i = 0; i < subscriberCount; i++)
                    {
                        handlers[i] = _ => { };
                        bus.Subscribe(handlers[i]);
                    }

                    int countBefore = bus.GetSubscriberCount<TestEvent>();

                    bus.Unsubscribe(handlers[0]);

                    int countAfter = bus.GetSubscriberCount<TestEvent>();

                    return countBefore == subscriberCount && countAfter == subscriberCount - 1;
                });

            property.Check(config);
        }

        private struct TestEvent
        {
            public int Value;
        }

        private struct TestCommand
        {
            public int Value;
        }

        private struct TestQuery : IQuery<int>
        {
            public int Multiplier;
        }

        private struct TestStringQuery : IQuery<string>
        {
            public int Id;
        }
    }
}
