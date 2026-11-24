using System;
using System.Globalization;
using System.IO;
using System.Media;
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
    /// HOA / 3D Spatial workspace: an overhead + elevation soundstage with a
    /// draggable source puck, isolated HOA state, order/preview/delivery
    /// selectors, and a track selector synced through <see cref="WorkspaceContext"/>.
    ///
    /// This view never calls the 7.1 bus matrix and never writes `[7.1] FL..SR`
    /// automation. HOA positions live only in <see cref="HoaSpatialState"/>.
    /// </summary>
    public partial class HoaSpatialView : UserControl
    {
        private readonly WorkspaceContext _context;
        private readonly HoaSpatialState _state = new HoaSpatialState();

        private bool _loaded;
        private bool _subscribed;
        private bool _suppressControlEvents;
        private bool _dragging;
        private bool _elevationDrag;

        private double _centerX;
        private double _centerY;
        private double _radius;
        private double _railX;
        private double _railTop;
        private double _railBottom;
        private double _railHalf;

        private Ellipse _puck;
        private Ellipse _spreadHalo;
        private Ellipse _elevationPuck;
        private Line _azimuthLine;
        private DispatcherTimer _positionLogTimer;
        private DispatcherTimer _stateSaveTimer;
        private SoundPlayer _soundPlayer;
        private bool _rendering;
        private string _hoaRenderStatusOverride;
        private bool _hoaRenderStatusIsError;
        private HoaSpatialState _slotA = new HoaSpatialState();
        private HoaSpatialState _slotB = new HoaSpatialState();
        private char _activeSlot = 'A';
        private int _snapDegrees;
        private bool _applyingPreset;

        public HoaSpatialView()
            : this(null)
        {
        }

        public HoaSpatialView(WorkspaceContext context)
        {
            _context = context;
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>The isolated HOA state for host/probe inspection.</summary>
        public HoaSpatialState State
        {
            get { return _state; }
        }

        /// <summary>Refreshes the HOA track list and native weight readout.</summary>
        public void RefreshAll()
        {
            if (!_loaded)
            {
                return;
            }

            if (_context != null)
            {
                _context.RefreshProjectIdentity();
            }

            PopulateTracks();
            UpdateReadouts();
            DrawStage();
            RefreshWeightInfo();
        }

        /// <summary>Restores the HOA workspace after an open/close transition.</summary>
        public void ResetForProjectChange()
        {
            if (_stateSaveTimer != null)
            {
                _stateSaveTimer.Stop();
            }

            _state.Reset();
            _hoaRenderStatusOverride = null;
            _hoaRenderStatusIsError = false;

            if (!_loaded)
            {
                return;
            }

            _suppressControlEvents = true;
            try
            {
                TrackCombo.Items.Clear();
                TrackCombo.SelectedItem = null;
            }
            finally
            {
                _suppressControlEvents = false;
            }

            SyncControlsFromState();
            PopulateTracks();
            bool restored = LoadProjectState();
            if (!restored)
            {
                UpdateReadouts();
                UpdateSourceVisuals();
                RefreshWeightInfo();
                UpdateHoaRenderState();
            }

            DrawStage();
            _slotA = _state.Clone();
            _slotB = _state.Clone();
            _activeSlot = 'A';
            UpdateHoaMeters(null);
            if (AbStatusText != null)
            {
                AbStatusText.Text = "ACTIVE A";
            }

            SetStatus(restored
                ? "Project state restored — HOA workspace ready."
                : "New project detected — HOA workspace reset.");
            Log(restored
                ? "Restored the saved HOA state for the new project; 7.1 busses were not touched."
                : "HOA workspace reset for the new project; 7.1 busses were not touched.");
        }

        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            PopulateOrderOptions();
            PopulatePreviewOptions();
            PopulateExportOptions();
            PopulateSourceModeOptions();
            PopulateDistanceModelOptions();
            PopulateHeadModelOptions();
            PopulateSourcePresetOptions();
            PopulateSnapOptions();
            SyncControlsFromState();
            _slotA = _state.Clone();
            _slotB = _state.Clone();
            PopulateTracks();
            DrawStage();
            SubscribeContext();

            _positionLogTimer = new DispatcherTimer(DispatcherPriority.Background);
            _positionLogTimer.Interval = TimeSpan.FromMilliseconds(600);
            _positionLogTimer.Tick += OnPositionLogTick;

            _stateSaveTimer = new DispatcherTimer(DispatcherPriority.Background);
            _stateSaveTimer.Interval = TimeSpan.FromMilliseconds(750);
            _stateSaveTimer.Tick += OnStateSaveTimerTick;

            if (HoaOutputPathBox != null &&
                string.IsNullOrWhiteSpace(HoaOutputPathBox.Text))
            {
                HoaOutputPathBox.Text = DefaultHoaOutputPath(".wav");
            }

            bool restored = LoadProjectState();
            if (!restored)
            {
                SyncControlsFromState();
                UpdateReadouts();
                UpdateSourceVisuals();
                RefreshWeightInfo();
                UpdateHoaRenderState();
            }

            _slotA = _state.Clone();
            _slotB = _state.Clone();
            UpdateHoaMeters(null);
            if (AbStatusText != null)
            {
                AbStatusText.Text = "ACTIVE A";
            }

            Log("HOA / 3D Spatial workspace ready. AmbiX WAV/W64/FLAC export " +
                "and stereo/binaural preview are available.");
            if (restored)
            {
                Log("Restored the saved HOA state for this project.");
            }
            Log("HOA renders are isolated from the [7.1] FL..SR bus sends.");
        }

        private bool LoadProjectState()
        {
            if (_context == null)
            {
                return false;
            }

            string outputPath;
            int snapDegrees;
            if (!HoaProjectStateStore.TryLoad(
                _context, _state, out outputPath, out snapDegrees))
            {
                return false;
            }

            _suppressControlEvents = true;
            try
            {
                if (!string.IsNullOrWhiteSpace(outputPath) && HoaOutputPathBox != null)
                {
                    HoaOutputPathBox.Text = outputPath;
                }

                _snapDegrees = snapDegrees;
                SyncControlsFromState();
                SyncSnapSelection();
                SyncExportChoiceFromOutputPath();
            }
            finally
            {
                _suppressControlEvents = false;
            }

            UpdateReadouts();
            UpdateSourceVisuals();
            RefreshWeightInfo();
            UpdateHoaRenderState();
            return true;
        }

        private void SyncSnapSelection()
        {
            if (SnapCombo == null)
            {
                return;
            }

            for (int index = 0; index < SnapCombo.Items.Count; index++)
            {
                var item = SnapCombo.Items[index] as SnapItem;
                if (item != null && item.Degrees == _snapDegrees)
                {
                    _suppressControlEvents = true;
                    try
                    {
                        SnapCombo.SelectedIndex = index;
                    }
                    finally
                    {
                        _suppressControlEvents = false;
                    }

                    return;
                }
            }
        }

        private void ScheduleProjectStateSave()
        {
            if (!_loaded || _context == null || _stateSaveTimer == null)
            {
                return;
            }

            _stateSaveTimer.Stop();
            _stateSaveTimer.Start();
        }

        private void OnStateSaveTimerTick(object sender, EventArgs args)
        {
            if (_stateSaveTimer != null)
            {
                _stateSaveTimer.Stop();
            }

            SaveProjectStateNow();
        }

        private void SaveProjectStateNow()
        {
            if (!_loaded || _context == null || _suppressControlEvents)
            {
                return;
            }

            HoaProjectStateStore.Save(
                _context,
                _state,
                HoaOutputPathBox == null ? string.Empty : HoaOutputPathBox.Text,
                _snapDegrees);
        }

        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            _dragging = false;
            _rendering = false;

            SaveProjectStateNow();

            if (_stateSaveTimer != null)
            {
                _stateSaveTimer.Stop();
                _stateSaveTimer.Tick -= OnStateSaveTimerTick;
                _stateSaveTimer = null;
            }

            if (_soundPlayer != null)
            {
                try
                {
                    _soundPlayer.Stop();
                }
                catch (Exception)
                {
                    // Playback may already have completed.
                }

                _soundPlayer.Dispose();
                _soundPlayer = null;
            }

            if (_positionLogTimer != null)
            {
                _positionLogTimer.Stop();
                _positionLogTimer.Tick -= OnPositionLogTick;
                _positionLogTimer = null;
            }

            if (_subscribed && _context != null)
            {
                _context.SelectedTrackChanged -= OnContextSelectedTrackChanged;
                _context.DiagnosticMessage -= OnDiagnosticMessage;
                _subscribed = false;
            }
        }

        private void SubscribeContext()
        {
            if (_subscribed || _context == null)
            {
                return;
            }

            _context.SelectedTrackChanged += OnContextSelectedTrackChanged;
            _context.DiagnosticMessage += OnDiagnosticMessage;
            _subscribed = true;
        }

        private void OnContextSelectedTrackChanged(object sender, EventArgs args)
        {
            if (!_loaded || _suppressControlEvents || _context == null)
            {
                return;
            }

            int index = _context.SelectedTrackIndex;
            _suppressControlEvents = true;
            try
            {
                TrackItem match = null;
                foreach (TrackItem item in TrackCombo.Items)
                {
                    if (item.Index == index)
                    {
                        match = item;
                        break;
                    }
                }

                TrackCombo.SelectedItem = match;
            }
            finally
            {
                _suppressControlEvents = false;
            }
        }

        private void PopulateOrderOptions()
        {
            _suppressControlEvents = true;
            try
            {
                OrderCombo.Items.Clear();
                for (int order = NativeBridge.HoaMinOrder;
                    order <= NativeBridge.HoaMaxOrder;
                    order++)
                {
                    OrderCombo.Items.Add(new OrderItem(order));
                }

                OrderCombo.SelectedIndex = 0;
            }
            finally
            {
                _suppressControlEvents = false;
            }
        }

        private void PopulatePreviewOptions()
        {
            _suppressControlEvents = true;
            try
            {
                PreviewCombo.Items.Clear();
                PreviewCombo.Items.Add(new PreviewItem(HoaPreviewMode.Binaural));
                PreviewCombo.Items.Add(new PreviewItem(HoaPreviewMode.StereoDownmix));
                PreviewCombo.Items.Add(new PreviewItem(HoaPreviewMode.Bypass));
                PreviewCombo.SelectedIndex = 0;
            }
            finally
            {
                _suppressControlEvents = false;
            }
        }

        private void PopulateExportOptions()
        {
            _suppressControlEvents = true;
            try
            {
                ExportCombo.Items.Clear();
                ExportCombo.Items.Add(new ExportChoiceItem(
                    HoaExportTarget.AmbiXWavFlac, ".wav", "HOA AmbiX WAV (.wav)"));
                ExportCombo.Items.Add(new ExportChoiceItem(
                    HoaExportTarget.AmbiXWavFlac, ".w64", "HOA AmbiX Wave64 (.w64)"));
                ExportCombo.Items.Add(new ExportChoiceItem(
                    HoaExportTarget.AmbiXWavFlac, ".flac", "HOA AmbiX FLAC (.flac)"));
                ExportCombo.Items.Add(new ExportChoiceItem(
                    HoaExportTarget.IamfEclipsaPlanned, null,
                    "IAMF / Eclipsa (Planned)"));
                ExportCombo.SelectedIndex = 0;
            }
            finally
            {
                _suppressControlEvents = false;
            }
        }

        private void PopulateSourceModeOptions()
        {
            _suppressControlEvents = true;
            try
            {
                SourceModeCombo.Items.Clear();
                SourceModeCombo.Items.Add(new SourceModeItem(HoaSourceMode.SelectedTrack));
                SourceModeCombo.Items.Add(new SourceModeItem(HoaSourceMode.FullMix));
                SourceModeCombo.SelectedIndex = 0;
            }
            finally
            {
                _suppressControlEvents = false;
            }
        }

        private void PopulateDistanceModelOptions()
        {
            _suppressControlEvents = true;
            try
            {
                DistanceModelCombo.Items.Clear();
                DistanceModelCombo.Items.Add(new DistanceModelItem(HoaDistanceModel.Inverse));
                DistanceModelCombo.Items.Add(new DistanceModelItem(HoaDistanceModel.InverseSquare));
                DistanceModelCombo.Items.Add(new DistanceModelItem(HoaDistanceModel.Linear));
                DistanceModelCombo.Items.Add(new DistanceModelItem(HoaDistanceModel.None));
                DistanceModelCombo.SelectedIndex = 0;
            }
            finally
            {
                _suppressControlEvents = false;
            }
        }

        private void PopulateHeadModelOptions()
        {
            _suppressControlEvents = true;
            try
            {
                HeadModelCombo.Items.Clear();
                HeadModelCombo.Items.Add(new HeadModelItem(HoaHeadModel.Both));
                HeadModelCombo.Items.Add(new HeadModelItem(HoaHeadModel.ItdOnly));
                HeadModelCombo.Items.Add(new HeadModelItem(HoaHeadModel.IldOnly));
                HeadModelCombo.Items.Add(new HeadModelItem(HoaHeadModel.Off));
                HeadModelCombo.SelectedIndex = 0;
            }
            finally
            {
                _suppressControlEvents = false;
            }
        }

        private void PopulateSourcePresetOptions()
        {
            _suppressControlEvents = true;
            try
            {
                SourcePresetCombo.Items.Clear();
                SourcePresetCombo.Items.Add(SourcePresetItem.Custom());
                SourcePresetCombo.Items.Add(SourcePresetItem.Fixed(
                    "Front", 0.0, 0.0, 0.70, 0.10));
                SourcePresetCombo.Items.Add(SourcePresetItem.Fixed(
                    "Center", 0.0, 0.0, 0.70, 0.00));
                SourcePresetCombo.Items.Add(SourcePresetItem.Fixed(
                    "Left", -90.0, 0.0, 0.70, 0.10));
                SourcePresetCombo.Items.Add(SourcePresetItem.Fixed(
                    "Right", 90.0, 0.0, 0.70, 0.10));
                SourcePresetCombo.Items.Add(SourcePresetItem.Fixed(
                    "Rear", 180.0, 0.0, 0.70, 0.10));
                SourcePresetCombo.Items.Add(SourcePresetItem.Fixed(
                    "Overhead", 0.0, 90.0, 0.70, 0.10));
                SourcePresetCombo.Items.Add(SourcePresetItem.Fixed(
                    "Wide", 0.0, 0.0, 0.70, 0.80));
                SourcePresetCombo.SelectedIndex = 0;
            }
            finally
            {
                _suppressControlEvents = false;
            }
        }

        private void PopulateSnapOptions()
        {
            _suppressControlEvents = true;
            try
            {
                SnapCombo.Items.Clear();
                SnapCombo.Items.Add(new SnapItem(0, "Off"));
                SnapCombo.Items.Add(new SnapItem(1, "1°"));
                SnapCombo.Items.Add(new SnapItem(5, "5°"));
                SnapCombo.Items.Add(new SnapItem(15, "15°"));
                SnapCombo.Items.Add(new SnapItem(45, "45°"));
                SnapCombo.SelectedIndex = 0;
                _snapDegrees = 0;
            }
            finally
            {
                _suppressControlEvents = false;
            }
        }

        private void SyncControlsFromState()
        {
            _suppressControlEvents = true;
            try
            {
                OrderCombo.SelectedIndex = (int)_state.Order - 1;
                PreviewCombo.SelectedIndex =
                    _state.PreviewMode == HoaPreviewMode.StereoDownmix
                        ? 1
                        : _state.PreviewMode == HoaPreviewMode.Bypass
                            ? 2
                            : 0;
                ExportCombo.SelectedIndex =
                    _state.ExportTarget == HoaExportTarget.IamfEclipsaPlanned ? 3 : 0;
                SourceModeCombo.SelectedIndex =
                    _state.SourceMode == HoaSourceMode.FullMix ? 1 : 0;
                DistanceModelCombo.SelectedIndex = (int)_state.DistanceModel;
                HeadModelCombo.SelectedIndex = (int)_state.HeadModel;
                AzimuthSlider.Value = _state.AzimuthDegrees;
                ElevationSlider.Value = _state.ElevationDegrees;
                DistanceSlider.Value = _state.Distance;
                SpreadSlider.Value = _state.Spread;
                AirSlider.Value = _state.AirAbsorption;
                HeadWidthSlider.Value = _state.HeadWidth;
                RoomAmountSlider.Value = _state.RoomAmount;
                RoomSizeSlider.Value = _state.RoomSize;
                if (!_applyingPreset && SourcePresetCombo != null)
                {
                    SourcePresetCombo.SelectedIndex = 0;
                }
            }
            finally
            {
                _suppressControlEvents = false;
            }

            UpdateSourceModeUi();
        }

        private void UpdateSourceModeUi()
        {
            bool selectedTrack = _state.SourceMode == HoaSourceMode.SelectedTrack;
            if (TrackCombo != null)
            {
                TrackCombo.IsEnabled = selectedTrack;
            }

            if (SourceModeHintText != null)
            {
                SourceModeHintText.Text = selectedTrack
                    ? "Selected track only"
                    : "Full project mix (all tracks + bus FX)";
            }
        }

        private void PopulateTracks()
        {
            if (TrackCombo == null)
            {
                return;
            }

            Project project = _context == null ? null : _context.GetProject();
            int preferredIndex = _context == null ? -1 : _context.SelectedTrackIndex;
            string preferredName = _context == null
                ? string.Empty
                : _context.SelectedTrackName;
            AudioTrack choose = null;

            _suppressControlEvents = true;
            try
            {
                TrackCombo.Items.Clear();
                if (project != null)
                {
                    foreach (Track track in project.Tracks)
                    {
                        var audio = track as AudioTrack;
                        if (audio == null || track is VideoTrack)
                        {
                            // Video tracks have no 7.1 send envelope and are
                            // deliberately excluded from both workspaces.
                            continue;
                        }

                        TrackCombo.Items.Add(new TrackItem(
                            audio.Index + 1, audio.Index, audio.Name));
                    }
                }

                TrackItem match = null;
                if (!string.IsNullOrEmpty(preferredName))
                {
                    foreach (TrackItem item in TrackCombo.Items)
                    {
                        if (string.Equals(item.Name, preferredName,
                            StringComparison.Ordinal))
                        {
                            match = item;
                            break;
                        }
                    }
                }

                if (match == null && preferredIndex >= 0)
                {
                    foreach (TrackItem item in TrackCombo.Items)
                    {
                        if (item.Index != preferredIndex)
                        {
                            continue;
                        }

                        if (string.IsNullOrEmpty(preferredName) ||
                            string.Equals(item.Name, preferredName,
                                StringComparison.Ordinal))
                        {
                            match = item;
                        }

                        break;
                    }
                }

                if (match == null)
                {
                    foreach (TrackItem item in TrackCombo.Items)
                    {
                        AudioTrack audio;
                        if (TryResolveAudioTrack(project, item.Index, out audio) &&
                            audio.Selected)
                        {
                            match = item;
                            break;
                        }
                    }
                }

                if (match == null && TrackCombo.Items.Count > 0)
                {
                    match = (TrackItem)TrackCombo.Items[0];
                }

                TrackCombo.SelectedItem = match;
                if (match != null)
                {
                    TryResolveAudioTrack(project, match.Index, out choose);
                }
            }
            finally
            {
                _suppressControlEvents = false;
            }

            if (_context != null)
            {
                _context.SetSelectedTrack(
                    choose == null ? -1 : choose.Index,
                    choose == null ? null : choose.Name);
            }
        }

        private static bool TryResolveAudioTrack(
            Project project, int index, out AudioTrack track)
        {
            track = null;
            if (project == null || index < 0)
            {
                return false;
            }

            try
            {
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
                // A track deleted between enumeration and lookup must not crash
                // the panel; the caller falls back to a safe empty selection.
                track = null;
                return false;
            }
        }

        private void OnSourceModeChanged(object sender, SelectionChangedEventArgs args)
        {
            if (_suppressControlEvents)
            {
                return;
            }

            var item = SourceModeCombo.SelectedItem as SourceModeItem;
            if (item == null)
            {
                return;
            }

            _state.SourceMode = item.Mode;
            _hoaRenderStatusOverride = null;
            _hoaRenderStatusIsError = false;
            UpdateSourceModeUi();
            UpdateHoaRenderState();
            ScheduleProjectStateSave();
            SetStatus("HOA source: " + HoaSpatialMath.SourceModeLabel(item.Mode));
            Log("HOA render source set to " +
                HoaSpatialMath.SourceModeLabel(item.Mode) + ".");
        }

        private void OnTrackSelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (_suppressControlEvents)
            {
                return;
            }

            var item = TrackCombo.SelectedItem as TrackItem;
            Project project = _context == null ? null : _context.GetProject();
            AudioTrack track = null;
            bool resolved = item != null &&
                TryResolveAudioTrack(project, item.Index, out track);

            if (_context != null)
            {
                _context.SetSelectedTrack(
                    resolved ? track.Index : -1,
                    resolved ? track.Name : null);
            }

            if (item != null)
            {
                Log(resolved
                    ? "HOA track selection: " + item.DisplayName
                    : "Track " + item.DisplayNumber +
                      " is no longer available. Click Refresh Tracks.");
            }
        }

        /// <summary>
        /// Rebuilds the track list without touching HOA or 7.1 automation.
        /// Called when VEGAS reports a track count change.
        /// </summary>
        public void RefreshTracks()
        {
            if (!_loaded)
            {
                return;
            }

            PopulateTracks();
        }

        private void OnRefreshTracksClicked(object sender, RoutedEventArgs args)
        {
            PopulateTracks();
            Log("HOA track list refreshed.");
        }

        private void OnAzimuthSliderChanged(
            object sender, RoutedPropertyChangedEventArgs<double> args)
        {
            if (_suppressControlEvents)
            {
                return;
            }

            double azimuth = HoaSpatialMath.NormalizeAzimuth(
                ApplySnap(args.NewValue));
            _state.AzimuthDegrees = azimuth;
            SetSliderValue(AzimuthSlider, azimuth);
            MarkPresetCustom();
            OnPositionChanged();
        }

        private void OnElevationSliderChanged(
            object sender, RoutedPropertyChangedEventArgs<double> args)
        {
            if (_suppressControlEvents)
            {
                return;
            }

            double elevation = HoaSpatialMath.Clamp(
                ApplySnap(args.NewValue),
                HoaSpatialMath.MinElevation,
                HoaSpatialMath.MaxElevation);
            _state.ElevationDegrees = elevation;
            SetSliderValue(ElevationSlider, elevation);
            MarkPresetCustom();
            OnPositionChanged();
        }

        private void OnDistanceSliderChanged(
            object sender, RoutedPropertyChangedEventArgs<double> args)
        {
            if (_suppressControlEvents)
            {
                return;
            }

            _state.Distance = HoaSpatialMath.Clamp(
                args.NewValue,
                HoaSpatialMath.MinDistance,
                HoaSpatialMath.MaxDistance);
            MarkPresetCustom();
            OnPositionChanged();
        }

        private void OnSpreadSliderChanged(
            object sender, RoutedPropertyChangedEventArgs<double> args)
        {
            if (_suppressControlEvents)
            {
                return;
            }

            _state.Spread = HoaSpatialMath.Clamp(
                args.NewValue,
                HoaSpatialMath.MinSpread,
                HoaSpatialMath.MaxSpread);
            MarkPresetCustom();
            OnPositionChanged();
        }

        private void OnPositionChanged()
        {
            UpdateReadouts();
            UpdateSourceVisuals();
            RefreshWeightInfo();

            if (_loaded && _positionLogTimer != null)
            {
                // One diagnostic line after the gesture settles, not per pixel.
                _positionLogTimer.Stop();
                _positionLogTimer.Start();
            }

            ScheduleProjectStateSave();
        }

        private void OnPositionLogTick(object sender, EventArgs args)
        {
            if (_positionLogTimer != null)
            {
                _positionLogTimer.Stop();
            }

            Log(string.Format(
                CultureInfo.InvariantCulture,
                "HOA source: AZ {0}, EL {1}, D {2}, S {3} · {4} ch (use Preview to audition).",
                HoaSpatialMath.FormatDegrees(_state.AzimuthDegrees),
                HoaSpatialMath.FormatDegrees(_state.ElevationDegrees),
                HoaSpatialMath.FormatUnit(_state.Distance),
                HoaSpatialMath.FormatUnit(_state.Spread),
                _state.ChannelCount));
        }

        private void OnOrderChanged(object sender, SelectionChangedEventArgs args)
        {
            if (_suppressControlEvents)
            {
                return;
            }

            var item = OrderCombo.SelectedItem as OrderItem;
            if (item == null)
            {
                return;
            }

            _state.Order = (HoaOrder)item.Order;
            _hoaRenderStatusOverride = null;
            _hoaRenderStatusIsError = false;
            UpdateOrderPill();
            RefreshWeightInfo();
            UpdateHoaRenderState();
            SetStatus("HOA order: " + HoaSpatialMath.OrderLabel(item.Order));
            Log("HOA order set to " + HoaSpatialMath.OrderLabel(item.Order) +
                " (ACN/SN3D).");
            ScheduleProjectStateSave();
        }

        private void OnPreviewChanged(object sender, SelectionChangedEventArgs args)
        {
            if (_suppressControlEvents)
            {
                return;
            }

            var item = PreviewCombo.SelectedItem as PreviewItem;
            if (item == null)
            {
                return;
            }

            _state.PreviewMode = item.Mode;
            _hoaRenderStatusOverride = null;
            _hoaRenderStatusIsError = false;
            UpdateReadouts();
            UpdateHoaRenderState();
            SetStatus("Preview mode: " + HoaSpatialMath.PreviewLabel(item.Mode));
            Log("HOA preview mode set to " + HoaSpatialMath.PreviewLabel(item.Mode) + ".");
            ScheduleProjectStateSave();
        }

        private void OnExportTargetChanged(object sender, SelectionChangedEventArgs args)
        {
            if (_suppressControlEvents)
            {
                return;
            }

            var item = ExportCombo.SelectedItem as ExportChoiceItem;
            if (item == null)
            {
                return;
            }

            _state.ExportTarget = item.Target;

            if (!string.IsNullOrEmpty(item.Extension))
            {
                string path = HoaOutputPathBox == null
                    ? string.Empty
                    : (HoaOutputPathBox.Text ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(path))
                {
                    path = DefaultHoaOutputPath(item.Extension);
                }
                else
                {
                    path = IOPath.ChangeExtension(path, item.Extension);
                }

                _suppressControlEvents = true;
                try
                {
                    HoaOutputPathBox.Text = path;
                }
                finally
                {
                    _suppressControlEvents = false;
                }
            }

            _hoaRenderStatusOverride = null;
            _hoaRenderStatusIsError = false;
            UpdateHoaRenderState();
            SetStatus("HOA export: " + item.Label);
            Log("HOA export set to " + item.Label +
                (item.Target == HoaExportTarget.IamfEclipsaPlanned
                    ? " (planned for Phase D)."
                    : " (AmbiX render pipeline)."));
            ScheduleProjectStateSave();
        }

        private void OnHoaSideTabChecked(object sender, RoutedEventArgs args)
        {
            if (SourcePanel == null || EffectsPanel == null || MonitorPanel == null)
            {
                return;
            }

            bool source = ReferenceEquals(sender, SourceTabButton);
            bool effects = ReferenceEquals(sender, EffectsTabButton);
            SourcePanel.Visibility = source ? Visibility.Visible : Visibility.Collapsed;
            EffectsPanel.Visibility = effects ? Visibility.Visible : Visibility.Collapsed;
            MonitorPanel.Visibility = !source && !effects
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void OnDistanceModelChanged(object sender, SelectionChangedEventArgs args)
        {
            if (_suppressControlEvents)
            {
                return;
            }

            var item = DistanceModelCombo.SelectedItem as DistanceModelItem;
            if (item == null)
            {
                return;
            }

            _state.DistanceModel = item.Model;
            UpdateHoaRenderState();
            SetStatus("Distance model: " + HoaSpatialMath.DistanceModelLabel(item.Model));
            Log("HOA distance model set to " +
                HoaSpatialMath.DistanceModelLabel(item.Model) + ".");
            ScheduleProjectStateSave();
        }

        private void OnHeadModelChanged(object sender, SelectionChangedEventArgs args)
        {
            if (_suppressControlEvents)
            {
                return;
            }

            var item = HeadModelCombo.SelectedItem as HeadModelItem;
            if (item == null)
            {
                return;
            }

            _state.HeadModel = item.Model;
            UpdateHoaRenderState();
            SetStatus("Head model: " + HoaSpatialMath.HeadModelLabel(item.Model));
            Log("HOA binaural head model set to " +
                HoaSpatialMath.HeadModelLabel(item.Model) + ".");
            ScheduleProjectStateSave();
        }

        private void OnAirChanged(
            object sender, RoutedPropertyChangedEventArgs<double> args)
        {
            if (_suppressControlEvents)
            {
                return;
            }

            _state.AirAbsorption = args.NewValue;
            UpdateReadouts();
            UpdateHoaRenderState();
            ScheduleProjectStateSave();
        }

        private void OnHeadWidthChanged(
            object sender, RoutedPropertyChangedEventArgs<double> args)
        {
            if (_suppressControlEvents)
            {
                return;
            }

            _state.HeadWidth = args.NewValue;
            UpdateReadouts();
            UpdateHoaRenderState();
            ScheduleProjectStateSave();
        }

        private void OnRoomAmountChanged(
            object sender, RoutedPropertyChangedEventArgs<double> args)
        {
            if (_suppressControlEvents)
            {
                return;
            }

            _state.RoomAmount = args.NewValue;
            UpdateReadouts();
            UpdateHoaRenderState();
            ScheduleProjectStateSave();
        }

        private void OnRoomSizeChanged(
            object sender, RoutedPropertyChangedEventArgs<double> args)
        {
            if (_suppressControlEvents)
            {
                return;
            }

            _state.RoomSize = args.NewValue;
            UpdateReadouts();
            UpdateHoaRenderState();
            ScheduleProjectStateSave();
        }

        private void OnResetEffectsClicked(object sender, RoutedEventArgs args)
        {
            _state.ResetEffects();
            _hoaRenderStatusOverride = null;
            _hoaRenderStatusIsError = false;

            _suppressControlEvents = true;
            try
            {
                DistanceModelCombo.SelectedIndex = 0;
                HeadModelCombo.SelectedIndex = 0;
                AirSlider.Value = 0.0;
                HeadWidthSlider.Value = 1.0;
                RoomAmountSlider.Value = 0.0;
                RoomSizeSlider.Value = 0.5;
            }
            finally
            {
                _suppressControlEvents = false;
            }

            UpdateReadouts();
            UpdateHoaRenderState();
            ScheduleProjectStateSave();
            SetStatus("HOA effects reset.");
            Log("HOA effects reset: inverse distance, no air absorption, " +
                "ITD+ILD head model, default width, dry room.");
        }

        private void OnSourcePresetChanged(object sender, SelectionChangedEventArgs args)
        {
            if (_suppressControlEvents || _applyingPreset)
            {
                return;
            }

            var item = SourcePresetCombo.SelectedItem as SourcePresetItem;
            if (item == null || item.IsCustom)
            {
                return;
            }

            _applyingPreset = true;
            try
            {
                _state.AzimuthDegrees = item.AzimuthDegrees;
                _state.ElevationDegrees = item.ElevationDegrees;
                _state.Distance = item.Distance;
                _state.Spread = item.Spread;
                SyncControlsFromState();
            }
            finally
            {
                _applyingPreset = false;
            }

            _hoaRenderStatusOverride = null;
            _hoaRenderStatusIsError = false;
            OnPositionChanged();
            UpdateHoaRenderState();
            SetStatus("Source preset: " + item.Label);
            Log("HOA source preset applied: " + item.Label + ".");
            ScheduleProjectStateSave();
        }

        private void OnSnapChanged(object sender, SelectionChangedEventArgs args)
        {
            if (_suppressControlEvents)
            {
                return;
            }

            var item = SnapCombo.SelectedItem as SnapItem;
            if (item == null)
            {
                return;
            }

            _snapDegrees = item.Degrees;
            SetStatus("Snap: " + item.Label);
            Log("HOA angle snap set to " + item.Label + ".");
            ScheduleProjectStateSave();
        }

        private void OnAbClicked(object sender, RoutedEventArgs args)
        {
            if (_activeSlot == 'A')
            {
                _slotA = _state.Clone();
                _state.CopyFrom(_slotB);
                _activeSlot = 'B';
            }
            else
            {
                _slotB = _state.Clone();
                _state.CopyFrom(_slotA);
                _activeSlot = 'A';
            }

            SyncControlsFromState();
            UpdateReadouts();
            UpdateSourceVisuals();
            RefreshWeightInfo();
            UpdateHoaRenderState();
            if (AbStatusText != null)
            {
                AbStatusText.Text = "ACTIVE " + _activeSlot;
            }

            SetStatus("A/B: recalled slot " + _activeSlot + ".");
            Log("HOA A/B compare recalled slot " + _activeSlot +
                " (stored the previous state in the other slot).");
            ScheduleProjectStateSave();
        }

        private double ApplySnap(double value)
        {
            if (_snapDegrees <= 0)
            {
                return value;
            }

            return Math.Round(value / _snapDegrees) * _snapDegrees;
        }

        private void MarkPresetCustom()
        {
            if (_applyingPreset || SourcePresetCombo == null)
            {
                return;
            }

            if (SourcePresetCombo.SelectedIndex != 0)
            {
                _suppressControlEvents = true;
                try
                {
                    SourcePresetCombo.SelectedIndex = 0;
                }
                finally
                {
                    _suppressControlEvents = false;
                }
            }
        }

        private void UpdateHoaMeters(HoaPreviewAnalysis analysis)
        {
            if (analysis == null)
            {
                if (HoaLeftMeter != null)
                {
                    HoaLeftMeter.Value = 0;
                }

                if (HoaRightMeter != null)
                {
                    HoaRightMeter.Value = 0;
                }

                if (HoaLeftDbText != null)
                {
                    HoaLeftDbText.Text = "—";
                }

                if (HoaRightDbText != null)
                {
                    HoaRightDbText.Text = "—";
                }

                if (HoaPeakText != null)
                {
                    HoaPeakText.Text = "PEAK —";
                }

                if (HoaCorrelationText != null)
                {
                    HoaCorrelationText.Text = "CORR —";
                }

                return;
            }

            HoaLeftMeter.Value = MeterFraction(analysis.LeftRmsDb);
            HoaRightMeter.Value = MeterFraction(analysis.RightRmsDb);
            HoaLeftDbText.Text = FormatMeterDb(analysis.LeftRmsDb);
            HoaRightDbText.Text = FormatMeterDb(analysis.RightRmsDb);
            HoaPeakText.Text = "PEAK " + FormatMeterDb(analysis.PeakDb);
            HoaCorrelationText.Text = "CORR " + analysis.Correlation.ToString(
                "+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
        }

        private static double MeterFraction(double db)
        {
            return HoaSpatialMath.Clamp(1.0 + db / 60.0, 0.0, 1.0);
        }

        private static string FormatMeterDb(double db)
        {
            return db <= -95.0
                ? "-\u221E"
                : db.ToString("0.0", CultureInfo.InvariantCulture) + " dB";
        }

        private void OnResetPositionClicked(object sender, RoutedEventArgs args)
        {
            _state.Reset();
            _hoaRenderStatusOverride = null;
            _hoaRenderStatusIsError = false;
            SyncControlsFromState();
            UpdateReadouts();
            UpdateSourceVisuals();
            RefreshWeightInfo();
            SetStatus("3D source position reset.");
            Log("HOA source position reset to AZ " +
                HoaSpatialMath.FormatDegrees(_state.AzimuthDegrees) + ", EL " +
                HoaSpatialMath.FormatDegrees(_state.ElevationDegrees) + ", D " +
                HoaSpatialMath.FormatUnit(_state.Distance) + ".");
            ScheduleProjectStateSave();
        }

        private void OnHoaOutputPathChanged(object sender, TextChangedEventArgs args)
        {
            if (_suppressControlEvents)
            {
                return;
            }

            SyncExportChoiceFromOutputPath();
            _hoaRenderStatusOverride = null;
            _hoaRenderStatusIsError = false;
            UpdateHoaRenderState();
            ScheduleProjectStateSave();
        }

        /// <summary>
        /// Keeps the Export selector aligned with a manually typed/selected
        /// output extension. It never changes an IAMF selection.
        /// </summary>
        private void SyncExportChoiceFromOutputPath()
        {
            if (ExportCombo == null || HoaOutputPathBox == null ||
                _state.ExportTarget == HoaExportTarget.IamfEclipsaPlanned)
            {
                return;
            }

            string extension = IOPath.GetExtension(
                HoaOutputPathBox.Text ?? string.Empty).ToLowerInvariant();
            int index = extension == ".w64"
                ? 1
                : extension == ".flac"
                    ? 2
                    : extension == ".wav"
                        ? 0
                        : -1;

            if (index < 0 || ExportCombo.SelectedIndex == index)
            {
                return;
            }

            _suppressControlEvents = true;
            try
            {
                ExportCombo.SelectedIndex = index;
            }
            finally
            {
                _suppressControlEvents = false;
            }
        }

        private static string DefaultHoaOutputPath(string extension)
        {
            string videos = Environment.GetFolderPath(
                Environment.SpecialFolder.MyVideos);
            string directory = string.IsNullOrEmpty(videos)
                ? Directory.GetCurrentDirectory()
                : videos;
            return IOPath.Combine(directory, "TellTale_HOA_AmbiX" + extension);
        }

        private void OnHoaNumericFieldLostFocus(object sender, RoutedEventArgs args)
        {
            CommitHoaNumericField(sender as TextBox);
        }

        private void OnHoaNumericFieldKeyDown(object sender, KeyEventArgs args)
        {
            if (args.Key != Key.Enter)
            {
                return;
            }

            CommitHoaNumericField(sender as TextBox);
            args.Handled = true;
        }

        private void CommitHoaNumericField(TextBox box)
        {
            if (box == null)
            {
                return;
            }

            double value;
            if (!TryParseHoaNumber(box.Text, out value))
            {
                SetStatus("Enter a valid numeric HOA value.");
                UpdateReadouts();
                return;
            }

            bool positionChanged = false;
            if (ReferenceEquals(box, AzSliderText))
            {
                value = HoaSpatialMath.NormalizeAzimuth(ApplySnap(value));
                _state.AzimuthDegrees = value;
                SetSliderValue(AzimuthSlider, value);
                positionChanged = true;
            }
            else if (ReferenceEquals(box, ElSliderText))
            {
                value = HoaSpatialMath.Clamp(
                    ApplySnap(value),
                    HoaSpatialMath.MinElevation,
                    HoaSpatialMath.MaxElevation);
                _state.ElevationDegrees = value;
                SetSliderValue(ElevationSlider, value);
                positionChanged = true;
            }
            else if (ReferenceEquals(box, DistSliderText))
            {
                value = HoaSpatialMath.Clamp(
                    value,
                    HoaSpatialMath.MinDistance,
                    HoaSpatialMath.MaxDistance);
                _state.Distance = value;
                SetSliderValue(DistanceSlider, value);
                positionChanged = true;
            }
            else if (ReferenceEquals(box, SpreadSliderText))
            {
                value = HoaSpatialMath.Clamp(
                    value,
                    HoaSpatialMath.MinSpread,
                    HoaSpatialMath.MaxSpread);
                _state.Spread = value;
                SetSliderValue(SpreadSlider, value);
                positionChanged = true;
            }
            else if (ReferenceEquals(box, AirValueText))
            {
                value = HoaSpatialMath.Clamp(value, 0.0, 1.0);
                _state.AirAbsorption = value;
                SetSliderValue(AirSlider, value);
            }
            else if (ReferenceEquals(box, HeadWidthValueText))
            {
                value = HoaSpatialMath.Clamp(value, 0.5, 1.5);
                _state.HeadWidth = value;
                SetSliderValue(HeadWidthSlider, value);
            }
            else if (ReferenceEquals(box, RoomAmountValueText))
            {
                value = HoaSpatialMath.Clamp(value, 0.0, 1.0);
                _state.RoomAmount = value;
                SetSliderValue(RoomAmountSlider, value);
            }
            else if (ReferenceEquals(box, RoomSizeValueText))
            {
                value = HoaSpatialMath.Clamp(value, 0.0, 1.0);
                _state.RoomSize = value;
                SetSliderValue(RoomSizeSlider, value);
            }
            else
            {
                return;
            }

            _hoaRenderStatusOverride = null;
            _hoaRenderStatusIsError = false;
            if (positionChanged)
            {
                MarkPresetCustom();
                OnPositionChanged();
            }
            else
            {
                UpdateReadouts();
                UpdateHoaRenderState();
                ScheduleProjectStateSave();
            }
        }

        private void SetSliderValue(Slider slider, double value)
        {
            if (slider == null)
            {
                return;
            }

            _suppressControlEvents = true;
            try
            {
                slider.Value = value;
            }
            finally
            {
                _suppressControlEvents = false;
            }
        }

        private static bool TryParseHoaNumber(string text, out double value)
        {
            value = 0.0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string cleaned = text
                .Replace("\u00B0", string.Empty)
                .Replace("+", string.Empty)
                .Trim();

            const NumberStyles style = NumberStyles.Float;
            return double.TryParse(cleaned, style, CultureInfo.InvariantCulture,
                       out value) ||
                   double.TryParse(cleaned, style, CultureInfo.CurrentCulture,
                       out value);
        }

        private void OnBrowseHoaOutputClicked(object sender, RoutedEventArgs args)
        {
            string current = HoaOutputPathBox == null
                ? string.Empty
                : (HoaOutputPathBox.Text ?? string.Empty).Trim();
            string extension = IOPath.GetExtension(current);
            if (string.IsNullOrEmpty(extension))
            {
                extension = ".wav";
            }

            var dialog = new SaveFileDialog
            {
                Title = "Export HOA AmbiX Master",
                Filter = "AmbiX WAV (*.wav)|*.wav|" +
                         "Wave64 (*.w64)|*.w64|" +
                         "FLAC (*.flac)|*.flac",
                DefaultExt = extension,
                AddExtension = true,
                OverwritePrompt = true,
                FileName = string.IsNullOrEmpty(current)
                    ? "TellTale_HOA_AmbiX" + extension
                    : IOPath.GetFileName(current)
            };

            string directory = IOPath.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                dialog.InitialDirectory = directory;
            }

            if (dialog.ShowDialog() == true)
            {
                HoaOutputPathBox.Text = dialog.FileName;
                _hoaRenderStatusOverride = null;
            _hoaRenderStatusIsError = false;
                UpdateHoaRenderState();
            }
        }

        private void OnHoaExportClicked(object sender, RoutedEventArgs args)
        {
            if (_rendering)
            {
                return;
            }

            if (HoaExportButton == null || !HoaExportButton.IsEnabled)
            {
                Log(HoaRenderStatusText == null
                    ? "HOA export is not ready."
                    : HoaRenderStatusText.Text);
                return;
            }

            string path = HoaOutputPathBox == null
                ? string.Empty
                : (HoaOutputPathBox.Text ?? string.Empty).Trim();

            SetRenderBusy(true);
            try
            {
                HoaRenderResult result = HoaRenderOrchestrator.RenderMaster(
                    _context, _state, path, OnHoaRenderProgress);
                _hoaRenderStatusOverride = result.Message;
                _hoaRenderStatusIsError = !result.Success;
                SetStatus(result.Success
                    ? result.Message
                    : "HOA export failed: " + result.Message);

                if (!result.Success)
                {
                    Log(result.Message);
                }
            }
            catch (Exception ex)
            {
                string message = "HOA export failed: " +
                    HostScriptBridge.DescribeException(ex);
                _hoaRenderStatusOverride = message;
                _hoaRenderStatusIsError = true;
                Log(message);
            }
            finally
            {
                SetRenderBusy(false);
            }
        }

        private void OnHoaPreviewClicked(object sender, RoutedEventArgs args)
        {
            if (_rendering)
            {
                return;
            }

            SetRenderBusy(true);
            try
            {
                HoaRenderResult result = HoaRenderOrchestrator.RenderPreview(
                    _context, _state, OnHoaRenderProgress);
                _hoaRenderStatusOverride = result.Message;
                _hoaRenderStatusIsError = !result.Success;

                if (!result.Success)
                {
                    UpdateHoaMeters(null);
                    SetStatus("HOA preview failed: " + result.Message);
                    Log(result.Message);
                    return;
                }

                UpdateHoaMeters(result.Analysis);
                PlayHoaPreview(result.OutputPath);
            }
            catch (Exception ex)
            {
                string message = "HOA preview failed: " +
                    HostScriptBridge.DescribeException(ex);
                _hoaRenderStatusOverride = message;
                _hoaRenderStatusIsError = true;
                Log(message);
            }
            finally
            {
                SetRenderBusy(false);
            }
        }

        private void OnHoaRenderProgress(string message)
        {
            Log(message);

            if (HoaRenderStatusText != null)
            {
                HoaRenderStatusText.Text = message;
                HoaRenderStatusText.Foreground =
                    new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA));
                HoaRenderStatusText.UpdateLayout();
            }

            try
            {
                System.Windows.Forms.Application.DoEvents();
            }
            catch (Exception)
            {
                // UI pumping is best-effort only.
            }
        }

        private void PlayHoaPreview(string path)
        {
            try
            {
                if (_soundPlayer != null)
                {
                    _soundPlayer.Stop();
                    _soundPlayer.Dispose();
                }

                _soundPlayer = new SoundPlayer(path);
                _soundPlayer.Play();

                string mode = HoaSpatialMath.PreviewLabel(_state.PreviewMode);
                SetStatus("Playing " + mode + " HOA preview.");
                Log("HOA preview playback started (" + mode + ").");
            }
            catch (Exception ex)
            {
                Log("HOA preview playback failed: " + ex.Message);
            }
        }

        private void SetRenderBusy(bool busy)
        {
            _rendering = busy;

            try
            {
                Mouse.OverrideCursor = busy ? Cursors.Wait : null;
            }
            catch (Exception)
            {
                // Cursor override is best effort.
            }

            UpdateHoaRenderState();
        }

        private void UpdateHoaRenderState()
        {
            if (HoaExportButton == null || HoaPreviewButton == null)
            {
                return;
            }

            string path = HoaOutputPathBox == null
                ? string.Empty
                : (HoaOutputPathBox.Text ?? string.Empty).Trim();
            string extension = IOPath.GetExtension(path).ToLowerInvariant();
            int channels = _state.ChannelCount;

            string error = null;
            if (_state.ExportTarget == HoaExportTarget.IamfEclipsaPlanned)
            {
                error = "IAMF / Eclipsa is planned for Phase D. " +
                    "Choose HOA WAV/FLAC (AmbiX).";
            }
            else if (string.IsNullOrEmpty(path))
            {
                error = "Choose an HOA output path.";
            }
            else if (extension != ".wav" &&
                     extension != ".w64" &&
                     extension != ".flac")
            {
                error = "HOA master output must be .wav, .w64, or .flac.";
            }
            else if (extension == ".flac" && channels > 8)
            {
                error = "FLAC supports at most 8 channels. " +
                    "Choose WAV or W64 for 2nd/3rd order masters.";
            }

            HoaExportButton.IsEnabled = error == null && !_rendering;
            HoaExportButton.ToolTip = error ??
                ("Render the selected track as a " + channels +
                 "-channel AmbiX master.");

            HoaPreviewButton.Content = _state.PreviewMode == HoaPreviewMode.Binaural
                ? "Preview (Binaural)"
                : _state.PreviewMode == HoaPreviewMode.Bypass
                    ? "Preview (Bypass)"
                    : "Preview (Stereo)";
            HoaPreviewButton.IsEnabled = !_rendering;
            HoaPreviewButton.ToolTip =
                "Render a short preview of the selected track without touching " +
                "the 7.1 bus sends.";

            if (HoaRenderStatusText == null)
            {
                return;
            }

            if (error != null)
            {
                HoaRenderStatusText.Text = error;
                HoaRenderStatusText.Foreground =
                    new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
            }
            else if (!_rendering &&
                     !string.IsNullOrEmpty(_hoaRenderStatusOverride))
            {
                HoaRenderStatusText.Text = _hoaRenderStatusOverride;
                HoaRenderStatusText.Foreground = new SolidColorBrush(
                    _hoaRenderStatusIsError
                        ? Color.FromRgb(0xFC, 0xA5, 0xA5)
                        : Color.FromRgb(0x86, 0xEF, 0xAC));
            }
            else
            {
                HoaRenderStatusText.Text = "Ready: " + extension + " · " +
                    channels + " ch · AmbiX ACN/SN3D";
                HoaRenderStatusText.Foreground =
                    new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA));
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

        private void UpdateOrderPill()
        {
            if (OrderPillText == null)
            {
                return;
            }

            OrderPillText.Text = HoaSpatialMath.OrderShortLabel((int)_state.Order) +
                " · " + _state.ChannelCount + " ch";
        }

        private void UpdateReadouts()
        {
            UpdateOrderPill();

            if (AzValueText != null)
            {
                AzValueText.Text = HoaSpatialMath.FormatDegrees(_state.AzimuthDegrees);
            }

            if (ElValueText != null)
            {
                ElValueText.Text = HoaSpatialMath.FormatDegrees(_state.ElevationDegrees);
            }

            if (DistValueText != null)
            {
                DistValueText.Text = HoaSpatialMath.FormatUnit(_state.Distance);
            }

            if (SpreadValueText != null)
            {
                SpreadValueText.Text = HoaSpatialMath.FormatUnit(_state.Spread);
            }

            if (AzSliderText != null)
            {
                AzSliderText.Text = _state.AzimuthDegrees.ToString(
                    "0.0", CultureInfo.InvariantCulture);
            }

            if (ElSliderText != null)
            {
                ElSliderText.Text = _state.ElevationDegrees.ToString(
                    "0.0", CultureInfo.InvariantCulture);
            }

            if (DistSliderText != null)
            {
                DistSliderText.Text = HoaSpatialMath.FormatUnit(_state.Distance);
            }

            if (SpreadSliderText != null)
            {
                SpreadSliderText.Text = HoaSpatialMath.FormatUnit(_state.Spread);
            }

            if (AirValueText != null)
            {
                AirValueText.Text = _state.AirAbsorption.ToString(
                    "0.00", CultureInfo.InvariantCulture);
            }

            if (HeadWidthValueText != null)
            {
                HeadWidthValueText.Text = _state.HeadWidth.ToString(
                    "0.00", CultureInfo.InvariantCulture);
            }

            if (RoomAmountValueText != null)
            {
                RoomAmountValueText.Text = _state.RoomAmount.ToString(
                    "0.00", CultureInfo.InvariantCulture);
            }

            if (RoomSizeValueText != null)
            {
                RoomSizeValueText.Text = _state.RoomSize.ToString(
                    "0.00", CultureInfo.InvariantCulture);
            }

            if (StageReadoutText != null)
            {
                string elevationHint = _state.ElevationDegrees >= 80.0
                    ? " · OVERHEAD"
                    : _state.ElevationDegrees <= -80.0
                        ? " · FLOOR"
                        : string.Empty;

                StageReadoutText.Text = "AZ " +
                    HoaSpatialMath.FormatDegrees(_state.AzimuthDegrees) + " · EL " +
                    HoaSpatialMath.FormatDegrees(_state.ElevationDegrees) + " · D " +
                    HoaSpatialMath.FormatUnit(_state.Distance) + elevationHint;
            }

            if (HeaderPreviewText != null)
            {
                HeaderPreviewText.Text = HoaSpatialMath.PreviewLabel(_state.PreviewMode);
            }
        }

        private void RefreshWeightInfo()
        {
            if (WeightInfoText == null)
            {
                return;
            }

            var position = new HoaSourcePosition
            {
                azimuth_deg = (float)_state.AzimuthDegrees,
                elevation_deg = (float)_state.ElevationDegrees,
                distance = (float)_state.Distance,
                spread = (float)_state.Spread,
                order = (uint)_state.Order
            };

            float[] weights;
            string error;
            if (NativeBridge.TryCalculateHoaWeights(position, out weights, out error))
            {
                double energy = 0.0;
                foreach (float weight in weights)
                {
                    energy += weight * weight;
                }

                WeightInfoText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} ch · ACN/SN3D · Σg² {1:0.00}",
                    weights.Length,
                    energy);
                return;
            }

            WeightInfoText.Text = "HOA native core unavailable — " + error;
        }

        private void OnStageSizeChanged(object sender, SizeChangedEventArgs args)
        {
            DrawStage();
        }

        private void OnStageMouseDown(object sender, MouseButtonEventArgs args)
        {
            if (!_loaded)
            {
                return;
            }

            Point point = args.GetPosition(StageCanvas);
            _dragging = true;
            _elevationDrag = point.X >= _railX - 20.0;
            StageCanvas.CaptureMouse();
            ApplyPointer(point);
        }

        private void OnStageMouseMove(object sender, MouseEventArgs args)
        {
            if (!_dragging || !_loaded)
            {
                return;
            }

            ApplyPointer(args.GetPosition(StageCanvas));
        }

        private void OnStageMouseUp(object sender, MouseButtonEventArgs args)
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            StageCanvas.ReleaseMouseCapture();
        }

        private void OnStageLostMouseCapture(object sender, MouseEventArgs args)
        {
            _dragging = false;
        }

        private void ApplyPointer(Point point)
        {
            _suppressControlEvents = true;
            try
            {
                if (_elevationDrag)
                {
                    double elevation = ApplySnap(HoaSpatialMath.ScreenYToElevation(
                        point.Y, _centerY, _railHalf));
                    _state.ElevationDegrees = elevation;
                    ElevationSlider.Value = elevation;
                }
                else
                {
                    double azimuth;
                    double distance;
                    HoaSpatialMath.FromOverheadScreen(
                        point.X, point.Y, _centerX, _centerY, _radius,
                        out azimuth, out distance);
                    azimuth = HoaSpatialMath.NormalizeAzimuth(ApplySnap(azimuth));
                    _state.AzimuthDegrees = azimuth;
                    _state.Distance = distance;
                    AzimuthSlider.Value = azimuth;
                    DistanceSlider.Value = distance;
                }
            }
            finally
            {
                _suppressControlEvents = false;
            }

            MarkPresetCustom();
            OnPositionChanged();
        }

        private void DrawStage()
        {
            if (!_loaded || StageCanvas == null)
            {
                return;
            }

            StageCanvas.Children.Clear();
            _puck = null;
            _spreadHalo = null;
            _elevationPuck = null;
            _azimuthLine = null;

            double width = StageCanvas.ActualWidth;
            double height = StageCanvas.ActualHeight;
            if (width < 160 || height < 160)
            {
                return;
            }

            const double railWidth = 104.0;
            double areaWidth = Math.Max(120.0, width - railWidth);
            _centerX = areaWidth / 2.0 + 10.0;
            _centerY = height / 2.0 + 4.0;
            _radius = Math.Min(areaWidth * 0.40, (height - 78.0) * 0.44);
            _radius = Math.Max(60.0, _radius);

            _railHalf = Math.Min(_radius * 0.86, (height - 150.0) / 2.0);
            if (_railHalf < 50.0)
            {
                _railHalf = 50.0;
            }

            _railX = width - 58.0;
            _railTop = _centerY - _railHalf;
            _railBottom = _centerY + _railHalf;

            var roomFill = new RadialGradientBrush();
            roomFill.GradientStops.Add(new GradientStop(
                Color.FromRgb(0x1B, 0x1B, 0x22), 0.0));
            roomFill.GradientStops.Add(new GradientStop(
                Color.FromRgb(0x14, 0x14, 0x17), 1.0));

            var room = new Ellipse
            {
                Width = _radius * 2.0,
                Height = _radius * 2.0,
                Stroke = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)),
                StrokeThickness = 1.5,
                Fill = roomFill
            };
            Canvas.SetLeft(room, _centerX - _radius);
            Canvas.SetTop(room, _centerY - _radius);
            StageCanvas.Children.Add(room);

            for (int ring = 1; ring <= 3; ring++)
            {
                double factor = ring / 4.0;
                var ringEllipse = new Ellipse
                {
                    Width = _radius * 2.0 * factor,
                    Height = _radius * 2.0 * factor,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x31)),
                    StrokeThickness = 1,
                    Opacity = 0.65
                };
                Canvas.SetLeft(ringEllipse, _centerX - _radius * factor);
                Canvas.SetTop(ringEllipse, _centerY - _radius * factor);
                StageCanvas.Children.Add(ringEllipse);
            }

            // Azimuth frame: eight 45-degree spokes with cardinal labels.
            for (int spoke = 0; spoke < 8; spoke++)
            {
                double angle = spoke * 45.0 * Math.PI / 180.0;
                double sin = Math.Sin(angle);
                double cos = Math.Cos(angle);
                var line = new Line
                {
                    X1 = _centerX + sin * _radius * 0.16,
                    Y1 = _centerY - cos * _radius * 0.16,
                    X2 = _centerX + sin * _radius,
                    Y2 = _centerY - cos * _radius,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x2B)),
                    StrokeThickness = 1,
                    Opacity = 0.55
                };
                StageCanvas.Children.Add(line);
            }

            AddStageLabel("0\u00B0", _centerX, _centerY - _radius * 0.90);
            AddStageLabel("+90\u00B0", _centerX + _radius * 0.90, _centerY);
            AddStageLabel("\u00B1180\u00B0", _centerX, _centerY + _radius * 0.86);
            AddStageLabel("-90\u00B0", _centerX - _radius * 0.90, _centerY);

            var listenerRing = new Ellipse
            {
                Width = 22,
                Height = 22,
                Stroke = new SolidColorBrush(Color.FromArgb(140, 0x38, 0xBD, 0xF8)),
                StrokeThickness = 1,
                Fill = new SolidColorBrush(Color.FromArgb(24, 0x38, 0xBD, 0xF8))
            };
            Canvas.SetLeft(listenerRing, _centerX - 11);
            Canvas.SetTop(listenerRing, _centerY - 11);
            StageCanvas.Children.Add(listenerRing);

            var listenerDot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6))
            };
            Canvas.SetLeft(listenerDot, _centerX - 3.5);
            Canvas.SetTop(listenerDot, _centerY - 3.5);
            StageCanvas.Children.Add(listenerDot);

            var frontMarker = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M -5 2 L 0 -6 L 5 2"),
                Stroke = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(frontMarker, _centerX);
            Canvas.SetTop(frontMarker, _centerY - 15);
            StageCanvas.Children.Add(frontMarker);

            DrawElevationRail();

            var haloFill = new RadialGradientBrush();
            haloFill.GradientStops.Add(new GradientStop(
                Color.FromArgb(52, 0x38, 0xBD, 0xF8), 0.0));
            haloFill.GradientStops.Add(new GradientStop(
                Color.FromArgb(0, 0x38, 0xBD, 0xF8), 1.0));

            _spreadHalo = new Ellipse
            {
                IsHitTestVisible = false,
                Fill = haloFill,
                Stroke = new SolidColorBrush(Color.FromArgb(95, 0x38, 0xBD, 0xF8)),
                StrokeThickness = 1
            };
            StageCanvas.Children.Add(_spreadHalo);

            _azimuthLine = new Line
            {
                X1 = _centerX,
                Y1 = _centerY,
                X2 = _centerX,
                Y2 = _centerY,
                Stroke = new SolidColorBrush(Color.FromArgb(150, 0x2D, 0xD4, 0xBF)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 },
                IsHitTestVisible = false
            };
            StageCanvas.Children.Add(_azimuthLine);

            _puck = new Ellipse
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
            StageCanvas.Children.Add(_puck);

            _elevationPuck = new Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = new SolidColorBrush(Color.FromRgb(0x2D, 0xD4, 0xBF)),
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
                Effect = new DropShadowEffect
                {
                    Color = Color.FromRgb(0x2D, 0xD4, 0xBF),
                    BlurRadius = 12,
                    ShadowDepth = 0,
                    Opacity = 0.85
                }
            };
            StageCanvas.Children.Add(_elevationPuck);

            UpdateSourceVisuals();
        }

        private void DrawElevationRail()
        {
            var railTrack = new Rectangle
            {
                Width = 8,
                Height = _railBottom - _railTop,
                RadiusX = 4,
                RadiusY = 4,
                Fill = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x14)),
                Stroke = new SolidColorBrush(Color.FromRgb(0x32, 0x32, 0x38)),
                StrokeThickness = 1
            };
            Canvas.SetLeft(railTrack, _railX - 4);
            Canvas.SetTop(railTrack, _railTop);
            StageCanvas.Children.Add(railTrack);

            AddStageLabel("ELEV", _railX - 2, _railTop - 20);

            for (int step = -90; step <= 90; step += 30)
            {
                double y = HoaSpatialMath.ElevationToScreenY(
                    step, _centerY, _railHalf);
                var tick = new Line
                {
                    X1 = _railX + 6,
                    Y1 = y,
                    X2 = _railX + 12,
                    Y2 = y,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
                    StrokeThickness = 1
                };
                StageCanvas.Children.Add(tick);

                var label = new TextBlock
                {
                    Text = (step > 0 ? "+" : string.Empty) +
                        step.ToString(CultureInfo.InvariantCulture) + "\u00B0",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x71, 0x71, 0x7A))
                };
                Canvas.SetLeft(label, _railX + 16);
                Canvas.SetTop(label, y - 7);
                StageCanvas.Children.Add(label);
            }
        }

        private void AddStageLabel(string text, double x, double y)
        {
            var label = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x71, 0x71, 0x7A))
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, x - label.DesiredSize.Width / 2.0);
            Canvas.SetTop(label, y - label.DesiredSize.Height / 2.0);
            StageCanvas.Children.Add(label);
        }

        private void UpdateSourceVisuals()
        {
            if (_puck == null)
            {
                return;
            }

            StagePoint point = HoaSpatialMath.ToOverheadScreen(
                _state.AzimuthDegrees,
                _state.Distance,
                _centerX,
                _centerY,
                _radius);

            Canvas.SetLeft(_puck, point.X - 11.0);
            Canvas.SetTop(_puck, point.Y - 11.0);

            if (_spreadHalo != null)
            {
                double haloRadius = HoaSpatialMath.SpreadHaloRadius(
                    _state.Spread, _radius);
                _spreadHalo.Width = haloRadius * 2.0;
                _spreadHalo.Height = haloRadius * 2.0;
                _spreadHalo.Opacity = 0.35 + 0.45 * _state.Spread;
                Canvas.SetLeft(_spreadHalo, point.X - haloRadius);
                Canvas.SetTop(_spreadHalo, point.Y - haloRadius);
            }

            if (_azimuthLine != null)
            {
                _azimuthLine.X2 = point.X;
                _azimuthLine.Y2 = point.Y;
            }

            if (_elevationPuck != null)
            {
                double railY = HoaSpatialMath.ElevationToScreenY(
                    _state.ElevationDegrees, _centerY, _railHalf);
                Canvas.SetLeft(_elevationPuck, _railX - 7.0);
                Canvas.SetTop(_elevationPuck, railY - 7.0);
            }
        }

        private void SetStatus(string message)
        {
            if (StatusFooterText != null && !string.IsNullOrEmpty(message))
            {
                StatusFooterText.Text = message;
            }
        }

        private void Log(string message)
        {
            SetStatus(message);
            if (_context != null)
            {
                _context.WriteDiagnostic(message);
                return;
            }

            AppendLogLine(message);
        }

        private void OnDiagnosticMessage(object sender, WorkspaceLogEventArgs args)
        {
            AppendLogLine(args.Message);
        }

        private void AppendLogLine(string message)
        {
            if (LogBox == null || string.IsNullOrEmpty(message))
            {
                return;
            }

            LogBox.AppendText(
                DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) +
                "  " + message + Environment.NewLine);
            LogBox.ScrollToEnd();
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

        private sealed class OrderItem
        {
            public OrderItem(int order)
            {
                Order = order;
            }

            public int Order { get; private set; }

            public override string ToString()
            {
                return HoaSpatialMath.OrderLabel(Order);
            }
        }

        private sealed class PreviewItem
        {
            public PreviewItem(HoaPreviewMode mode)
            {
                Mode = mode;
            }

            public HoaPreviewMode Mode { get; private set; }

            public override string ToString()
            {
                return HoaSpatialMath.PreviewLabel(Mode);
            }
        }

        private sealed class SourceModeItem
        {
            public SourceModeItem(HoaSourceMode mode)
            {
                Mode = mode;
            }

            public HoaSourceMode Mode { get; private set; }

            public override string ToString()
            {
                return HoaSpatialMath.SourceModeLabel(Mode);
            }
        }

        private sealed class DistanceModelItem
        {
            public DistanceModelItem(HoaDistanceModel model)
            {
                Model = model;
            }

            public HoaDistanceModel Model { get; private set; }

            public override string ToString()
            {
                return HoaSpatialMath.DistanceModelLabel(Model);
            }
        }

        private sealed class HeadModelItem
        {
            public HeadModelItem(HoaHeadModel model)
            {
                Model = model;
            }

            public HoaHeadModel Model { get; private set; }

            public override string ToString()
            {
                return HoaSpatialMath.HeadModelLabel(Model);
            }
        }

        private sealed class SourcePresetItem
        {
            private SourcePresetItem(
                string label,
                double azimuth,
                double elevation,
                double distance,
                double spread,
                bool custom)
            {
                Label = label;
                AzimuthDegrees = azimuth;
                ElevationDegrees = elevation;
                Distance = distance;
                Spread = spread;
                IsCustom = custom;
            }

            public string Label { get; private set; }
            public double AzimuthDegrees { get; private set; }
            public double ElevationDegrees { get; private set; }
            public double Distance { get; private set; }
            public double Spread { get; private set; }
            public bool IsCustom { get; private set; }

            public static SourcePresetItem Custom()
            {
                return new SourcePresetItem("Custom", 0, 0, 0, 0, true);
            }

            public static SourcePresetItem Fixed(
                string label,
                double azimuth,
                double elevation,
                double distance,
                double spread)
            {
                return new SourcePresetItem(
                    label, azimuth, elevation, distance, spread, false);
            }

            public override string ToString()
            {
                return Label;
            }
        }

        private sealed class SnapItem
        {
            public SnapItem(int degrees, string label)
            {
                Degrees = degrees;
                Label = label;
            }

            public int Degrees { get; private set; }
            public string Label { get; private set; }

            public override string ToString()
            {
                return Label;
            }
        }

        private sealed class ExportChoiceItem
        {
            public ExportChoiceItem(
                HoaExportTarget target, string extension, string label)
            {
                Target = target;
                Extension = extension;
                Label = label;
            }

            public HoaExportTarget Target { get; private set; }
            public string Extension { get; private set; }
            public string Label { get; private set; }

            public override string ToString()
            {
                return Label;
            }
        }
    }
}
