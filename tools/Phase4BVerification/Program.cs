using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using VegasSurround71;
using VegasSurround71.UI;

namespace VegasSurround71.Phase4BVerification
{
    /// <summary>
    /// Phase 4B offline verification: soundstage math, live-meter math, render-plan
    /// validation/temp cleanup, and deployment staging.
    /// </summary>
    internal static class Program
    {
        private static int _checks;
        private static int _failures;

        private static int Main()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveScriptPortal;

            try
            {
                VerifySoundstageMath();
                VerifySpeakerLayout();
                VerifySendMapping();
                VerifyExtensionSettings();
                VerifyRenamedBindings();
                VerifyLoopRegionPlan();
                VerifyLivePanWiring();
                VerifyProjectResetWiring();
                VerifyPanelXamlLoads();
                VerifyWorkspaceShell();
                VerifyHoaNativeRender();
                VerifyDeliveryPresets();
                VerifyRenderPlan();
                VerifyDeployment();
            }
            catch (Exception ex)
            {
                _failures++;
                Console.WriteLine("[FAIL] Unhandled verification exception: " + ex);
            }

            Console.WriteLine();
            Console.WriteLine("Checks: {0}    Failures: {1}", _checks, _failures);
            Console.WriteLine(_failures == 0 ? "PHASE 4B VERIFICATION PASSED" : "PHASE 4B VERIFICATION FAILED");
            return _failures == 0 ? 0 : 1;
        }

        private static void VerifySoundstageMath()
        {
            Check("GainToDb(1.0) == 0 dB", Math.Abs(SoundstageMath.GainToDb(1.0)) < 1e-9);
            Check("GainToDb(0.5) ~= -6.02 dB",
                Math.Abs(SoundstageMath.GainToDb(0.5) - (-6.0206)) < 0.01);
            Check("GainToDb(0) == meter floor",
                Math.Abs(SoundstageMath.GainToDb(0.0) - SoundstageMath.MeterFloorDb) < 1e-9);
            Check("MeterFillFraction clamps high", SoundstageMath.MeterFillFraction(2.0) == 1.0);
            Check("MeterFillFraction clamps low", SoundstageMath.MeterFillFraction(-1.0) == 0.0);
            Check("FormatLinear formats 0.5", SoundstageMath.FormatLinear(0.5) == "0.500");

            StagePoint center = SoundstageMath.ToScreen(0, 0, 100, 100, 50);
            Check("ToScreen center", center.X == 100 && center.Y == 100);

            StagePoint front = SoundstageMath.ToScreen(0, 1, 100, 100, 50);
            Check("ToScreen front is above center", front.X == 100 && front.Y == 50);

            StagePoint roundTrip = SoundstageMath.FromScreen(125, 100, 100, 100, 50);
            Check("FromScreen maps +X", Math.Abs(roundTrip.X - 0.5) < 1e-9 &&
                                        Math.Abs(roundTrip.Y) < 1e-9);

            StagePoint clamped = SoundstageMath.FromScreen(300, 100, 100, 100, 50);
            Check("FromScreen clamps to unit circle",
                Math.Abs(clamped.X - 1.0) < 1e-9 && Math.Abs(clamped.Y) < 1e-9);

            StagePoint degenerate = SoundstageMath.FromScreen(10, 10, 0, 0, 0);
            Check("FromScreen handles zero radius", degenerate.X == 0 && degenerate.Y == 0);
        }

        private static void VerifySpeakerLayout()
        {
            Check("Eight speaker nodes", SoundstageMath.Speakers.Length == 8);
            Check("Eight meter labels", SoundstageMath.ChannelLabels.Length == 8);
            Check("Eight spatial meter labels", SoundstageMath.MeterLabels.Length == 8);
            Check("FL meter label is Front L",
                SoundstageMath.MeterLabels[0] == "FL \u00B7 Front L");
            Check("LFE meter label is Sub",
                SoundstageMath.MeterLabels[3] == "LFE \u00B7 Sub");
            Check("SR meter label is Side R",
                SoundstageMath.MeterLabels[7] == "SR \u00B7 Side R");
            Check("FL is left-front", FindSpeaker("FL").X < 0 && FindSpeaker("FL").Y > 0);
            Check("FR is right-front", FindSpeaker("FR").X > 0 && FindSpeaker("FR").Y > 0);
            Check("FC is front-center", Math.Abs(FindSpeaker("FC").X) < 1e-9 &&
                                        FindSpeaker("FC").Y > 0);
            Check("BL is left-rear", FindSpeaker("BL").X < 0 && FindSpeaker("BL").Y < 0);
            Check("BR is right-rear", FindSpeaker("BR").X > 0 && FindSpeaker("BR").Y < 0);
            Check("SL is left-side", FindSpeaker("SL").X < 0 &&
                                     Math.Abs(FindSpeaker("SL").Y) < 1e-9);
            Check("SR is right-side", FindSpeaker("SR").X > 0 &&
                                      Math.Abs(FindSpeaker("SR").Y) < 1e-9);
            Check("LFE is drawn below the room", FindSpeaker("LFE").Y < 0);
        }

        private static void VerifySendMapping()
        {
            Check("Send envelope 0..2 linear maps 0.50",
                Math.Abs(BusMatrixManager.LinearGainToEnvelopeValue(0.50, 0.0, 2.0) - 0.50) < 1e-9);
            Check("Send envelope 0..2 linear maps 0.99",
                Math.Abs(BusMatrixManager.LinearGainToEnvelopeValue(0.99, 0.0, 2.0) - 0.99) < 1e-9);
            Check("Send envelope 0..2 linear maps 0.05",
                Math.Abs(BusMatrixManager.LinearGainToEnvelopeValue(0.05, 0.0, 2.0) - 0.05) < 1e-9);
            Check("Send envelope 0..2 maps unity to 1.0",
                Math.Abs(BusMatrixManager.LinearGainToEnvelopeValue(1.0, 0.0, 2.0) - 1.0) < 1e-9);
            Check("Legacy dB envelope still maps 0.50 to -6.02",
                Math.Abs(BusMatrixManager.LinearGainToEnvelopeValue(0.50, -96.0, 12.0) + 6.0206) < 0.01);
        }

