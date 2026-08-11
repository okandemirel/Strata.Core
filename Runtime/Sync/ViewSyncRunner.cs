using UnityEngine;

namespace Strada.Core.Sync
{
    public enum ViewSyncMode
    {
        /// <summary>
        /// Only syncs bindings that were explicitly marked via
        /// <c>ComponentBinding&lt;T&gt;.MarkDirty()</c>. Nothing in the framework marks
        /// bindings for you, so this mode syncs NOTHING unless your own code calls
        /// MarkDirty — choose it only when you drive the flag yourself.
        /// </summary>
        DirtyOnly,

        /// <summary>
        /// Reads every bound component every frame and publishes the ones whose value
        /// actually changed. The safe default.
        /// </summary>
        ForceAll,

        /// <summary>No automatic syncing; call the registry yourself.</summary>
        Manual
    }

    public class ViewSyncRunner : MonoBehaviour
    {
        [Tooltip("Sync mode: DirtyOnly (reactive), ForceAll (every frame), or Manual (disabled)")]
        [SerializeField] private ViewSyncMode _syncMode = ViewSyncMode.ForceAll;

        private ViewRegistry _viewRegistry;
        private bool _warnedAboutDirtyOnly;

        /// <summary>
        /// Gets or sets the sync mode.
        /// </summary>
        public ViewSyncMode SyncMode
        {
            get => _syncMode;
            set => _syncMode = value;
        }

        public void Initialize(ViewRegistry viewRegistry)
        {
            _viewRegistry = viewRegistry;
        }

        private void LateUpdate()
        {
            if (_viewRegistry == null) return;

            switch (_syncMode)
            {
                case ViewSyncMode.DirtyOnly:
                    // Only visits bindings marked via MarkDirty(). Nothing in the framework
                    // marks them, so without caller-driven marking this silently syncs
                    // nothing — say so once rather than leaving the user to wonder.
                    if (!_warnedAboutDirtyOnly)
                    {
                        _warnedAboutDirtyOnly = true;
                        Debug.LogWarning(
                            "[Strada] ViewSyncRunner is in DirtyOnly mode. Bindings are only synced " +
                            "after ComponentBinding<T>.MarkDirty() is called; if your code never calls " +
                            "it, no view will update. Use ForceAll unless you drive the dirty flag.",
                            this);
                    }
                    _viewRegistry.SyncAll();
                    break;

                case ViewSyncMode.ForceAll:
                    // Force sync all view bindings (legacy behavior)
                    _viewRegistry.ForceSyncAll();
                    break;

                case ViewSyncMode.Manual:
                    // Do nothing - user will call sync manually
                    break;
            }
        }

        /// <summary>
        /// Manually trigger a sync of all views with dirty bindings.
        /// </summary>
        public void SyncDirty()
        {
            _viewRegistry?.SyncAll();
        }

        /// <summary>
        /// Manually trigger a force sync of all views regardless of dirty flag.
        /// </summary>
        public void ForceSync()
        {
            _viewRegistry?.ForceSyncAll();
        }
    }
}
