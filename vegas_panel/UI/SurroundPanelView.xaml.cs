using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using ScriptPortal.Vegas;
using IOPath = System.IO.Path;

namespace VegasSurround71.UI
{
    /// <summary>
    /// WPF 2D overhead soundstage, live 8-channel meters, pan controls and the
    /// 3-pass 7.1 render section. Hosted in VEGAS through HostPanel/ElementHost.
    /// </summary>
    public partial class SurroundPanelView : UserControl
    {
        private readonly List<MeterRow> _meters = new List<MeterRow>();
        private readonly Stack<PuckState> _undoStack = new Stack<PuckState>();
        private readonly List<PanPoint> _pendingPoints = new List<PanPoint>();
        private readonly List<SpeakerVisual> _speakerVisuals = new List<SpeakerVisual>();
        private List<VideoTemplateItem> _videoTemplates = new List<VideoTemplateItem>();
        private const double InitialPuckX = 0.0;
        private const double InitialPuckY = 0.70;
        private static readonly string PanelLogPath =
            IOPath.Combine(ExtensionSettings.SettingsDirectory, "panel.log");

        private Vegas _vegas;
        private AudioTrack _selectedTrack;
        private int _selectedTrackIndex = -1;
        private string _selectedTrackName = string.Empty;
        private int _lastTrackCount = -1;
        private bool _loaded;
        private bool _dragging;
        private bool _suppressControlEvents;
        private double _puckX = InitialPuckX;
        private double _puckY = InitialPuckY;
        private double _lfeSend = 0.25;
        private double _spread = 0.10;
        private double _roomCenterX;
        private double _roomCenterY;
        private double _roomRadius;
        private Ellipse _puckEllipse;
        private PuckState _lastCommitted;
        private DispatcherTimer _transportTimer;
        private DispatcherTimer _liveTimer;
        private LivePanSession _liveSession;
        private PuckState _liveStartState;
        private bool _liveDirty;
        private bool _liveApplied;
        private bool _liveFailed;
        private bool _liveLogged;
        private DateTime _lastLiveUiRefreshUtc;
        private bool _wasPlaying;
        private bool _hasPendingStatic;
        private PuckState _pendingStaticState;
        private DateTime _lastPendingSampleUtc;
        private bool _flushing;
        private VideoTemplateItem _selectedVideoTemplate;
        private DeliveryPreset _deliveryPreset = DeliveryPreset.FlacMkv;
        private bool _suppressDeliveryEvents;
        private string _videoTemplateSignature;
        private string _loopSignature;
        private string _extensionDisplayName = ExtensionSettings.CurrentDisplayName;
        private Ellipse _spreadHalo;
        private Project _activeProject;
        private IntPtr _activeProjectCom;
        private bool _resettingProject;
        private WorkspaceContext _workspaceContext;

        public SurroundPanelView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            _lastCommitted = new PuckState(_puckX, _puckY, _lfeSend, _spread);
        }

        /// <summary>Raised when the user saves a new extension display name.</summary>
        public event EventHandler ExtensionDisplayNameChanged;

        /// <summary>Raised when the selected audio track changes.</summary>
        public event EventHandler SelectedTrackChanged;

        public string ExtensionDisplayName
        {
            get { return _extensionDisplayName; }
        }

        /// <summary>
        /// Optional shared workspace context. When present, track selection is
        /// published to the other workspaces and diagnostics are mirrored to the
        /// shared transcript. The 7.1 pan/automation behavior is unchanged.
        /// </summary>
        public WorkspaceContext WorkspaceContext
        {
            get { return _workspaceContext; }
            set
            {
                _workspaceContext = value;
                PublishSelectedTrack();
            }
        }

        /// <summary>The audio track currently targeted by the 7.1 panner.</summary>
        public AudioTrack SelectedTrack
        {
            get { return _selectedTrack; }
        }

        /// <summary>Snapshot of the isolated 7.1 panner state (read-only copy).</summary>
        public Surround71State SnapshotState()
        {
            var state = new Surround71State
            {
                PuckX = _puckX,
                PuckY = _puckY,
                LfeSend = _lfeSend,
                Spread = _spread
            };
            return state;
        }

        private void PublishSelectedTrack()
        {
            EventHandler handler = SelectedTrackChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }

