using System;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using ScriptPortal.Vegas;

namespace VegasSurround71.UI
{
    /// <summary>
    /// Dockable VEGAS host for the WPF soundstage view. The ElementHost keeps
    /// WPF rendering isolated while the DockableControl handles VEGAS docking.
    /// </summary>
    public sealed class HostPanel : DockableControl
    {
        private readonly WorkspaceShellView _shell;

        // Retained so host probes and diagnostics can still reach the 7.1 view.
        private readonly SurroundPanelView _view;
        private readonly Vegas _vegas;

        public HostPanel(string instanceName, Vegas vegas)
            : base(instanceName)
        {
            _vegas = vegas;
            SuspendLayout();
            BackColor = Color.FromArgb(30, 30, 30);
            DisplayName = ExtensionSettings.CurrentDisplayName;

            var elementHost = new ElementHost
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 30, 30)
            };

            _shell = new WorkspaceShellView(vegas);
            _view = _shell.SurroundView;
            _view.ExtensionDisplayNameChanged += OnViewDisplayNameChanged;
            elementHost.Child = _shell;
            Controls.Add(elementHost);
            ResumeLayout(true);

            if (_vegas != null)
            {
                _vegas.ProjectOpened += OnProjectLifecycleChanged;
                _vegas.ProjectClosed += OnProjectLifecycleChanged;
                _vegas.TrackCountChanged += OnTrackCountChanged;
            }

            Disposed += OnHostPanelDisposed;
        }

        /// <summary>Resets the WPF panel when VEGAS opens/closes a project.</summary>
        public void NotifyProjectChanged()
        {
            if (_shell == null)
            {
                return;
            }

            try
            {
                if (_shell.Dispatcher.CheckAccess())
                {
                    _shell.ResetForProjectChange();
                }
                else
                {
                    _shell.Dispatcher.BeginInvoke(
                        new Action(_shell.ResetForProjectChange));
                }
            }
            catch (Exception)
            {
                // A project transition must never take down the host.
            }
        }

        private void OnProjectLifecycleChanged(object sender, EventArgs args)
        {
            NotifyProjectChanged();
        }

        /// <summary>
        /// Called by VEGAS when a track is added or deleted. Rebuilding the
        /// selectors here prevents stale indices and dead COM wrappers.
        /// </summary>
        public void NotifyTrackListChanged()
        {
            if (_shell == null)
            {
                return;
            }

            try
            {
                if (_shell.Dispatcher.CheckAccess())
                {
                    _shell.NotifyTrackListChanged();
                }
                else
                {
                    _shell.Dispatcher.BeginInvoke(
                        new Action(_shell.NotifyTrackListChanged));
                }
            }
            catch (Exception)
            {
                // A track-list change must never take down the host.
            }
        }

        private void OnTrackCountChanged(object sender, EventArgs args)
        {
            NotifyTrackListChanged();
        }

        private void OnHostPanelDisposed(object sender, EventArgs args)
        {
            if (_vegas == null)
            {
                return;
            }

            try
            {
                _vegas.ProjectOpened -= OnProjectLifecycleChanged;
                _vegas.ProjectClosed -= OnProjectLifecycleChanged;
                _vegas.TrackCountChanged -= OnTrackCountChanged;
            }
            catch (Exception)
            {
                // Best effort during shutdown.
            }
        }

        /// <summary>The current extension display name shown on the dock tab.</summary>
        public string ExtensionDisplayName
        {
            get { return _shell == null ? DisplayName : _shell.ExtensionDisplayName; }
        }

        /// <summary>Applies a display name to the live header and dock title.</summary>
        public void ApplyExtensionDisplayName(string displayName)
        {
            if (_shell != null)
            {
                _shell.SetExtensionDisplayName(displayName);
            }

            if (!string.IsNullOrWhiteSpace(displayName))
            {
                DisplayName = displayName.Trim();
            }
        }

        private void OnViewDisplayNameChanged(object sender, EventArgs args)
        {
            DisplayName = _shell.ExtensionDisplayName;
        }

        /// <summary>Refreshes the active workspace's project/native state.</summary>
        public void RefreshStatus()
        {
            if (_shell != null)
            {
                _shell.RefreshAll();
            }
        }
    }
}
