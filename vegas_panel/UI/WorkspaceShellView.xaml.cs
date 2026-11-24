using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ScriptPortal.Vegas;

namespace VegasSurround71.UI
{
    /// <summary>
    /// Top-level Resolve-style workspace shell. The 7.1 panner and the HOA / 3D
    /// Spatial view are created once and cached; the segmented switcher only
    /// toggles visibility, so each workspace keeps its own UI state.
    ///
    /// The shell owns the shared <see cref="WorkspaceContext"/> (selected track,
    /// playhead, loop region, project identity, diagnostics) while the two
    /// workspaces keep isolated state objects.
    /// </summary>
    public partial class WorkspaceShellView : UserControl
    {
        private readonly WorkspaceContext _context;
        private readonly SurroundPanelView _surroundView;
        private readonly HoaSpatialView _hoaView;
        private WorkspaceKind _current = WorkspaceKind.Surround71;
        private bool _constructed;
        private bool _switching;
        private bool _refreshingTracks;
        private int _knownTrackCount = -1;
        private DispatcherTimer _headerTimer;

        public WorkspaceShellView()
            : this(null)
        {
        }

        public WorkspaceShellView(Vegas vegas)
        {
            InitializeComponent();

            _context = new WorkspaceContext(vegas);
            _surroundView = new SurroundPanelView(vegas);
            _surroundView.WorkspaceContext = _context;
            _surroundView.ExtensionDisplayNameChanged += OnSurroundDisplayNameChanged;
            _hoaView = new HoaSpatialView(_context);

            WorkspaceHost.Children.Add(_surroundView);
            WorkspaceHost.Children.Add(_hoaView);
            _hoaView.Visibility = Visibility.Collapsed;

            _context.SelectedTrackChanged += OnSelectedTrackChanged;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;

            _constructed = true;
            ApplyWorkspace(WorkspaceKind.Surround71, false);
        }

        /// <summary>Raised when the extension display name changes.</summary>
        public event EventHandler ExtensionDisplayNameChanged;

        /// <summary>The current workspace.</summary>
        public WorkspaceKind CurrentWorkspace
        {
            get { return _current; }
        }

        /// <summary>The cached 7.1 workspace instance.</summary>
        public SurroundPanelView SurroundView
        {
            get { return _surroundView; }
        }

        /// <summary>The cached HOA / 3D Spatial workspace instance.</summary>
        public HoaSpatialView HoaView
        {
            get { return _hoaView; }
        }

        /// <summary>The shared project-level context.</summary>
        public WorkspaceContext Context
        {
            get { return _context; }
        }

        /// <summary>Current extension display name (owned by the 7.1 header).</summary>
        public string ExtensionDisplayName
        {
            get
            {
                return _surroundView == null
                    ? ExtensionSettings.CurrentDisplayName
                    : _surroundView.ExtensionDisplayName;
            }
        }

        /// <summary>Applies a display name to the live 7.1 header and dock tab.</summary>
        public void SetExtensionDisplayName(string displayName)
        {
            if (_surroundView != null)
            {
                _surroundView.SetExtensionDisplayName(displayName);
            }
        }

        /// <summary>Programmatically selects a workspace.</summary>
        public void ShowWorkspace(WorkspaceKind kind)
        {
            ApplyWorkspace(kind, true);
        }

        /// <summary>Refreshes the active workspace without touching the other.</summary>
        public void RefreshAll()
        {
            if (_context != null)
            {
                _context.RefreshProjectIdentity();
            }

            UpdateHeader();

            if (_current == WorkspaceKind.Surround71)
            {
                if (_surroundView != null)
                {
                    _surroundView.RefreshAll();
                }
            }
            else if (_hoaView != null)
            {
                _hoaView.RefreshAll();
            }
        }

        /// <summary>
        /// Refreshes both track selectors after VEGAS adds/removes a track.
        /// This is read-only: it never writes 7.1 automation or HOA state.
        /// </summary>
        public void NotifyTrackListChanged()
        {
            if (_refreshingTracks)
            {
                return;
            }

            _refreshingTracks = true;
            try
            {
                if (_surroundView != null)
                {
                    _surroundView.RefreshTracks();
                }

                if (_hoaView != null)
                {
                    _hoaView.RefreshTracks();
                }

                if (_context != null)
                {
                    _context.RefreshProjectIdentity();
                }

                _knownTrackCount = GetSafeTrackCount();
            }
            finally
            {
                _refreshingTracks = false;
            }

            UpdateHeader();
        }

