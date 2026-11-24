using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using VegasSurround71;

namespace VegasSurround71.AbiVerification
{
    /// <summary>
    /// Standalone Phase 3 ABI verification. Confirms managed/native layout parity,
    /// UTF-8 path marshaling, and that P/Invoke calls reach the compiled
    /// telltale_7point1_core.dll with the documented status codes.
    /// </summary>
    internal static class Program
    {
        private const string DefaultVegasSdkPath =
            @"C:\Program Files\BorisFX\Vegas Pro 2027\ScriptPortal.Vegas.dll";

        private static int _checks;
        private static int _failures;

        private static int Main()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveScriptPortal;

            try
            {
                VerifyStructLayout();
                VerifyEnumsAndStatusCodes();
                VerifyUtf8Marshaling();
                VerifyNativeCalls();
                VerifyHoaAbi();
                VerifyHoaRenderAbi();
            }
            catch (Exception ex)
            {
                _failures++;
                Console.WriteLine("[FAIL] Unhandled verification exception: " + ex);
            }

            Console.WriteLine();
            Console.WriteLine("Checks: {0}    Failures: {1}", _checks, _failures);
            Console.WriteLine(_failures == 0 ? "ABI VERIFICATION PASSED" : "ABI VERIFICATION FAILED");
            return _failures == 0 ? 0 : 1;
        }

        private static void VerifyStructLayout()
        {
            int size = Marshal.SizeOf(typeof(ChannelGains71));
            Check("ChannelGains71 Marshal.SizeOf == 32", size == 32, size + " bytes");

            CheckOffset("fl", 0);
            CheckOffset("fr", 4);
            CheckOffset("fc", 8);
            CheckOffset("lfe", 12);
            CheckOffset("bl", 16);
            CheckOffset("br", 20);
            CheckOffset("sl", 24);
            CheckOffset("sr", 28);

            var gains = new ChannelGains71
            {
                fl = 0.1f,
                fr = 0.2f,
                fc = 0.3f,
                lfe = 0.4f,
                bl = 0.5f,
                br = 0.6f,
                sl = 0.7f,
                sr = 0.8f
            };

            float[] flattened = gains.ToArray();
            bool ordered = flattened.Length == 8 &&
                           flattened[0] == 0.1f && flattened[3] == 0.4f && flattened[7] == 0.8f;
            Check("ChannelGains71 bus order FL,FR,FC,LFE,BL,BR,SL,SR", ordered);
            Check("ChannelGains71 indexer round-trip", gains[3] == 0.4f && gains[7] == 0.8f);
        }

        private static void VerifyEnumsAndStatusCodes()
        {
            Check("AudioCodec71.Flac == 2", (int)AudioCodec71.Flac == 2);
            Check("AudioCodec71.Pcm24 == 3", (int)AudioCodec71.Pcm24 == 3);
            Check("AudioCodec71.Opus == 4", (int)AudioCodec71.Opus == 4);
            Check("AudioCodec71.Vorbis == 5", (int)AudioCodec71.Vorbis == 5);
            Check("AudioCodec71.WavPack == 6", (int)AudioCodec71.WavPack == 6);
            Check("Royalty-free codecs reported supported",
                NativeBridge.IsCodecSupported(AudioCodec71.Flac) &&
                NativeBridge.IsCodecSupported(AudioCodec71.Pcm24) &&
                NativeBridge.IsCodecSupported(AudioCodec71.Opus) &&
                NativeBridge.IsCodecSupported(AudioCodec71.Vorbis) &&
                NativeBridge.IsCodecSupported(AudioCodec71.WavPack));

            Check("MUX_OK == 0", NativeBridge.MUX_OK == 0);
            Check("MUX_ERR_INVALID_ARGUMENT == -1", NativeBridge.MUX_ERR_INVALID_ARGUMENT == -1);
            Check("MUX_ERR_MISSING_INPUT == -2", NativeBridge.MUX_ERR_MISSING_INPUT == -2);
            Check("MUX_ERR_FFMPEG == -3", NativeBridge.MUX_ERR_FFMPEG == -3);
            Check("MUX_ERR_INVALID_CODEC == -4", NativeBridge.MUX_ERR_INVALID_CODEC == -4);
            Check("MUX_ERR_PANIC == -99", NativeBridge.MUX_ERR_PANIC == -99);
        }

        private static void VerifyUtf8Marshaling()
        {
            const string unicodePath = @"C:\Renders\Café 東京 🎬\Master 7.1.w64";
            byte[] encoded = Utf8Path.ToNullTerminatedUtf8(unicodePath);

            Check("UTF-8 path is NUL terminated", encoded[encoded.Length - 1] == 0);
            Check("UTF-8 path stores no embedded NUL",
                Array.IndexOf(encoded, (byte)0) == encoded.Length - 1);
            Check("UTF-8 Unicode/space round-trip",
                Utf8Path.FromNullTerminatedUtf8(encoded) == unicodePath,
                encoded.Length + " bytes");
            Check("UTF-8 null path stays null", Utf8Path.ToNullTerminatedUtf8(null) == null);

            bool rejectedNul = false;
            try
            {
                Utf8Path.ToNullTerminatedUtf8("bad\0path.w64");
            }
            catch (ArgumentException)
            {
                rejectedNul = true;
            }

            Check("UTF-8 embedded NUL is rejected", rejectedNul);
        }

