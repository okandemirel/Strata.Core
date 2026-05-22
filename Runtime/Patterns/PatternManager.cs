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

            if (controller is IFixedTickController fixedController)
                _fixedControllers.Add(fixedController);

            RegisterTickables(controller);
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

        private void RegisterTickables(object component)
        {
            if (component is ITickable tickable)
                _tickables.Add(tickable);

            if (component is IFixedTickable fixedTickable)
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
                try { _tickables[i].Tick(deltaTime); }
                catch (Exception ex) { UnityEngine.Debug.LogError($"Exception in pattern update: {ex}"); }
            }
        }

        public void OnFixedUpdate(float fixedDeltaTime)
        {
            for (int i = 0; i < _fixedControllers.Count; i++)
            {
                try { _fixedControllers[i].FixedTick(fixedDeltaTime); }
                catch (Exception ex) { UnityEngine.Debug.LogError($"Exception in pattern update: {ex}"); }
            }

            for (int i = 0; i < _fixedTickables.Count; i++)
            {
                try { _fixedTickables[i].FixedTick(fixedDeltaTime); }
                catch (Exception ex) { UnityEngine.Debug.LogError($"Exception in pattern update: {ex}"); }
            }
        }

        public void OnLateUpdate(float deltaTime)
        {
            for (int i = 0; i < _lateTickables.Count; i++)
            {
                try { _lateTickables[i].LateTick(deltaTime); }
                catch (Exception ex) { UnityEngine.Debug.LogError($"Exception in pattern update: {ex}"); }
            }
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

            for (int i = _controllers.Count - 1; i >= 0; i--)
            {
                if (_controllers[i] is IDisposable disposable)
                    disposable.Dispose();
            }

            for (int i = _services.Count - 1; i >= 0; i--)
            {
                if (_services[i] is IDisposable disposable)
                    disposable.Dispose();
            }

            _controllers.Clear();
            _services.Clear();
            _fixedControllers.Clear();
            _tickables.Clear();
            _fixedTickables.Clear();
            _lateTickables.Clear();
        }
    }
}