        private static void VerifyExtensionSettings()
        {
            Check("Extension display name defaults to TellTale-Seven-Point-One",
                ExtensionSettings.ResolveDisplayName(null, null, null) == "TellTale-Seven-Point-One");
            Check("Extension display name constant is TellTale-Seven-Point-One",
                ExtensionSettings.DefaultDisplayName == "TellTale-Seven-Point-One");
            Check("Build metadata supplies TellTale-Seven-Point-One",
                ExtensionSettings.BuildDisplayName == "TellTale-Seven-Point-One");
            Check("Build name used when no settings/env",
                ExtensionSettings.ResolveDisplayName(null, null, "Build 7.1") == "Build 7.1");
            Check("Legacy TellTale 7.1 build name migrates",
                ExtensionSettings.ResolveDisplayName(null, null, "TellTale 7.1") ==
                "TellTale-Seven-Point-One");
            Check("Legacy spaced build name migrates",
                ExtensionSettings.ResolveDisplayName(null, null, "TellTale 7-Point-One") ==
                "TellTale-Seven-Point-One");
            Check("Environment overrides build name",
                ExtensionSettings.ResolveDisplayName(null, "Env 7.1", "Build 7.1") == "Env 7.1");

            string dir = Path.Combine(
                Path.GetTempPath(), "vs71_settings_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "settings.json");

            try
            {
                Check("Missing settings file returns null",
                    ExtensionSettings.LoadFrom(path) == null);
                Check("Settings save succeeds",
                    ExtensionSettings.SaveTo(path, "Studio 7.1"));

                ExtensionSettings loaded = ExtensionSettings.LoadFrom(path);
                Check("Settings persist display name",
                    loaded != null && loaded.DisplayName == "Studio 7.1");
                Check("Settings override build name",
                    ExtensionSettings.ResolveDisplayName(path, null, "Build 7.1") ==
                    "Studio 7.1");
                Check("Environment overrides settings",
                    ExtensionSettings.ResolveDisplayName(path, "Env 7.1", "Build 7.1") ==
                    "Env 7.1");

                ExtensionSettings.SaveTo(path, "TellTale 7.1");
                Check("Legacy settings name migrates",
                    ExtensionSettings.ResolveDisplayName(path, null, "Build 7.1") ==
                    "TellTale-Seven-Point-One");

                ExtensionSettings.SaveTo(path, "TellTale 7-Point-One");
                Check("Legacy spaced settings name migrates",
                    ExtensionSettings.ResolveDisplayName(path, null, "Build 7.1") ==
                    "TellTale-Seven-Point-One");

                Check("Blank save writes default",
                    ExtensionSettings.SaveTo(path, "   "));
                loaded = ExtensionSettings.LoadFrom(path);
                Check("Blank save persists default",
                    loaded != null && loaded.DisplayName == "TellTale-Seven-Point-One");

                string tricky = "A \"quoted\" \\ name";
                ExtensionSettings.SaveTo(path, tricky);
                loaded = ExtensionSettings.LoadFrom(path);
                Check("JSON escapes round-trip",
                    loaded != null && loaded.DisplayName == tricky);
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                    // Best effort.
                }
            }
        }

        private static void VerifyRenamedBindings()
        {
            Check("Settings folder is TellTale7PointOne",
                string.Equals(Path.GetFileName(ExtensionSettings.SettingsDirectory),
                    "TellTale7PointOne", StringComparison.Ordinal));
            Check("Managed assembly is TellTale7PointOne",
                string.Equals(typeof(ExtensionSettings).Assembly.GetName().Name,
                    "TellTale7PointOne", StringComparison.Ordinal));
            Check("Native DLL binding is telltale_7point1_core.dll",
                string.Equals(NativeBridge.DllFileName, "telltale_7point1_core.dll",
                    StringComparison.Ordinal));
            Check("Legacy settings folder remains readable",
                ExtensionSettings.LegacySettingsDirectory.EndsWith(
                    "VegasSurround71", StringComparison.OrdinalIgnoreCase));
        }

