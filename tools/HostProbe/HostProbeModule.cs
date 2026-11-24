using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Media;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using ScriptPortal.Vegas;
using VegasSurround71;
using VegasSurround71.UI;

namespace VegasSurround71.HostProbe
{
    /// <summary>
    /// Phase 4A in-host probe. Loaded per-session via
    /// <c>vegas2027.exe -CMDMODULE:"...\VegasSurround71.HostProbe.dll"</c>.
    /// Runs the full validation, loads the dockable host panel, and exits.
    /// </summary>
    public sealed class HostProbeModule : ICustomCommandModule
    {
        private const string ArtifactDir = @"D:\VEGAS Extensions\VegasSurround71\artifacts\phase4a";
        private static readonly string LogPath = Path.Combine(ArtifactDir, "host-probe.log");
        private static readonly string ValidationPath = Path.Combine(ArtifactDir, "host-validation.txt");
        private static readonly string ModePath = Path.Combine(ArtifactDir, "probe-mode.txt");

        private Vegas _vegas;
        private Timer _startTimer;
        private Timer _exitTimer;
        private bool _ran;

        public void InitializeModule(Vegas vegas)
        {
            _vegas = vegas;
            Log("InitializeModule entry");
            Log("Thread: id=" + System.Threading.Thread.CurrentThread.ManagedThreadId +
                " apartment=" + System.Threading.Thread.CurrentThread.GetApartmentState());
            Log("VEGAS: " + Safe(() => vegas.Version.ToString()));
            Log("Exe: " + Safe(() => vegas.ExeName));
            Log("Install: " + Safe(() => vegas.InstallationDirectory));

            _startTimer = new Timer();
            _startTimer.Interval =
                (ReadMode() == "templates" || ReadMode() == "panel" ||
                 ReadMode() == "send-diag" || ReadMode() == "pan-readback" ||
                 ReadMode() == "pan-batch" || ReadMode() == "video-templates" ||
                 ReadMode() == "direct-write" || ReadMode() == "live-loop" ||
                 ReadMode() == "direct-live" || ReadMode() == "panel-live" ||
                 ReadMode() == "hoa-smoke")
                    ? 15000 : 60000;
            _startTimer.Tick += OnStartTimer;
            _startTimer.Start();
            Log("Start timer armed (60s)");
        }

        public ICollection GetCustomCommands()
        {
            var command = new CustomCommand(CommandCategory.Tools, "VegasSurround71HostProbeFull");
            command.DisplayName = "VegasSurround71 Host Probe (Full)";
            command.Invoked += OnStartTimer;
            Log("GetCustomCommands returned 1 command");
            return new CustomCommand[] { command };
        }

        private void OnStartTimer(object sender, EventArgs args)
        {
            if (sender is Timer && ReadMode() == "menu")
            {
                Log("Menu mode: timer ignored, waiting for command invocation");
                return;
            }

            if (_ran)
            {
                return;
            }

            _ran = true;

            if (_startTimer != null)
            {
                _startTimer.Stop();
            }

            Log("Probe trigger thread: id=" + System.Threading.Thread.CurrentThread.ManagedThreadId +
                " apartment=" + System.Threading.Thread.CurrentThread.GetApartmentState() +
                " sender=" + (sender == null ? "<null>" : sender.GetType().Name));

            try
            {
                if (ReadMode() == "script")
                {
                    RunScriptDiagnostic();
                }
                else if (ReadMode() == "diagnose")
                {
                    RunDiagnostics();
                }
                else if (ReadMode() == "diagnose-open")
                {
                    RunOpenDiagnostics();
                }
                else if (ReadMode() == "undo-direct2")
                {
                    RunUndoDirect2();
                }
                else if (ReadMode() == "templates")
                {
                    RunTemplateInventory();
                }
                else if (ReadMode() == "panel")
                {
                    Log("Loading WPF host panel");
                    LoadPanel();
                    Log("Panel loaded: " +
                        Safe(() => _vegas.FindDockView("VegasSurround71HostPanel").ToString()));
                    SafeAction(() => _vegas.UpdateUI());
                }
                else if (ReadMode() == "send-diag")
                {
                    RunSendDiagnosticsProbe();
                }
                else if (ReadMode() == "pan-readback")
                {
                    RunPanReadbackProbe();
                }
                else if (ReadMode() == "pan-batch")
                {
                    RunPanBatchProbe();
                }
                else if (ReadMode() == "video-templates")
                {
                    RunVideoTemplateInventory();
                }
                else if (ReadMode() == "direct-write")
                {
                    RunDirectWriteProbe();
                }
                else if (ReadMode() == "live-loop")
                {
                    RunLiveLoopProbe();
                }
                else if (ReadMode() == "direct-live")
                {
                    RunDirectLiveProbe();
                }
                else if (ReadMode() == "panel-live")
                {
                    RunPanelLiveProbe();
                }
                else if (ReadMode() == "hoa-smoke")
                {
                    RunHoaSmokeProbe();
                }
                else
                {
                    Log("Running HostValidation via script host bridge");
                    ValidationOutcome outcome = HostScriptBridge.RunValidation(
                        _vegas, ValidationPath, true);
                    Log(string.Format(CultureInfo.InvariantCulture,
                        "HostValidation finished: {0} passed, {1} failed, result={2}",
                        outcome.PassedCount, outcome.FailedCount,
                        outcome.Passed ? "PASS" : "FAIL"));
                    if (!string.IsNullOrEmpty(outcome.ReportText) &&
                        outcome.ReportText.IndexOf("Host script bridge failed", StringComparison.Ordinal) >= 0)
                    {
                        Log("BRIDGE FAILURE: " + outcome.ReportText);
                    }

                    RunDirectUndoProbe();

                    Log("Loading dockable host panel");
                    LoadPanel();
                    Log("Panel loaded: " + Safe(() => _vegas.FindDockView("VegasSurround71HostPanel").ToString()));
                    SafeAction(() => _vegas.UpdateUI());
                }
            }
            catch (Exception ex)
            {
                Log("PROBE EXCEPTION: " + ex);
            }

            // Keep the panel visible long enough for external screenshot capture.
            _exitTimer = new Timer();
            _exitTimer.Interval = 15000;
            _exitTimer.Tick += OnExitTimer;
            _exitTimer.Start();
            Log("Exit timer armed (15s)");
        }

        private void OnExitTimer(object sender, EventArgs args)
        {
            _exitTimer.Stop();
            Log("Requesting VEGAS exit");
            try
            {
                _vegas.Exit();
            }
            catch (Exception ex)
            {
                Log("Exit failed: " + ex.Message);
            }
        }

