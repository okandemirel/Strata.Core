using System;
using System.Collections.Generic;
using System.Linq;
using Strada.Core.Core;
using Strada.Core.Patterns.Interfaces;

namespace Strada.Core.Patterns
{
    /// <summary>
    /// Orchestrates the Patterns architecture.
    /// Manages the lifecycle, ticking, and updates of Controllers and Services.
    /// </summary>
    public sealed class PatternManager : IDisposable, ILoopRunner
    {
        private readonly List<IController> _controllers = new();
        private readonly List<IService> _services = new();
        private readonly List<IFixedTickController> _fixedControllers = new();
        private readonly List<ITickable> _tickables = new();
        private readonly List<IFixedTickable> _fixedTickables = new();
        private readonly List<ILateTickable> _lateTickables = new();
        private bool _disposed;
        private bool _registeredWithLoop;

        /// <summary>
        /// Gets the number of registered controllers.
        /// </summary>
        public int ControllerCount => _controllers.Count;

        /// <summary>
        /// Gets the number of registered services.
        /// </summary>
        public int ServiceCount => _services.Count;

        /// <summary>
        /// Registers a controller and adds it to relevant update loops.
        /// </summary>
        public void RegisterController(IController controller)
        {
            if (controller == null) throw new ArgumentNullException(nameof(controller));
            if (_controllers.Contains(controller))
                throw new InvalidOperationException(
                    $"Controller '{controller.GetType().Name}' is already registered.");

            _controllers.Add(controller);

            var alreadyFixedTicked = false;
            if (controller is IFixedTickController fixedController)
            {
                _fixedControllers.Add(fixedController);
                alreadyFixedTicked = true;
            }

            // IFixedTickController.FixedTick(float) and IFixedTickable.FixedTick(float) are the
            // same signature, so one C# method satisfies both interfaces. Registering the
            // controller in both lists made OnFixedUpdate invoke it twice per physics step.
            RegisterTickables(controller, alreadyFixedTicked);
        }

        /// <summary>
        /// Registers a service and adds it to relevant update loops.
        /// </summary>
        public void RegisterService(IService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            if (_services.Contains(service))
                throw new InvalidOperationException(
                    $"Service '{service.GetType().Name}' is already registered.");

            _services.Add(service);
            RegisterTickables(service);
        }

        private void RegisterTickables(object component, bool skipFixedTickable = false)
        {
            if (component is ITickable tickable)
                _tickables.Add(tickable);

            if (!skipFixedTickable && component is IFixedTickable fixedTickable)
                _fixedTickables.Add(fixedTickable);

            if (component is ILateTickable lateTickable)
                _lateTickables.Add(lateTickable);
        }

        /// <summary>
        /// Initializes all registered services and controllers.
        /// Services are initialized first, ordered by priority.
        /// </summary>
        public void Initialize()
        {
            var orderedServices = _services
                .OrderBy(s => s is IOrderedService ordered ? ordered.InitializationOrder : int.MaxValue);

            foreach (var service in orderedServices)
                service.Initialize();

            foreach (var controller in _controllers)
                controller.Initialize();

            RegisterWithPlayerLoop();
        }

        /// <summary>
        /// Registers update callbacks with the PlayerLoop.
        /// </summary>
        public void RegisterWithPlayerLoop()
        {
            if (_registeredWithLoop) return;
            _registeredWithLoop = true;

            PlayerLoop.RegisterUpdate(OnUpdate);
            PlayerLoop.RegisterLateUpdate(OnLateUpdate);
            PlayerLoop.RegisterFixedUpdate(OnFixedUpdate);
        }

        /// <summary>
        /// Unregisters update callbacks from the PlayerLoop.
        /// </summary>
        public void UnregisterFromPlayerLoop()
        {
            if (!_registeredWithLoop) return;
            _registeredWithLoop = false;

            PlayerLoop.UnregisterUpdate(OnUpdate);
            PlayerLoop.UnregisterLateUpdate(OnLateUpdate);
            PlayerLoop.UnregisterFixedUpdate(OnFixedUpdate);
        }