        private static void VerifyLoopRegionPlan()
        {
            string dir = Path.Combine(
                Path.GetTempPath(), "vs71_loop_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                RenderPlan full = RenderPlan.Create(
                    Path.Combine(dir, "full.mkv"), AudioCodec71.Flac, true,
                    DeliveryPreset.FlacMkv, VideoCodecFamily.HEVC);
                Check("Full timeline plan has no loop range",
                    !full.RenderLoopRegionOnly && full.LoopStartNanos == 0L &&
                    full.LoopLengthNanos == 0L);
                Check("Full timeline plan is valid", full.Validate() == null);

                RenderPlan zero = RenderPlan.Create(
                    Path.Combine(dir, "zero.mkv"), AudioCodec71.Flac, true,
                    DeliveryPreset.FlacMkv, VideoCodecFamily.HEVC,
                    true, 0L, 0L);
                Check("Loop-only zero length rejected",
                    zero.Validate() ==
                    "Set In/Out loop points on the VEGAS timeline first.");

                RenderPlan negative = RenderPlan.Create(
                    Path.Combine(dir, "negative.mkv"), AudioCodec71.Flac, true,
                    DeliveryPreset.FlacMkv, VideoCodecFamily.HEVC,
                    true, -1L, 1000000000L);
                Check("Loop-only negative start rejected",
                    negative.Validate() == "Loop region start is invalid.");

                RenderPlan valid = RenderPlan.Create(
                    Path.Combine(dir, "loop.mkv"), AudioCodec71.Flac, true,
                    DeliveryPreset.FlacMkv, VideoCodecFamily.HEVC,
                    true, 1000000000L, 5000000000L);
                Check("Loop-only valid range accepted", valid.Validate() == null);
                Check("Loop plan records start and length",
                    valid.RenderLoopRegionOnly &&
                    valid.LoopStartNanos == 1000000000L &&
                    valid.LoopLengthNanos == 5000000000L);

                string root = FindRepoRoot();
                string orchestratorPath = root == null
                    ? string.Empty
                    : Path.Combine(root, "vegas_panel", "RenderOrchestrator.cs");
                string orchestrator = File.Exists(orchestratorPath)
                    ? File.ReadAllText(orchestratorPath)
                    : string.Empty;
                Check("Orchestrator uses loop range for the video pass",
                    orchestrator.IndexOf(
                        "plan.VideoTempPath, videoTemplate, start, length",
                        StringComparison.Ordinal) >= 0);
                Check("Orchestrator uses loop range for the audio pass",
                    orchestrator.IndexOf("Start = start", StringComparison.Ordinal) >= 0 &&
                    orchestrator.IndexOf("Length = length", StringComparison.Ordinal) >= 0);
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                    // Best effort.
                }
            }
        }

        private static void VerifyDeliveryPresets()
        {
            string dir = Path.Combine(
                Path.GetTempPath(), "vs71_presets_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                Check("FLAC MKV extension",
                    DeliveryPresets.Extension(DeliveryPreset.FlacMkv) == ".mkv");
                Check("Opus MKV extension",
                    DeliveryPresets.Extension(DeliveryPreset.OpusMkv) == ".mkv");
                Check("FLAC MKV codec is FLAC",
                    DeliveryPresets.Codec(DeliveryPreset.FlacMkv) == AudioCodec71.Flac);
                Check("Opus MKV codec is Opus",
                    DeliveryPresets.Codec(DeliveryPreset.OpusMkv) == AudioCodec71.Opus);
                Check("FLAC maps to FLAC preset",
                    DeliveryPresets.ForCodec(AudioCodec71.Flac) == DeliveryPreset.FlacMkv);
                Check("Opus maps to Opus preset",
                    DeliveryPresets.ForCodec(AudioCodec71.Opus) == DeliveryPreset.OpusMkv);
                Check("Vorbis MKV extension",
                    DeliveryPresets.Extension(DeliveryPreset.VorbisMkv) == ".mkv");
                Check("WavPack MKV extension",
                    DeliveryPresets.Extension(DeliveryPreset.WavPackMkv) == ".mkv");
                Check("LPCM MKV extension",
                    DeliveryPresets.Extension(DeliveryPreset.PcmMkv) == ".mkv");
                Check("Vorbis codec maps",
                    DeliveryPresets.Codec(DeliveryPreset.VorbisMkv) == AudioCodec71.Vorbis);
                Check("WavPack codec maps",
                    DeliveryPresets.Codec(DeliveryPreset.WavPackMkv) == AudioCodec71.WavPack);
                Check("LPCM codec maps",
                    DeliveryPresets.Codec(DeliveryPreset.PcmMkv) == AudioCodec71.Pcm24);
                Check("Vorbis maps to Vorbis preset",
                    DeliveryPresets.ForCodec(AudioCodec71.Vorbis) == DeliveryPreset.VorbisMkv);
                Check("WavPack maps to WavPack preset",
                    DeliveryPresets.ForCodec(AudioCodec71.WavPack) == DeliveryPreset.WavPackMkv);
                Check("LPCM maps to LPCM preset",
                    DeliveryPresets.ForCodec(AudioCodec71.Pcm24) == DeliveryPreset.PcmMkv);

                Check("Detect AVC/H.264 family",
                    DeliveryPresets.DetectVideoFamily("AVC (H.264)", "video/h264") ==
                    VideoCodecFamily.H264);
                Check("Detect HEVC family",
                    DeliveryPresets.DetectVideoFamily("HEVC (H.265)", "video/h265") ==
                    VideoCodecFamily.HEVC);
                Check("Detect ProRes family",
                    DeliveryPresets.DetectVideoFamily("Apple ProRes", "video/quicktime") ==
                    VideoCodecFamily.ProRes);
                Check("Detect AV1 family",
                    DeliveryPresets.DetectVideoFamily("AV1", "video/av1") ==
                    VideoCodecFamily.AV1);

                Check("FLAC MKV + ProRes valid",
                    PresetPlan(dir, "lossless.mkv", AudioCodec71.Flac,
                        DeliveryPreset.FlacMkv, VideoCodecFamily.ProRes).Validate() == null);
                Check("Opus MKV + HEVC valid",
                    PresetPlan(dir, "opus.mkv", AudioCodec71.Opus,
                        DeliveryPreset.OpusMkv, VideoCodecFamily.HEVC).Validate() == null);
                Check("Opus MKV + AV1 valid",
                    PresetPlan(dir, "opus-av1.mkv", AudioCodec71.Opus,
                        DeliveryPreset.OpusMkv, VideoCodecFamily.AV1).Validate() == null);
                Check("Vorbis MKV + H.264 valid",
                    PresetPlan(dir, "vorbis.mkv", AudioCodec71.Vorbis,
                        DeliveryPreset.VorbisMkv, VideoCodecFamily.H264).Validate() == null);
                Check("WavPack MKV + ProRes valid",
                    PresetPlan(dir, "wavpack.mkv", AudioCodec71.WavPack,
                        DeliveryPreset.WavPackMkv, VideoCodecFamily.ProRes).Validate() == null);
                Check("LPCM MKV + HEVC valid",
                    PresetPlan(dir, "lpcm.mkv", AudioCodec71.Pcm24,
                        DeliveryPreset.PcmMkv, VideoCodecFamily.HEVC).Validate() == null);
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                    // Best effort.
                }
            }
        }

        private static RenderPlan PresetPlan(
            string dir,
            string name,
            AudioCodec71 codec,
            DeliveryPreset preset,
            VideoCodecFamily family)
        {
            return RenderPlan.Create(
                Path.Combine(dir, name), codec, true, preset, family);
        }

        private static void VerifyRenderPlan()
        {
            string dir = Path.Combine(
                Path.GetTempPath(), "vs71_phase4b_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                Check("FLAC/MKV plan valid",
                    Plan(dir, "master.mkv", AudioCodec71.Flac).Validate() == null);
                Check("Opus/MKV plan valid",
                    Plan(dir, "master-opus.mkv", AudioCodec71.Opus).Validate() == null);
                Check("Vorbis/MKV plan valid",
                    Plan(dir, "master-vorbis.mkv", AudioCodec71.Vorbis).Validate() == null);
                Check("WavPack/MKV plan valid",
                    Plan(dir, "master-wavpack.mkv", AudioCodec71.WavPack).Validate() == null);
                Check("LPCM/MKV plan valid",
                    Plan(dir, "master-lpcm.mkv", AudioCodec71.Pcm24).Validate() == null);
                Check("FLAC/MP4 rejected",
                    Plan(dir, "master.mp4", AudioCodec71.Flac).Validate() != null);
                Check("Opus/MOV rejected",
                    Plan(dir, "master.mov", AudioCodec71.Opus).Validate() != null);
                Check("Unknown extension rejected",
                    Plan(dir, "master.avi", AudioCodec71.Flac).Validate() != null);

                RenderPlan plan = Plan(dir, "cleanup.mkv", AudioCodec71.Flac);
                Check("Video temp is a sibling",
                    Path.GetDirectoryName(plan.VideoTempPath) == dir);
                Check("Audio temp is a sibling",
                    Path.GetDirectoryName(plan.AudioTempPath) == dir);
                Check("Temp stems are distinct",
                    plan.VideoTempPath != plan.AudioTempPath);

                File.WriteAllText(plan.VideoTempPath, "video");
                File.WriteAllText(plan.AudioTempPath, "audio");
                plan.CleanupTemps();
                Check("Temp cleanup removes video",
                    !File.Exists(plan.VideoTempPath));
                Check("Temp cleanup removes audio",
                    !File.Exists(plan.AudioTempPath));

                Check("Describe FLAC",
                    RenderPlan.DescribeCodec(AudioCodec71.Flac) == "FLAC Lossless 7.1");
                Check("Describe Opus",
                    RenderPlan.DescribeCodec(AudioCodec71.Opus).IndexOf("Opus",
                        StringComparison.OrdinalIgnoreCase) >= 0);
                Check("Describe Vorbis",
                    RenderPlan.DescribeCodec(AudioCodec71.Vorbis).IndexOf("Vorbis",
                        StringComparison.OrdinalIgnoreCase) >= 0);
                Check("Describe WavPack",
                    RenderPlan.DescribeCodec(AudioCodec71.WavPack).IndexOf("WavPack",
                        StringComparison.OrdinalIgnoreCase) >= 0);
                Check("Describe LPCM",
                    RenderPlan.DescribeCodec(AudioCodec71.Pcm24).IndexOf("LPCM",
                        StringComparison.OrdinalIgnoreCase) >= 0);
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                    // Best effort.
                }
            }
        }

        private static void VerifyLivePanWiring()
        {
            string root = FindRepoRoot();
            Check("Live pan: repository root located", root != null);
            if (root == null)
            {
                return;
            }

            Type session = typeof(LivePanSession);
            Check("LivePanSession is public", session.IsPublic);
            Check("LivePanSession implements IDisposable",
                typeof(IDisposable).IsAssignableFrom(session));
            Check("LivePanSession.TryBegin exists",
                session.GetMethod("TryBegin", BindingFlags.Public | BindingFlags.Static) != null);
            Check("LivePanSession.Apply exists",
                session.GetMethod("Apply", BindingFlags.Public | BindingFlags.Instance) != null);
            Check("LivePanSession.Readback exists",
                session.GetMethod("Readback", BindingFlags.Public | BindingFlags.Instance) != null);

            string xamlPath = Path.Combine(
                root, "vegas_panel", "UI", "SurroundPanelView.xaml");
            string xaml = File.Exists(xamlPath) ? File.ReadAllText(xamlPath) : string.Empty;
            Check("Live pan toggle present in panel XAML",
                xaml.IndexOf("x:Name=\"LivePanToggle\"", StringComparison.Ordinal) >= 0);
            Check("Live status indicator present in panel XAML",
                xaml.IndexOf("x:Name=\"LiveStatusText\"", StringComparison.Ordinal) >= 0);
            Check("LFE slider starts a live drag",
                xaml.IndexOf("x:Name=\"LfeSlider\"", StringComparison.Ordinal) >= 0 &&
                xaml.IndexOf("PreviewMouseLeftButtonDown=\"OnSliderPressed\"",
                    StringComparison.Ordinal) >= 0);
            Check("Spread slider starts a live drag",
                xaml.IndexOf("x:Name=\"SpreadSlider\"", StringComparison.Ordinal) >= 0 &&
                xaml.IndexOf("PreviewMouseLeftButtonDown=\"OnSliderPressed\"",
                    StringComparison.Ordinal) >= 0);

            string codePath = Path.Combine(
                root, "vegas_panel", "UI", "SurroundPanelView.xaml.cs");
            string code = File.Exists(codePath) ? File.ReadAllText(codePath) : string.Empty;
            Check("Panel starts a live session on puck down",
                code.IndexOf("BeginLivePan(\"Puck drag\")", StringComparison.Ordinal) >= 0);
            Check("Panel applies live updates from a timer",
                code.IndexOf("OnLiveTimerTick", StringComparison.Ordinal) >= 0 &&
                code.IndexOf("ApplyLiveNow", StringComparison.Ordinal) >= 0);
            Check("Panel falls back to deferred commit when live fails",
                code.IndexOf("CommitPanning(\"Puck drag\")", StringComparison.Ordinal) >= 0);
            Check("Puck drag recovers lost mouse capture",
                xaml.IndexOf("LostMouseCapture=\"OnRoomLostMouseCapture\"",
                    StringComparison.Ordinal) >= 0 &&
                code.IndexOf("OnRoomLostMouseCapture", StringComparison.Ordinal) >= 0);
            Check("Track list uses 1-based Track N labels",
                code.IndexOf("\"Track \" + DisplayNumber",
                    StringComparison.Ordinal) >= 0);
            Check("Track list appends the track name after a dash",
                code.IndexOf("\"Track \" + DisplayNumber + \" - \" + Name",
                    StringComparison.Ordinal) >= 0);
            Check("Track list uses real VEGAS track numbers",
                code.IndexOf("audio.Index + 1", StringComparison.Ordinal) >= 0);
            Check("Track list excludes video tracks",
                code.IndexOf("track is VideoTrack", StringComparison.Ordinal) >= 0);

            string matrixPath = Path.Combine(
                root, "vegas_panel", "BusMatrixManager.cs");
            string matrix = File.Exists(matrixPath) ? File.ReadAllText(matrixPath) : string.Empty;
            Check("Live writes use a single drag undo block",
                matrix.IndexOf("new UndoBlock(project, \"7.1 Live Pan\")",
                    StringComparison.Ordinal) >= 0);
            Check("Static live mode keeps a time-zero anchor",
                matrix.IndexOf("staticCollapsed", StringComparison.Ordinal) >= 0);
        }

        private static void VerifyProjectResetWiring()
        {
            string root = FindRepoRoot();
            Check("Project reset: repository root located", root != null);
            if (root == null)
            {
                return;
            }

            string hostPath = Path.Combine(
                root, "vegas_panel", "UI", "HostPanel.cs");
            string host = File.Exists(hostPath) ? File.ReadAllText(hostPath) : string.Empty;
            Check("HostPanel subscribes to ProjectOpened",
                host.IndexOf("ProjectOpened", StringComparison.Ordinal) >= 0);
            Check("HostPanel subscribes to ProjectClosed",
                host.IndexOf("ProjectClosed", StringComparison.Ordinal) >= 0);
            Check("HostPanel forwards project changes to the view",
                host.IndexOf("ResetForProjectChange", StringComparison.Ordinal) >= 0);

            string viewPath = Path.Combine(
                root, "vegas_panel", "UI", "SurroundPanelView.xaml.cs");
            string view = File.Exists(viewPath) ? File.ReadAllText(viewPath) : string.Empty;
            Check("Panel exposes ResetForProjectChange",
                view.IndexOf("ResetForProjectChange", StringComparison.Ordinal) >= 0);
            Check("Project reset clears the undo stack",
                view.IndexOf("_undoStack.Clear()", StringComparison.Ordinal) >= 0);
            Check("Project reset clears the selected track",
                view.IndexOf("_selectedTrack = null", StringComparison.Ordinal) >= 0);
            Check("Project reset clears pending points",
                view.IndexOf("_pendingPoints.Clear()", StringComparison.Ordinal) >= 0);
            Check("Panel detects project identity changes",
                view.IndexOf("EnsureProjectCurrent", StringComparison.Ordinal) >= 0 &&
                view.IndexOf("Project.COM", StringComparison.Ordinal) >= 0);
            Check("Panel writes a persistent diagnostic log",
                view.IndexOf("panel.log", StringComparison.Ordinal) >= 0);

            string xamlPath = Path.Combine(
                root, "vegas_panel", "UI", "SurroundPanelView.xaml");
            string xaml = File.Exists(xamlPath) ? File.ReadAllText(xamlPath) : string.Empty;
            Check("XAML exposes Reset Puck Position",
                xaml.IndexOf("Reset Puck Position", StringComparison.Ordinal) >= 0);
            Check("XAML exposes Reset Track Keyframes",
                xaml.IndexOf("Reset Track Keyframes", StringComparison.Ordinal) >= 0);
            Check("XAML exposes Reset All",
                xaml.IndexOf("Reset All", StringComparison.Ordinal) >= 0);

            string matrixPath = Path.Combine(
                root, "vegas_panel", "BusMatrixManager.cs");
            string matrix = File.Exists(matrixPath) ? File.ReadAllText(matrixPath) : string.Empty;
            Check("BusMatrixManager clears track keyframes",
                matrix.IndexOf("ClearTrackKeyframes", StringComparison.Ordinal) >= 0);
            Check("Reset actions use confirmation dialogs",
                view.IndexOf("MessageBoxButton.YesNo", StringComparison.Ordinal) >= 0);
        }

        private static void VerifyPanelXamlLoads()
        {
            Exception failure = null;
            var thread = new Thread(delegate()
            {
                try
                {
                    var view = new SurroundPanelView();
                    view.Measure(new Size(1120, 780));
                    view.Arrange(new Rect(0, 0, 1120, 780));
                    view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    view.ResetForProjectChange();
                    view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            Check("Panel XAML loads on an STA thread", failure == null,
                failure == null ? null : failure.Message);
        }

        private static void VerifyWorkspaceShell()
        {
            string root = FindRepoRoot();
            Check("Workspace shell: repository root located", root != null);
            if (root == null)
            {
                return;
            }

            string shellXamlPath = Path.Combine(
                root, "vegas_panel", "UI", "WorkspaceShellView.xaml");
            string shellXaml = File.Exists(shellXamlPath)
                ? File.ReadAllText(shellXamlPath)
                : string.Empty;
            Check("Workspace shell XAML exists",
                shellXaml.IndexOf("WorkspaceShellView", StringComparison.Ordinal) >= 0);
            Check("Workspace shell exposes the 7.1 segmented tab",
                shellXaml.IndexOf("x:Name=\"Workspace71Button\"", StringComparison.Ordinal) >= 0 &&
                shellXaml.IndexOf("7.1 Panner", StringComparison.Ordinal) >= 0);
            Check("Workspace shell exposes the HOA segmented tab",
                shellXaml.IndexOf("x:Name=\"WorkspaceHoaButton\"", StringComparison.Ordinal) >= 0 &&
                shellXaml.IndexOf("HOA / 3D Spatial", StringComparison.Ordinal) >= 0);

            string shellCodePath = Path.Combine(
                root, "vegas_panel", "UI", "WorkspaceShellView.xaml.cs");
            string shellCode = File.Exists(shellCodePath)
                ? File.ReadAllText(shellCodePath)
                : string.Empty;
            Check("Workspace shell caches workspace instances",
                shellCode.IndexOf("WorkspaceHost.Children.Add", StringComparison.Ordinal) >= 0 &&
                shellCode.IndexOf("ShowWorkspace", StringComparison.Ordinal) >= 0);
            Check("Workspace switching only toggles visibility",
                shellCode.IndexOf("Visibility.Collapsed", StringComparison.Ordinal) >= 0 &&
                shellCode.IndexOf("BusMatrixManager", StringComparison.Ordinal) < 0 &&
                shellCode.IndexOf("EnvelopeType", StringComparison.Ordinal) < 0);

            string hoaXamlPath = Path.Combine(
                root, "vegas_panel", "UI", "HoaSpatialView.xaml");
            string hoaXaml = File.Exists(hoaXamlPath)
                ? File.ReadAllText(hoaXamlPath)
                : string.Empty;
            Check("HOA XAML exists",
                hoaXaml.IndexOf("HoaSpatialView", StringComparison.Ordinal) >= 0);
            Check("HOA XAML exposes the 3D soundstage canvas",
                hoaXaml.IndexOf("x:Name=\"StageCanvas\"", StringComparison.Ordinal) >= 0);
            Check("HOA XAML exposes order/preview/export selectors",
                hoaXaml.IndexOf("x:Name=\"OrderCombo\"", StringComparison.Ordinal) >= 0 &&
                hoaXaml.IndexOf("x:Name=\"PreviewCombo\"", StringComparison.Ordinal) >= 0 &&
                hoaXaml.IndexOf("x:Name=\"ExportCombo\"", StringComparison.Ordinal) >= 0);
            Check("HOA XAML exposes Reset 3D Position",
                hoaXaml.IndexOf("Reset 3D Position", StringComparison.Ordinal) >= 0);

            string hoaCodePath = Path.Combine(
                root, "vegas_panel", "UI", "HoaSpatialView.xaml.cs");
            string hoaCode = File.Exists(hoaCodePath)
                ? File.ReadAllText(hoaCodePath)
                : string.Empty;
            Check("HOA workspace never touches the 7.1 bus matrix",
                hoaCode.IndexOf("BusMatrixManager", StringComparison.Ordinal) < 0 &&
                hoaCode.IndexOf("EnvelopeType", StringComparison.Ordinal) < 0 &&
                hoaCode.IndexOf("ApplyTrackPanning", StringComparison.Ordinal) < 0 &&
                hoaCode.IndexOf("LivePanSession", StringComparison.Ordinal) < 0);
            Check("HOA workspace shares WorkspaceContext tracking",
                hoaCode.IndexOf("WorkspaceContext", StringComparison.Ordinal) >= 0 &&
                hoaCode.IndexOf("SetSelectedTrack", StringComparison.Ordinal) >= 0);
            Check("HOA track list excludes video tracks",
                hoaCode.IndexOf("track is VideoTrack", StringComparison.Ordinal) >= 0);
            Check("HOA track list uses real VEGAS track numbers",
                hoaCode.IndexOf("audio.Index + 1", StringComparison.Ordinal) >= 0);
            Check("HOA XAML declares a readable dark TextBox style",
                hoaXaml.IndexOf("<Style TargetType=\"TextBox\">",
                    StringComparison.Ordinal) >= 0);
            Check("HOA XAML advertises AmbiX render readiness",
                hoaXaml.IndexOf("AMBIX RENDER READY", StringComparison.Ordinal) >= 0);
            Check("HOA XAML exposes export and preview controls",
                hoaXaml.IndexOf("x:Name=\"HoaExportButton\"", StringComparison.Ordinal) >= 0 &&
                hoaXaml.IndexOf("x:Name=\"HoaPreviewButton\"", StringComparison.Ordinal) >= 0 &&
                hoaXaml.IndexOf("x:Name=\"HoaOutputPathBox\"", StringComparison.Ordinal) >= 0 &&
                hoaXaml.IndexOf("Export HOA Master", StringComparison.Ordinal) >= 0 &&
                hoaXaml.IndexOf("Preview (Binaural)", StringComparison.Ordinal) >= 0);
            Check("HOA source changes emit diagnostics",
                hoaCode.IndexOf("HOA source:", StringComparison.Ordinal) >= 0);
            Check("HOA view wires export, preview, and SoundPlayer",
                hoaCode.IndexOf("OnHoaExportClicked", StringComparison.Ordinal) >= 0 &&
                hoaCode.IndexOf("OnHoaPreviewClicked", StringComparison.Ordinal) >= 0 &&
                hoaCode.IndexOf("SoundPlayer", StringComparison.Ordinal) >= 0 &&
                hoaCode.IndexOf("RenderMaster", StringComparison.Ordinal) >= 0 &&
                hoaCode.IndexOf("RenderPreview", StringComparison.Ordinal) >= 0);
            Check("HOA Export selector lists WAV/W64/FLAC and IAMF",
                hoaXaml.IndexOf("x:Name=\"ExportCombo\"",
                    StringComparison.Ordinal) >= 0 &&
                hoaCode.IndexOf("HOA AmbiX WAV", StringComparison.Ordinal) >= 0 &&
                hoaCode.IndexOf("HOA AmbiX Wave64", StringComparison.Ordinal) >= 0 &&
                hoaCode.IndexOf("HOA AmbiX FLAC", StringComparison.Ordinal) >= 0 &&
                hoaCode.IndexOf("IAMF / Eclipsa (Planned)",
                    StringComparison.Ordinal) >= 0);
            Check("HOA XAML exposes precise numeric entry fields",
                hoaXaml.IndexOf("OnHoaNumericFieldLostFocus",
                    StringComparison.Ordinal) >= 0 &&
                hoaXaml.IndexOf("OnHoaNumericFieldKeyDown",
                    StringComparison.Ordinal) >= 0 &&
                hoaCode.IndexOf("TryParseHoaNumber", StringComparison.Ordinal) >= 0);
            Check("HOA view disables export for IAMF/Eclipsa",
                hoaCode.IndexOf("IamfEclipsaPlanned", StringComparison.Ordinal) >= 0 &&
                hoaCode.IndexOf("HoaExportButton.IsEnabled",
                    StringComparison.Ordinal) >= 0);
            Check("HOA XAML exposes source/effects/monitor tabs",
                hoaXaml.IndexOf("x:Name=\"SourceTabButton\"", StringComparison.Ordinal) >= 0 &&
                hoaXaml.IndexOf("x:Name=\"EffectsTabButton\"", StringComparison.Ordinal) >= 0 &&
                hoaXaml.IndexOf("x:Name=\"MonitorTabButton\"", StringComparison.Ordinal) >= 0);
            Check("HOA XAML exposes distance/air/head/room controls",
                hoaXaml.IndexOf("x:Name=\"DistanceModelCombo\"", StringComparison.Ordinal) >= 0 &&
                hoaXaml.IndexOf("x:Name=\"AirSlider\"", StringComparison.Ordinal) >= 0 &&
                hoaXaml.IndexOf("x:Name=\"HeadModelCombo\"", StringComparison.Ordinal) >= 0 &&
                hoaXaml.IndexOf("x:Name=\"HeadWidthSlider\"", StringComparison.Ordinal) >= 0 &&
                hoaXaml.IndexOf("x:Name=\"RoomAmountSlider\"", StringComparison.Ordinal) >= 0 &&
                hoaXaml.IndexOf("x:Name=\"RoomSizeSlider\"", StringComparison.Ordinal) >= 0);
            Check("HOA view exposes selected-track vs full-mix source mode",
                hoaXaml.IndexOf("x:Name=\"SourceModeCombo\"", StringComparison.Ordinal) >= 0 &&
                hoaCode.IndexOf("OnSourceModeChanged", StringComparison.Ordinal) >= 0 &&
                hoaCode.IndexOf("FullMix", StringComparison.Ordinal) >= 0);
            Check("HOA XAML exposes presets, snap, A/B, and meters",
                hoaXaml.IndexOf("x:Name=\"SourcePresetCombo\"", StringComparison.Ordinal) >= 0 &&
                hoaXaml.IndexOf("x:Name=\"SnapCombo\"", StringComparison.Ordinal) >= 0 &&
                hoaXaml.IndexOf("x:Name=\"AbButton\"", StringComparison.Ordinal) >= 0 &&
                hoaXaml.IndexOf("x:Name=\"HoaLeftMeter\"", StringComparison.Ordinal) >= 0 &&
                hoaXaml.IndexOf("x:Name=\"HoaRightMeter\"", StringComparison.Ordinal) >= 0);
            Check("HOA view wires effects, presets, snap, A/B, meters",
                hoaCode.IndexOf("OnDistanceModelChanged", StringComparison.Ordinal) >= 0 &&
                hoaCode.IndexOf("OnAirChanged", StringComparison.Ordinal) >= 0 &&
                hoaCode.IndexOf("OnHeadModelChanged", StringComparison.Ordinal) >= 0 &&
                hoaCode.IndexOf("OnSourcePresetChanged", StringComparison.Ordinal) >= 0 &&
                hoaCode.IndexOf("OnSnapChanged", StringComparison.Ordinal) >= 0 &&
                hoaCode.IndexOf("OnAbClicked", StringComparison.Ordinal) >= 0 &&
                hoaCode.IndexOf("UpdateHoaMeters", StringComparison.Ordinal) >= 0);
            Check("HOA view persists and restores project state",
                hoaCode.IndexOf("HoaProjectStateStore.Save", StringComparison.Ordinal) >= 0 &&
                hoaCode.IndexOf("HoaProjectStateStore.TryLoad", StringComparison.Ordinal) >= 0 &&
                hoaCode.IndexOf("ScheduleProjectStateSave", StringComparison.Ordinal) >= 0);
            Check("HOA view exposes Reset Effects",
                hoaXaml.IndexOf("Reset Effects", StringComparison.Ordinal) >= 0 &&
                hoaCode.IndexOf("OnResetEffectsClicked", StringComparison.Ordinal) >= 0);
            Check("HOA workspace state store exists",
                File.Exists(Path.Combine(root, "vegas_panel", "UI",
                    "HoaProjectStateStore.cs")));

            var savedState = new HoaSpatialState
            {
                AzimuthDegrees = 37.5,
                ElevationDegrees = -12.0,
                Distance = 0.42,
                Spread = 0.66,
                Order = HoaOrder.Third,
                PreviewMode = HoaPreviewMode.Bypass,
                ExportTarget = HoaExportTarget.AmbiXWavFlac,
                SourceMode = HoaSourceMode.FullMix,
                DistanceModel = HoaDistanceModel.InverseSquare,
                AirAbsorption = 0.75,
                HeadModel = HoaHeadModel.ItdOnly,
                HeadWidth = 1.25,
                RoomAmount = 0.40,
                RoomSize = 0.80
            };
            string serialized = HoaProjectStateStore.Serialize(
                savedState, @"C:\Renders\HOA Test.wav", 15);
            var loadedState = new HoaSpatialState();
            string loadedPath;
            int loadedSnap;
            bool loaded = HoaProjectStateStore.TryDeserialize(
                serialized, loadedState, out loadedPath, out loadedSnap);
            Check("HOA project state round-trips",
                loaded &&
                Math.Abs(loadedState.AzimuthDegrees - 37.5) < 1e-9 &&
                Math.Abs(loadedState.AirAbsorption - 0.75) < 1e-9 &&
                loadedState.Order == HoaOrder.Third &&
                loadedState.SourceMode == HoaSourceMode.FullMix &&
                loadedState.HeadModel == HoaHeadModel.ItdOnly &&
                Math.Abs(loadedState.RoomSize - 0.80) < 1e-9 &&
                loadedPath == @"C:\Renders\HOA Test.wav" &&
                loadedSnap == 15);
            Check("HOA project state rejects unknown versions",
                !HoaProjectStateStore.TryDeserialize(
                    "version=99\r\norder=1\r\n", new HoaSpatialState(),
                    out loadedPath, out loadedSnap));

            var resetState = new HoaSpatialState
            {
                AzimuthDegrees = 45.0,
                Order = HoaOrder.Second,
                AirAbsorption = 1.0,
                HeadModel = HoaHeadModel.IldOnly,
                RoomAmount = 0.9
            };
            resetState.ResetEffects();
            Check("HOA Reset Effects preserves position/order",
                Math.Abs(resetState.AzimuthDegrees - 45.0) < 1e-9 &&
                resetState.Order == HoaOrder.Second &&
                Math.Abs(resetState.AirAbsorption) < 1e-9 &&
                resetState.HeadModel == HoaHeadModel.Both &&
                Math.Abs(resetState.RoomAmount) < 1e-9);
            Check("HOA state exposes distance/head model enums",
                File.Exists(Path.Combine(root, "vegas_panel", "UI", "HoaSpatialState.cs")) &&
                File.ReadAllText(Path.Combine(root, "vegas_panel", "UI",
                    "HoaSpatialState.cs")).IndexOf("HoaDistanceModel",
                    StringComparison.Ordinal) >= 0 &&
                File.ReadAllText(Path.Combine(root, "vegas_panel", "UI",
                    "HoaSpatialState.cs")).IndexOf("HoaHeadModel",
                    StringComparison.Ordinal) >= 0);
            string contextPath = Path.Combine(
                root, "vegas_panel", "UI", "WorkspaceContext.cs");
            string context = File.Exists(contextPath)
                ? File.ReadAllText(contextPath)
                : string.Empty;
            Check("WorkspaceContext exposes selected track/playhead/loop/diagnostics",
                context.IndexOf("SelectedTrack", StringComparison.Ordinal) >= 0 &&
                context.IndexOf("GetPlayhead", StringComparison.Ordinal) >= 0 &&
                context.IndexOf("TryGetLoopRegion", StringComparison.Ordinal) >= 0 &&
                context.IndexOf("WriteDiagnostic", StringComparison.Ordinal) >= 0);
            Check("WorkspaceContext caches track index/name only",
                context.IndexOf("public int SelectedTrackIndex", StringComparison.Ordinal) >= 0 &&
                context.IndexOf("public string SelectedTrackName", StringComparison.Ordinal) >= 0 &&
                context.IndexOf("SetSelectedTrack(int index, string name)",
                    StringComparison.Ordinal) >= 0 &&
                context.IndexOf("private AudioTrack _selectedTrack",
                    StringComparison.Ordinal) < 0);

            string hostPath = Path.Combine(
                root, "vegas_panel", "UI", "HostPanel.cs");
            string host = File.Exists(hostPath) ? File.ReadAllText(hostPath) : string.Empty;
            Check("HostPanel listens for track count changes",
                host.IndexOf("TrackCountChanged", StringComparison.Ordinal) >= 0 &&
                host.IndexOf("NotifyTrackListChanged", StringComparison.Ordinal) >= 0);
            Check("Workspace shell refreshes selectors after track changes",
                shellCode.IndexOf("NotifyTrackListChanged", StringComparison.Ordinal) >= 0);

            string haoRenderPath = Path.Combine(
                root, "vegas_panel", "HoaRenderOrchestrator.cs");
            string haoRender = File.Exists(haoRenderPath)
                ? File.ReadAllText(haoRenderPath)
                : string.Empty;
            Check("HOA orchestrator forwards extended render options",
                haoRender.IndexOf("distance_model =", StringComparison.Ordinal) >= 0 &&
                haoRender.IndexOf("air_absorption =", StringComparison.Ordinal) >= 0 &&
                haoRender.IndexOf("head_model =", StringComparison.Ordinal) >= 0 &&
                haoRender.IndexOf("room_amount =", StringComparison.Ordinal) >= 0 &&
                haoRender.IndexOf("RenderHoaMasterEx", StringComparison.Ordinal) >= 0 &&
                haoRender.IndexOf("RenderHoaPreviewEx", StringComparison.Ordinal) >= 0);
            Check("HOA render orchestrator exists",
                haoRender.IndexOf("HoaRenderOrchestrator", StringComparison.Ordinal) >= 0);
            Check("HOA orchestrator uses the selected track and loop region",
                haoRender.IndexOf("SelectedTrackIndex", StringComparison.Ordinal) >= 0 &&
                haoRender.IndexOf("TryGetLoopRegion", StringComparison.Ordinal) >= 0);
            Check("HOA orchestrator isolates and restores track state",
                haoRender.IndexOf("TrackState.Capture", StringComparison.Ordinal) >= 0 &&
                haoRender.IndexOf("ApplyIsolation", StringComparison.Ordinal) >= 0 &&
                haoRender.IndexOf("RestoreIsolation", StringComparison.Ordinal) >= 0);
            Check("HOA orchestrator isolates [7.1] bus returns",
                haoRender.IndexOf("ApplyBusIsolation", StringComparison.Ordinal) >= 0 &&
                haoRender.IndexOf("BusState.Capture", StringComparison.Ordinal) >= 0 &&
                haoRender.IndexOf("RestoreBuses", StringComparison.Ordinal) >= 0);
            Check("HOA orchestrator renders a source stem and cleans it up",
                haoRender.IndexOf("vegas.Render(", StringComparison.Ordinal) >= 0 &&
                haoRender.IndexOf("File.Delete(stemPath)", StringComparison.Ordinal) >= 0);
            Check("HOA orchestrator never touches the 7.1 bus matrix",
                haoRender.IndexOf("BusMatrixManager.", StringComparison.Ordinal) < 0 &&
                haoRender.IndexOf("EnvelopeType.", StringComparison.Ordinal) < 0 &&
                haoRender.IndexOf("ApplyTrackPanning(", StringComparison.Ordinal) < 0);
            Check("HOA orchestrator blocks FLAC above 8 channels",
                haoRender.IndexOf("FLAC supports at most 8 channels",
                    StringComparison.Ordinal) >= 0 &&
                haoRender.IndexOf("HOA_preview", StringComparison.Ordinal) >= 0);

            string bridgePath = Path.Combine(
                root, "vegas_panel", "NativeBridge.cs");
            string bridge = File.Exists(bridgePath)
                ? File.ReadAllText(bridgePath)
                : string.Empty;
            Check("NativeBridge exposes HOA master and preview exports",
                bridge.IndexOf("render_hoa_master", StringComparison.Ordinal) >= 0 &&
                bridge.IndexOf("render_hoa_preview", StringComparison.Ordinal) >= 0 &&
                bridge.IndexOf("RenderHoaMaster", StringComparison.Ordinal) >= 0 &&
                bridge.IndexOf("RenderHoaPreview", StringComparison.Ordinal) >= 0);
            Check("Workspaces keep isolated state objects",
                File.Exists(Path.Combine(root, "vegas_panel", "UI", "Surround71State.cs")) &&
                File.Exists(Path.Combine(root, "vegas_panel", "UI", "HoaSpatialState.cs")));

            Check("HOA channel count 1st order == 4",
                HoaSpatialMath.ChannelCount(1) == 4);
            Check("HOA channel count 2nd order == 9",
                HoaSpatialMath.ChannelCount(2) == 9);
            Check("HOA channel count 3rd order == 16",
                HoaSpatialMath.ChannelCount(3) == 16);
            Check("HOA order label 2nd order",
                HoaSpatialMath.OrderLabel(2) == "2nd Order (9 ch)");

            StagePoint front = HoaSpatialMath.ToOverheadScreen(0.0, 1.0, 100, 100, 50);
            Check("HOA overhead 0° maps to front", front.X == 100 && front.Y == 50);
            StagePoint right = HoaSpatialMath.ToOverheadScreen(90.0, 1.0, 100, 100, 50);
            Check("HOA overhead +90° maps right", right.X == 150 && right.Y == 100);
            StagePoint rear = HoaSpatialMath.ToOverheadScreen(180.0, 1.0, 100, 100, 50);
            Check("HOA overhead 180° maps rear", rear.X == 100 && rear.Y == 150);

            double azimuth;
            double distance;
            HoaSpatialMath.FromOverheadScreen(
                125, 100, 100, 100, 50, out azimuth, out distance);
            Check("HOA overhead round-trip +90° / 0.5",
                Math.Abs(azimuth - 90.0) < 1e-9 && Math.Abs(distance - 0.5) < 1e-9);

            Check("HOA elevation +90 maps to rail top",
                Math.Abs(HoaSpatialMath.ElevationToScreenY(90, 100, 50) - 50) < 1e-9);
            Check("HOA elevation -90 maps to rail bottom",
                Math.Abs(HoaSpatialMath.ElevationToScreenY(-90, 100, 50) - 150) < 1e-9);

            var state = new HoaSpatialState();
            Check("HOA state defaults to 1st order",
                state.Order == HoaOrder.First && state.ChannelCount == 4);
            state.AzimuthDegrees = 123.0;
            state.Order = HoaOrder.Third;
            state.Reset();
            Check("HOA reset restores defaults",
                state.Order == HoaOrder.First &&
                Math.Abs(state.AzimuthDegrees - HoaSpatialState.DefaultAzimuthDegrees) < 1e-9);

            Surround71State surround = Surround71State.CreateDefault();
            Check("7.1 state defaults remain unchanged",
                Math.Abs(surround.PuckX) < 1e-9 &&
                Math.Abs(surround.PuckY - 0.70) < 1e-9 &&
                Math.Abs(surround.LfeSend - 0.25) < 1e-9 &&
                Math.Abs(surround.Spread - 0.10) < 1e-9);

            var contextModel = new WorkspaceContext(null);
            long loopStart;
            long loopLength;
            Check("WorkspaceContext is safe without a project",
                contextModel.ProjectIdentity == "No project" &&
                contextModel.SelectedTrackIndex == -1 &&
                contextModel.GetProject() == null &&
                !contextModel.TryGetLoopRegion(out loopStart, out loopLength));

            int hoaChannels = NativeBridge.HoaChannelCount(3);
            Check("Native HOA core reports 3rd order 16 channels",
                hoaChannels == 16, "channels=" + hoaChannels);

            Exception failure = null;
            var thread = new Thread(delegate()
            {
                try
                {
                    var shell = new WorkspaceShellView();
                    shell.Measure(new Size(1120, 780));
                    shell.Arrange(new Rect(0, 0, 1120, 780));

                    shell.SurroundView.RaiseEvent(
                        new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    shell.HoaView.RaiseEvent(
                        new RoutedEventArgs(FrameworkElement.LoadedEvent));

                    shell.ShowWorkspace(WorkspaceKind.HoaSpatial);
                    shell.ShowWorkspace(WorkspaceKind.Surround71);
                    shell.ResetForProjectChange();
                    shell.RefreshAll();

                    shell.HoaView.RaiseEvent(
                        new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                    shell.SurroundView.RaiseEvent(
                        new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            Check("Workspace shell + both workspaces load and switch on an STA thread",
                failure == null, failure == null ? null : failure.ToString());
        }

        private static void VerifyHoaNativeRender()
        {
            string dir = Path.Combine(
                Path.GetTempPath(),
                "vegas_surround71_hoa_render_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                string input = Path.Combine(dir, "stem.wav");
                var samples = new short[4096];
                for (int index = 0; index < samples.Length; index++)
                {
                    samples[index] = (short)(8000.0 *
                        Math.Sin(2.0 * Math.PI * 440.0 * index / 48000.0));
                }

                WriteTestWav(input, 48000, samples);

                string master = Path.Combine(dir, "master_2nd.wav");
                int status = NativeBridge.RenderHoaMaster(
                    input, master, 2, 10.0, 5.0, 0.0, 0.0);
                Check("Native HOA 2nd-order master renders",
                    status == NativeBridge.HOA_RENDER_OK, "status=" + status);

                int channels;
                int bits;
                ReadTestWavInfo(master, out channels, out bits);
                Check("HOA 2nd-order master has 9 channels",
                    channels == 9, "channels=" + channels);
                Check("HOA master is 24-bit", bits == 24, "bits=" + bits);

                string preview = Path.Combine(dir, "preview.wav");
                status = NativeBridge.RenderHoaPreview(
                    input, preview, 1, 90.0, 0.0, 0.0, 0.0,
                    NativeBridge.HoaPreviewStereo);
                Check("Native HOA stereo preview renders",
                    status == NativeBridge.HOA_RENDER_OK, "status=" + status);

                ReadTestWavInfo(preview, out channels, out bits);
                Check("HOA preview has 2 channels",
                    channels == 2, "channels=" + channels);
                Check("HOA preview is 16-bit", bits == 16, "bits=" + bits);

                MethodInfo balanceReader = typeof(HoaRenderOrchestrator).GetMethod(
                    "DescribePreviewBalance",
                    BindingFlags.NonPublic | BindingFlags.Static);
                string balance = balanceReader == null
                    ? null
                    : balanceReader.Invoke(null, new object[] { preview }) as string;
                Check("HOA preview L/R balance diagnostic is available",
                    balance != null &&
                    balance.IndexOf("L ", StringComparison.Ordinal) >= 0 &&
                    balance.IndexOf("R ", StringComparison.Ordinal) >= 0,
                    balance);

                Check("HoaRenderOptions Marshal.SizeOf == 48",
                    HoaRenderOptions.MarshalSize == 48,
                    HoaRenderOptions.MarshalSize + " bytes");

                var exOptions = new HoaRenderOptions
                {
                    order = 1,
                    azimuth_deg = 20f,
                    elevation_deg = 10f,
                    distance = 0.4f,
                    spread = 0.1f,
                    distance_model = NativeBridge.HoaDistanceInverse,
                    air_absorption = 0.5f,
                    head_model = NativeBridge.HoaHeadBoth,
                    head_width = 1.0f,
                    room_amount = 0.5f,
                    room_size = 0.6f,
                    preview_mode = NativeBridge.HoaPreviewBinaural
                };

                string masterEx = Path.Combine(dir, "master_ex.wav");
                status = NativeBridge.RenderHoaMasterEx(exOptions, input, masterEx);
                Check("HOA extended master render succeeds",
                    status == NativeBridge.HOA_RENDER_OK, "status=" + status);
                ReadTestWavInfo(masterEx, out channels, out bits);
                Check("HOA extended master has 4 channels",
                    channels == 4, "channels=" + channels);

                string previewEx = Path.Combine(dir, "preview_ex.wav");
                status = NativeBridge.RenderHoaPreviewEx(exOptions, input, previewEx);
                Check("HOA extended preview render succeeds",
                    status == NativeBridge.HOA_RENDER_OK, "status=" + status);
                ReadTestWavInfo(previewEx, out channels, out bits);
                Check("HOA extended preview has 2 channels",
                    channels == 2, "channels=" + channels);

                status = NativeBridge.RenderHoaMaster(
                    input, Path.Combine(dir, "invalid.wav"), 4, 0.0, 0.0, 0.0, 0.0);
                Check("HOA master rejects order 4",
                    status == NativeBridge.HOA_RENDER_ERR_INVALID_ORDER,
                    "status=" + status);

                status = NativeBridge.RenderHoaMaster(
                    input, Path.Combine(dir, "master_3rd.flac"), 3, 0.0, 0.0, 0.0, 0.0);
                Check("HOA FLAC rejects 3rd order",
                    status == NativeBridge.HOA_RENDER_ERR_UNSUPPORTED_FORMAT,
                    "status=" + status);

                Check("HOA failed renders leave no temp files",
                    Directory.GetFiles(dir, "*.tmp.*").Length == 0);
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                    // Best effort.
                }
            }
        }

        private static void WriteTestWav(string path, int sampleRate, short[] samples)
        {
            using (FileStream stream = File.Create(path))
            using (var writer = new BinaryWriter(stream))
            {
                int dataSize = samples.Length * 2;
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + dataSize);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(sampleRate);
                writer.Write(sampleRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(dataSize);

                foreach (short sample in samples)
                {
                    writer.Write(sample);
                }
            }
        }

        private static void ReadTestWavInfo(
            string path, out int channels, out int bitsPerSample)
        {
            channels = 0;
            bitsPerSample = 0;
            if (!File.Exists(path))
            {
                return;
            }

            using (FileStream stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                reader.BaseStream.Position = 22;
                channels = reader.ReadUInt16();
                reader.BaseStream.Position = 34;
                bitsPerSample = reader.ReadUInt16();
            }
        }

        private static void VerifyDeployment()
        {
            string root = FindRepoRoot();
            Check("Repository root located", root != null);
            if (root == null)
            {
                return;
            }

            string deploy = Path.Combine(root, "deploy");
            string panel = Path.Combine(deploy, "TellTale7PointOne.dll");
            string native = Path.Combine(deploy, "telltale_7point1_core.dll");

            Check("Deployment folder exists", Directory.Exists(deploy));
            Check("Staged TellTale7PointOne.dll exists", File.Exists(panel));
            Check("Staged telltale_7point1_core.dll exists", File.Exists(native));
            Check("Staged panel DLL is non-empty",
                File.Exists(panel) && new FileInfo(panel).Length > 0);
            Check("Staged native DLL is non-empty",
                File.Exists(native) && new FileInfo(native).Length > 0);
        }

        private static RenderPlan Plan(string dir, string name, AudioCodec71 codec)
        {
            return RenderPlan.Create(Path.Combine(dir, name), codec, true);
        }

        private static SpeakerNode FindSpeaker(string channel)
        {
            foreach (SpeakerNode node in SoundstageMath.Speakers)
            {
                if (node.Channel == channel)
                {
                    return node;
                }
            }

            throw new InvalidOperationException("Speaker not found: " + channel);
        }

        private static string FindRepoRoot()
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            while (!string.IsNullOrEmpty(directory))
            {
                if (Directory.Exists(Path.Combine(directory, "vegas_panel")) &&
                    Directory.Exists(Path.Combine(directory, "deploy")))
                {
                    return directory;
                }

                DirectoryInfo parent = Directory.GetParent(directory);
                if (parent == null)
                {
                    break;
                }

                directory = parent.FullName;
            }

            return null;
        }

        private static Assembly ResolveScriptPortal(object sender, ResolveEventArgs args)
        {
            if (args.Name.StartsWith("ScriptPortal.Vegas", StringComparison.OrdinalIgnoreCase))
            {
                string path = @"C:\Program Files\BorisFX\Vegas Pro 2027\ScriptPortal.Vegas.dll";
                if (File.Exists(path))
                {
                    return Assembly.LoadFrom(path);
                }
            }

            return null;
        }

        private static void Check(string name, bool condition)
        {
            Check(name, condition, null);
        }

        private static void Check(string name, bool condition, string detail)
        {
            _checks++;
            if (!condition)
            {
                _failures++;
            }

            Console.WriteLine("[{0}] {1}{2}",
                condition ? "PASS" : "FAIL",
                name,
                string.IsNullOrEmpty(detail) ? string.Empty : " (" + detail + ")");
        }
    }
}
