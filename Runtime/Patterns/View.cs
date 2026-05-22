using System;
using Strada.Core.Patterns.Interfaces;
using UnityEngine;

namespace Strada.Core.Patterns
{
    /// <summary>
    /// Base class for all Views in the MVCS architecture.
    /// Manages visibility state and lifecycle events.
    /// </summary>
    public abstract class View : MonoBehaviour, IView
    {
        /// <summary>
        /// Gets a value indicating whether the view is currently visible (active).
        /// </summary>
        public bool IsVisible { get; private set; }

        /// <summary>
        /// Initializes the view state. Called by the framework during setup.
        /// </summary>
        public virtual void Initialize()
        {
            IsVisible = gameObject.activeSelf;
        }

        /// <summary>
        /// Shows the view (sets GameObject active).
        /// </summary>
        public virtual void Show()
        {
            gameObject.SetActive(true);
            IsVisible = true;
            OnShow();
        }

        /// <summary>
        /// Hides the view (sets GameObject inactive).
        /// </summary>
        public virtual void Hide()
        {
            gameObject.SetActive(false);
            IsVisible = false;
            OnHide();
        }

        /// <summary>
        /// Called when the view is shown.
        /// </summary>
        protected virtual void OnShow() { }

        /// <summary>
        /// Called when the view is hidden.
        /// </summary>
        protected virtual void OnHide() { }
    }

    /// <summary>
    /// Generic base class for Views that are bound to a specific Model type.
    /// </summary>
    /// <typeparam name="TModel">The type of the Model.</typeparam>
    public abstract class View<TModel> : View, IView<TModel> where TModel : IModel
    {
        /// <summary>
        /// Gets the current model instance.
        /// </summary>
        protected TModel Model { get; private set; }

        /// <summary>
        /// Sets the model for this view.
        /// </summary>
        /// <param name="model">The model instance.</param>
        public void SetModel(TModel model)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
            OnModelSet();
        }

        /// <summary>
        /// Updates the view with data from the model.
        /// </summary>
        /// <param name="model">The model instance to update from.</param>
        public virtual void UpdateView(TModel model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            Model = model;
            OnViewUpdate();
        }

        /// <summary>
        /// Called when the model is first set.
        /// </summary>
        protected virtual void OnModelSet() { }

        /// <summary>
        /// Called when the view needs to update its visual state from the model.
        /// </summary>
        protected virtual void OnViewUpdate() { }
    }

    /// <summary>
    /// Base class for Views that require cleanup logic (IDisposable).
    /// Automatically calls Dispose when the GameObject is destroyed.
    /// </summary>
    public abstract class DisposableView : View, IDisposableView
    {
        private bool _disposed;

        /// <summary>
        /// Unity OnDestroy callback. Calls Dispose().
        /// </summary>
        protected virtual void OnDestroy()
        {
            Dispose();
        }

        /// <summary>
        /// Disposes the view and suppresses finalization.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            OnDispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Called when the view is being disposed. Override to add cleanup logic.
        /// </summary>
        protected virtual void OnDispose() { }
    }
}