            if (_workspaceContext != null)
            {
                // Publish the stable index/name pair, never the COM wrapper.
                _workspaceContext.SetSelectedTrack(
                    _selectedTrackIndex, _selectedTrackName);
            }
        }

        private void SetSelectedTrackInternal(AudioTrack track)
        {
            if (track == null)
            {
                _selectedTrack = null;
                _selectedTrackIndex = -1;
                _selectedTrackName = string.Empty;
                return;
            }

            _selectedTrack = track;
            _selectedTrackIndex = track.Index;
            _selectedTrackName = track.Name ?? string.Empty;
        }

        private bool TryResolveAudioTrack(int index, out AudioTrack track)
        {
            track = null;
            if (_vegas == null || _vegas.Project == null || index < 0)
            {
                return false;
            }

            try
            {
                Project project = _vegas.Project;
                if (index >= project.Tracks.Count)
                {
                    return false;
                }

                Track raw = project.Tracks[index];
                if (raw is VideoTrack)
                {
                    return false;
                }

                track = raw as AudioTrack;
                return track != null;
            }
            catch (Exception)
            {
                track = null;
                return false;
            }
        }

        /// <summary>
        /// Rebuilds the 7.1 track selector after VEGAS reports a track count
        /// change. This only reads project state; it never writes automation.
        /// </summary>
        public void RefreshTracks()
        {
            if (!_loaded)
            {
                return;
            }

            if (!EnsureProjectCurrent())
            {
                return;
            }

            AbortLivePan();
            DiscardPendingPan("the VEGAS track list changed");
            PopulateTracks();
            UpdateBusStatus();
        }

        private void DiscardPendingPan(string reason)
        {
            bool hadPending = HasPending;
            _pendingPoints.Clear();
            _hasPendingStatic = false;
            ClearPendingStatus();

            if (hadPending)
            {
                Log("Pending pan discarded: " + reason + ".");
            }
        }

        public SurroundPanelView(Vegas vegas)
            : this()
        {
            _vegas = vegas;
            _activeProject = vegas == null ? null : vegas.Project;
            CaptureActiveProject();
        }

        /// <summary>Refreshes native status, bus status, track list, stage and meters.</summary>
        public void RefreshAll()
        {
            if (!_loaded)
            {
                return;
            }

            if (!EnsureProjectCurrent())
            {
                return;
            }

            PopulateTracks();
            PopulateVideoTemplates(false);
            UpdateBusStatus();
            UpdateNativeStatus();
            DrawRoom();
            UpdateMetersFromPuck();
            UpdateLoopRangeStatus();
            CaptureActiveProject();
        }

        /// <summary>Clears all project-bound UI state and reloads for the new project.</summary>
        public void ResetForProjectChange()
        {
            if (!_loaded || _resettingProject)
            {
                return;
            }

            _resettingProject = true;
            try
            {
                AbortLivePan();
                _undoStack.Clear();
                _pendingPoints.Clear();
                _hasPendingStatic = false;
                _pendingStaticState = CurrentState;
                _liveFailed = false;
                _liveLogged = false;
                _liveDirty = false;
                _liveApplied = false;
                _lastLiveUiRefreshUtc = DateTime.MinValue;
                _flushing = false;
                _dragging = false;
                SetSelectedTrackInternal(null);
                PublishSelectedTrack();
                _lastTrackCount = -1;
                _videoTemplates.Clear();
                _selectedVideoTemplate = null;
                _videoTemplateSignature = null;
                _loopSignature = null;
                CaptureActiveProject();
                _puckX = InitialPuckX;
                _puckY = InitialPuckY;
                _lastCommitted = CurrentState;
                _wasPlaying = IsTransportPlaying();

                try
                {
                    RoomCanvas.ReleaseMouseCapture();
                }
                catch (Exception)
                {
                    // Mouse capture is best effort during a project transition.
                }

                if (TrackCombo != null)
                {
                    TrackCombo.Items.Clear();
                    TrackCombo.SelectedItem = null;
                }

                if (VideoTemplateCombo != null)
                {
                    VideoTemplateCombo.Items.Clear();
                    VideoTemplateCombo.SelectedItem = null;
                }

                ClearPendingStatus();
                HideLiveStatus();

                PopulateTracks();
                PopulateVideoTemplates(true);
                UpdateBusStatus();
                UpdateNativeStatus();
                DrawRoom();
                UpdateMetersFromPuck();
                UpdateLoopRangeStatus();
                UpdateRenderCompatibility();

                SetStatus("New project detected — panel reset.");
                Log("Project changed — panel reset. Select a track and click Setup 7.1 Busses.");
            }
            catch (Exception ex)
            {
                Log("Project reset failed: " + HostScriptBridge.DescribeException(ex));
            }
            finally
            {
                _resettingProject = false;
            }
        }

        private bool EnsureProjectCurrent()
        {
            if (_vegas == null)
            {
                return false;
            }

            if (_resettingProject)
            {
                return true;
            }

            IntPtr currentCom;
            if (!TryGetProjectCom(out currentCom))
            {
                // The host is between projects; the ProjectOpened event will
                // perform the reset when the new project is ready.
                return true;
            }

            try
            {
                if (_activeProjectCom == IntPtr.Zero)
                {
                    _activeProjectCom = currentCom;
                    return true;
                }

                if (currentCom == _activeProjectCom)
                {
                    return true;
                }
            }
            finally
            {
                if (currentCom != IntPtr.Zero && currentCom != _activeProjectCom)
                {
                    Marshal.Release(currentCom);
                }
            }

            ResetForProjectChange();
            return false;
        }

        private void CaptureActiveProject()
        {
            _activeProject = _vegas == null ? null : _vegas.Project;

            IntPtr currentCom;
            if (TryGetProjectCom(out currentCom))
            {
                if (_activeProjectCom != IntPtr.Zero)
                {
                    Marshal.Release(_activeProjectCom);
                }

                _activeProjectCom = currentCom;
            }
            else if (_activeProjectCom != IntPtr.Zero)
            {
                Marshal.Release(_activeProjectCom);
                _activeProjectCom = IntPtr.Zero;
            }
        }

        private static bool TryGetProjectCom(out IntPtr projectCom)
        {
            projectCom = IntPtr.Zero;

            try
            {
                IProjectCOM raw = Project.COM;
                if (raw == null)
                {
                    return false;
                }

                projectCom = Marshal.GetIUnknownForObject(raw);
                return projectCom != IntPtr.Zero;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Updates the in-panel header without persisting (host use).</summary>
        public void SetExtensionDisplayName(string displayName)
        {
            ApplyExtensionDisplayName(displayName);
        }

        private void ApplyExtensionDisplayName(string displayName)
        {
            _extensionDisplayName = string.IsNullOrWhiteSpace(displayName)
                ? ExtensionSettings.DefaultDisplayName
                : displayName.Trim();

            if (PanelTitleText != null)
            {
                PanelTitleText.Text = _extensionDisplayName;
            }

            EventHandler handler = ExtensionDisplayNameChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            _puckX = InitialPuckX;
            _puckY = InitialPuckY;
            BuildMeterRows();
            PopulateCodecs();
            PopulateDeliveryPresets();
            CaptureActiveProject();
            ApplyExtensionDisplayName(_extensionDisplayName);
            UpdateLoopRangeStatus();

            if (string.IsNullOrEmpty(OutputPathBox.Text))
            {
                string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                OutputPathBox.Text = IOPath.Combine(
                    string.IsNullOrEmpty(videos) ? Directory.GetCurrentDirectory() : videos,
                    "VegasSurround71_Master.mkv");
            }

            LfeSlider.PreviewMouseLeftButtonUp += OnSliderReleased;
            SpreadSlider.PreviewMouseLeftButtonUp += OnSliderReleased;
            LfeSlider.Value = _lfeSend;
            SpreadSlider.Value = _spread;
            UpdateSliderLabels();

            DeliveryPresetCombo.SelectionChanged += OnDeliveryPresetChanged;
            CodecCombo.SelectionChanged += OnCodecSelectionChanged;
            VideoTemplateCombo.SelectionChanged += OnVideoTemplateChanged;

            ApplyPreset(DeliveryPreset.FlacMkv, false);
            PopulateVideoTemplates(true);
            UpdateRenderCompatibility();
            RefreshAll();

            _wasPlaying = IsTransportPlaying();
            _transportTimer = new DispatcherTimer(DispatcherPriority.Background);
            _transportTimer.Interval = TimeSpan.FromMilliseconds(150);
            _transportTimer.Tick += OnTransportTimerTick;
            _transportTimer.Start();

            _liveTimer = new DispatcherTimer(DispatcherPriority.Background);
            _liveTimer.Interval = TimeSpan.FromMilliseconds(33);
            _liveTimer.Tick += OnLiveTimerTick;
            _liveTimer.Start();

            if (LivePanToggle != null)
            {
                LivePanToggle.Checked += OnLivePanToggled;
                LivePanToggle.Unchecked += OnLivePanToggled;
            }

            Unloaded += OnPanelUnloaded;

            Log("Panel ready. Drag the puck; send levels update live on the mixer.");
            LogNativeStatus();
            if (_wasPlaying)
            {
                Log("Playback detected — live pan updates send levels without stopping playback.");
            }
        }

        private void OnPanelUnloaded(object sender, RoutedEventArgs args)
        {
            AbortLivePan();

            if (_liveTimer != null)
            {
                _liveTimer.Stop();
                _liveTimer.Tick -= OnLiveTimerTick;
                _liveTimer = null;
            }

            if (LivePanToggle != null)
            {
                LivePanToggle.Checked -= OnLivePanToggled;
                LivePanToggle.Unchecked -= OnLivePanToggled;
            }

            if (_transportTimer != null)
            {
                _transportTimer.Stop();
                _transportTimer.Tick -= OnTransportTimerTick;
                _transportTimer = null;
            }

            if (_activeProjectCom != IntPtr.Zero)
            {
                Marshal.Release(_activeProjectCom);
                _activeProjectCom = IntPtr.Zero;
            }
        }

        private bool EnsureBussesReady()
        {
            if (_vegas == null || _vegas.Project == null)
            {
                Log("No active VEGAS project.");
                return false;
            }

            if (BusMatrixManager.FindCanonicalBusses(_vegas.Project) != null)
            {
                return true;
            }

            const string message = "Click 'Setup 7.1 Busses' to route tracks first.";
            Log(message);
            SetStatus(message);
            return false;
        }

        private void OnNativeInfoClicked(object sender, RoutedEventArgs args)
        {
            if (NativeInfoPopup != null)
            {
                NativeInfoPopup.IsOpen = !NativeInfoPopup.IsOpen;
            }
        }

        private void OnToggleLogClicked(object sender, RoutedEventArgs args)
        {
            if (LogDrawer == null)
            {
                return;
            }

            bool expand = LogDrawer.Visibility != Visibility.Visible;
            LogDrawer.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            if (LogToggleButton != null)
            {
                LogToggleButton.Content = expand ? "▼ Diagnostic" : "▲ Diagnostic";
            }

            if (expand && LogBox != null)
            {
                LogBox.ScrollToEnd();
            }
        }

        private void SetStatus(string message)
        {
            if (StatusFooterText != null && !string.IsNullOrEmpty(message))
            {
                StatusFooterText.Text = message;
            }
        }

        private void BuildMeterRows()
        {
            MeterStack.Children.Clear();
            _meters.Clear();

            Style meterStyle = (Style)FindResource("GainMeter");

            for (int i = 0; i < SoundstageMath.ChannelLabels.Length; i++)
            {
                var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });

                var labelHost = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand,
                    ToolTip = "Open VEGAS FX Chain for [7.1] " +
                        SoundstageMath.ChannelLabels[i]
                };

                var dot = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
                    Margin = new Thickness(0, 0, 7, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };

                var label = new TextBlock
                {
                    Text = i < SoundstageMath.MeterLabels.Length
                        ? SoundstageMath.MeterLabels[i]
                        : SoundstageMath.ChannelLabels[i],
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6))
                };

                int channelIndex = i;
                labelHost.MouseLeftButtonUp += delegate { OpenBusFxChain(channelIndex); };
                labelHost.Children.Add(dot);
                labelHost.Children.Add(label);
                Grid.SetColumn(labelHost, 0);

                var bar = new ProgressBar
                {
                    Style = meterStyle,
                    Value = 0,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 8, 0)
                };
                Grid.SetColumn(bar, 1);

                var db = new TextBlock
                {
                    Text = "-\u221E",
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Right,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA))
                };
                Grid.SetColumn(db, 2);

                var linear = new TextBlock
                {
                    Text = "0.000",
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Right,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x71, 0x71, 0x7A)),
                    Margin = new Thickness(6, 0, 0, 0)
                };
                Grid.SetColumn(linear, 3);

                grid.Children.Add(labelHost);
                grid.Children.Add(bar);
                grid.Children.Add(db);
                grid.Children.Add(linear);
                MeterStack.Children.Add(grid);
                _meters.Add(new MeterRow(grid, label, dot, bar, db, linear));
            }
        }

        private void PopulateCodecs()
        {
            CodecCombo.Items.Clear();
            CodecCombo.Items.Add(new CodecItem("FLAC Lossless 7.1", AudioCodec71.Flac));
            CodecCombo.Items.Add(new CodecItem("Opus 7.1 (768k)", AudioCodec71.Opus));
            CodecCombo.Items.Add(new CodecItem("Vorbis 7.1 (q8)", AudioCodec71.Vorbis));
            CodecCombo.Items.Add(new CodecItem("WavPack Lossless 7.1", AudioCodec71.WavPack));
            CodecCombo.Items.Add(new CodecItem("LPCM 24-bit 7.1", AudioCodec71.Pcm24));
            CodecCombo.SelectedIndex = 0;
        }

        private void PopulateDeliveryPresets()
        {
            _suppressDeliveryEvents = true;
            try
            {
                DeliveryPresetCombo.Items.Clear();
                foreach (DeliveryPreset preset in DeliveryPresets.All)
                {
                    DeliveryPresetCombo.Items.Add(new PresetItem(preset));
                }

                DeliveryPresetCombo.SelectedIndex = 0;
                _deliveryPreset = DeliveryPreset.FlacMkv;
            }
            finally
            {
                _suppressDeliveryEvents = false;
            }
        }

        private void OnDeliveryPresetChanged(object sender, SelectionChangedEventArgs args)
        {
            if (_suppressDeliveryEvents)
            {
                return;
            }

            var item = DeliveryPresetCombo.SelectedItem as PresetItem;
            if (item == null)
            {
                return;
            }

            ApplyPreset(item.Preset, true);
        }

        private void OnCodecSelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (_suppressDeliveryEvents)
            {
                return;
            }

            var item = CodecCombo.SelectedItem as CodecItem;
            if (item == null)
            {
                return;
            }

            ApplyPreset(DeliveryPresets.ForCodec(item.Codec), true);
        }

        private void ApplyPreset(DeliveryPreset preset, bool updateStatus)
        {
            _deliveryPreset = preset;

            _suppressDeliveryEvents = true;
            try
            {
                int presetIndex = Array.IndexOf(DeliveryPresets.All, preset);
                if (presetIndex >= 0)
                {
                    DeliveryPresetCombo.SelectedIndex = presetIndex;
                }

                int codecIndex = FindCodecIndex(DeliveryPresets.Codec(preset));
                if (codecIndex >= 0)
                {
                    CodecCombo.SelectedIndex = codecIndex;
                }
            }
            finally
            {
                _suppressDeliveryEvents = false;
            }

            SyncOutputExtension(DeliveryPresets.Extension(preset));
            if (updateStatus)
            {
                UpdateRenderCompatibility();
            }
        }

        private int FindCodecIndex(AudioCodec71 codec)
        {
            for (int i = 0; i < CodecCombo.Items.Count; i++)
            {
                var item = CodecCombo.Items[i] as CodecItem;
                if (item != null && item.Codec == codec)
                {
                    return i;
                }
            }

            return -1;
        }

        private void SyncOutputExtension(string extension)
        {
            string path = OutputPathBox.Text == null ? string.Empty : OutputPathBox.Text.Trim();
            if (string.IsNullOrEmpty(path))
            {
                string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                path = IOPath.Combine(
                    string.IsNullOrEmpty(videos) ? Directory.GetCurrentDirectory() : videos,
                    "VegasSurround71_Master" + extension);
                OutputPathBox.Text = path;
                return;
            }

            string directory = IOPath.GetDirectoryName(path);
            string stem = IOPath.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(stem))
            {
                stem = "VegasSurround71_Master";
            }

            if (string.IsNullOrEmpty(directory))
            {
                directory = Directory.GetCurrentDirectory();
            }

            OutputPathBox.Text = IOPath.Combine(directory, stem + extension);
        }

        private void PopulateVideoTemplates(bool force)
        {
            if (_vegas == null || _vegas.Project == null)
            {
                _videoTemplates.Clear();
                VideoTemplateCombo.Items.Clear();
                _selectedVideoTemplate = null;
                return;
            }

            Project project = _vegas.Project;
            string signature = project.Video.Width + "x" + project.Video.Height +
                "@" + project.Video.FrameRate.ToString("0.###", CultureInfo.InvariantCulture) +
                "|" + _vegas.Renderers.Count;
            if (!force && signature == _videoTemplateSignature &&
                _videoTemplates.Count > 0)
            {
                return;
            }

            _videoTemplateSignature = signature;
            string previous = _selectedVideoTemplate == null
                ? null
                : _selectedVideoTemplate.DisplayName;

            _videoTemplates = RenderOrchestrator.BuildVideoTemplateCatalog(
                _vegas, project);

            _suppressDeliveryEvents = true;
            try
            {
                VideoTemplateCombo.Items.Clear();
                foreach (VideoTemplateItem item in _videoTemplates)
                {
                    VideoTemplateCombo.Items.Add(item);
                }

                VideoTemplateItem choose = null;
                if (!string.IsNullOrEmpty(previous))
                {
                    foreach (VideoTemplateItem item in _videoTemplates)
                    {
                        if (string.Equals(item.DisplayName, previous,
                            StringComparison.Ordinal))
                        {
                            choose = item;
                            break;
                        }
                    }
                }

                if (choose == null)
                {
                    foreach (VideoTemplateItem item in _videoTemplates)
                    {
                        if (DeliveryPresets.SupportsVideo(_deliveryPreset, item.Family))
                        {
                            choose = item;
                            break;
                        }
                    }
                }

                if (choose == null && _videoTemplates.Count > 0)
                {
                    choose = _videoTemplates[0];
                }

                VideoTemplateCombo.SelectedItem = choose;
                _selectedVideoTemplate = choose;
            }
            finally
            {
                _suppressDeliveryEvents = false;
            }

            UpdateRenderCompatibility();
        }

        private void OnVideoTemplateChanged(object sender, SelectionChangedEventArgs args)
        {
            if (_suppressDeliveryEvents)
            {
                return;
            }

            _selectedVideoTemplate = VideoTemplateCombo.SelectedItem as VideoTemplateItem;
            UpdateRenderCompatibility();
        }

        private void UpdateRenderCompatibility()
        {
            bool warning;
            string error = ComputeRenderError(out warning);

            if (error != null)
            {
                RenderStatusText.Text = error;
                RenderButton.IsEnabled = false;
                SetReadinessBadge(error, warning);
                SetStatus(error);
                return;
            }

            RenderButton.IsEnabled = true;
            RenderStatusText.Text = _selectedVideoTemplate.DisplayName + "  ->  " +
                DeliveryPresets.DisplayName(_deliveryPreset);
            SetReadinessBadge(null, false);
        }

        private string ComputeRenderError(out bool warning)
        {
            warning = false;

            if (_selectedVideoTemplate == null)
            {
                warning = true;
                return "Select a video template for Pass 1.";
            }

            string compatibility = DeliveryPresets.CompatibilityError(
                _deliveryPreset, _selectedVideoTemplate.Family);
            if (compatibility != null)
            {
                return compatibility;
            }

            var codecItem = CodecCombo.SelectedItem as CodecItem;
            if (codecItem == null)
            {
                warning = true;
                return "Choose an output codec.";
            }

            if (!NativeBridge.IsCodecSupported(codecItem.Codec))
            {
                return "Unsupported output codec.";
            }

            string output = OutputPathBox.Text == null
                ? string.Empty
                : OutputPathBox.Text.Trim();
            string expectedExtension = DeliveryPresets.Extension(_deliveryPreset);
            if (output.Length > 0 &&
                !string.Equals(IOPath.GetExtension(output), expectedExtension,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "The " + DeliveryPresets.DisplayName(_deliveryPreset) +
                    " preset requires a " + expectedExtension + " output file.";
            }

            if (RenderLoopOnlyCheck.IsChecked == true)
            {
                long start;
                long length;
                if (!TryGetLoopRange(out start, out length) || length <= 0)
                {
                    warning = true;
                    return "Set In/Out loop points on the VEGAS timeline first.";
                }
            }

            return null;
        }

        private void SetReadinessBadge(string error, bool warning)
        {
            if (ReadinessBadgeBorder == null || ReadinessBadgeText == null)
            {
                return;
            }

            if (error == null)
            {
                ReadinessBadgeBorder.Background =
                    new SolidColorBrush(Color.FromRgb(0x12, 0x35, 0x1F));
                ReadinessBadgeBorder.BorderBrush =
                    new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                ReadinessBadgeText.Foreground =
                    new SolidColorBrush(Color.FromRgb(0x86, 0xEF, 0xAC));
                ReadinessBadgeText.Text = "Ready";
                ReadinessBadgeBorder.ToolTip =
                    "Render settings are valid. E-AC-3 presets produce a 5.1 compatibility downmix.";
                return;
            }

            if (warning)
            {
                ReadinessBadgeBorder.Background =
                    new SolidColorBrush(Color.FromRgb(0x3A, 0x2A, 0x12));
                ReadinessBadgeBorder.BorderBrush =
                    new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                ReadinessBadgeText.Foreground =
                    new SolidColorBrush(Color.FromRgb(0xFC, 0xD3, 0x4D));
                ReadinessBadgeText.Text = "Setup Needed";
            }
            else
            {
                ReadinessBadgeBorder.Background =
                    new SolidColorBrush(Color.FromRgb(0x3A, 0x15, 0x15));
                ReadinessBadgeBorder.BorderBrush =
                    new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                ReadinessBadgeText.Foreground =
                    new SolidColorBrush(Color.FromRgb(0xFC, 0xA5, 0xA5));
                ReadinessBadgeText.Text = "Not Ready";
            }

            ReadinessBadgeBorder.ToolTip = error;
        }

        private bool TryGetLoopRange(out long startNanos, out long lengthNanos)
        {
            startNanos = 0L;
            lengthNanos = 0L;

            try
            {
                TransportControl transport = _vegas == null ? null : _vegas.Transport;
                if (transport == null ||
                    transport.LoopRegionStart == null ||
                    transport.LoopRegionLength == null)
                {
                    return false;
                }

                startNanos = transport.LoopRegionStart.Nanos;
                lengthNanos = transport.LoopRegionLength.Nanos;
                return lengthNanos > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void UpdateLoopRangeStatus()
        {
            if (LoopRangeText == null)
            {
                return;
            }

            long start;
            long length;
            bool hasLoop = TryGetLoopRange(out start, out length);
            string signature = hasLoop
                ? start.ToString(CultureInfo.InvariantCulture) + ":" +
                  length.ToString(CultureInfo.InvariantCulture)
                : "none";
            bool changed = !string.Equals(signature, _loopSignature,
                StringComparison.Ordinal);
            _loopSignature = signature;

            if (!hasLoop)
            {
                LoopRangeText.Text = "No loop region set";
                LoopRangeText.Foreground =
                    new SolidColorBrush(Color.FromRgb(0xD7, 0x9B, 0x00));
            }
            else
            {
                Timecode loopStart = Timecode.FromNanos(start);
                Timecode loopEnd = Timecode.FromNanos(start + length);
                LoopRangeText.Text = "Loop: " + loopStart + " -> " + loopEnd;
                LoopRangeText.Foreground =
                    new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0));
            }

            // Re-evaluate the export button when In/Out points appear or move
            // while the loop-only option is active.
            if (changed && RenderLoopOnlyCheck != null &&
                RenderLoopOnlyCheck.IsChecked == true)
            {
                UpdateRenderCompatibility();
            }
        }

        private void OnRenderLoopChanged(object sender, RoutedEventArgs args)
        {
            UpdateLoopRangeStatus();
            UpdateRenderCompatibility();
        }

        private void UpdateNativeStatus()
        {
            string error;
            bool available = NativeBridge.IsNativeAvailable(out error);
            string info =
                "Managed: " + typeof(ExtensionSettings).Assembly.GetName().Name + ".dll" +
                Environment.NewLine +
                "Native: " + NativeBridge.DllFileName + Environment.NewLine +
                "ABI: ChannelGains71 " + ChannelGains71.MarshalSize + " bytes" +
                Environment.NewLine +
                "Status: " + (available ? "loaded" : "NOT loaded — " + error);

            if (NativeInfoText != null)
            {
                NativeInfoText.Text = info;
            }

            if (NativeInfoButton != null)
            {
                NativeInfoButton.ToolTip = info;
            }
        }

        private void LogNativeStatus()
        {
            string error;
            if (NativeBridge.IsNativeAvailable(out error))
            {
                Log(string.Format(CultureInfo.InvariantCulture,
                    "Native core loaded: {0} ({1}-byte ChannelGains71).",
                    NativeBridge.DllFileName, ChannelGains71.MarshalSize));
            }
            else
            {
                Log("Native core NOT loaded: " + error);
            }
        }

        private void UpdateBusStatus()
        {
            if (BusStatusText == null || BusStatusPill == null || BusStatusDot == null)
            {
                return;
            }

            Project project = _vegas == null ? null : _vegas.Project;
            if (project == null)
            {
                BusStatusText.Text = "Busses 0/8";
                BusStatusText.Foreground =
                    new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA));
                BusStatusDot.Fill =
                    new SolidColorBrush(Color.FromRgb(0x71, 0x71, 0x7A));
                BusStatusPill.Background =
                    new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F));
                BusStatusPill.BorderBrush =
                    new SolidColorBrush(Color.FromRgb(0x32, 0x32, 0x38));
                BusStatusPill.ToolTip = "No active project.";
                if (BusModeText != null)
                {
                    BusModeText.Text = "Bus mode —";
                }

                return;
            }

            AudioBusTrack[] busses = BusMatrixManager.FindCanonicalBusses(project);
            bool ready = busses != null && busses.Length == Bus71Layout.BusCount;

            if (ready)
            {
                BusStatusText.Text = "8/8 Busses Ready";
                BusStatusText.Foreground =
                    new SolidColorBrush(Color.FromRgb(0x86, 0xEF, 0xAC));
                BusStatusDot.Fill =
                    new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                BusStatusPill.Background =
                    new SolidColorBrush(Color.FromRgb(0x12, 0x35, 0x1F));
                BusStatusPill.BorderBrush =
                    new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                BusStatusPill.ToolTip = "Eight canonical 7.1 busses are ready.";
            }
            else
            {
                BusStatusText.Text = "Busses 0/8";
                BusStatusText.Foreground =
                    new SolidColorBrush(Color.FromRgb(0xFC, 0xD3, 0x4D));
                BusStatusDot.Fill =
                    new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                BusStatusPill.Background =
                    new SolidColorBrush(Color.FromRgb(0x3A, 0x2A, 0x12));
                BusStatusPill.BorderBrush =
                    new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                BusStatusPill.ToolTip = "Click 'Setup 7.1 Busses' to route tracks first.";
            }

            if (BusModeText != null)
            {
                BusModeText.Text = "Bus mode: " + project.Audio.MasterBusMode;
            }
        }

        private void PopulateTracks()
        {
            if (_vegas == null || _vegas.Project == null)
            {
                return;
            }

            // Only use cached index/name values. Reading a deleted track's COM
            // wrapper from the list or a timer can crash the host.
            string previousName = _selectedTrackName;
            int previousIndex = _selectedTrackIndex;
            AudioTrack chooseTrack = null;

            _suppressControlEvents = true;
            try
            {
                Project project = _vegas.Project;
                TrackCombo.Items.Clear();
                foreach (Track track in project.Tracks)
                {
                    var audio = track as AudioTrack;
                    if (audio == null || track is VideoTrack)
                    {
                        // Video tracks are not routing targets for the 7.1
                        // send envelopes, so they never enter the selector.
                        continue;
                    }

                    TrackCombo.Items.Add(new TrackItem(
                        audio.Index + 1, audio.Index, audio.Name));
                }

                _lastTrackCount = project.Tracks.Count;

                TrackItem choose = null;
                if (!string.IsNullOrEmpty(previousName))
                {
                    foreach (TrackItem item in TrackCombo.Items)
                    {
                        if (string.Equals(item.Name, previousName,
                            StringComparison.Ordinal))
                        {
                            choose = item;
                            break;
                        }
                    }
                }

                if (choose == null && previousIndex >= 0)
                {
                    foreach (TrackItem item in TrackCombo.Items)
                    {
                        if (item.Index != previousIndex)
                        {
                            continue;
                        }

                        // Do not silently adopt a different track that shifted
                        // into the deleted track's index.
                        if (string.IsNullOrEmpty(previousName) ||
                            string.Equals(item.Name, previousName,
                                StringComparison.Ordinal))
                        {
                            choose = item;
                        }

                        break;
                    }
                }

                if (choose == null)
                {
                    foreach (TrackItem item in TrackCombo.Items)
                    {
                        AudioTrack audio;
                        if (TryResolveAudioTrack(item.Index, out audio) &&
                            audio.Selected)
                        {
                            choose = item;
                            break;
                        }
                    }
                }

                if (choose == null && TrackCombo.Items.Count > 0)
                {
                    choose = (TrackItem)TrackCombo.Items[0];
                }

                TrackCombo.SelectedItem = choose;
                if (choose != null)
                {
                    TryResolveAudioTrack(choose.Index, out chooseTrack);
                }
            }
            finally
            {
                _suppressControlEvents = false;
            }

            SetSelectedTrackInternal(chooseTrack);
            PublishSelectedTrack();
        }

        /// <summary>
        /// Returns a fresh AudioTrack for the cached selection. If the track list
        /// changed since it was populated, the selector is rebuilt first.
        /// </summary>
        private bool EnsureSelectedTrackValid()
        {
            if (_vegas == null || _vegas.Project == null)
            {
                return false;
            }

            try
            {
                if (_vegas.Project.Tracks.Count != _lastTrackCount)
                {
                    // The selection may now point at a shifted or deleted
                    // track: never flush queued pan data onto it.
                    AbortLivePan();
                    DiscardPendingPan("the VEGAS track list changed");
                    PopulateTracks();
                }
            }
            catch (Exception)
            {
                SetSelectedTrackInternal(null);
                return false;
            }

            AudioTrack fresh;
            if (!TryResolveAudioTrack(_selectedTrackIndex, out fresh))
            {
                SetSelectedTrackInternal(null);
                PublishSelectedTrack();
                return false;
            }

            SetSelectedTrackInternal(fresh);
            return true;
        }

        private void DrawRoom()
        {
            if (!_loaded)
            {
                return;
            }

            RoomCanvas.Children.Clear();
            _speakerVisuals.Clear();
            _spreadHalo = null;

            double width = RoomCanvas.ActualWidth;
            double height = RoomCanvas.ActualHeight;
            if (width < 80 || height < 80)
            {
                return;
            }

            // Reserve space below the room for the LFE node label so it can
            // never be clipped by the canvas edge.
            double availableHeight = Math.Max(80.0, height - 38.0);
            _roomRadius = Math.Max(40.0,
                Math.Min(width * 0.40, availableHeight * 0.46));
            _roomCenterX = width / 2.0;
            _roomCenterY = height / 2.0 + 4.0;

            double maxCenterY = height - 36.0 - _roomRadius * 0.92;
            if (_roomCenterY > maxCenterY)
            {
                _roomCenterY = Math.Max(_roomRadius + 12.0, maxCenterY);
            }

            var roomFill = new RadialGradientBrush();
            roomFill.GradientStops.Add(new GradientStop(
                Color.FromRgb(0x1B, 0x1B, 0x22), 0.0));
            roomFill.GradientStops.Add(new GradientStop(
                Color.FromRgb(0x14, 0x14, 0x17), 1.0));

            var room = new Ellipse
            {
                Width = _roomRadius * 2.0,
                Height = _roomRadius * 2.0,
                Stroke = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)),
                StrokeThickness = 1.5,
                Fill = roomFill
            };
            Canvas.SetLeft(room, _roomCenterX - _roomRadius);
            Canvas.SetTop(room, _roomCenterY - _roomRadius);
            RoomCanvas.Children.Add(room);

            // Faint concentric distance rings.
            for (int ring = 1; ring <= 3; ring++)
            {
                double factor = ring / 4.0;
                var ringEllipse = new Ellipse
                {
                    Width = _roomRadius * 2.0 * factor,
                    Height = _roomRadius * 2.0 * factor,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x31)),
                    StrokeThickness = 1,
                    Opacity = 0.65
                };
                Canvas.SetLeft(ringEllipse, _roomCenterX - _roomRadius * factor);
                Canvas.SetTop(ringEllipse, _roomCenterY - _roomRadius * factor);
                RoomCanvas.Children.Add(ringEllipse);
            }

            var outerRing = new Ellipse
            {
                Width = _roomRadius * 1.82,
                Height = _roomRadius * 1.82,
                Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x31)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 4 },
                Opacity = 0.8
            };
            Canvas.SetLeft(outerRing, _roomCenterX - _roomRadius * 0.91);
            Canvas.SetTop(outerRing, _roomCenterY - _roomRadius * 0.91);
            RoomCanvas.Children.Add(outerRing);

            // Translucent spread halo, updated live from the Spread slider.
            var haloFill = new RadialGradientBrush();
            haloFill.GradientStops.Add(new GradientStop(
                Color.FromArgb(42, 0x38, 0xBD, 0xF8), 0.0));
            haloFill.GradientStops.Add(new GradientStop(
                Color.FromArgb(0, 0x38, 0xBD, 0xF8), 1.0));

            _spreadHalo = new Ellipse
            {
                IsHitTestVisible = false,
                Fill = haloFill,
                Stroke = new SolidColorBrush(Color.FromArgb(95, 0x38, 0xBD, 0xF8)),
                StrokeThickness = 1
            };
            RoomCanvas.Children.Add(_spreadHalo);

            foreach (SpeakerNode speaker in SoundstageMath.Speakers)
            {
                StagePoint point = SoundstageMath.ToScreen(
                    speaker.X, speaker.Y, _roomCenterX, _roomCenterY, _roomRadius);

                var line = new Line
                {
                    X1 = _roomCenterX,
                    Y1 = _roomCenterY,
                    X2 = point.X,
                    Y2 = point.Y,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x2B)),
                    StrokeThickness = 1,
                    Opacity = 0.7
                };
                RoomCanvas.Children.Add(line);

                bool lfe = speaker.Channel == "LFE";
                Color nodeColor = lfe
                    ? Color.FromRgb(0xF5, 0x9E, 0x0B)
                    : Color.FromRgb(0x00, 0x7A, 0xCC);

                var glow = new DropShadowEffect
                {
                    Color = lfe
                        ? Color.FromRgb(0xF5, 0x9E, 0x0B)
                        : Color.FromRgb(0x38, 0xBD, 0xF8),
                    BlurRadius = 14,
                    ShadowDepth = 0,
                    Opacity = 0.0
                };

                var node = new Ellipse
                {
                    Width = 17,
                    Height = 17,
                    Fill = new SolidColorBrush(nodeColor),
                    Stroke = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)),
                    StrokeThickness = 1,
                    Effect = glow,
                    Opacity = 0.55
                };
                Canvas.SetLeft(node, point.X - 8.5);
                Canvas.SetTop(node, point.Y - 8.5);
                RoomCanvas.Children.Add(node);

                var label = new TextBlock
                {
                    Text = speaker.Label,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(lfe
                        ? Color.FromRgb(0xF5, 0x9E, 0x0B)
                        : Color.FromRgb(0xE6, 0xE6, 0xE6)),
                    Opacity = 0.65
                };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double labelWidth = label.DesiredSize.Width > 0.0
                    ? label.DesiredSize.Width : 24.0;
                double labelHeight = label.DesiredSize.Height > 0.0
                    ? label.DesiredSize.Height : 15.0;
                double labelLeft = point.X - labelWidth / 2.0;
                double labelTop = point.Y + 10.0;

                if (lfe && labelTop + labelHeight > height - 2.0)
                {
                    labelTop = point.Y - 10.0 - labelHeight;
                }

                labelLeft = Math.Max(2.0,
                    Math.Min(labelLeft, width - labelWidth - 2.0));
                labelTop = Math.Max(2.0,
                    Math.Min(labelTop, height - labelHeight - 2.0));
                Canvas.SetLeft(label, labelLeft);
                Canvas.SetTop(label, labelTop);
                RoomCanvas.Children.Add(label);

                _speakerVisuals.Add(new SpeakerVisual(
                    speaker.Channel, node, label, glow));
            }

            // Subtle top-down listener marker at (0,0).
            var listenerRing = new Ellipse
            {
                Width = 22,
                Height = 22,
                Stroke = new SolidColorBrush(Color.FromArgb(140, 0x38, 0xBD, 0xF8)),
                StrokeThickness = 1,
                Fill = new SolidColorBrush(Color.FromArgb(24, 0x38, 0xBD, 0xF8))
            };
            Canvas.SetLeft(listenerRing, _roomCenterX - 11);
            Canvas.SetTop(listenerRing, _roomCenterY - 11);
            RoomCanvas.Children.Add(listenerRing);

            var listenerDot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6))
            };
            Canvas.SetLeft(listenerDot, _roomCenterX - 3.5);
            Canvas.SetTop(listenerDot, _roomCenterY - 3.5);
            RoomCanvas.Children.Add(listenerDot);

            var frontMarker = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M -5 2 L 0 -6 L 5 2"),
                Stroke = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(frontMarker, _roomCenterX);
            Canvas.SetTop(frontMarker, _roomCenterY - 15);
            RoomCanvas.Children.Add(frontMarker);

            _puckEllipse = new Ellipse
            {
                Width = 22,
                Height = 22,
                Fill = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC)),
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = 2,
                IsHitTestVisible = false,
                Effect = new DropShadowEffect
                {
                    Color = Color.FromRgb(0x38, 0xBD, 0xF8),
                    BlurRadius = 16,
                    ShadowDepth = 0,
                    Opacity = 0.85
                }
            };
            RoomCanvas.Children.Add(_puckEllipse);
            PositionPuck();
        }

        private void PositionPuck()
        {
            if (_puckEllipse == null)
            {
                return;
            }

            StagePoint point = SoundstageMath.ToScreen(
                _puckX, _puckY, _roomCenterX, _roomCenterY, _roomRadius);
            Canvas.SetLeft(_puckEllipse, point.X - 11);
            Canvas.SetTop(_puckEllipse, point.Y - 11);
            UpdateSpreadHalo(point);
        }

        private void UpdateSpreadHalo()
        {
            if (_puckEllipse == null)
            {
                return;
            }

            UpdateSpreadHalo(SoundstageMath.ToScreen(
                _puckX, _puckY, _roomCenterX, _roomCenterY, _roomRadius));
        }

        private void UpdateSpreadHalo(StagePoint point)
        {
            if (_spreadHalo == null)
            {
                return;
            }

            double radius = _roomRadius * (0.10 + 0.90 * _spread);
            _spreadHalo.Width = radius * 2.0;
            _spreadHalo.Height = radius * 2.0;
            _spreadHalo.Opacity = 0.35 + 0.45 * _spread;
            Canvas.SetLeft(_spreadHalo, point.X - radius);
            Canvas.SetTop(_spreadHalo, point.Y - radius);
        }

        private void UpdateMetersFromPuck()
        {
            if (_meters.Count != SoundstageMath.ChannelLabels.Length)
            {
                return;
            }

            try
            {
                ChannelGains71 gains = NativeBridge.calculate_71_gains(
                    (float)_puckX, (float)_puckY, (float)_lfeSend, (float)_spread);
                float[] levels = gains.ToArray();
                for (int i = 0; i < _meters.Count && i < levels.Length; i++)
                {
                    double level = levels[i];
                    _meters[i].Bar.Value = SoundstageMath.MeterFillFraction(level);
                    _meters[i].Db.Text = SoundstageMath.FormatDb(level);
                    _meters[i].Linear.Text = SoundstageMath.FormatLinear(level);

                    bool active = level > 0.0001;
                    _meters[i].Root.Opacity = active ? 1.0 : 0.42;
                    _meters[i].Label.Foreground = new SolidColorBrush(active
                        ? Color.FromRgb(0xE6, 0xE6, 0xE6)
                        : Color.FromRgb(0x71, 0x71, 0x7A));
                    _meters[i].Dot.Opacity = active ? 1.0 : 0.35;
                }

                UpdateSpeakerVisuals(levels);
            }
            catch (Exception ex)
            {
                Log("Gain calculation failed: " + ex.Message);
            }
        }

        private void UpdateSpeakerVisuals(float[] levels)
        {
            for (int i = 0; i < _speakerVisuals.Count; i++)
            {
                SpeakerVisual visual = _speakerVisuals[i];
                int channel = Array.IndexOf(
                    SoundstageMath.ChannelLabels, visual.Channel);
                double level = channel >= 0 && channel < levels.Length
                    ? levels[channel]
                    : 0.0;
                double normalized = SoundstageMath.MeterFillFraction(level);

                visual.Node.Opacity = 0.45 + 0.55 * normalized;
                visual.Label.Opacity = 0.55 + 0.45 * normalized;
                visual.Node.StrokeThickness = normalized > 0.10 ? 1.5 : 0.75;
                if (visual.Glow != null)
                {
                    visual.Glow.Opacity = 0.10 + 0.90 * normalized;
                }
            }
        }

        private void OnRoomSizeChanged(object sender, SizeChangedEventArgs args)
        {
            DrawRoom();
        }

        private void OnRoomMouseDown(object sender, MouseButtonEventArgs args)
        {
            if (!_loaded)
            {
                return;
            }

            _dragging = true;
            RoomCanvas.CaptureMouse();
            BeginLivePan("Puck drag");
            MovePuckTo(args.GetPosition(RoomCanvas));
        }

        private void OnRoomMouseMove(object sender, MouseEventArgs args)
        {
            if (!_dragging)
            {
                return;
            }

            MovePuckTo(args.GetPosition(RoomCanvas));
        }

        private void OnRoomMouseUp(object sender, MouseButtonEventArgs args)
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            RoomCanvas.ReleaseMouseCapture();
            if (!CompleteLivePan("Puck drag"))
            {
                CommitPanning("Puck drag");
            }
        }

        private void OnRoomLostMouseCapture(object sender, MouseEventArgs args)
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            if (!CompleteLivePan("Puck drag"))
            {
                CommitPanning("Puck drag");
            }
        }

        private void MovePuckTo(Point point)
        {
            double previousX = _puckX;
            double previousY = _puckY;

            StagePoint normalized = SoundstageMath.FromScreen(
                point.X, point.Y, _roomCenterX, _roomCenterY, _roomRadius);
            _puckX = normalized.X;
            _puckY = normalized.Y;
            PositionPuck();
            UpdateMetersFromPuck();

            bool moved = Math.Abs(previousX - _puckX) > 1e-9 ||
                         Math.Abs(previousY - _puckY) > 1e-9;

            if (_liveSession != null && (moved || _liveDirty))
            {
                _liveDirty = true;
            }

            if (IsTransportPlaying())
            {
                if (_liveSession != null)
                {
                    // The live timer writes the send envelopes directly; no
                    // deferred script transaction is needed.
                    return;
                }

                if (KeyframeToggle.IsChecked == true)
                {
                    DateTime now = DateTime.UtcNow;
                    if ((now - _lastPendingSampleUtc).TotalMilliseconds >= 100.0)
                    {
                        _lastPendingSampleUtc = now;
                        QueuePendingPoint();
                    }
                }
                else
                {
                    _pendingPoints.Clear();
                    _hasPendingStatic = true;
                    _pendingStaticState = CurrentState;
                    ShowPendingStatus();
                }
            }
        }

        private void OnTrackSelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (_suppressControlEvents)
            {
                return;
            }

            AbortLivePan();
            DiscardPendingPan("the selected track changed");
            _liveFailed = false;

            var item = TrackCombo.SelectedItem as TrackItem;
            if (item == null || _vegas == null || _vegas.Project == null)
            {
                SetSelectedTrackInternal(null);
                PublishSelectedTrack();
                return;
            }

            AudioTrack track;
            if (!TryResolveAudioTrack(item.Index, out track))
            {
                SetSelectedTrackInternal(null);
                PublishSelectedTrack();
                Log("Track " + item.DisplayNumber +
                    " is no longer available. Click Refresh Tracks.");
                return;
            }

            SetSelectedTrackInternal(track);
            PublishSelectedTrack();
            Log("Selected track: " + item.DisplayName);
        }

        private void OnLfeChanged(object sender, RoutedPropertyChangedEventArgs<double> args)
        {
            _lfeSend = args.NewValue;
            UpdateSliderLabels();
            if (_loaded)
            {
                UpdateMetersFromPuck();
                if (_liveSession != null)
                {
                    _liveDirty = true;
                }
            }
        }

        private void OnSpreadChanged(object sender, RoutedPropertyChangedEventArgs<double> args)
        {
            _spread = args.NewValue;
            UpdateSliderLabels();
            UpdateSpreadHalo();
            if (_loaded)
            {
                UpdateMetersFromPuck();
                if (_liveSession != null)
                {
                    _liveDirty = true;
                }
            }
        }

        private void UpdateSliderLabels()
        {
            if (LfeValueText == null || SpreadValueText == null)
            {
                return;
            }

            LfeValueText.Text = _lfeSend.ToString("0.00", CultureInfo.InvariantCulture);
            SpreadValueText.Text = _spread.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private void OnSliderPressed(object sender, MouseButtonEventArgs args)
        {
            BeginLivePan("Slider drag");
        }

        private void OnSliderReleased(object sender, MouseButtonEventArgs args)
        {
            if (!CompleteLivePan("Slider change"))
            {
                CommitPanning("Slider change");
            }
        }

        private void OnApplyClicked(object sender, RoutedEventArgs args)
        {
            if (!TryCommitLive("Apply"))
            {
                CommitPanning("Apply");
            }
        }

        private void OnPanelUndoClicked(object sender, RoutedEventArgs args)
        {
            if (!EnsureProjectCurrent())
            {
                return;
            }

            if (_undoStack.Count == 0)
            {
                Log("Panel undo stack is empty.");
                return;
            }

            PuckState state = _undoStack.Pop();
            _suppressControlEvents = true;
            try
            {
                _puckX = state.X;
                _puckY = state.Y;
                _lfeSend = state.Lfe;
                _spread = state.Spread;
                LfeSlider.Value = _lfeSend;
                SpreadSlider.Value = _spread;
                UpdateSliderLabels();
            }
            finally
            {
                _suppressControlEvents = false;
            }

            PositionPuck();
            UpdateMetersFromPuck();
            if (!TryCommitLive("Panel undo"))
            {
                CommitPanning("Panel undo");
            }
        }

        private void OnResetPuckClicked(object sender, RoutedEventArgs args)
        {
            if (!EnsureProjectCurrent())
            {
                return;
            }

            _puckX = InitialPuckX;
            _puckY = InitialPuckY;
            PositionPuck();
            UpdateMetersFromPuck();
            SetStatus("Puck reset to its initial position.");
            Log("Puck reset to initial position (0.00, 0.70). Use Apply to Track to commit it.");
        }

        private void OnResetTrackKeyframesClicked(object sender, RoutedEventArgs args)
        {
            if (!EnsureProjectCurrent())
            {
                return;
            }

            if (_vegas == null || _vegas.Project == null)
            {
                Log("No active VEGAS project.");
                return;
            }

            if (!EnsureSelectedTrackValid())
            {
                Log(HostScriptBridge.TrackRoutingGuidance);
                SetStatus(HostScriptBridge.TrackRoutingGuidance);
                return;
            }

            string trackName = string.IsNullOrWhiteSpace(_selectedTrack.Name)
                ? "Track " + (_selectedTrack.Index + 1)
                : _selectedTrack.Name;

            MessageBoxResult answer = MessageBox.Show(
                "Reset Track Keyframes will delete all 7.1 send automation " +
                "points on the selected track:\r\n\r\n" + trackName +
                "\r\n\r\nThis cannot be undone from the panel. Continue?",
                "Reset Track Keyframes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                SetStatus("Reset Track Keyframes cancelled.");
                return;
            }

            try
            {
                int cleared = BusMatrixManager.ClearTrackKeyframes(
                    _vegas, _selectedTrack);
                Log("Reset Track Keyframes: removed " + cleared +
                    " automation point(s) from '" + trackName + "'.");
                SetStatus("Track keyframes cleared: " + trackName);
            }
            catch (Exception ex)
            {
                Log("Reset Track Keyframes failed: " +
                    HostScriptBridge.DescribeException(ex));
            }
        }

        private void OnResetAllClicked(object sender, RoutedEventArgs args)
        {
            if (!EnsureProjectCurrent())
            {
                return;
            }

            MessageBoxResult answer = MessageBox.Show(
                "Reset All will return the panel to factory defaults and clear " +
                "all 7.1 send automation keyframes on every audio track.\r\n\r\n" +
                "This reverses all extension progress in this project and cannot " +
                "be undone from the panel.\r\n\r\nContinue?",
                "Reset All",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                SetStatus("Reset All cancelled.");
                return;
            }

            try
            {
                AbortLivePan();
                _undoStack.Clear();
                _pendingPoints.Clear();
                _hasPendingStatic = false;
                ClearPendingStatus();
                HideLiveStatus();

                int cleared = 0;
                if (_vegas != null && _vegas.Project != null)
                {
                    cleared = BusMatrixManager.ClearAllTrackKeyframes(_vegas);
                }

                _suppressControlEvents = true;
                try
                {
                    _puckX = InitialPuckX;
                    _puckY = InitialPuckY;
                    _lfeSend = 0.25;
                    _spread = 0.10;
                    LfeSlider.Value = _lfeSend;
                    SpreadSlider.Value = _spread;
                    KeyframeToggle.IsChecked = false;
                    LivePanToggle.IsChecked = true;
                    LfeLowpassCheck.IsChecked = true;
                    RenderLoopOnlyCheck.IsChecked = false;
                    OutputPathBox.Text = string.Empty;
                    _deliveryPreset = DeliveryPreset.FlacMkv;
                    ApplyPreset(DeliveryPreset.FlacMkv, false);
                    _videoTemplates.Clear();
                    VideoTemplateCombo.Items.Clear();
                    _selectedVideoTemplate = null;
                    _videoTemplateSignature = null;
                    _lastCommitted = CurrentState;
                }
                finally
                {
                    _suppressControlEvents = false;
                }

                UpdateSliderLabels();
                PositionPuck();
                UpdateMetersFromPuck();
                PopulateVideoTemplates(true);
                UpdateLoopRangeStatus();
                UpdateRenderCompatibility();
                UpdateBusStatus();
                UpdateNativeStatus();
                SetStatus("Reset All complete — factory defaults restored.");
                Log("Reset All: panel returned to factory defaults; removed " +
                    cleared + " automation point(s) from all audio tracks.");
            }
            catch (Exception ex)
            {
                Log("Reset All failed: " + HostScriptBridge.DescribeException(ex));
            }
        }

        private void OnInitializeBussesClicked(object sender, RoutedEventArgs args)
        {
            if (!EnsureProjectCurrent())
            {
                return;
            }

            if (_vegas == null)
            {
                Log("No VEGAS instance.");
                return;
            }

            string error;
            if (HostScriptBridge.RunProvision(_vegas, out error))
            {
                Log("7.1 busses set up/reused.");
            }
            else
            {
                Log("Bus initialization failed: " + error);
            }

            RefreshAll();
        }

        private void OnRefreshTracksClicked(object sender, RoutedEventArgs args)
        {
            if (!EnsureProjectCurrent())
            {
                return;
            }

            AbortLivePan();
            DiscardPendingPan("the track list was refreshed");
            PopulateTracks();
            UpdateBusStatus();
            UpdateNativeStatus();
        }

        private bool LivePanEnabled
        {
            get { return LivePanToggle != null && LivePanToggle.IsChecked == true; }
        }

        /// <summary>
        /// Starts a direct, non-script live pan session. Unlike the script-host
        /// bridge this does not stop the transport, so send levels and the VEGAS
        /// Mixing Console follow the puck while playback continues.
        /// </summary>
        private bool BeginLivePan(string reason)
        {
            if (!EnsureProjectCurrent())
            {
                return false;
            }

            if (_liveSession != null)
            {
                return true;
            }

            if (!LivePanEnabled)
            {
                return false;
            }

            if (!EnsureSelectedTrackValid())
            {
                Log(HostScriptBridge.TrackRoutingGuidance);
                SetStatus(HostScriptBridge.TrackRoutingGuidance);
                return false;
            }

            if (_vegas == null)
            {
                return false;
            }

            if (!EnsureBussesReady())
            {
                return false;
            }

            string error;
            LivePanSession session = LivePanSession.TryBegin(
                _vegas, _selectedTrack, out error);
            if (session == null)
            {
                Log("Live pan unavailable for " + reason + ": " + error +
                    " Falling back to commit-on-stop.");
                _liveFailed = true;
                return false;
            }

            _liveSession = session;
            _liveStartState = _lastCommitted;
            _liveDirty = false;
            _liveApplied = false;
            _liveFailed = false;
            _liveLogged = false;
            _lastLiveUiRefreshUtc = DateTime.MinValue;

            // A live write supersedes any samples queued before the session.
            _pendingPoints.Clear();
            _hasPendingStatic = false;
            ClearPendingStatus();

            ShowLiveStatus();
            return true;
        }

        private bool ApplyLiveNow(bool final)
        {
            if (_liveSession == null || _liveFailed)
            {
                return false;
            }

            bool keyframes = KeyframeToggle.IsChecked == true;
            Timecode cursor = keyframes ? GetLiveCursor() : Timecode.FromNanos(0);
            bool refreshUi = final ||
                (DateTime.UtcNow - _lastLiveUiRefreshUtc).TotalMilliseconds >= 150.0;

            string error;
            bool ok = _liveSession.Apply(
                cursor,
                (float)_puckX,
                (float)_puckY,
                (float)_lfeSend,
                (float)_spread,
                keyframes,
                refreshUi,
                out error);

            if (ok)
            {
                _liveDirty = false;
                _liveApplied = true;
                if (refreshUi)
                {
                    _lastLiveUiRefreshUtc = DateTime.UtcNow;
                }

                if (!_liveLogged)
                {
                    _liveLogged = true;
                    Log("Live pan active — mixer send levels follow the puck" +
                        (IsTransportPlaying() ? " during playback." : "."));
                }

                return true;
            }

            _liveFailed = true;
            Log("Live pan update failed: " + error +
                " Falling back to commit-on-stop.");
            HideLiveStatus();
            return false;
        }

        private bool CompleteLivePan(string reason)
        {
            if (_liveSession == null)
            {
                return false;
            }

            bool changed = _liveDirty || _liveApplied ||
                !CurrentState.Equals(_liveStartState);
            if (!changed)
            {
                DisposeLiveSession();
                HideLiveStatus();
                return true;
            }

            if (!ApplyLiveNow(true))
            {
                DisposeLiveSession();
                HideLiveStatus();
                return false;
            }

            Timecode cursor = KeyframeToggle.IsChecked == true
                ? GetLiveCursor()
                : Timecode.FromNanos(0);
            string readback = _liveSession.Readback(cursor);

            if (!CurrentState.Equals(_liveStartState))
            {
                _undoStack.Push(_liveStartState);
            }

            _lastCommitted = CurrentState;
            Log(string.Format(CultureInfo.InvariantCulture,
                "{0} applied live at {1}: x={2:0.###} y={3:0.###} lfe={4:0.###} spread={5:0.###}",
                reason, cursor, _puckX, _puckY, _lfeSend, _spread));
            Log("Read-back: " + readback);

            DisposeLiveSession();
            HideLiveStatus();
            return true;
        }

        /// <summary>
        /// Applies the current puck state live for one-shot actions (Apply,
        /// panel undo, reset). Returns false when the caller should use the
        /// script-host commit path instead.
        /// </summary>
        private bool TryCommitLive(string reason)
        {
            if (!LivePanEnabled)
            {
                return false;
            }

            if (_liveSession == null && !BeginLivePan(reason))
            {
                return false;
            }

            _liveDirty = true;
            return CompleteLivePan(reason);
        }

        private void OnLiveTimerTick(object sender, EventArgs args)
        {
            if (!_loaded || _liveSession == null || !_liveDirty)
            {
                return;
            }

            if (!ApplyLiveNow(false))
            {
                // Disable this session; the mouse-up handler falls back to the
                // deferred commit path if the write failed.
                DisposeLiveSession();
            }
        }

        private void OnLivePanToggled(object sender, RoutedEventArgs args)
        {
            if (!LivePanEnabled && _liveSession != null)
            {
                if (!CompleteLivePan("Live pan disabled"))
                {
                    CommitPanning("Live pan disabled");
                }
            }
        }

        private Timecode GetLiveCursor()
        {
            try
            {
                TransportControl transport = _vegas == null ? null : _vegas.Transport;
                if (transport != null)
                {
                    if (transport.IsPlaying && transport.PlayCursorPosition != null)
                    {
                        return transport.PlayCursorPosition;
                    }

                    if (transport.CursorPosition != null)
                    {
                        return transport.CursorPosition;
                    }
                }
            }
            catch (Exception)
            {
                // Fall through to the project start.
            }

            return Timecode.FromNanos(0);
        }

        private void AbortLivePan()
        {
            DisposeLiveSession();
            HideLiveStatus();
        }

        private void DisposeLiveSession()
        {
            if (_liveSession != null)
            {
                _liveSession.Dispose();
                _liveSession = null;
            }

            _liveDirty = false;
        }

        private void ShowLiveStatus()
        {
            if (LiveStatusText == null)
            {
                return;
            }

            LiveStatusText.Text = IsTransportPlaying()
                ? "LIVE — send levels updating during playback"
                : "LIVE — send levels updating";
            LiveStatusText.Visibility = Visibility.Visible;
        }

        private void HideLiveStatus()
        {
            if (LiveStatusText != null)
            {
                LiveStatusText.Visibility = Visibility.Collapsed;
            }
        }

        private void CommitPanning(string reason)
        {
            if (!EnsureProjectCurrent())
            {
                return;
            }

            if (!EnsureSelectedTrackValid())
            {
                Log(HostScriptBridge.TrackRoutingGuidance);
                SetStatus(HostScriptBridge.TrackRoutingGuidance);
                return;
            }

            if (_vegas == null || _vegas.Transport == null)
            {
                Log("VEGAS transport is unavailable.");
                return;
            }

            if (!EnsureBussesReady())
            {
                return;
            }

            if (IsTransportPlaying())
            {
                QueuePending(reason);
                return;
            }

            if (HasPending)
            {
                FlushPendingCommits();
                if (CurrentState.Equals(_lastCommitted))
                {
                    return;
                }
            }

            PuckState state = CurrentState;
            if (state.Equals(_lastCommitted))
            {
                return;
            }

            Timecode cursor = GetCursor();
            bool keyframes = KeyframeToggle.IsChecked == true;
            _undoStack.Push(_lastCommitted);

            string error;
            string readback;
            bool ok = HostScriptBridge.ApplyTrackPanning(
                _vegas,
                _selectedTrack,
                cursor,
                _puckX,
                _puckY,
                _lfeSend,
                _spread,
                keyframes,
                out error,
                out readback);

            if (ok)
            {
                _lastCommitted = state;
                Log(string.Format(CultureInfo.InvariantCulture,
                    "{0} committed ({1}) at {2}: x={3:0.###} y={4:0.###} lfe={5:0.###} spread={6:0.###}",
                    reason, keyframes ? "keyframe" : "static", cursor,
                    _puckX, _puckY, _lfeSend, _spread));
                Log("Read-back: " + readback);
            }
            else
            {
                if (_undoStack.Count > 0)
                {
                    _undoStack.Pop();
                }

                Log(reason + " commit failed: " + error);
            }
        }

        private bool HasPending
        {
            get { return _pendingPoints.Count > 0 || _hasPendingStatic; }
        }

        private bool IsTransportPlaying()
        {
            try
            {
                return _vegas != null && _vegas.Transport != null &&
                       _vegas.Transport.IsPlaying;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private Timecode GetCursor()
        {
            try
            {
                if (_vegas != null && _vegas.Transport != null &&
                    _vegas.Transport.CursorPosition != null)
                {
                    return _vegas.Transport.CursorPosition;
                }

                return _vegas == null ? Timecode.FromNanos(0) : _vegas.Cursor;
            }
            catch (Exception)
            {
                return Timecode.FromNanos(0);
            }
        }

        private void QueuePending(string reason)
        {
            if (KeyframeToggle.IsChecked == true)
            {
                QueuePendingPoint();
                _hasPendingStatic = false;
            }
            else
            {
                _pendingPoints.Clear();
                _hasPendingStatic = true;
                _pendingStaticState = CurrentState;
            }

            ShowPendingStatus();
            Log(reason + " queued — will commit when playback stops.");
        }

        private void QueuePendingPoint()
        {
            Timecode cursor = GetCursor();
            if (cursor == null)
            {
                return;
            }

            _pendingPoints.Add(new PanPoint(
                cursor.Nanos, _puckX, _puckY, _lfeSend, _spread));
            ShowPendingStatus();
        }

        private void OnTransportTimerTick(object sender, EventArgs args)
        {
            if (!_loaded)
            {
                return;
            }

            if (!EnsureProjectCurrent())
            {
                return;
            }

            bool playing = IsTransportPlaying();
            UpdateLoopRangeStatus();
            if (_liveSession != null)
            {
                ShowLiveStatus();

                // Live writes own the sends for this drag; never run a script
                // flush or leave stale deferred samples behind it.
                _pendingPoints.Clear();
                _hasPendingStatic = false;
                ClearPendingStatus();
                _wasPlaying = playing;
                return;
            }

            if (!playing && HasPending)
            {
                FlushPendingCommits();
            }
            else if (playing && HasPending)
            {
                ShowPendingStatus();
            }
            else if (!HasPending)
            {
                ClearPendingStatus();
            }

            _wasPlaying = playing;
        }

        private void FlushPendingCommits()
        {
            if (_flushing || !HasPending)
            {
                return;
            }

            _flushing = true;
            try
            {
                if (!EnsureSelectedTrackValid())
                {
                    _pendingPoints.Clear();
                    _hasPendingStatic = false;
                    ClearPendingStatus();
                    Log("Pending pan discarded: no audio track selected.");
                    return;
                }

                if (!EnsureBussesReady())
                {
                    _pendingPoints.Clear();
                    _hasPendingStatic = false;
                    ClearPendingStatus();
                    return;
                }

                if (_pendingPoints.Count > 0)
                {
                    var batch = new List<PanPoint>(_pendingPoints);
                    _pendingPoints.Clear();
                    _hasPendingStatic = false;

                    _undoStack.Push(_lastCommitted);
                    string error;
                    string readback;
                    bool ok = HostScriptBridge.ApplyTrackPanningBatch(
                        _vegas, _selectedTrack, batch, out error, out readback);

                    if (ok)
                    {
                        PanPoint last = batch[batch.Count - 1];
                        _lastCommitted = new PuckState(
                            last.X, last.Y, last.Lfe, last.Spread);
                        Log(string.Format(CultureInfo.InvariantCulture,
                            "Deferred keyframe batch committed: {0} points at {1}.",
                            batch.Count, _selectedTrack.Name));
                        Log("Read-back: " + readback);
                    }
                    else
                    {
                        Log("Deferred keyframe batch failed: " + error);
                    }
                }
                else if (_hasPendingStatic)
                {
                    PuckState state = _pendingStaticState;
                    _hasPendingStatic = false;

                    _undoStack.Push(_lastCommitted);
                    string error;
                    string readback;
                    bool ok = HostScriptBridge.ApplyTrackPanning(
                        _vegas,
                        _selectedTrack,
                        GetCursor(),
                        state.X,
                        state.Y,
                        state.Lfe,
                        state.Spread,
                        false,
                        out error,
                        out readback);

                    if (ok)
                    {
                        _lastCommitted = state;
                        Log("Deferred static pan committed at playback stop.");
                        Log("Read-back: " + readback);
                    }
                    else
                    {
                        Log("Deferred static pan failed: " + error);
                    }
                }
            }
            finally
            {
                _flushing = false;
                if (!HasPending)
                {
                    ClearPendingStatus();
                }
            }
        }

        private void ShowPendingStatus()
        {
            if (PendingStatusText == null)
            {
                return;
            }

            PendingStatusText.Text = "Pending commit — will write on stop";
            PendingStatusText.Visibility = Visibility.Visible;
        }

        private void ClearPendingStatus()
        {
            if (PendingStatusText == null)
            {
                return;
            }

            PendingStatusText.Visibility = Visibility.Collapsed;
        }

        private void OpenBusFxChain(int channelIndex)
        {
            if (_vegas == null || _vegas.Project == null)
            {
                return;
            }

            AudioBusTrack[] busses = BusMatrixManager.FindCanonicalBusses(_vegas.Project);
            if (busses == null)
            {
                Log("Setup the 7.1 busses before opening a bus FX chain.");
                return;
            }

            AudioBusTrack bus = busses[channelIndex];
            string label = "[7.1] " + SoundstageMath.ChannelLabels[channelIndex];

            try
            {
                MethodInfo open = bus.GetType().GetMethod(
                    "OpenEffectsDialog", BindingFlags.Public | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (open != null)
                {
                    open.Invoke(bus, null);
                    Log("Opened VEGAS FX chain for " + label + " (" + bus.Name + ").");
                    return;
                }

                open = bus.Effects.GetType().GetMethod(
                    "OpenEffectsDialog", BindingFlags.Public | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (open != null)
                {
                    open.Invoke(bus.Effects, null);
                    Log("Opened VEGAS FX chain for " + label + " (" + bus.Name + ").");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log("FX dialog reflection failed: " + HostScriptBridge.DescribeException(ex));
            }

            if (TryInvokeCommand("View", "Mixing Console") ||
                TryInvokeCommand("View", "Mixer") ||
                TryInvokeCommand("Tools", "Mixing Console"))
            {
                Log("This VEGAS build has no OpenEffectsDialog API; requested the Mixing Console. " +
                    "If it is not visible, open View > Mixing Console, then click Bus FX on " +
                    label + " (" + bus.Name + ").");
                return;
            }

            MessageBox.Show(
                "This VEGAS build does not expose OpenEffectsDialog to extensions.\r\n\r\n" +
                "Open the Mixing Console and click Bus FX on " + label +
                " (" + bus.Name + ").",
                "VegasSurround71 Bus FX",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private bool TryInvokeCommand(string section, string name)
        {
            try
            {
                _vegas.InvokeCommand(section, name);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void OnBrowseOutputClicked(object sender, RoutedEventArgs args)
        {
            string extension = DeliveryPresets.Extension(_deliveryPreset);
            string filter = "Matroska (*.mkv)|*.mkv";

            var dialog = new SaveFileDialog
            {
                Title = "Export 7.1 Master",
                Filter = filter,
                DefaultExt = extension,
                AddExtension = true,
                OverwritePrompt = true,
                FileName = IOPath.GetFileName(OutputPathBox.Text)
            };

            string directory = IOPath.GetDirectoryName(OutputPathBox.Text);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                dialog.InitialDirectory = directory;
            }

            if (dialog.ShowDialog() == true)
            {
                OutputPathBox.Text = dialog.FileName;
                SyncOutputExtension(extension);
            }
        }

        private void OnRenderClicked(object sender, RoutedEventArgs args)
        {
            if (!EnsureProjectCurrent())
            {
                return;
            }

            if (_vegas == null || _vegas.Project == null)
            {
                RenderStatusText.Text = "No active VEGAS project.";
                return;
            }

            string output = OutputPathBox.Text.Trim();
            var codecItem = CodecCombo.SelectedItem as CodecItem;
            if (codecItem == null)
            {
                RenderStatusText.Text = "Choose an output codec.";
                return;
            }

            VideoTemplateItem videoItem = _selectedVideoTemplate;
            if (videoItem == null)
            {
                RenderStatusText.Text = "Choose a video template for Pass 1.";
                return;
            }

            bool renderLoopOnly = RenderLoopOnlyCheck.IsChecked == true;
            long loopStart = 0L;
            long loopLength = 0L;
            if (renderLoopOnly &&
                (!TryGetLoopRange(out loopStart, out loopLength) || loopLength <= 0))
            {
                RenderStatusText.Text =
                    "Set In/Out loop points on the VEGAS timeline first.";
                SetReadinessBadge(RenderStatusText.Text, true);
                Log(RenderStatusText.Text);
                return;
            }

            RenderPlan plan;
            try
            {
                plan = RenderPlan.Create(
                    output,
                    codecItem.Codec,
                    LfeLowpassCheck.IsChecked == true,
                    _deliveryPreset,
                    videoItem.Family,
                    renderLoopOnly,
                    loopStart,
                    loopLength);
            }
            catch (Exception ex)
            {
                RenderStatusText.Text = ex.Message;
                return;
            }

            string validation = plan.Validate();
            if (validation != null)
            {
                RenderStatusText.Text = validation;
                Log(validation);
                return;
            }

            RenderButton.IsEnabled = false;
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                Log("Starting 3-pass 7.1 render: " + plan.OutputPath);
                Log("Video template: " + videoItem.DisplayName +
                    " (" + DeliveryPresets.FamilyLabel(videoItem.Family) + ")");
                Log("Delivery: " + DeliveryPresets.DisplayName(_deliveryPreset));
                if (renderLoopOnly)
                {
                    Log("Render range: loop " + Timecode.FromNanos(loopStart) +
                        " -> " + Timecode.FromNanos(loopStart + loopLength));
                }
                else
                {
                    Log("Render range: full timeline");
                }
                RenderResult result = RenderOrchestrator.Run(
                    _vegas, plan, videoItem.Template, OnRenderProgress);
                RenderStatusText.Text = result.Message;
                Log(result.Message);
            }
            catch (Exception ex)
            {
                RenderStatusText.Text = "Render failed: " + ex.Message;
                Log(RenderStatusText.Text);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                UpdateRenderCompatibility();
            }
        }

        private void OnRenderProgress(string message)
        {
            RenderStatusText.Text = message;
            Log(message);
            RenderStatusText.UpdateLayout();
            try
            {
                System.Windows.Forms.Application.DoEvents();
            }
            catch (Exception)
            {
                // UI pumping is best-effort only.
            }
        }

        private void Log(string message)
        {
            SetStatus(message);

            if (LogBox == null)
            {
                return;
            }

            string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) +
                "  " + message + Environment.NewLine;
            LogBox.AppendText(line);
            LogBox.ScrollToEnd();

            if (_workspaceContext != null)
            {
                // The shared logger writes the same panel.log transcript and
                // mirrors the line to the HOA workspace drawer.
                _workspaceContext.WriteDiagnostic(message);
                return;
            }

            try
            {
                Directory.CreateDirectory(ExtensionSettings.SettingsDirectory);
                File.AppendAllText(PanelLogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                    "  " + message + Environment.NewLine);
            }
            catch (Exception)
            {
                // Persistent logging is best effort.
            }
        }

        private PuckState CurrentState
        {
            get { return new PuckState(_puckX, _puckY, _lfeSend, _spread); }
        }

        private struct PuckState : IEquatable<PuckState>
        {
            public PuckState(double x, double y, double lfe, double spread)
            {
                X = x;
                Y = y;
                Lfe = lfe;
                Spread = spread;
            }

            public double X { get; private set; }
            public double Y { get; private set; }
            public double Lfe { get; private set; }
            public double Spread { get; private set; }

            public bool Equals(PuckState other)
            {
                return Math.Abs(X - other.X) < 1e-6 &&
                       Math.Abs(Y - other.Y) < 1e-6 &&
                       Math.Abs(Lfe - other.Lfe) < 1e-6 &&
                       Math.Abs(Spread - other.Spread) < 1e-6;
            }

            public override bool Equals(object obj)
            {
                return obj is PuckState && Equals((PuckState)obj);
            }

            public override int GetHashCode()
            {
                return X.GetHashCode() ^ Y.GetHashCode() ^
                       Lfe.GetHashCode() ^ Spread.GetHashCode();
            }
        }

        private sealed class MeterRow
        {
            public MeterRow(
                Grid root, TextBlock label, Ellipse dot,
                ProgressBar bar, TextBlock db, TextBlock linear)
            {
                Root = root;
                Label = label;
                Dot = dot;
                Bar = bar;
                Db = db;
                Linear = linear;
            }

            public Grid Root { get; private set; }
            public TextBlock Label { get; private set; }
            public Ellipse Dot { get; private set; }
            public ProgressBar Bar { get; private set; }
            public TextBlock Db { get; private set; }
            public TextBlock Linear { get; private set; }
        }

        private sealed class SpeakerVisual
        {
            public SpeakerVisual(
                string channel, Ellipse node, TextBlock label, DropShadowEffect glow)
            {
                Channel = channel;
                Node = node;
                Label = label;
                Glow = glow;
            }

            public string Channel { get; private set; }
            public Ellipse Node { get; private set; }
            public TextBlock Label { get; private set; }
            public DropShadowEffect Glow { get; private set; }
        }

        private sealed class TrackItem
        {
            public TrackItem(int displayNumber, int index, string name)
            {
                DisplayNumber = displayNumber;
                Index = index;
                Name = name ?? string.Empty;
            }

            public int DisplayNumber { get; private set; }
            public int Index { get; private set; }
            public string Name { get; private set; }

            public string DisplayName
            {
                get
                {
                    return string.IsNullOrWhiteSpace(Name)
                        ? "Track " + DisplayNumber
                        : "Track " + DisplayNumber + " - " + Name;
                }
            }

            public override string ToString()
            {
                return DisplayName;
            }
        }

        private sealed class CodecItem
        {
            public CodecItem(string display, AudioCodec71 codec)
            {
                Display = display;
                Codec = codec;
            }

            public string Display { get; private set; }
            public AudioCodec71 Codec { get; private set; }

            public override string ToString()
            {
                return Display;
            }
        }

        private sealed class PresetItem
        {
            public PresetItem(DeliveryPreset preset)
            {
                Preset = preset;
            }

            public DeliveryPreset Preset { get; private set; }

            public override string ToString()
            {
                return DeliveryPresets.DisplayName(Preset);
            }
        }
    }
}
