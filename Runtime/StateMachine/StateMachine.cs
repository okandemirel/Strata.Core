using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Strada.Core.StateMachine
{
    public abstract class StateMachineCore<TState> where TState : class, IState
    {
        protected readonly Dictionary<Type, TState> States = new(8);
        protected readonly Dictionary<Type, List<Transition<TState>>> Transitions = new(8);
        protected readonly List<Transition<TState>> AnyTransitions = new(4);
        protected TState CurrentStateInternal;
        protected Type CurrentStateTypeInternal;
        protected bool IsTransitioningInternal;

        public TState CurrentState => CurrentStateInternal;
        public Type CurrentStateType => CurrentStateTypeInternal;
        public bool IsRunning => CurrentStateInternal != null;

        public event Action<TState, TState> OnStateChanged;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddState<T>(T state) where T : TState
        {
            OnStateAdded(state);
            States[typeof(T)] = state;

            // typeof(T) is the type written at the call site, not the type of the instance. When
            // states are added through a base-typed variable — a factory return, a foreach over a
            // List<TState>, a helper with a base-typed parameter — every registration collapses
            // onto that one key and silently overwrites the previous state, leaving
            // SetState<Concrete>() with nothing to find. Index by the runtime type as well so
            // concrete lookups always resolve.
            if (state != null)
            {
                var runtimeType = state.GetType();
                if (runtimeType != typeof(T))
                    States[runtimeType] = state;
            }
        }

        protected virtual void OnStateAdded(TState state) { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddTransition<TFrom, TTo>(Func<bool> condition) where TFrom : TState where TTo : TState
        {
            var fromType = typeof(TFrom);
            if (!Transitions.TryGetValue(fromType, out var list))
            {
                list = new List<Transition<TState>>(4);
                Transitions[fromType] = list;
            }

            list.Add(new Transition<TState>(typeof(TTo), condition));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddAnyTransition<TTo>(Func<bool> condition) where TTo : TState
        {
            AnyTransitions.Add(new Transition<TState>(typeof(TTo), condition));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Start<T>() where T : TState
        {
            if (CurrentStateInternal != null) return;
            SetState(typeof(T));
        }

        public void Update(float deltaTime)
        {
            if (CurrentStateInternal == null || IsTransitioningInternal) return;

            CheckTransitions();

            // The guard above ran before the transition pass. CheckTransitions can enter a new
            // state, and OnExit/OnEnter/OnStateChanged are all free to call Stop(), which nulls
            // CurrentStateInternal — dereferencing it again here would throw on the frame a
            // terminal state is entered. IsTransitioningInternal is already cleared by then.
            var state = CurrentStateInternal;
            if (state == null) return;

            state.OnUpdate(deltaTime);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetState<T>() where T : TState
        {
            SetState(typeof(T));
        }

        public void Stop()
        {
            if (CurrentStateInternal == null) return;

            CurrentStateInternal.OnExit();
            CurrentStateInternal = null;
            CurrentStateTypeInternal = null;
        }

        protected void SetState(Type stateType)
        {
            if (stateType == CurrentStateTypeInternal) return;
            if (!States.TryGetValue(stateType, out var newState))
            {
                Debug.LogWarning($"Attempted transition to unregistered state: {stateType}");
                return;
            }

            IsTransitioningInternal = true;
            var previousState = CurrentStateInternal;

            try
            {
                previousState?.OnExit();
                CurrentStateInternal = newState;
                CurrentStateTypeInternal = stateType;
                CurrentStateInternal.OnEnter();
                OnStateChanged?.Invoke(previousState, CurrentStateInternal);
            }
            finally
            {
                IsTransitioningInternal = false;
            }
        }

        private void CheckTransitions()
        {
            foreach (var transition in AnyTransitions)
            {
                if (transition.ToType != CurrentStateTypeInternal && Evaluate(transition))
                {
                    SetState(transition.ToType);
                    return;
                }
            }

            if (CurrentStateTypeInternal != null && Transitions.TryGetValue(CurrentStateTypeInternal, out var stateTransitions))
            {
                foreach (var transition in stateTransitions)
                {
                    if (Evaluate(transition))
                    {
                        SetState(transition.ToType);
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Evaluates a transition condition, treating a throwing condition as "not taken".
        /// </summary>
        /// <remarks>
        /// Conditions are arbitrary user delegates and nothing above this call wraps them: the
        /// machine is driven directly by caller code, not by PatternManager or the PlayerLoop.
        /// An escaping exception therefore aborted Update before the current state's OnUpdate
        /// ran, so the state machine stalled for as long as the fault persisted.
        /// </remarks>
        private bool Evaluate(in Transition<TState> transition)
        {
            var condition = transition.Condition;
            if (condition == null) return false;

            try
            {
                return condition();
            }
            catch (Exception ex)
            {
                var from = CurrentStateTypeInternal != null ? CurrentStateTypeInternal.Name : "<none>";
                var to = transition.ToType != null ? transition.ToType.Name : "<null>";
                Debug.LogError($"Transition condition {from} -> {to} threw; treating it as false.");
                Debug.LogException(ex);
                return false;
            }
        }
    }

    public sealed class StateMachine<TState> : StateMachineCore<TState> where TState : class, IState
    {
    }

    public sealed class StateMachine<TState, TContext> : StateMachineCore<TState> where TState : class, IState<TContext>
    {
        private readonly TContext _context;

        public TContext Context => _context;

        public StateMachine(TContext context)
        {
            _context = context;
        }

        protected override void OnStateAdded(TState state)
        {
            state.SetContext(_context);
        }
    }

    public readonly struct Transition<TState> where TState : class, IState
    {
        public readonly Type ToType;
        public readonly Func<bool> Condition;

        public Transition(Type toType, Func<bool> condition)
        {
            ToType = toType;
            Condition = condition;
        }
    }
}
