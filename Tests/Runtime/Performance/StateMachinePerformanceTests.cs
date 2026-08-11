using NUnit.Framework;
using Strada.Core.StateMachine;
using Unity.PerformanceTesting;

namespace Strada.Core.Tests.Tests.Runtime.Performance
{
    [TestFixture]
    [Category("Performance")]
    public sealed class StateMachinePerformanceTests
    {
        private sealed class TestState : StateBase
        {
            public int UpdateCount;
            public int EnterCount;
            public int ExitCount;
            public override void OnEnter() => EnterCount++;
            public override void OnUpdate(float deltaTime) => UpdateCount++;
            public override void OnExit() => ExitCount++;
        }

        // A second, distinct state type. StateMachineCore.AddState keys States by typeof(T), so
        // registering two instances of the same class collapses them onto one entry, and
        // SetState early-returns when the target type equals the current one: a machine built
        // from two TestStates can never transition, which is what the transition benchmarks
        // below used to measure.
        private sealed class TestStateB : StateBase
        {
            public int UpdateCount;
            public int EnterCount;
            public int ExitCount;
            public override void OnEnter() => EnterCount++;
            public override void OnUpdate(float deltaTime) => UpdateCount++;
            public override void OnExit() => ExitCount++;
        }

        [Test, Performance]
        public void Benchmark_StateMachine_Update_10k()
        {
            var sm = new StateMachine<StateBase>();
            var state = new TestState();
            sm.AddState(state);
            sm.Start<TestState>();

            Measure.Method(() =>
            {
                for (var i = 0; i < 10000; i++)
                {
                    sm.Update(0.016f);
                }
            })
            .WarmupCount(3)
            .MeasurementCount(10)
            .Run();

            Assert.Greater(state.UpdateCount, 0, "Update should have reached the current state's OnUpdate");
        }

        [Test, Performance]
        public void Benchmark_StateMachine_TransitionCheck_10k()
        {
            var sm = new StateMachine<StateBase>();
            var stateA = new TestState();
            var stateB = new TestStateB();
            var evaluations = 0;

            sm.AddState(stateA);
            sm.AddState(stateB);

            // Five transitions off the current state, none of which fire, so the measurement is
            // the cost of walking the transition list and evaluating every predicate.
            for (var i = 0; i < 5; i++)
                sm.AddTransition<TestState, TestStateB>(() => { evaluations++; return false; });

            sm.Start<TestState>();

            Measure.Method(() =>
            {
                for (var i = 0; i < 10000; i++)
                {
                    sm.Update(0.016f);
                }
            })
            .WarmupCount(3)
            .MeasurementCount(10)
            .Run();

            Assert.Greater(evaluations, 0, "Transition predicates should have been evaluated");
            Assert.AreEqual(0, stateB.EnterCount, "No transition condition returns true, so B is never entered");
        }

        [Test, Performance]
        public void Benchmark_StateTransitions_1k()
        {
            var sm = new StateMachine<StateBase>();
            var stateA = new TestState();
            var stateB = new TestStateB();
            var toggle = false;
            var transitions = 0;

            sm.AddState(stateA);
            sm.AddState(stateB);

            // Alternating between two distinct types is what makes these real transitions.
            // A -> A would be rejected by SetState's `stateType == CurrentStateTypeInternal`
            // guard before any OnExit/OnEnter/OnStateChanged work happened.
            sm.AddTransition<TestState, TestStateB>(() => toggle);
            sm.AddTransition<TestStateB, TestState>(() => !toggle);
            sm.Start<TestState>();
            sm.OnStateChanged += (from, to) => transitions++;

            Measure.Method(() =>
            {
                for (var i = 0; i < 1000; i++)
                {
                    toggle = true;
                    sm.Update(0.016f);
                    toggle = false;
                    sm.Update(0.016f);
                }
            })
            .WarmupCount(3)
            .MeasurementCount(10)
            .Run();

            Assert.Greater(transitions, 0, "The benchmark must actually change state");
            Assert.Greater(stateB.EnterCount, 0);
            Assert.Greater(stateA.ExitCount, 0);

            // The measured body ends on toggle == false, so the machine is back in TestState.
            // One controlled round trip pins the exact transition count independently of however
            // many warmup and measurement passes the harness chose to run — before the two
            // states were given distinct types this came out as 0.
            transitions = 0;
            toggle = true;
            sm.Update(0.016f);
            toggle = false;
            sm.Update(0.016f);
            Assert.AreEqual(2, transitions, "One toggle round trip is two state changes");
        }
    }
}