        public void Update(float deltaTime) => OnUpdate(deltaTime);
        public void FixedUpdate(float fixedDeltaTime) => OnFixedUpdate(fixedDeltaTime);
        public void LateUpdate(float deltaTime) => OnLateUpdate(deltaTime);

        public void OnUpdate(float deltaTime)
        {
            for (int i = 0; i < _tickables.Count; i++)
            {
                var tickable = _tickables[i];
                try { tickable.Tick(deltaTime); }
                catch (Exception ex) { LogComponentException(tickable, "Tick", ex); }
            }
        }

        public void OnFixedUpdate(float fixedDeltaTime)
        {
            for (int i = 0; i < _fixedControllers.Count; i++)
            {
                var controller = _fixedControllers[i];
                try { controller.FixedTick(fixedDeltaTime); }
                catch (Exception ex) { LogComponentException(controller, "FixedTick", ex); }
            }

            for (int i = 0; i < _fixedTickables.Count; i++)
            {
                var fixedTickable = _fixedTickables[i];
                try { fixedTickable.FixedTick(fixedDeltaTime); }
                catch (Exception ex) { LogComponentException(fixedTickable, "FixedTick", ex); }
            }
        }

        public void OnLateUpdate(float deltaTime)
        {
            for (int i = 0; i < _lateTickables.Count; i++)
            {
                var lateTickable = _lateTickables[i];
                try { lateTickable.LateTick(deltaTime); }
                catch (Exception ex) { LogComponentException(lateTickable, "LateTick", ex); }
            }
        }

        /// <summary>
        /// Reports a failure from a registered component.
        /// </summary>
        /// <remarks>
        /// The message names the offending type — with dozens of registered tickables the old
        /// text identified none of them. The exception goes through Debug.LogException rather
        /// than being interpolated into the message: Exception.ToString() drags the full stack
        /// trace (and, in a Mono build with symbols, absolute source paths) into release player
        /// logs, and allocates a multi-KB string every frame when a component throws every frame.
        /// </remarks>
        private static void LogComponentException(object component, string phase, Exception ex)
        {
            var typeName = component != null ? component.GetType().Name : "<null>";
            UnityEngine.Debug.LogError($"Exception in {typeName}.{phase}: {ex.Message}");
            UnityEngine.Debug.LogException(ex);
        }

        /// <summary>
        /// Retrieves a registered service of the specified type.
        /// </summary>
        public T GetService<T>() where T : class, IService
        {
            return _services.OfType<T>().FirstOrDefault();
        }

        /// <summary>
        /// Retrieves a registered controller of the specified type.
        /// </summary>
        public T GetController<T>() where T : class, IController
        {
            return _controllers.OfType<T>().FirstOrDefault();
        }

        /// <summary>
        /// Disposes the manager and all registered components.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            UnregisterFromPlayerLoop();

            // The tick loops already isolate every callback; teardown needs the same treatment.
            // A single throwing controller used to abort the remaining controllers, skip the
            // entire service loop and skip the list clears below — and because _disposed is set
            // first, Dispose could never be retried, so the whole registered component graph
            // leaked on one failure.
            try
            {
                for (int i = _controllers.Count - 1; i >= 0; i--)
                    DisposeComponent(_controllers[i]);

                for (int i = _services.Count - 1; i >= 0; i--)
                    DisposeComponent(_services[i]);
            }
            finally
            {
                _controllers.Clear();
                _services.Clear();
                _fixedControllers.Clear();
                _tickables.Clear();
                _fixedTickables.Clear();
                _lateTickables.Clear();
            }
        }

        private static void DisposeComponent(object component)
        {
            if (!(component is IDisposable disposable)) return;

            try { disposable.Dispose(); }
            catch (Exception ex) { LogComponentException(component, "Dispose", ex); }
        }
    }
}
