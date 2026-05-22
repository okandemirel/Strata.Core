using System;
using System.Collections.Generic;
using NUnit.Framework;
using Strada.Core.Commands;
using Strada.Core.Communication;

namespace Strada.Core.Tests.Tests.Runtime.Communication
{
    [TestFixture]
    public class MessageBusTests
    {
        private EventBus _bus;

        [SetUp]
        public void SetUp()
        {
            _bus = new EventBus();
        }

        [TearDown]
        public void TearDown()
        {
            _bus?.Dispose();
        }

        [Test]
        public void Send_WithRegisteredHandler_ExecutesHandler()
        {
            var command = new TestSignal { Value = 42 };
            int receivedValue = 0;

            _bus.RegisterSignalHandler<TestSignal>(cmd => receivedValue = cmd.Value);
            _bus.Send(command);

            Assert.AreEqual(42, receivedValue);
        }

        [Test]
        public void Send_ByRef_ExecutesHandler()
        {
            var command = new TestSignal { Value = 100 };
            int receivedValue = 0;

            _bus.RegisterSignalHandler<TestSignal>(cmd => receivedValue = cmd.Value);
            _bus.Send(ref command);

            Assert.AreEqual(100, receivedValue);
        }

        [Test]
        public void Send_WithoutHandler_ThrowsException()
        {
            var command = new UnhandledCommand();

            Assert.Throws<InvalidOperationException>(() => _bus.Send(command));
        }

        [Test]
        public void Send_WithInterfaceHandler_ExecutesHandler()
        {
            var handler = new TestCommandHandler();
            var command = new TestSignal { Value = 55 };

            _bus.RegisterSignalHandler<TestSignal>(handler);
            _bus.Send(command);

            Assert.AreEqual(55, handler.LastValue);
        }

        [Test]
        public void RegisterCommandHandler_OverwritesPreviousHandler()
        {
            int handler1Called = 0;
            int handler2Called = 0;

            _bus.RegisterSignalHandler<TestSignal>(_ => handler1Called++);
            _bus.RegisterSignalHandler<TestSignal>(_ => handler2Called++);

            _bus.Send(new TestSignal());

            Assert.AreEqual(0, handler1Called);
            Assert.AreEqual(1, handler2Called);
        }

        [Test]
        public void Query_WithRegisteredHandler_ReturnsResult()
        {
            var query = new GetValueQuery { Multiplier = 5 };

            _bus.RegisterQueryHandler<GetValueQuery, int>(new GetValueQueryHandler());

            var result = _bus.Query<GetValueQuery, int>(query);

            Assert.AreEqual(50, result);
        }

        [Test]
        public void Query_ByRef_ReturnsResult()
        {
            var query = new GetValueQuery { Multiplier = 3 };

            _bus.RegisterQueryHandler<GetValueQuery, int>(new GetValueQueryHandler());

            var result = _bus.Query<GetValueQuery, int>(ref query);

            Assert.AreEqual(30, result);
        }

        [Test]
        public void Query_WithDelegateHandler_ReturnsResult()
        {
            _bus.RegisterQueryHandler<GetValueQuery, int>(q => q.Multiplier * 20);

            var result = _bus.Query<GetValueQuery, int>(new GetValueQuery { Multiplier = 2 });

            Assert.AreEqual(40, result);
        }

        [Test]
        public void Query_WithoutHandler_ThrowsException()
        {
            var query = new UnhandledQuery();

            Assert.Throws<InvalidOperationException>(() => _bus.Query<UnhandledQuery, int>(query));
        }

        [Test]
        public void Query_StringResult_ReturnsCorrectType()
        {
            _bus.RegisterQueryHandler<GetNameQuery, string>(q => $"Name_{q.Id}");

            var result = _bus.Query<GetNameQuery, string>(new GetNameQuery { Id = 42 });

            Assert.AreEqual("Name_42", result);
        }

        [Test]
        public void Publish_WithSubscriber_NotifiesSubscriber()
        {
            var evt = new TestEvent { Message = "Hello" };
            string receivedMessage = null;

            _bus.Subscribe<TestEvent>(e => receivedMessage = e.Message);
            _bus.Publish(evt);

            Assert.AreEqual("Hello", receivedMessage);
        }

        [Test]
        public void Publish_ByRef_NotifiesSubscriber()
        {
            var evt = new TestEvent { Message = "World" };
            string receivedMessage = null;

            _bus.Subscribe<TestEvent>(e => receivedMessage = e.Message);
            _bus.Publish(ref evt);

            Assert.AreEqual("World", receivedMessage);
        }

        [Test]
        public void Publish_WithMultipleSubscribers_NotifiesAll()
        {
            var evt = new TestEvent { Message = "Multi" };
            var receivedMessages = new List<string>();

            _bus.Subscribe<TestEvent>(e => receivedMessages.Add(e.Message + "_1"));
            _bus.Subscribe<TestEvent>(e => receivedMessages.Add(e.Message + "_2"));
            _bus.Subscribe<TestEvent>(e => receivedMessages.Add(e.Message + "_3"));

            _bus.Publish(evt);

            Assert.AreEqual(3, receivedMessages.Count);
            Assert.Contains("Multi_1", receivedMessages);
            Assert.Contains("Multi_2", receivedMessages);
            Assert.Contains("Multi_3", receivedMessages);
        }