        private void RunHoaSmokeProbe()
        {
            string dir = @"D:\VEGAS Extensions\VegasSurround71\artifacts\phaseC";
            Directory.CreateDirectory(dir);
            string reportPath = Path.Combine(dir, "hoa-smoke.txt");
            var report = new StringBuilder();
            Action<string> line = delegate(string message)
            {
                Log(message);
                report.AppendLine(DateTime.Now.ToString(
                    "HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + message);
            };

            bool passed = true;
            string masterPath = Path.Combine(dir, "hoa_smoke_master.wav");
            string master2Path = Path.Combine(dir, "hoa_smoke_master_2nd.wav");
            string previewPath = Path.Combine(dir, "hoa_smoke_preview.wav");

            line("HOA-SMOKE start");

            try
            {
                string tone = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Temp", "opencode", "vs71_tone.wav");
                if (!File.Exists(tone))
                {
                    throw new FileNotFoundException("Tone fixture missing", tone);
                }

                string panelPath = Path.Combine(
                    Path.GetDirectoryName(typeof(HostProbeModule).Assembly.Location),
                    "TellTale7PointOne.dll");
                string panelLiteral = "@\"" + panelPath.Replace("\"", "\"\"") + "\"";
                string toneLiteral = "@\"" + tone.Replace("\"", "\"\"") + "\"";

                string fixture = @"
using System;
using System.Reflection;
using ScriptPortal.Vegas;
public class EntryPoint {
    public void FromVegas(Vegas vegas) {
        vegas.NewProject(false, false);
        Assembly asm = Assembly.LoadFrom(" + panelLiteral + @");
        Type bm = asm.GetType(""VegasSurround71.BusMatrixManager"", true);
        bm.GetMethod(""Provision71BusTopology"", new Type[] { typeof(Vegas) })
            .Invoke(null, new object[] { vegas });
        AudioTrack track = vegas.Project.AddAudioTrack();
        track.Name = ""[DIAG] HOA Smoke"";
        Media media = new Media(" + toneLiteral + @");
        AudioEvent audioEvent = track.AddAudioEvent(
            Timecode.FromSeconds(0), Timecode.FromSeconds(8));
        audioEvent.AddTake(media.GetAudioStreamByIndex(0));
        bm.GetMethod(""EnsureBusSends"", new Type[] { typeof(Vegas), typeof(AudioTrack) })
            .Invoke(null, new object[] { vegas, track });
    }
}";

                line("Creating HOA smoke fixture via script host...");
                _vegas.RunScriptText(ScriptEngineType.CSharp, fixture);
                _vegas.WaitForIdle();

                AudioTrack track = FindAudioTrack(_vegas.Project, "[DIAG] HOA Smoke");
                if (track == null)
                {
                    throw new InvalidOperationException("HOA smoke track was not created.");
                }

                BusSendRoute[] routes = BusMatrixManager.EnsureBusSends(_vegas, track);
                if (routes == null || routes.Length != 8)
                {
                    throw new InvalidOperationException("7.1 send routes were not created.");
                }

                var beforeValues = new double[routes.Length];
                for (int index = 0; index < routes.Length; index++)
                {
                    beforeValues[index] = routes[index].Envelope.ValueAt(
                        Timecode.FromNanos(0));
                }

                bool soloBefore = track.Solo;
                bool muteBefore = track.Mute;
                _vegas.WaitForIdle();

                var context = new WorkspaceContext(_vegas);
                context.SetSelectedTrack(track.Index, track.Name);
                var state = new HoaSpatialState
                {
                    Order = HoaOrder.First,
                    AzimuthDegrees = 90.0,
                    ElevationDegrees = 0.0,
                    Distance = 0.7,
                    Spread = 0.1,
                    PreviewMode = HoaPreviewMode.Binaural,
                    DistanceModel = HoaDistanceModel.Inverse,
                    AirAbsorption = 0.35,
                    HeadModel = HoaHeadModel.Both,
                    HeadWidth = 1.0,
                    RoomAmount = 0.25,
                    RoomSize = 0.5
                };

                line("Rendering 1st-order HOA master...");
                HoaRenderResult master = HoaRenderOrchestrator.RenderMaster(
                    context, state, masterPath, line);
                line("Master success=" + master.Success +
                    " status=" + master.NativeStatus +
                    " message=" + master.Message);
                passed &= master.Success;

                int channels;
                int bits;
                if (master.Success && TryReadWavInfo(masterPath, out channels, out bits))
                {
                    line("Master channels=" + channels + " bits=" + bits);
                    passed &= channels == 4 && bits == 24;
                }
                else
                {
                    passed = false;
                }

                state.Order = HoaOrder.Second;
                line("Rendering 2nd-order HOA master...");
                HoaRenderResult master2 = HoaRenderOrchestrator.RenderMaster(
                    context, state, master2Path, line);
                line("2nd-order master success=" + master2.Success +
                    " status=" + master2.NativeStatus);
                passed &= master2.Success;
                if (master2.Success && TryReadWavInfo(master2Path, out channels, out bits))
                {
                    line("2nd-order channels=" + channels + " bits=" + bits);
                    passed &= channels == 9 && bits == 24;
                }
                else
                {
                    passed = false;
                }

                state.Order = HoaOrder.First;
                state.PreviewMode = HoaPreviewMode.Binaural;
                line("Rendering binaural preview...");
                HoaRenderResult preview = HoaRenderOrchestrator.RenderPreview(
                    context, state, line);
                line("Preview success=" + preview.Success +
                    " status=" + preview.NativeStatus +
                    " message=" + preview.Message);
                passed &= preview.Success;
                if (preview.Success && TryReadWavInfo(
                    preview.OutputPath, out channels, out bits))
                {
                    line("Preview channels=" + channels + " bits=" + bits);
                    passed &= channels == 2 && bits == 16;
                }
                else
                {
                    passed = false;
                }

                if (preview.Success)
                {
                    try
                    {
                        using (var player = new SoundPlayer(preview.OutputPath))
                        {
                            player.Play();
                            System.Threading.Thread.Sleep(1200);
                            player.Stop();
                        }

                        line("Preview playback started/stopped successfully.");
                    }
                    catch (Exception playbackEx)
                    {
                        line("Preview playback warning: " +
                            Describe(playbackEx));
                    }
                }

                state.PreviewMode = HoaPreviewMode.StereoDownmix;
                HoaRenderResult stereo = HoaRenderOrchestrator.RenderPreview(
                    context, state, line);
                line("Stereo preview success=" + stereo.Success +
                    " status=" + stereo.NativeStatus);
                passed &= stereo.Success;

                state.PreviewMode = HoaPreviewMode.Bypass;
                HoaRenderResult bypass = HoaRenderOrchestrator.RenderPreview(
                    context, state, line);
                line("Bypass preview success=" + bypass.Success +
                    " status=" + bypass.NativeStatus);
                passed &= bypass.Success;

                state.SourceMode = HoaSourceMode.FullMix;
                string fullMixPath = Path.Combine(dir, "hoa_smoke_master_fullmix.wav");
                line("Rendering full-mix HOA master...");
                HoaRenderResult fullMix = HoaRenderOrchestrator.RenderMaster(
                    context, state, fullMixPath, line);
                line("Full-mix master success=" + fullMix.Success +
                    " status=" + fullMix.NativeStatus);
                passed &= fullMix.Success;
                if (fullMix.Success && TryReadWavInfo(
                    fullMixPath, out channels, out bits))
                {
                    line("Full-mix channels=" + channels + " bits=" + bits);
                    passed &= channels == 4 && bits == 24;
                }
                else
                {
                    passed = false;
                }

                line("Track solo/mute before=" + soloBefore + "/" + muteBefore +
                    " after=" + track.Solo + "/" + track.Mute);
                passed &= track.Solo == soloBefore && track.Mute == muteBefore;

                for (int index = 0; index < routes.Length; index++)
                {
                    double after = routes[index].Envelope.ValueAt(
                        Timecode.FromNanos(0));
                    bool unchanged = Math.Abs(after - beforeValues[index]) < 1e-6;
                    if (!unchanged)
                    {
                        line("Bus send changed: " + routes[index].BusName +
                            " before=" + beforeValues[index] +
                            " after=" + after);
                    }

                    passed &= unchanged;
                }

                AudioBusTrack[] busses = BusMatrixManager.FindCanonicalBusses(
                    _vegas.Project);
                line("Canonical busses after render=" +
                    (busses == null ? -1 : busses.Length));
                passed &= busses != null && busses.Length == 8;

                if (Directory.GetFiles(dir, "*.tmp.*").Length != 0)
                {
                    line("Temporary files remain in the smoke output folder.");
                    passed = false;
                }

                string projectPath = Path.Combine(dir, "hoa_smoke_project.veg");
                try
                {
                    bool savedProject = _vegas.SaveProject(projectPath);
                    line("Saved smoke project=" + savedProject + " path=" + projectPath);
                    passed &= savedProject;
                }
                catch (Exception saveEx)
                {
                    line("Save smoke project failed: " + Describe(saveEx));
                    passed = false;
                }

                state.Order = HoaOrder.First;
                state.PreviewMode = HoaPreviewMode.Binaural;
                state.RoomSize = 0.77;
                bool stored = HoaProjectStateStore.Save(
                    context, state, previewPath, 15);
                var loadedState = new HoaSpatialState();
                string loadedPath;
                int loadedSnap;
                bool loaded = HoaProjectStateStore.TryLoad(
                    context, loadedState, out loadedPath, out loadedSnap);
                line("HOA state saved=" + stored + " loaded=" + loaded +
                    " roomSize=" + loadedState.RoomSize +
                    " snap=" + loadedSnap +
                    " path=" + loadedPath);
                passed &= stored && loaded &&
                    Math.Abs(loadedState.RoomSize - 0.77) < 1e-6 &&
                    loadedState.SourceMode == HoaSourceMode.FullMix &&
                    loadedSnap == 15 &&
                    string.Equals(loadedPath, previewPath,
                        StringComparison.OrdinalIgnoreCase);

                line(passed ? "HOA-SMOKE PASSED" : "HOA-SMOKE FAILED");
            }
            catch (Exception ex)
            {
                line("HOA-SMOKE EXCEPTION " + Describe(ex));
                passed = false;
            }
            finally
            {
                try
                {
                    File.WriteAllText(reportPath, report.ToString());
                }
                catch (Exception writeEx)
                {
                    Log("HOA-SMOKE report write failed: " + Describe(writeEx));
                }

                Log("HOA-SMOKE result=" + (passed ? "PASS" : "FAIL"));
            }
        }

        private static bool TryReadWavInfo(
            string path, out int channels, out int bitsPerSample)
        {
            channels = 0;
            bitsPerSample = 0;
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length < 44 ||
                    Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
                    Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
                {
                    return false;
                }

                channels = BitConverter.ToInt16(bytes, 22);
                bitsPerSample = BitConverter.ToInt16(bytes, 34);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string ReadMode()
        {
            try
            {
                return File.Exists(ModePath)
                    ? File.ReadAllText(ModePath).Trim().ToLowerInvariant()
                    : "full";
            }
            catch (Exception)
            {
                return "full";
            }
        }

        /// <summary>
        /// Diagnostic: determines whether bus-send envelope points can be mutated
        /// directly from the extension UI thread (which would allow live panning
        /// during playback without RunScriptText). Logs HRESULTs and timings.
        /// </summary>
        private void RunDirectWriteProbe()
        {
            Log("DIRECT-WRITE start thread=" +
                System.Threading.Thread.CurrentThread.ManagedThreadId +
                " apartment=" +
                System.Threading.Thread.CurrentThread.GetApartmentState());

            try
            {
                bool created;
                try
                {
                    created = _vegas.NewProject(false, false);
                    Log("DIRECT-WRITE NewProject=" + created);
                }
                catch (Exception ex)
                {
                    Log("DIRECT-WRITE NewProject EX " + Describe(ex));
                }

                _vegas.WaitForIdle();

                try
                {
                    AudioTrack added = _vegas.Project.AddAudioTrack();
                    added.Name = "[DIAG] Direct Write";
                    Log("DIRECT-WRITE AddAudioTrack OK index=" + added.Index);
                }
                catch (Exception ex)
                {
                    Log("DIRECT-WRITE AddAudioTrack EX " + Describe(ex));
                }

                try
                {
                    using (_vegas.Project.CreateBackgroundProjectActiveState())
                    using (new UndoBlock(_vegas.Project, "VS71 Direct Add Track"))
                    {
                        _vegas.WaitForIdle();
                        AudioTrack added = _vegas.Project.AddAudioTrack();
                        added.Name = "[DIAG] Direct Write (active)";
                        _vegas.WaitForIdle();
                        Log("DIRECT-WRITE AddAudioTrack[active] OK index=" + added.Index);
                    }
                }
                catch (Exception ex)
                {
                    Log("DIRECT-WRITE AddAudioTrack[active] EX " + Describe(ex));
                }

                try
                {
                    using (new UndoBlock(_vegas.Project, "VS71 Direct Add Track (undo only)"))
                    {
                        AudioTrack added = _vegas.Project.AddAudioTrack();
                        added.Name = "[DIAG] Direct Write (undo)";
                        Log("DIRECT-WRITE AddAudioTrack[undo] OK index=" + added.Index);
                    }
                }
                catch (Exception ex)
                {
                    Log("DIRECT-WRITE AddAudioTrack[undo] EX " + Describe(ex));
                }

                string provisionError;
                bool provisioned = HostScriptBridge.RunProvision(_vegas, out provisionError);
                Log("DIRECT-WRITE provision=" + provisioned +
                    " error=" + (provisionError ?? "<none>"));
                _vegas.WaitForIdle();

                // Create the fixture track + send envelopes from the script host
                // (the supported mutation path), so the direct-write test below
                // only measures envelope-point mutation from the extension.
                TryCreateFixtureScript();

                Project project = _vegas.Project;
                AudioTrack track = project == null ? null : FindAudioTrack(project, "[DIAG] Direct Write");
                AudioBusTrack[] busses = project == null
                    ? null
                    : BusMatrixManager.FindCanonicalBusses(project);
                Log("DIRECT-WRITE fixture track=" + (track != null) +
                    " busses=" + (busses != null));

                if (track == null || busses == null)
                {
                    Log("DIRECT-WRITE ABORT fixture missing");
                    return;
                }

                Envelope env = null;
                using (project.CreateBackgroundProjectActiveState())
                {
                    BusSendRoute[] sends = BusMatrixManager.EnsureBusSends(track, busses);
                    env = sends[0].Envelope;
                }

                Log("DIRECT-WRITE envelope type=" + env.Type +
                    " min=" + env.Min.ToString("0.###", CultureInfo.InvariantCulture) +
                    " max=" + env.Max.ToString("0.###", CultureInfo.InvariantCulture) +
                    " points=" + env.Points.Count);

                Timecode zero = Timecode.FromNanos(0);
                Log("DIRECT-WRITE valueAt0(before)=" +
                    env.ValueAt(zero).ToString("0.####", CultureInfo.InvariantCulture));

                // Test A: plain direct mutation, no active state, no undo block.
                TryDirectEnvelopeWrite("plain", project, env, zero, 0.42, false);

                // Test B: UndoBlock only.
                TryDirectEnvelopeWrite("undo-only", project, env, zero, 0.51, false);

                // Test C: UndoBlock + active project state.
                TryDirectEnvelopeWrite("undo+active", project, env, zero, 0.63, true);

                // Test D: rapid 50-write loop using one UndoBlock.
                try
                {
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    using (new UndoBlock(project, "VS71 Direct Write Loop"))
                    {
                        for (int i = 0; i < 50; i++)
                        {
                            env.Points[0].Y = 0.30 + (i % 10) * 0.05;
                            project.ForceEnvelopeSync();
                        }
                    }

                    stopwatch.Stop();
                    _vegas.WaitForIdle();
                    Log("DIRECT-WRITE loop50 elapsedMs=" +
                        stopwatch.Elapsed.TotalMilliseconds.ToString("0.0",
                            CultureInfo.InvariantCulture) +
                        " valueAt0=" +
                        env.ValueAt(zero).ToString("0.####", CultureInfo.InvariantCulture));
                }
                catch (Exception ex)
                {
                    Log("DIRECT-WRITE loop50 EX " + Describe(ex));
                }

                // Test E: transport stays running through direct writes.
                try
                {
                    _vegas.Transport.CursorPosition = Timecode.FromSeconds(1);
                    _vegas.Transport.Play();
                    System.Threading.Thread.Sleep(400);
                    bool playingBefore = _vegas.Transport.IsPlaying;
                    TryDirectEnvelopeWrite("while-playing", project, env, zero, 0.88, false);
                    System.Threading.Thread.Sleep(400);
                    bool playingAfter = _vegas.Transport.IsPlaying;
                    _vegas.Transport.Stop();
                    Log("DIRECT-WRITE transport playingBefore=" + playingBefore +
                        " playingAfter=" + playingAfter);
                }
                catch (Exception ex)
                {
                    Log("DIRECT-WRITE playback EX " + Describe(ex));
                }

                // Test F: legacy 5.1 pan properties (live mixer panner path).
                try
                {
                    using (new UndoBlock(project, "VS71 Direct Pan Trim"))
                    {
                        track.PanX = -0.75f;
                        track.PanY = 0.25f;
                    }

                    Log("DIRECT-WRITE AudioTrack.PanX/PanY OK panX=" +
                        track.PanX.ToString("0.###", CultureInfo.InvariantCulture) +
                        " panY=" +
                        track.PanY.ToString("0.###", CultureInfo.InvariantCulture));
                }
                catch (Exception ex)
                {
                    Log("DIRECT-WRITE AudioTrack.PanX/PanY EX " + Describe(ex));
                }

                try
                {
                    _vegas.UpdateUI();
                }
                catch (Exception ex)
                {
                    Log("DIRECT-WRITE UpdateUI EX " + Describe(ex));
                }
            }
            catch (Exception ex)
            {
                Log("DIRECT-WRITE FATAL " + Describe(ex));
            }

            Log("DIRECT-WRITE done");
        }

        private void RunLiveLoopProbe()
        {
            string tonePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp", "opencode", "vs71_tone.wav");
            string scriptLog = Path.Combine(ArtifactDir, "live-loop-script.txt");
            string panelPath = Path.Combine(
                Path.GetDirectoryName(typeof(HostProbeModule).Assembly.Location),
                "TellTale7PointOne.dll");
            string panelLiteral = "@\"" + panelPath.Replace("\"", "\"\"") + "\"";
            string toneLiteral = "@\"" + tonePath.Replace("\"", "\"\"") + "\"";
            string logLiteral = "@\"" + scriptLog.Replace("\"", "\"\"") + "\"";

            Log("LIVE-LOOP start tone=" + tonePath + " exists=" + File.Exists(tonePath));

            // Nested UI-event proof: a WinForms timer must tick while the script
            // loop is running, otherwise the panel could not receive mouse moves.
            int nestedTicks = 0;
            var nestedTimer = new Timer { Interval = 500 };
            nestedTimer.Tick += delegate
            {
                nestedTicks++;
                Log("LIVE-LOOP nested timer tick " + nestedTicks);
            };
            nestedTimer.Start();

            string code = @"
using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using ScriptPortal.Vegas;
public class EntryPoint {
    public void FromVegas(Vegas vegas) {
        var log = new StringBuilder();
        DateTime started = DateTime.UtcNow;
        try {
        vegas.ResumePlaybackOnScriptExit = true;
        log.AppendLine(""entry playing="" + vegas.Transport.IsPlaying +
            "" cursor="" + vegas.Transport.CursorPosition.Nanos);

        Assembly asm = Assembly.LoadFrom(" + panelLiteral + @");
        Type bm = asm.GetType(""VegasSurround71.BusMatrixManager"", true);
        bm.GetMethod(""Provision71BusTopology"", new Type[] { typeof(Vegas) })
            .Invoke(null, new object[] { vegas });

        AudioTrack track = vegas.Project.AddAudioTrack();
        track.Name = ""[DIAG] Live Loop"";
        Media media = new Media(" + toneLiteral + @");
        AudioEvent audioEvent = track.AddAudioEvent(
            Timecode.FromSeconds(0), Timecode.FromSeconds(12));
        audioEvent.AddTake(media.GetAudioStreamByIndex(0));
        bm.GetMethod(""EnsureBusSends"", new Type[] { typeof(Vegas), typeof(AudioTrack) })
            .Invoke(null, new object[] { vegas, track });

        vegas.Transport.CursorPosition = Timecode.FromSeconds(1);
        vegas.Transport.Play();
        log.AppendLine(""after play="" + vegas.Transport.IsPlaying +
            "" cursor="" + vegas.Transport.CursorPosition.Nanos);

        Envelope env = track.Envelopes.FindByType(EnvelopeType.BusA);
        EnvelopePoint anchor = env.Points.GetPointAtX(Timecode.FromNanos(0));
        if (anchor == null) {
            throw new InvalidOperationException(""BusA anchor missing"");
        }

        for (int i = 0; i < 30; i++) {
            anchor.Y = 0.20 + (i % 10) * 0.08;
            vegas.Project.ForceEnvelopeSync();
            Application.DoEvents();
            System.Threading.Thread.Sleep(100);
            if (i % 5 == 0) {
                log.AppendLine(""i="" + i +
                    "" playing="" + vegas.Transport.IsPlaying +
                    "" cursor="" + vegas.Transport.CursorPosition.Nanos +
                    "" value="" + env.ValueAt(Timecode.FromNanos(0)).ToString(""0.###""));
            }
        }

        log.AppendLine(""exit playing="" + vegas.Transport.IsPlaying +
            "" elapsedMs="" + (DateTime.UtcNow - started).TotalMilliseconds.ToString(""0""));
        vegas.Transport.Stop();
        } catch (Exception ex) {
            log.AppendLine(""SCRIPT EXCEPTION "" + ex.ToString());
        } finally {
            File.WriteAllText(" + logLiteral + @", log.ToString());
        }
    }
}";

            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                _vegas.RunScriptText(ScriptEngineType.CSharp, code);
                Log("LIVE-LOOP RunScriptText returned");
            }
            catch (Exception ex)
            {
                Log("LIVE-LOOP EX " + ex);
            }
            finally
            {
                stopwatch.Stop();
                nestedTimer.Stop();
                nestedTimer.Dispose();
            }

            Log("LIVE-LOOP elapsedMs=" +
                stopwatch.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) +
                " nestedTicks=" + nestedTicks +
                " logExists=" + File.Exists(scriptLog));
            if (File.Exists(scriptLog))
            {
                Log("LIVE-LOOP script log:\n" + File.ReadAllText(scriptLog));
            }
        }

        private void RunDirectLiveProbe()
        {
            string tonePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp", "opencode", "vs71_tone.wav");
            string panelPath = Path.Combine(
                Path.GetDirectoryName(typeof(HostProbeModule).Assembly.Location),
                "TellTale7PointOne.dll");
            string panelLiteral = "@\"" + panelPath.Replace("\"", "\"\"") + "\"";
            string toneLiteral = "@\"" + tonePath.Replace("\"", "\"\"") + "\"";

            Log("DIRECT-LIVE start toneExists=" + File.Exists(tonePath));

            string fixture = @"
using System;
using System.Reflection;
using ScriptPortal.Vegas;
public class EntryPoint {
    public void FromVegas(Vegas vegas) {
        vegas.NewProject(false, false);
        Assembly asm = Assembly.LoadFrom(" + panelLiteral + @");
        Type bm = asm.GetType(""VegasSurround71.BusMatrixManager"", true);
        bm.GetMethod(""Provision71BusTopology"", new Type[] { typeof(Vegas) })
            .Invoke(null, new object[] { vegas });
        AudioTrack track = vegas.Project.AddAudioTrack();
        track.Name = ""[DIAG] Direct Live"";
        Media media = new Media(" + toneLiteral + @");
        AudioEvent audioEvent = track.AddAudioEvent(
            Timecode.FromSeconds(0), Timecode.FromSeconds(12));
        audioEvent.AddTake(media.GetAudioStreamByIndex(0));
        bm.GetMethod(""EnsureBusSends"", new Type[] { typeof(Vegas), typeof(AudioTrack) })
            .Invoke(null, new object[] { vegas, track });
        vegas.ResumePlaybackOnScriptExit = true;
        vegas.Transport.CursorPosition = Timecode.FromSeconds(1);
        vegas.Transport.Play();
    }
}";

            try
            {
                _vegas.RunScriptText(ScriptEngineType.CSharp, fixture);
                Log("DIRECT-LIVE fixture OK");
            }
            catch (Exception ex)
            {
                Log("DIRECT-LIVE fixture EX " + Describe(ex) +
                    " INNER=" + (ex.InnerException == null ? "<none>" : ex.InnerException.ToString()));
                return;
            }

            _vegas.WaitForIdle();
            Project project = _vegas.Project;
            AudioTrack track = FindAudioTrack(project, "[DIAG] Direct Live");
            AudioBusTrack[] busses = BusMatrixManager.FindCanonicalBusses(project);
            Log("DIRECT-LIVE fixture track=" + (track != null) +
                " busses=" + (busses != null));
            if (track == null || busses == null)
            {
                return;
            }

            Envelope[] envelopes = new Envelope[Bus71Layout.BusCount];
            for (int i = 0; i < envelopes.Length; i++)
            {
                envelopes[i] = track.Envelopes.FindByType(
                    Bus71Layout.GetSendEnvelopeType(busses[i]));
                if (envelopes[i] == null)
                {
                    Log("DIRECT-LIVE envelope missing at " + i);
                    return;
                }
            }

            Timecode zero = Timecode.FromNanos(0);
            try
            {
                System.Threading.Thread.Sleep(600);
                Log("DIRECT-LIVE after fixture script; playing=" +
                    _vegas.Transport.IsPlaying + " cursor=" +
                    _vegas.Transport.CursorPosition.Nanos);
            }
            catch (Exception ex)
            {
                Log("DIRECT-LIVE play state EX " + ex);
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            using (new UndoBlock(project, "DIRECT-LIVE Probe"))
            {
                for (int i = 0; i < 60; i++)
                {
                    try
                    {
                        float x = (i % 20 - 10) / 10.0f;
                        ChannelGains71 gains = NativeBridge.calculate_71_gains(
                            x, 0.5f, 0.25f, 0.1f);
                        float[] levels = gains.ToArray();
                        for (int c = 0; c < envelopes.Length; c++)
                        {
                            double value = BusMatrixManager.LinearGainToEnvelopeValue(
                                levels[c], envelopes[c].Min, envelopes[c].Max);
                            envelopes[c].Points[0].Y = value;
                        }

                        project.ForceEnvelopeSync();
                        if (i % 10 == 0)
                        {
                            System.Windows.Forms.Application.DoEvents();
                            try
                            {
                                _vegas.UpdateUI();
                            }
                            catch (Exception)
                            {
                                // Best effort.
                            }

                            Log("DIRECT-LIVE i=" + i +
                                " playing=" + _vegas.Transport.IsPlaying +
                                " cursor=" + _vegas.Transport.CursorPosition.Nanos +
                                " playCursor=" + _vegas.Transport.PlayCursorPosition.Nanos +
                                " FL=" + envelopes[0].ValueAt(zero)
                                    .ToString("0.###", CultureInfo.InvariantCulture));
                        }

                        System.Threading.Thread.Sleep(30);
                    }
                    catch (Exception ex)
                    {
                        Log("DIRECT-LIVE write[" + i + "] EX " + Describe(ex));
                        break;
                    }
                }
            }

            stopwatch.Stop();
            try
            {
                bool playing = _vegas.Transport.IsPlaying;
                Timecode cursor = _vegas.Transport.CursorPosition;
                _vegas.Transport.Stop();
                Log("DIRECT-LIVE done elapsedMs=" +
                    stopwatch.Elapsed.TotalMilliseconds.ToString("0",
                        CultureInfo.InvariantCulture) +
                    " playingAfter=" + playing +
                    " cursorAfter=" + cursor.Nanos);
            }
            catch (Exception ex)
            {
                Log("DIRECT-LIVE stop EX " + ex);
            }
        }

        private void RunPanelLiveProbe()
        {
            Log("PANEL-LIVE start");

            string panelPath = Path.Combine(
                Path.GetDirectoryName(typeof(HostProbeModule).Assembly.Location),
                "TellTale7PointOne.dll");
            string panelLiteral = "@\"" + panelPath.Replace("\"", "\"\"") + "\"";
            string fixture = @"
using System;
using System.Reflection;
using ScriptPortal.Vegas;
public class EntryPoint {
    public void FromVegas(Vegas vegas) {
        vegas.NewProject(false, false);
        Assembly asm = Assembly.LoadFrom(" + panelLiteral + @");
        Type bm = asm.GetType(""VegasSurround71.BusMatrixManager"", true);
        bm.GetMethod(""Provision71BusTopology"", new Type[] { typeof(Vegas) })
            .Invoke(null, new object[] { vegas });
        AudioTrack track = vegas.Project.AddAudioTrack();
        track.Name = ""[DIAG] Panel Live"";
        bm.GetMethod(""EnsureBusSends"", new Type[] { typeof(Vegas), typeof(AudioTrack) })
            .Invoke(null, new object[] { vegas, track });
    }
}";

            try
            {
                _vegas.RunScriptText(ScriptEngineType.CSharp, fixture);
                Log("PANEL-LIVE fixture OK");
            }
            catch (Exception ex)
            {
                Log("PANEL-LIVE fixture EX " + Describe(ex));
                return;
            }

            _vegas.WaitForIdle();
            LoadPanel();

            object view = null;
            for (int attempt = 0; attempt < 20 && view == null; attempt++)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(100);
                IDockView dockView;
                if (_vegas.FindDockView("VegasSurround71HostPanel", out dockView))
                {
                    var host = dockView as HostPanel;
                    if (host != null)
                    {
                        FieldInfo field = typeof(HostPanel).GetField(
                            "_view", BindingFlags.NonPublic | BindingFlags.Instance);
                        view = field == null ? null : field.GetValue(host);
                    }
                }
            }

            if (view == null)
            {
                Log("PANEL-LIVE panel view not found");
                return;
            }

            Type viewType = view.GetType();
            MethodInfo refresh = viewType.GetMethod(
                "RefreshAll", BindingFlags.Public | BindingFlags.Instance);
            if (refresh != null)
            {
                refresh.Invoke(view, null);
            }

            Application.DoEvents();
            System.Threading.Thread.Sleep(300);

            FieldInfo trackField = viewType.GetField(
                "_selectedTrack", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo xField = viewType.GetField(
                "_puckX", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo yField = viewType.GetField(
                "_puckY", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo dirtyField = viewType.GetField(
                "_liveDirty", BindingFlags.NonPublic | BindingFlags.Instance);

            FieldInfo toggleField = viewType.GetField(
                "LivePanToggle", BindingFlags.NonPublic | BindingFlags.Instance);
            object toggle = toggleField == null
                ? null
                : toggleField.GetValue(view);
            object toggleChecked = null;
            if (toggle != null)
            {
                PropertyInfo checkedProperty = toggle.GetType().GetProperty("IsChecked");
                if (checkedProperty != null)
                {
                    toggleChecked = checkedProperty.GetValue(toggle, null);
                }
            }

            Log("PANEL-LIVE LivePanToggle=" +
                (toggleChecked == null ? "<null>" : toggleChecked.ToString()));

            AudioTrack track = trackField == null
                ? null
                : trackField.GetValue(view) as AudioTrack;
            Log("PANEL-LIVE selectedTrack=" +
                (track == null ? "<null>" : track.Name));
            if (track == null || xField == null || yField == null)
            {
                Log("PANEL-LIVE fixture not ready");
                return;
            }

            xField.SetValue(view, -0.60);
            yField.SetValue(view, 0.40);
            if (dirtyField != null)
            {
                dirtyField.SetValue(view, true);
            }

            MethodInfo begin = viewType.GetMethod(
                "BeginLivePan", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo apply = viewType.GetMethod(
                "ApplyLiveNow", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo complete = viewType.GetMethod(
                "CompleteLivePan", BindingFlags.NonPublic | BindingFlags.Instance);

            bool began = begin != null && (bool)begin.Invoke(
                view, new object[] { "Probe drag" });
            Log("PANEL-LIVE begin=" + began);
            Log("PANEL-LIVE panel log after begin:\n" +
                ReadPanelLog(view, viewType));

            bool applied = apply != null && (bool)apply.Invoke(
                view, new object[] { false });
            Log("PANEL-LIVE apply=" + applied);

            bool completed = complete != null && (bool)complete.Invoke(
                view, new object[] { "Probe drag" });
            Log("PANEL-LIVE complete=" + completed);

            // Verify the envelope values now equal the native VBAP gains.
            AudioBusTrack[] busses = BusMatrixManager.FindCanonicalBusses(_vegas.Project);
            ChannelGains71 gains = NativeBridge.calculate_71_gains(
                -0.60f, 0.40f, 0.25f, 0.10f);
            float[] levels = gains.ToArray();
            Timecode zero = Timecode.FromNanos(0);
            for (int i = 0; i < levels.Length; i++)
            {
                EnvelopeType type = Bus71Layout.GetSendEnvelopeType(busses[i]);
                Envelope env = track.Envelopes.FindByType(type);
                if (env == null)
                {
                    Log("PANEL-LIVE [" + Bus71Layout.ChannelLabels[i] + "] envelope missing");
                    continue;
                }

                double expected = BusMatrixManager.LinearGainToEnvelopeValue(
                    levels[i], env.Min, env.Max);
                double actual = env.ValueAt(zero);
                Log(string.Format(CultureInfo.InvariantCulture,
                    "PANEL-LIVE [{0}] expected={1:0.####} actual={2:0.####} {3}",
                    Bus71Layout.ChannelLabels[i], expected, actual,
                    Math.Abs(expected - actual) < 1e-6 ? "PASS" : "FAIL"));
            }

            Log("PANEL-LIVE done");
        }

        private static string ReadPanelLog(object view, Type viewType)
        {
            try
            {
                FieldInfo field = viewType.GetField(
                    "LogBox", BindingFlags.NonPublic | BindingFlags.Instance);
                object box = field == null ? null : field.GetValue(view);
                if (box == null)
                {
                    return "<no log box>";
                }

                PropertyInfo textProperty = box.GetType().GetProperty("Text");
                string text = textProperty == null
                    ? string.Empty
                    : textProperty.GetValue(box, null) as string ?? string.Empty;
                return text.Length <= 3000
                    ? text
                    : text.Substring(text.Length - 3000);
            }
            catch (Exception ex)
            {
                return "<log read failed: " + ex.Message + ">";
            }
        }

        private void TryCreateFixtureScript()
        {
            string panelPath = Path.Combine(
                Path.GetDirectoryName(typeof(HostProbeModule).Assembly.Location),
                "TellTale7PointOne.dll");
            string panelLiteral = "@\"" + panelPath.Replace("\"", "\"\"") + "\"";
            string code = @"
using System;
using System.Reflection;
using ScriptPortal.Vegas;
public class EntryPoint {
    public void FromVegas(Vegas vegas) {
        Assembly asm = Assembly.LoadFrom(" + panelLiteral + @");
        Type bm = asm.GetType(""VegasSurround71.BusMatrixManager"", true);
        bm.GetMethod(""Provision71BusTopology"", new Type[] { typeof(Vegas) })
            .Invoke(null, new object[] { vegas });
        AudioTrack track = vegas.Project.AddAudioTrack();
        track.Name = ""[DIAG] Direct Write"";
        bm.GetMethod(""EnsureBusSends"", new Type[] { typeof(Vegas), typeof(AudioTrack) })
            .Invoke(null, new object[] { vegas, track });
    }
}";

            try
            {
                _vegas.RunScriptText(ScriptEngineType.CSharp, code);
                Log("DIRECT-WRITE fixture script OK");
            }
            catch (Exception ex)
            {
                Log("DIRECT-WRITE fixture script EX " + Describe(ex));
            }
        }

        private static AudioTrack FindAudioTrack(Project project, string name)
        {
            foreach (Track candidate in project.Tracks)
            {
                var audio = candidate as AudioTrack;
                if (audio != null && string.Equals(audio.Name, name, StringComparison.Ordinal))
                {
                    return audio;
                }
            }

            return null;
        }

        private void TryDirectEnvelopeWrite(
            string phase, Project project, Envelope env, Timecode at, double value,
            bool activeState)
        {
            try
            {
                if (activeState)
                {
                    using (project.CreateBackgroundProjectActiveState())
                    using (new UndoBlock(project, "VS71 Direct Write"))
                    {
                        WritePoint(project, env, at, value);
                    }
                }
                else
                {
                    using (new UndoBlock(project, "VS71 Direct Write"))
                    {
                        WritePoint(project, env, at, value);
                    }
                }

                _vegas.WaitForIdle();
                Log("DIRECT-WRITE [" + phase + "] OK requested=" +
                    value.ToString("0.###", CultureInfo.InvariantCulture) +
                    " valueAt0=" +
                    env.ValueAt(at).ToString("0.####", CultureInfo.InvariantCulture) +
                    " points=" + env.Points.Count);
            }
            catch (Exception ex)
            {
                Log("DIRECT-WRITE [" + phase + "] EX " + Describe(ex));
            }
        }

        private static void WritePoint(
            Project project, Envelope env, Timecode at, double value)
        {
            if (env.Points.Count > 0)
            {
                env.Points[0].Y = value;
            }
            else
            {
                env.Points.Add(new EnvelopePoint(at, value, CurveType.None));
            }

            project.ForceEnvelopeSync();
        }

        private static string Describe(Exception ex)
        {
            var text = new System.Text.StringBuilder();
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                text.Append('[').Append(current.GetType().Name).Append("] ");
                text.Append(current.Message);
                if (current is System.Runtime.InteropServices.ExternalException)
                {
                    text.Append(" HRESULT=0x").Append(
                        current.HResult.ToString("X8", CultureInfo.InvariantCulture));
                }

                if (current.InnerException != null)
                {
                    text.Append(" -> ");
                }
            }

            return text.ToString();
        }

        private void RunVideoTemplateInventory()
        {
            string path = Path.Combine(ArtifactDir, "video-templates.txt");
            string code = @"
using System;
using System.IO;
using System.Text;
using ScriptPortal.Vegas;
public class EntryPoint {
    public void FromVegas(Vegas vegas) {
        var sb = new StringBuilder();
        foreach (Renderer r in vegas.Renderers) {
            if (!r.SupportsVideo) continue;
            foreach (RenderTemplate t in r.Templates) {
                if (t.VideoWidth <= 0) continue;
                try {
                    sb.AppendLine(r.Name + ""|"" + t.Name +
                        ""|idx="" + t.Index + ""|id="" + t.TemplateID +
                        ""|guid="" + t.TemplateGuid +
                        ""|w="" + t.VideoWidth + ""|h="" + t.VideoHeight +
                        ""|fps="" + t.VideoFrameRate +
                        ""|desc="" + t.Description + ""|notes="" + t.Notes);
                } catch (Exception ex) {
                    sb.AppendLine(r.Name + ""|"" + t.Name + ""|ERR "" + ex.Message);
                }
            }
        }
        File.WriteAllText(@""__PATH__"", sb.ToString());
    }
}".Replace("__PATH__", path);

            Log("VIDEO-TEMPLATES running");
            try
            {
                _vegas.RunScriptText(ScriptEngineType.CSharp, code);
            }
            catch (Exception ex)
            {
                Log("VIDEO-TEMPLATES EX " + ex);
            }

            Log("VIDEO-TEMPLATES written=" + File.Exists(path));
            if (File.Exists(path))
            {
                string text = File.ReadAllText(path);
                Log("VIDEO-TEMPLATES lines=" +
                    text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Length);
            }
        }

        private void RunPanBatchProbe()
        {
            string path = Path.Combine(ArtifactDir, "pan-batch.txt");
            string panelPath = Path.Combine(
                Path.GetDirectoryName(typeof(HostProbeModule).Assembly.Location),
                "TellTale7PointOne.dll");
            string panelLiteral = "@\"" + panelPath.Replace("\"", "\"\"") + "\"";
            string readbackLiteral = "@\"" + path.Replace("\"", "\"\"") + "\"";

            string code = @"
using System;
using System.Reflection;
using ScriptPortal.Vegas;
public class EntryPoint {
    public void FromVegas(Vegas vegas) {
        vegas.NewProject(false, false);
        Assembly asm = Assembly.LoadFrom(" + panelLiteral + @");
        Type bm = asm.GetType(""VegasSurround71.BusMatrixManager"", true);
        bm.GetMethod(""Provision71BusTopology"", new Type[] { typeof(Vegas) })
            .Invoke(null, new object[] { vegas });
        AudioTrack track = vegas.Project.AddAudioTrack();
        track.Name = ""[DIAG] Pan Batch"";
        Type hs = asm.GetType(""VegasSurround71.HostScriptBridge"", true);
        MethodInfo apply = hs.GetMethod(""ApplyPanningBatchInScript"", new Type[] {
            typeof(Vegas), typeof(int), typeof(long[]), typeof(double[]),
            typeof(double[]), typeof(double[]), typeof(double[]), typeof(string) });
        long[] nanos = new long[] { 0L, 100000000L, 200000000L };
        double[] xs = new double[] { -0.5, 0.0, 0.5 };
        double[] ys = new double[] { 0.5, 0.5, 0.5 };
        double[] lfes = new double[] { 0.25, 0.25, 0.25 };
        double[] spreads = new double[] { 0.1, 0.1, 0.1 };
        apply.Invoke(null, new object[] {
            vegas, track.Index, nanos, xs, ys, lfes, spreads, " + readbackLiteral + @" });
    }
}";

            Log("PAN-BATCH running");
            try
            {
                _vegas.RunScriptText(ScriptEngineType.CSharp, code);
            }
            catch (Exception ex)
            {
                Log("PAN-BATCH EX " + ex);
                if (ex.InnerException != null)
                {
                    Log("PAN-BATCH inner EX " + ex.InnerException);
                }
            }

            Log("PAN-BATCH written=" + File.Exists(path));
            if (File.Exists(path))
            {
                Log("PAN-BATCH content:\n" + File.ReadAllText(path));
            }
        }

        private void RunPanReadbackProbe()
        {
            string path = Path.Combine(ArtifactDir, "pan-readback.txt");
            string panelPath = Path.Combine(
                Path.GetDirectoryName(typeof(HostProbeModule).Assembly.Location),
                "TellTale7PointOne.dll");
            string panelLiteral = "@\"" + panelPath.Replace("\"", "\"\"") + "\"";
            string readbackLiteral = "@\"" + path.Replace("\"", "\"\"") + "\"";

            string code = @"
using System;
using System.Reflection;
using ScriptPortal.Vegas;
public class EntryPoint {
    public void FromVegas(Vegas vegas) {
        vegas.NewProject(false, false);
        Assembly asm = Assembly.LoadFrom(" + panelLiteral + @");
        Type bm = asm.GetType(""VegasSurround71.BusMatrixManager"", true);
        MethodInfo provision = bm.GetMethod(""Provision71BusTopology"", new Type[] { typeof(Vegas) });
        provision.Invoke(null, new object[] { vegas });
        AudioTrack track = vegas.Project.AddAudioTrack();
        track.Name = ""[DIAG] Pan Readback"";
        Type hs = asm.GetType(""VegasSurround71.HostScriptBridge"", true);
        MethodInfo apply = hs.GetMethod(""ApplyPanningInScript"", new Type[] {
            typeof(Vegas), typeof(int), typeof(double), typeof(double),
            typeof(double), typeof(double), typeof(long), typeof(bool),
            typeof(string) });
        apply.Invoke(null, new object[] {
            vegas, track.Index, -0.5, 0.5, 0.25, 0.1, 0L, false, " + readbackLiteral + @" });
    }
}";

            Log("PAN-READBACK running");
            try
            {
                _vegas.RunScriptText(ScriptEngineType.CSharp, code);
            }
            catch (Exception ex)
            {
                Log("PAN-READBACK EX " + ex);
                if (ex.InnerException != null)
                {
                    Log("PAN-READBACK inner EX " + ex.InnerException);
                }
            }

            Log("PAN-READBACK written=" + File.Exists(path));
            if (File.Exists(path))
            {
                Log("PAN-READBACK content:\n" + File.ReadAllText(path));
            }
        }

        private void RunSendDiagnosticsProbe()
        {
            string path = Path.Combine(ArtifactDir, "send-diagnostics.txt");
            // HostProbe and the panel are staged side by side; use the panel path.
            string panelPath = Path.Combine(
                Path.GetDirectoryName(typeof(HostProbeModule).Assembly.Location),
                "TellTale7PointOne.dll");
            string panelLiteral = "@\"" + panelPath.Replace("\"", "\"\"") + "\"";
            string reportLiteral = "@\"" + path.Replace("\"", "\"\"") + "\"";

            string code = @"
using System;
using System.Reflection;
using ScriptPortal.Vegas;
public class EntryPoint {
    public void FromVegas(Vegas vegas) {
        Assembly asm = Assembly.LoadFrom(" + panelLiteral + @");
        Type type = asm.GetType(""VegasSurround71.HostValidation"", true);
        MethodInfo method = type.GetMethod(""RunSendDiagnostics"", new Type[] { typeof(Vegas), typeof(string) });
        if (method == null) throw new MissingMethodException(""HostValidation.RunSendDiagnostics"");
        method.Invoke(null, new object[] { vegas, " + reportLiteral + @" });
    }
}";

            Log("SEND-DIAG running");
            try
            {
                _vegas.RunScriptText(ScriptEngineType.CSharp, code);
            }
            catch (Exception ex)
            {
                Log("SEND-DIAG EX " + ex);
                if (ex.InnerException != null)
                {
                    Log("SEND-DIAG inner EX " + ex.InnerException);
                }
            }

            Log("SEND-DIAG written=" + File.Exists(path));
            if (File.Exists(path))
            {
                Log("SEND-DIAG content:\n" + File.ReadAllText(path));
            }
        }

        private void RunTemplateInventory()
        {
            string path = Path.Combine(ArtifactDir, "render-templates.txt");
            string code = @"
using System;
using System.IO;
using System.Text;
using ScriptPortal.Vegas;
public class EntryPoint {
    public void FromVegas(Vegas vegas) {
        var sb = new StringBuilder();
        try {
            foreach (Renderer r in vegas.Renderers) {
                try {
                    sb.AppendLine(""RENDERER|"" + r.Name + ""|ext="" + r.FileExtension +
                        ""|video="" + r.SupportsVideo + ""|audio="" + r.SupportsAudio +
                        ""|multichannel="" + r.SupportsMultichannel +
                        ""|multistream="" + r.SupportsMultistream +
                        ""|templates="" + r.Templates.Count);
                    foreach (RenderTemplate t in r.Templates) {
                        try {
                            sb.AppendLine(""  T|"" + t.Name + ""|codec="" + t.VideoCodec + ""/"" + t.AudioCodec +
                                ""|ach="" + t.AudioChannelCount + ""|ar="" + t.AudioSampleRate +
                                ""|vw="" + t.VideoWidth + ""|vh="" + t.VideoHeight +
                                ""|fr="" + t.VideoFrameRate + ""|type="" + t.RenderType);
                        } catch (Exception ex) {
                            sb.AppendLine(""  T-ERROR|"" + ex.Message);
                        }
                    }
                } catch (Exception ex) {
                    sb.AppendLine(""RENDERER-ERROR|"" + ex.Message);
                }
            }
        } catch (Exception ex) {
            sb.AppendLine(""ERROR|"" + ex);
        }
        File.WriteAllText(@""__PATH__"", sb.ToString());
    }
}".Replace("__PATH__", path);

            Log("TEMPLATES running inventory script");
            try
            {
                _vegas.RunScriptText(ScriptEngineType.CSharp, code);
            }
            catch (Exception ex)
            {
                Log("TEMPLATES script EX: " + ex);
                if (ex.InnerException != null)
                {
                    Log("TEMPLATES inner EX: " + ex.InnerException);
                }
            }

            Log("TEMPLATES written=" + File.Exists(path));
            if (File.Exists(path))
            {
                Log("TEMPLATES content:\n" + File.ReadAllText(path));
            }
        }

        private void RunUndoDirect2()
        {
            string path = Path.Combine(ArtifactDir, "undo-direct2.txt");
            string code = @"
using System;
using System.IO;
using ScriptPortal.Vegas;
public class EntryPoint {
    public void FromVegas(Vegas vegas) {
        try {
            using (new UndoBlock(""Probe: Add Bus"")) {
                AudioBusTrack b = vegas.Project.AddAudioBusTrack();
                b.Description = ""Probe Bus"";
            }
            File.WriteAllText(@""__PATH__"", ""script OK userBuses="" + (vegas.Project.BusTracks.Count - 2));
        } catch (Exception ex) {
            File.WriteAllText(@""__PATH__"", ""script EX "" + ex);
        }
    }
}".Replace("__PATH__", path);

            Log("UNDO2 running script");
            _vegas.RunScriptText(ScriptEngineType.CSharp, code);
            Log("UNDO2 script file: " + (File.Exists(path) ? File.ReadAllText(path) : "<missing>"));
            Log("UNDO2 before extension Undo: buses=" + CountAudioBusses(_vegas.Project));

            try
            {
                using (_vegas.Project.CreateBackgroundProjectActiveState())
                {
                    _vegas.WaitForIdle();
                    _vegas.Project.Undo();
                    _vegas.WaitForIdle();
                }

                Log("UNDO2 extension Undo OK: buses=" + CountAudioBusses(_vegas.Project));
            }
            catch (Exception ex)
            {
                Log("UNDO2 extension Undo EX: " + ex);
            }

            try
            {
                using (_vegas.Project.CreateBackgroundProjectActiveState())
                {
                    _vegas.WaitForIdle();
                    _vegas.Project.Redo();
                    _vegas.WaitForIdle();
                }

                Log("UNDO2 extension Redo OK: buses=" + CountAudioBusses(_vegas.Project));
            }
            catch (Exception ex)
            {
                Log("UNDO2 extension Redo EX: " + ex);
            }
        }

        private void RunDirectUndoProbe()
        {
            try
            {
                Project project = _vegas.Project;
                Log("DIRECT UNDO before: buses=" + CountAudioBusses(project) +
                    " tracks=" + project.Tracks.Count);

                try
                {
                    project.Undo();
                    Log("DIRECT UNDO call OK");
                }
                catch (Exception ex)
                {
                    Log("DIRECT UNDO EX " + ex.GetType().Name + ": " + ex.Message);
                }

                Log("DIRECT UNDO after: buses=" + CountAudioBusses(project) +
                    " tracks=" + project.Tracks.Count);

                try
                {
                    project.Redo();
                    Log("DIRECT REDO call OK");
                }
                catch (Exception ex)
                {
                    Log("DIRECT REDO EX " + ex.GetType().Name + ": " + ex.Message);
                }

                Log("DIRECT REDO after: buses=" + CountAudioBusses(project) +
                    " tracks=" + project.Tracks.Count);
            }
            catch (Exception ex)
            {
                Log("DIRECT UNDO probe EX: " + ex);
            }
        }

        private static int CountAudioBusses(Project project)
        {
            int count = 0;
            foreach (BusTrack bus in project.BusTracks)
            {
                var audio = bus as AudioBusTrack;
                if (audio != null && audio.IsAudio() && !audio.IsMaster())
                {
                    count++;
                }
            }

            return count;
        }

        private void RunScriptDiagnostic()
        {
            string path = Path.Combine(ArtifactDir, "script-diag.txt");
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // ignore
            }

            string code = @"
using System;
using System.IO;
using ScriptPortal.Vegas;
public class EntryPoint {
    public void FromVegas(Vegas vegas) {
        string path = @""__PATH__"";
        try {
            File.AppendAllText(path, ""script start\n"");
            AudioTrack t = vegas.Project.AddAudioTrack();
            t.Name = ""[SCRIPT] Track"";
            File.AppendAllText(path, ""AddAudioTrack OK\n"");
            AudioBusTrack b = vegas.Project.AddAudioBusTrack();
            b.Description = ""[SCRIPT] Bus"";
            File.AppendAllText(path, ""AddAudioBusTrack OK name="" + b.Name + ""\n"");
            vegas.Project.Audio.MasterBusMode = AudioBusMode.Surround;
            File.AppendAllText(path, ""MasterBusMode=Surround OK\n"");
        } catch (Exception ex) {
            File.AppendAllText(path, ""EX "" + ex.ToString() + ""\n"");
        }
    }
}".Replace("__PATH__", path);

            Log("RunScriptText start");
            try
            {
                _vegas.RunScriptText(ScriptEngineType.CSharp, code, string.Empty);
                Log("RunScriptText returned");
            }
            catch (Exception ex)
            {
                Log("RunScriptText EX: " + ex);
            }

            try
            {
                Log("script-diag exists=" + File.Exists(path));
                if (File.Exists(path))
                {
                    Log("script-diag content:\n" + File.ReadAllText(path));
                }
            }
            catch (Exception ex)
            {
                Log("script-diag read EX: " + ex.Message);
            }
        }

        private void RunOpenDiagnostics()
        {
            Log("DIAG-OPEN start");
            Log("DIAG license: demo=" + Safe(() => _vegas.IsDemoMode.ToString()) +
                " trial=" + Safe(() => _vegas.IsTrialMode.ToString()) +
                " subscription=" + Safe(() => _vegas.IsSubscriptionLicense.ToString()));

            try
            {
                _vegas.WaitForIdle();
                Log("DIAG-OPEN WaitForIdle OK");
            }
            catch (Exception ex)
            {
                Log("DIAG-OPEN WaitForIdle EX: " + ex.Message);
            }

            LogProject("open");

            try
            {
                using (_vegas.Project.CreateBackgroundProjectActiveState())
                {
                    Log("DIAG-OPEN active state entered");

                    AudioTrack track = null;
                    try
                    {
                        track = _vegas.Project.AddAudioTrack();
                        track.Name = "[DIAG] Audio Track";
                        Log("DIAG-OPEN AddAudioTrack OK index=" + track.Index +
                            " name='" + track.Name + "'");
                    }
                    catch (Exception ex)
                    {
                        Log("DIAG-OPEN AddAudioTrack EX: " + ex.GetType().Name + ": " + ex.Message);
                    }

                    AudioBusTrack bus = TryAddBus("active");
                    if (bus != null)
                    {
                        RetryFlags(bus, "stereo-mode");
                    }

                    try
                    {
                        _vegas.Project.Audio.MasterBusMode = AudioBusMode.Surround;
                        Log("DIAG-OPEN set MasterBusMode=Surround OK");
                        if (bus != null)
                        {
                            RetryFlags(bus, "surround-mode");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("DIAG-OPEN set Surround EX: " + ex.GetType().Name + ": " + ex.Message);
                    }

                    LogProject("active-after");
                }
            }
            catch (Exception ex)
            {
                Log("DIAG-OPEN active-state EX: " + ex.GetType().Name + ": " + ex.Message);
            }

            LogProject("after");
            try
            {
                bool saved = _vegas.SaveProject(Path.Combine(ArtifactDir, "diagnostic-open-project.veg"));
                Log("DIAG-OPEN SaveProject=" + saved);
            }
            catch (Exception ex)
            {
                Log("DIAG-OPEN SaveProject EX: " + ex.Message);
            }
        }

        private static void RetryFlags(AudioBusTrack bus, string phase)
        {
            TryFlag("[" + phase + "] MonoRenderEnabled=true", () => bus.MonoRenderEnabled = true);
            TryFlag("[" + phase + "] MonoRenderOrder=0", () => bus.MonoRenderOrder = 0);
            TryFlag("[" + phase + "] StereoRenderEnabled=false", () => bus.StereoRenderEnabled = false);
            TryFlag("[" + phase + "] SurroundRenderEnabled=false", () => bus.SurroundRenderEnabled = false);
        }

        private void RunDiagnostics()
        {
            Log("DIAG start");
            Log("DIAG license: demo=" + Safe(() => _vegas.IsDemoMode.ToString()) +
                " trial=" + Safe(() => _vegas.IsTrialMode.ToString()) +
                " subscription=" + Safe(() => _vegas.IsSubscriptionLicense.ToString()));
            LogProject("before");
            LogActiveProject();

            bool created = false;
            try
            {
                created = _vegas.NewProject(false, false);
                Log("DIAG NewProject=" + created);
            }
            catch (Exception ex)
            {
                Log("DIAG NewProject EX: " + ex);
            }

            try
            {
                _vegas.WaitForIdle();
            }
            catch (Exception ex)
            {
                Log("DIAG WaitForIdle EX: " + ex.Message);
            }

            LogProject("after NewProject");
            LogActiveProject();

            // Does adding a track first change bus behavior?
            try
            {
                AudioTrack track = _vegas.Project.AddAudioTrack();
                track.Name = "[DIAG] Audio Track";
                Log("DIAG AddAudioTrack OK index=" + track.Index + " name='" + track.Name + "'");
            }
            catch (Exception ex)
            {
                Log("DIAG AddAudioTrack EX: " + ex.GetType().Name + ": " + ex.Message);
            }

            LogMasterFlags();

            AudioBusTrack bus = TryAddBus("convenience");
            if (bus == null)
            {
                TryDirectBusAdd();
            }

            if (bus == null)
            {
                try
                {
                    _vegas.Project.Audio.MasterBusMode = AudioBusMode.Surround;
                    Log("DIAG set MasterBusMode=Surround OK");
                    bus = TryAddBus("after-surround");
                }
                catch (Exception ex)
                {
                    Log("DIAG set Surround EX: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            if (bus == null)
            {
                try
                {
                    using (_vegas.Project.CreateBackgroundProjectActiveState())
                    {
                        Log("DIAG activated background project state");
                        bus = TryAddBus("active-state");
                        if (bus == null)
                        {
                            TryDirectBusAdd();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("DIAG active-state EX: " + ex.Message);
                }
            }

            if (bus != null)
            {
                try
                {
                    bool saved = _vegas.SaveProject(Path.Combine(ArtifactDir, "diagnostic-project.veg"));
                    Log("DIAG SaveProject=" + saved);
                }
                catch (Exception ex)
                {
                    Log("DIAG SaveProject EX: " + ex.Message);
                }
            }

            LogProject("after diagnostics");
        }

        private void LogActiveProject()
        {
            try
            {
                Project active = Project.ActiveProject;
                Project current = _vegas.Project;
                Log("DIAG activeProject: present=" + (active != null) +
                    " sameAsVegasProject=" + object.ReferenceEquals(active, current) +
                    " activePath=" + (active == null ? "<null>" : Safe(() => active.FilePath)) +
                    " currentPath=" + (current == null ? "<null>" : Safe(() => current.FilePath)));
            }
            catch (Exception ex)
            {
                Log("DIAG activeProject EX: " + ex.Message);
            }
        }

        private void LogMasterFlags()
        {
            try
            {
                AudioBusTrack master = _vegas.Project.MasterBus;
                Log("DIAG master: name='" + master.Name + "' desc='" + master.Description + "'");
                TryFlag("master.MonoRenderEnabled=true", () => master.MonoRenderEnabled = true);
                TryFlag("master.StereoRenderEnabled=true", () => master.StereoRenderEnabled = true);
                TryFlag("master.SurroundRenderEnabled=true", () => master.SurroundRenderEnabled = true);
                TryFlag("master.MonoRenderOrder=0", () => master.MonoRenderOrder = 0);
            }
            catch (Exception ex)
            {
                Log("DIAG master EX: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void TryDirectBusAdd()
        {
            try
            {
                AudioBusTrack bus = new AudioBusTrack(_vegas.Project);
                Log("DIAG new AudioBusTrack OK name='" + bus.Name + "' valid=" + bus.IsValid());
                _vegas.Project.BusTracks.Add(bus);
                Log("DIAG BusTracks.Add OK name='" + bus.Name + "' valid=" + bus.IsValid());
            }
            catch (Exception ex)
            {
                Log("DIAG direct bus add EX: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void LogProject(string phase)
        {
            try
            {
                Project project = _vegas.Project;
                if (project == null)
                {
                    Log("DIAG " + phase + ": project=<null>");
                    return;
                }

                Log(string.Format(CultureInfo.InvariantCulture,
                    "DIAG {0}: buses={1} tracks={2} mode={3} modified={4}",
                    phase, project.BusTracks.Count, project.Tracks.Count,
                    project.Audio.MasterBusMode, project.IsModified));
            }
            catch (Exception ex)
            {
                Log("DIAG " + phase + " EX: " + ex.Message);
            }
        }

        private AudioBusTrack TryAddBus(string phase)
        {
            try
            {
                AudioBusTrack bus = _vegas.Project.AddAudioBusTrack();
                Log("DIAG AddAudioBusTrack[" + phase + "] OK name='" + bus.Name +
                    "' desc='" + bus.Description + "' index=" + bus.Index);

                TryFlag("MonoRenderEnabled=true", () => bus.MonoRenderEnabled = true);
                TryFlag("MonoRenderOrder=0", () => bus.MonoRenderOrder = 0);
                TryFlag("StereoRenderEnabled=false", () => bus.StereoRenderEnabled = false);
                TryFlag("SurroundRenderEnabled=false", () => bus.SurroundRenderEnabled = false);

                Log("DIAG flags after: monoEnabled=" + Safe(() => bus.MonoRenderEnabled.ToString()) +
                    " monoOrder=" + Safe(() => bus.MonoRenderOrder.ToString()) +
                    " stereoEnabled=" + Safe(() => bus.StereoRenderEnabled.ToString()) +
                    " surroundEnabled=" + Safe(() => bus.SurroundRenderEnabled.ToString()));
                return bus;
            }
            catch (Exception ex)
            {
                Log("DIAG AddAudioBusTrack[" + phase + "] EX " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static void TryFlag(string name, Action action)
        {
            try
            {
                action();
                Log("DIAG flag " + name + " OK");
            }
            catch (Exception ex)
            {
                Log("DIAG flag " + name + " EX " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void LoadPanel()
        {
            if (_vegas.ActivateDockView("VegasSurround71HostPanel"))
            {
                return;
            }

            var panel = new HostPanel("VegasSurround71HostPanel", _vegas);
            panel.DisplayName = "VegasSurround71 7.1 Host";
            panel.PersistDockWindowState = false;
            panel.DefaultFloatingSize = new System.Drawing.Size(1120, 780);
            panel.DefaultDockWindowStyle = DockWindowStyle.Floating;
            _vegas.LoadDockView(panel);
        }

        private static void SafeAction(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log("SafeAction failed: " + ex.Message);
            }
        }

        private static string Safe(Func<string> getter)
        {
            try
            {
                return getter() ?? "<null>";
            }
            catch (Exception ex)
            {
                return "<error: " + ex.Message + ">";
            }
        }

        private static void Log(string message)
        {
            Directory.CreateDirectory(ArtifactDir);
            File.AppendAllText(LogPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                " | " + message + Environment.NewLine);
        }
    }
}
