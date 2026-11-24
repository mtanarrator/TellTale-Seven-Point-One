using System;
using System.Globalization;
using System.IO;

namespace VegasSurround71
{
    /// <summary>
    /// Pure render-plan model: validates the requested 7.1 delivery container
    /// and owns the temporary video/audio stem paths. No VEGAS API dependency,
    /// so it is unit-testable outside the host.
    /// </summary>
    public sealed class RenderPlan
    {
        private RenderPlan(
            string outputPath,
            string videoTempPath,
            string audioTempPath,
            AudioCodec71 codec,
            bool applyLfeLowpass,
            DeliveryPreset preset,
            VideoCodecFamily videoCodec,
            bool renderLoopRegionOnly,
            long loopStartNanos,
            long loopLengthNanos)
        {
            OutputPath = outputPath;
            VideoTempPath = videoTempPath;
            AudioTempPath = audioTempPath;
            Codec = codec;
            ApplyLfeLowpass = applyLfeLowpass;
            Preset = preset;
            VideoCodec = videoCodec;
            RenderLoopRegionOnly = renderLoopRegionOnly;
            LoopStartNanos = loopStartNanos;
            LoopLengthNanos = loopLengthNanos;
        }

        public string OutputPath { get; private set; }
        public string VideoTempPath { get; private set; }
        public string AudioTempPath { get; private set; }
        public AudioCodec71 Codec { get; private set; }
        public bool ApplyLfeLowpass { get; private set; }
        public DeliveryPreset Preset { get; private set; }
        public VideoCodecFamily VideoCodec { get; private set; }
        public bool RenderLoopRegionOnly { get; private set; }
        public long LoopStartNanos { get; private set; }
        public long LoopLengthNanos { get; private set; }

        /// <summary>
        /// Creates a plan with sibling temporary stems unique to this render.
        /// Preset defaults from the codec; video compatibility is not checked.
        /// </summary>
        public static RenderPlan Create(
            string outputPath, AudioCodec71 codec, bool applyLfeLowpass)
        {
            return Create(
                outputPath, codec, applyLfeLowpass,
                DeliveryPresets.ForCodec(codec), VideoCodecFamily.Unknown);
        }

        /// <summary>
        /// Creates a plan for a selected delivery preset and video template family.
        /// </summary>
        public static RenderPlan Create(
            string outputPath,
            AudioCodec71 codec,
            bool applyLfeLowpass,
            DeliveryPreset preset,
            VideoCodecFamily videoCodec)
        {
            return Create(
                outputPath, codec, applyLfeLowpass, preset, videoCodec,
                false, 0L, 0L);
        }

        /// <summary>
        /// Creates a plan with an optional loop-region render range.
        /// </summary>
        public static RenderPlan Create(
            string outputPath,
            AudioCodec71 codec,
            bool applyLfeLowpass,
            DeliveryPreset preset,
            VideoCodecFamily videoCodec,
            bool renderLoopRegionOnly,
            long loopStartNanos,
            long loopLengthNanos)
        {
            if (string.IsNullOrEmpty(outputPath))
            {
                throw new ArgumentException("Output path is required.", nameof(outputPath));
            }

            string directory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(directory))
            {
                directory = Directory.GetCurrentDirectory();
            }

            Directory.CreateDirectory(directory);
            string token = Guid.NewGuid().ToString("N");
            string video = Path.Combine(directory, ".vs71_" + token + "_video.mp4");
            string audio = Path.Combine(directory, ".vs71_" + token + "_audio.w64");

            return new RenderPlan(
                outputPath, video, audio, codec, applyLfeLowpass,
                preset, videoCodec,
                renderLoopRegionOnly, loopStartNanos, loopLengthNanos);
        }

        /// <summary>Returns null when valid, otherwise a user-facing reason.</summary>
        public string Validate()
        {
            if (!NativeBridge.IsCodecSupported(Codec))
            {
                return "Unsupported output codec.";
            }

            if (RenderLoopRegionOnly && LoopLengthNanos <= 0)
            {
                return "Set In/Out loop points on the VEGAS timeline first.";
            }

            if (RenderLoopRegionOnly && LoopStartNanos < 0)
            {
                return "Loop region start is invalid.";
            }

            string extension = Path.GetExtension(OutputPath);
            if (string.IsNullOrEmpty(extension))
            {
                return "Output file needs a " +
                    DeliveryPresets.Extension(Preset) + " extension.";
            }

            extension = extension.ToLowerInvariant();
            string expected = DeliveryPresets.Extension(Preset);
            if (extension != expected)
            {
                return "The " + DeliveryPresets.DisplayName(Preset) +
                    " preset requires a " + expected + " output file.";
            }

            if (VideoCodec != VideoCodecFamily.Unknown)
            {
                string compatibility = DeliveryPresets.CompatibilityError(
                    Preset, VideoCodec);
                if (compatibility != null)
                {
                    return compatibility;
                }
            }

            return null;
        }

        /// <summary>Deletes the temporary video/audio stems (best effort).</summary>
        public void CleanupTemps()
        {
            TryDelete(VideoTempPath);
            TryDelete(AudioTempPath);
        }

        public static string DescribeCodec(AudioCodec71 codec)
        {
            switch (codec)
            {
                case AudioCodec71.Flac:
                    return "FLAC Lossless 7.1";
                case AudioCodec71.Pcm24:
                    return "LPCM 24-bit 7.1";
                case AudioCodec71.Vorbis:
                    return "Vorbis 7.1";
                case AudioCodec71.WavPack:
                    return "WavPack Lossless 7.1";
                case AudioCodec71.Opus:
                    return "Opus 7.1 (768k)";
                default:
                    return codec.ToString();
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Best effort; a later cleanup can retry.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort.
            }
        }
    }

    /// <summary>Result of a 3-pass render orchestration.</summary>
    public sealed class RenderResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string OutputPath { get; set; }
        public int MuxStatus { get; set; }
        public bool UsedChannelMappingFallback { get; set; }
    }
}