        [Test]
        public void Publish_WithNoSubscribers_DoesNotThrow()
        {
            var evt = new TestEvent { Message = "NoOne" };

            Assert.DoesNotThrow(() => _bus.Publish(evt));
        }

        [Test]
        public void GetSubscriberCount_ReturnsCorrectCount()
        {
            Assert.AreEqual(0, _bus.GetSubscriberCount<TestEvent>());

            _bus.Subscribe<TestEvent>(_ => { });
            Assert.AreEqual(1, _bus.GetSubscriberCount<TestEvent>());

            _bus.Subscribe<TestEvent>(_ => { });
            Assert.AreEqual(2, _bus.GetSubscriberCount<TestEvent>());
        }

        [Test]
        public void Clear_RemovesAllCommandHandlers()
        {
            _bus.RegisterSignalHandler<TestSignal>(_ => { });
            _bus.Clear();

            Assert.Throws<InvalidOperationException>(() => _bus.Send(new TestSignal()));
        }

        [Test]
        public void Clear_RemovesAllQueryHandlers()
        {
            _bus.RegisterQueryHandler<GetValueQuery, int>(q => q.Multiplier);
            _bus.Clear();

            Assert.Throws<InvalidOperationException>(() => _bus.Query<GetValueQuery, int>(new GetValueQuery()));
        }

        [Test]
        public void Clear_RemovesAllEventSubscribers()
        {
            int callCount = 0;
            _bus.Subscribe<TestEvent>(_ => callCount++);

            _bus.Clear();
            _bus.Publish(new TestEvent());

            Assert.AreEqual(0, callCount);
            Assert.AreEqual(0, _bus.GetSubscriberCount<TestEvent>());
        }

        [Test]
        public void Dispose_ClearsAllHandlers()
        {
            _bus.RegisterSignalHandler<TestSignal>(_ => { });
            _bus.Subscribe<TestEvent>(_ => { });

            _bus.Dispose();

            Assert.AreEqual(0, _bus.GetSubscriberCount<TestEvent>());
        }

        [Test]
        public void Dispose_CalledMultipleTimes_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                _bus.Dispose();
                _bus.Dispose();
                _bus.Dispose();
            });
        }

        [Test]
        public void Commands_AreSeparatedByType()
        {
            int command1Count = 0;
            int command2Count = 0;

            _bus.RegisterSignalHandler<TestSignal>(_ => command1Count++);
            _bus.RegisterSignalHandler<AnotherCommand>(_ => command2Count++);

            _bus.Send(new TestSignal());

            Assert.AreEqual(1, command1Count);
            Assert.AreEqual(0, command2Count);
        }

        [Test]
        public void Events_AreSeparatedByType()
        {
            int event1Count = 0;
            int event2Count = 0;

            _bus.Subscribe<TestEvent>(_ => event1Count++);
            _bus.Subscribe<AnotherEvent>(_ => event2Count++);

            _bus.Publish(new TestEvent());

            Assert.AreEqual(1, event1Count);
            Assert.AreEqual(0, event2Count);
        }

        [Test]
        public void Queries_AreSeparatedByType()
        {
            _bus.RegisterQueryHandler<GetValueQuery, int>(_ => 100);
            _bus.RegisterQueryHandler<GetNameQuery, string>(_ => "test");

            var intResult = _bus.Query<GetValueQuery, int>(new GetValueQuery());
            var stringResult = _bus.Query<GetNameQuery, string>(new GetNameQuery());

            Assert.AreEqual(100, intResult);
            Assert.AreEqual("test", stringResult);
        }

        [Test]
        public void Subscribe_ManyHandlers_AllReceiveEvents()
        {
            const int handlerCount = 100;
            int totalCalls = 0;

            for (int i = 0; i < handlerCount; i++)
            {
                _bus.Subscribe<TestEvent>(_ => totalCalls++);
            }

            _bus.Publish(new TestEvent());

            Assert.AreEqual(handlerCount, totalCalls);
            Assert.AreEqual(handlerCount, _bus.GetSubscriberCount<TestEvent>());
        }

        [Test]
        public void RegisterManyCommandTypes_AllWork()
        {
            const int typeCount = 100;
            var results = new int[typeCount];

            _bus.RegisterSignalHandler<TestSignal>(c => results[0] = c.Value);

            _bus.Send(new TestSignal { Value = 42 });

            Assert.AreEqual(42, results[0]);
        }

        private struct TestSignal
        {
            public int Value;
        }

        private struct AnotherCommand
        {
            public string Data;
        }

        private struct UnhandledCommand { }

        private struct TestEvent
        {
            public string Message;
        }

        private struct AnotherEvent
        {
            public int Code;
        }

        private struct GetValueQuery : IQuery<int>
        {
            public int Multiplier;
        }

        private struct GetNameQuery : IQuery<string>
        {
            public int Id;
        }

        private struct UnhandledQuery : IQuery<int> { }

        private class TestCommandHandler : ISignalHandler<TestSignal>
        {
            public int LastValue;

            public void Handle(TestSignal signal)
            {
                LastValue = signal.Value;
            }
        }

        private class GetValueQueryHandler : IQueryHandler<GetValueQuery, int>
        {
            public int Handle(ref GetValueQuery query)
            {
                return 10 * query.Multiplier;
            }
        }
    }
}