        private int GetSafeTrackCount()
        {
            if (_context == null)
            {
                return -1;
            }

            Project project = _context.GetProject();
            if (project == null)
            {
                return -1;
            }

            try
            {
                return project.Tracks.Count;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>Resets both workspaces after a VEGAS project transition.</summary>
        public void ResetForProjectChange()
        {
            if (_surroundView != null)
            {
                _surroundView.ResetForProjectChange();
            }

            if (_hoaView != null)
            {
                _hoaView.ResetForProjectChange();
            }

            if (_context != null)
            {
                // Never carry a track object across a project boundary.
                _context.ClearSelectedTrack();
                _context.RefreshProjectIdentity();
                _context.WriteDiagnostic(
                    "Project changed — 7.1 and HOA workspaces reset.");
            }

            UpdateHeader();
        }

        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            if (_context != null)
            {
                _context.RefreshProjectIdentity();
                _knownTrackCount = GetSafeTrackCount();
            }

            UpdateHeader();

            if (_headerTimer == null)
            {
                _headerTimer = new DispatcherTimer(DispatcherPriority.Background);
                _headerTimer.Interval = TimeSpan.FromMilliseconds(500);
                _headerTimer.Tick += OnHeaderTimerTick;
            }

            _headerTimer.Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            if (_headerTimer != null)
            {
                _headerTimer.Stop();
                _headerTimer.Tick -= OnHeaderTimerTick;
                _headerTimer = null;
            }

            if (_context != null)
            {
                _context.SelectedTrackChanged -= OnSelectedTrackChanged;
            }

            if (_surroundView != null)
            {
                _surroundView.ExtensionDisplayNameChanged -= OnSurroundDisplayNameChanged;
            }
        }

        private void OnWorkspaceChecked(object sender, RoutedEventArgs args)
        {
            if (!_constructed || _switching)
            {
                return;
            }

            ApplyWorkspace(
                sender == WorkspaceHoaButton
                    ? WorkspaceKind.HoaSpatial
                    : WorkspaceKind.Surround71,
                true);
        }

        private void ApplyWorkspace(WorkspaceKind kind, bool log)
        {
            if (_switching)
            {
                return;
            }

            bool surround = kind == WorkspaceKind.Surround71;
            _switching = true;
            try
            {
                _current = kind;

                if (_surroundView != null)
                {
                    _surroundView.Visibility = surround
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }

                if (_hoaView != null)
                {
                    _hoaView.Visibility = surround
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                }

                // Checking a RadioButton raises Checked synchronously; the
                // _switching guard keeps this from re-entering ApplyWorkspace.
                if (surround && Workspace71Button != null)
                {
                    Workspace71Button.IsChecked = true;
                }
                else if (!surround && WorkspaceHoaButton != null)
                {
                    WorkspaceHoaButton.IsChecked = true;
                }

                UpdateHeader();
            }
            finally
            {
                _switching = false;
            }

            if (log && _context != null)
            {
                _context.WriteDiagnostic(
                    "Workspace switched to " + WorkspaceLabel(kind) +
                    ". No bus envelopes or track automation were modified.");

                if (kind == WorkspaceKind.HoaSpatial)
                {
                    _context.WriteDiagnostic(
                        "HOA workspace ready: AmbiX WAV/W64/FLAC export and " +
                        "stereo/binaural preview; IAMF / Eclipsa remains planned.");
                }
            }
        }

        private void OnSelectedTrackChanged(object sender, EventArgs args)
        {
            UpdateHeader();
        }

        private void OnHeaderTimerTick(object sender, EventArgs args)
        {
            // Fallback for hosts that do not raise TrackCountChanged: refresh the
            // selectors as soon as the project track count changes.
            int trackCount = GetSafeTrackCount();
            if (trackCount != _knownTrackCount)
            {
                _knownTrackCount = trackCount;
                NotifyTrackListChanged();
            }

            UpdateHeader();
        }

        private void OnSurroundDisplayNameChanged(object sender, EventArgs args)
        {
            EventHandler handler = ExtensionDisplayNameChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void UpdateHeader()
        {
            if (WorkspaceStatusText != null)
            {
                WorkspaceStatusText.Text = WorkspaceLabel(_current);
            }

            if (WorkspaceDot != null)
            {
                WorkspaceDot.Fill = new SolidColorBrush(
                    _current == WorkspaceKind.HoaSpatial
                        ? Color.FromRgb(0x2D, 0xD4, 0xBF)
                        : Color.FromRgb(0x38, 0xBD, 0xF8));
            }

            if (ProjectIdentityText == null || _context == null)
            {
                return;
            }

            string identity = _context.ProjectIdentity;
            if (_context.HasSelectedTrack)
            {
                string name = _context.SelectedTrackName;
                identity += "  ·  " + (string.IsNullOrWhiteSpace(name)
                    ? "Track " + (_context.SelectedTrackIndex + 1)
                    : "Track " + (_context.SelectedTrackIndex + 1) + " - " + name);
            }

            string transport = _context.DescribeTransport();
            if (!string.IsNullOrEmpty(transport))
            {
                identity += "  ·  " + transport;
            }

            ProjectIdentityText.Text = identity;
        }

        private static string WorkspaceLabel(WorkspaceKind kind)
        {
            return kind == WorkspaceKind.HoaSpatial
                ? "HOA / 3D Spatial"
                : "7.1 Panner";
        }
    }
}