        private static void VerifyNativeCalls()
        {
            string error;
            if (!NativeBridge.IsNativeAvailable(out error))
            {
                Check("Native DLL load and calculate_71_gains probe", false, error);
                return;
            }

            Check("Native DLL load and calculate_71_gains probe", true, NativeBridge.DllFileName);

            ChannelGains71 center = NativeBridge.calculate_71_gains(0f, 1f, 0.5f, 0f);
            Check("calculate_71_gains front-center routes to FC",
                Math.Abs(center.fc - 1.0f) < 1e-4f, "fc=" + center.fc.ToString("0.####"));
            Check("calculate_71_gains LFE send preserved",
                Math.Abs(center.lfe - 0.5f) < 1e-4f, "lfe=" + center.lfe.ToString("0.####"));

            int nullStatus = NativeBridge.mux_71_master(null, null, null, (int)AudioCodec71.Flac, false);
            Check("mux_71_master null pointers -> -1",
                nullStatus == NativeBridge.MUX_ERR_INVALID_ARGUMENT, "status=" + nullStatus);

            string tempDir = Path.Combine(
                Path.GetTempPath(), "vegas_surround71_abi_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string video = Path.Combine(tempDir, "source video.mp4");
                string audio = Path.Combine(tempDir, "vegas render 7.1.w64");
                string output = Path.Combine(tempDir, "master 7.1.flac.mkv");
                File.WriteAllBytes(video, new byte[] { 0x00 });
                File.WriteAllBytes(audio, new byte[] { 0x00 });

                int missing = NativeBridge.Mux71Master(
                    video,
                    Path.Combine(tempDir, "absent.w64"),
                    output,
                    AudioCodec71.Flac,
                    false);
                Check("mux_71_master missing input -> -2",
                    missing == NativeBridge.MUX_ERR_MISSING_INPUT, "status=" + missing);

                int opus = NativeBridge.Mux71Master(
                    video, audio, output, AudioCodec71.Opus, false);
                Check("mux_71_master Opus is not codec-gated",
                    opus != NativeBridge.MUX_ERR_INVALID_CODEC, "status=" + opus);

                int invalid = NativeBridge.mux_71_master(
                    Utf8Path.ToNullTerminatedUtf8(video),
                    Utf8Path.ToNullTerminatedUtf8(audio),
                    Utf8Path.ToNullTerminatedUtf8(output),
                    42,
                    false);
                Check("mux_71_master invalid codec -> -4",
                    invalid == NativeBridge.MUX_ERR_INVALID_CODEC, "status=" + invalid);
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup only.
                }
            }
        }

        private static void VerifyHoaAbi()
        {
            int size = Marshal.SizeOf(typeof(HoaSourcePosition));
            Check("HoaSourcePosition Marshal.SizeOf == 20", size == 20, size + " bytes");
            Check("Offset azimuth_deg == 0",
                Marshal.OffsetOf(typeof(HoaSourcePosition), "azimuth_deg").ToInt32() == 0);
            Check("Offset elevation_deg == 4",
                Marshal.OffsetOf(typeof(HoaSourcePosition), "elevation_deg").ToInt32() == 4);
            Check("Offset distance == 8",
                Marshal.OffsetOf(typeof(HoaSourcePosition), "distance").ToInt32() == 8);
            Check("Offset spread == 12",
                Marshal.OffsetOf(typeof(HoaSourcePosition), "spread").ToInt32() == 12);
            Check("Offset order == 16",
                Marshal.OffsetOf(typeof(HoaSourcePosition), "order").ToInt32() == 16);

            Check("hoa_channel_count 1st order == 4",
                NativeBridge.hoa_channel_count(1) == 4);
            Check("hoa_channel_count 2nd order == 9",
                NativeBridge.hoa_channel_count(2) == 9);
            Check("hoa_channel_count 3rd order == 16",
                NativeBridge.hoa_channel_count(3) == 16);

            var front = new HoaSourcePosition
            {
                azimuth_deg = 0f,
                elevation_deg = 0f,
                distance = 0f,
                spread = 0f,
                order = 1
            };

            float[] weights;
            string error;
            bool ok = NativeBridge.TryCalculateHoaWeights(front, out weights, out error);
            Check("calculate_hoa_weights front-center writes 4 weights",
                ok && weights.Length == 4, error);
            Check("calculate_hoa_weights front-center W/X are 1",
                ok && Math.Abs(weights[0] - 1f) < 1e-5f &&
                     Math.Abs(weights[3] - 1f) < 1e-5f);

            var overhead = new HoaSourcePosition
            {
                azimuth_deg = 0f,
                elevation_deg = 90f,
                distance = 0f,
                spread = 0f,
                order = 1
            };

            ok = NativeBridge.TryCalculateHoaWeights(overhead, out weights, out error);
            Check("calculate_hoa_weights overhead Z is 1",
                ok && Math.Abs(weights[2] - 1f) < 1e-5f, error);
        }

        private static void VerifyHoaRenderAbi()
        {
            Check("HOA_RENDER_OK == 0", NativeBridge.HOA_RENDER_OK == 0);
            Check("HOA_RENDER_ERR_INVALID_ARGUMENT == -1",
                NativeBridge.HOA_RENDER_ERR_INVALID_ARGUMENT == -1);
            Check("HOA_RENDER_ERR_PANIC == -99",
                NativeBridge.HOA_RENDER_ERR_PANIC == -99);
            Check("HOA preview mode binaural == 0",
                NativeBridge.HoaPreviewBinaural == 0);
            Check("HOA preview mode stereo == 1",
                NativeBridge.HoaPreviewStereo == 1);
            Check("HOA preview mode bypass == 2",
                NativeBridge.HoaPreviewBypass == 2);
            Check("HOA distance model none == 3",
                NativeBridge.HoaDistanceNone == 3);
            Check("HOA head model off == 3",
                NativeBridge.HoaHeadOff == 3);

            int optionsSize = Marshal.SizeOf(typeof(HoaRenderOptions));
            Check("HoaRenderOptions Marshal.SizeOf == 48",
                optionsSize == 48, optionsSize + " bytes");
            Check("HoaRenderOptions order offset == 0",
                Marshal.OffsetOf(typeof(HoaRenderOptions), "order").ToInt32() == 0);
            Check("HoaRenderOptions spread offset == 16",
                Marshal.OffsetOf(typeof(HoaRenderOptions), "spread").ToInt32() == 16);
            Check("HoaRenderOptions distance_model offset == 20",
                Marshal.OffsetOf(typeof(HoaRenderOptions), "distance_model").ToInt32() == 20);
            Check("HoaRenderOptions air_absorption offset == 24",
                Marshal.OffsetOf(typeof(HoaRenderOptions), "air_absorption").ToInt32() == 24);
            Check("HoaRenderOptions head_model offset == 28",
                Marshal.OffsetOf(typeof(HoaRenderOptions), "head_model").ToInt32() == 28);
            Check("HoaRenderOptions head_width offset == 32",
                Marshal.OffsetOf(typeof(HoaRenderOptions), "head_width").ToInt32() == 32);
            Check("HoaRenderOptions room_amount offset == 36",
                Marshal.OffsetOf(typeof(HoaRenderOptions), "room_amount").ToInt32() == 36);
            Check("HoaRenderOptions room_size offset == 40",
                Marshal.OffsetOf(typeof(HoaRenderOptions), "room_size").ToInt32() == 40);
            Check("HoaRenderOptions preview_mode offset == 44",
                Marshal.OffsetOf(typeof(HoaRenderOptions), "preview_mode").ToInt32() == 44);

            var exOptions = new HoaRenderOptions();
            int exMaster = NativeBridge.render_hoa_master_ex(
                ref exOptions, null, null);
            Check("render_hoa_master_ex null paths -> -1",
                exMaster == NativeBridge.HOA_RENDER_ERR_INVALID_ARGUMENT,
                "status=" + exMaster);

            int exPreview = NativeBridge.render_hoa_preview_ex(
                ref exOptions, null, null);
            Check("render_hoa_preview_ex null paths -> -1",
                exPreview == NativeBridge.HOA_RENDER_ERR_INVALID_ARGUMENT,
                "status=" + exPreview);

            int masterStatus = NativeBridge.render_hoa_master(
                null, null, 1, 0f, 0f, 0f, 0f);
            Check("render_hoa_master null pointers -> -1",
                masterStatus == NativeBridge.HOA_RENDER_ERR_INVALID_ARGUMENT,
                "status=" + masterStatus);

            int previewStatus = NativeBridge.render_hoa_preview(
                null, null, 1, 0f, 0f, 0f, 0f,
                NativeBridge.HoaPreviewBinaural);
            Check("render_hoa_preview null pointers -> -1",
                previewStatus == NativeBridge.HOA_RENDER_ERR_INVALID_ARGUMENT,
                "status=" + previewStatus);
        }

        private static void CheckOffset(string field, int expected)
        {
            int offset = (int)Marshal.OffsetOf(typeof(ChannelGains71), field);
            Check("Offset " + field + " == " + expected, offset == expected, offset + " bytes");
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

        private static Assembly ResolveScriptPortal(object sender, ResolveEventArgs args)
        {
            if (args.Name.StartsWith("ScriptPortal.Vegas", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(DefaultVegasSdkPath))
            {
                return Assembly.LoadFrom(DefaultVegasSdkPath);
            }

            return null;
        }
    }
}
