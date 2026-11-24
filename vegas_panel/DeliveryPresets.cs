using System;

namespace VegasSurround71
{
    /// <summary>Royalty-free delivery presets exposed in the render section.</summary>
    public enum DeliveryPreset
    {
        FlacMkv = 0,
        OpusMkv = 1,
        VorbisMkv = 2,
        WavPackMkv = 3,
        PcmMkv = 4
    }

    /// <summary>Video codec family used for container compatibility checks.</summary>
    public enum VideoCodecFamily
    {
        Unknown = 0,
        H264 = 1,
        HEVC = 2,
        AV1 = 3,
        ProRes = 4
    }

    /// <summary>
    /// Pure delivery rules: preset -> container/codec, video-family compatibility,
    /// and codec display names. Shared by the WPF render section and tests.
    /// </summary>
    public static class DeliveryPresets
    {
        public static readonly DeliveryPreset[] All =
        {
            DeliveryPreset.FlacMkv,
            DeliveryPreset.OpusMkv,
            DeliveryPreset.VorbisMkv,
            DeliveryPreset.WavPackMkv,
            DeliveryPreset.PcmMkv
        };

        public static string DisplayName(DeliveryPreset preset)
        {
            switch (preset)
            {
                case DeliveryPreset.FlacMkv:
                    return "FLAC 7.1 MKV (.mkv — lossless 24-bit)";
                case DeliveryPreset.OpusMkv:
                    return "Opus 7.1 MKV (.mkv — 768 kbps)";
                case DeliveryPreset.VorbisMkv:
                    return "Vorbis 7.1 MKV (.mkv — quality 8)";
                case DeliveryPreset.WavPackMkv:
                    return "WavPack 7.1 MKV (.mkv — lossless)";
                case DeliveryPreset.PcmMkv:
                    return "LPCM 24-bit 7.1 MKV (.mkv — uncompressed)";
                default:
                    return preset.ToString();
            }
        }

        public static string Extension(DeliveryPreset preset)
        {
            return ".mkv";
        }

        public static AudioCodec71 Codec(DeliveryPreset preset)
        {
            switch (preset)
            {
                case DeliveryPreset.OpusMkv:
                    return AudioCodec71.Opus;
                case DeliveryPreset.VorbisMkv:
                    return AudioCodec71.Vorbis;
                case DeliveryPreset.WavPackMkv:
                    return AudioCodec71.WavPack;
                case DeliveryPreset.PcmMkv:
                    return AudioCodec71.Pcm24;
                case DeliveryPreset.FlacMkv:
                default:
                    return AudioCodec71.Flac;
            }
        }

        /// <summary>Maps an audio codec to its canonical delivery preset.</summary>
        public static DeliveryPreset ForCodec(AudioCodec71 codec)
        {
            switch (codec)
            {
                case AudioCodec71.Opus:
                    return DeliveryPreset.OpusMkv;
                case AudioCodec71.Vorbis:
                    return DeliveryPreset.VorbisMkv;
                case AudioCodec71.WavPack:
                    return DeliveryPreset.WavPackMkv;
                case AudioCodec71.Pcm24:
                    return DeliveryPreset.PcmMkv;
                case AudioCodec71.Flac:
                default:
                    return DeliveryPreset.FlacMkv;
            }
        }

        public static bool SupportsVideo(DeliveryPreset preset, VideoCodecFamily family)
        {
            switch (preset)
            {
                case DeliveryPreset.FlacMkv:
                case DeliveryPreset.OpusMkv:
                case DeliveryPreset.VorbisMkv:
                case DeliveryPreset.WavPackMkv:
                case DeliveryPreset.PcmMkv:
                    return family == VideoCodecFamily.H264 ||
                           family == VideoCodecFamily.HEVC ||
                           family == VideoCodecFamily.AV1 ||
                           family == VideoCodecFamily.ProRes;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Returns a user-facing compatibility error, or null when the combination
        /// is valid.
        /// </summary>
        public static string CompatibilityError(
            DeliveryPreset preset, VideoCodecFamily family)
        {
            if (family == VideoCodecFamily.Unknown)
            {
                return "Unsupported video codec for " + DisplayName(preset) + ".";
            }

            if (SupportsVideo(preset, family))
            {
                return null;
            }

            return family + " video is not supported in " + Extension(preset) + ".";
        }

        /// <summary>
        /// Detects the video codec family from a renderer name and FFmpeg codec
        /// string (e.g. "video/h264", "video/quicktime", "video/av1").
        /// </summary>
        public static VideoCodecFamily DetectVideoFamily(
            string rendererName, string videoCodec)
        {
            string value = ((rendererName ?? string.Empty) + " " +
                            (videoCodec ?? string.Empty)).ToLowerInvariant();

            if (value.IndexOf("prores", StringComparison.Ordinal) >= 0 ||
                value.IndexOf("quicktime", StringComparison.Ordinal) >= 0)
            {
                return VideoCodecFamily.ProRes;
            }

            if (value.IndexOf("av1", StringComparison.Ordinal) >= 0)
            {
                return VideoCodecFamily.AV1;
            }

            if (value.IndexOf("h265", StringComparison.Ordinal) >= 0 ||
                value.IndexOf("hevc", StringComparison.Ordinal) >= 0)
            {
                return VideoCodecFamily.HEVC;
            }

            if (value.IndexOf("h264", StringComparison.Ordinal) >= 0 ||
                value.IndexOf("avc", StringComparison.Ordinal) >= 0 ||
                value.IndexOf("xavc", StringComparison.Ordinal) >= 0)
            {
                return VideoCodecFamily.H264;
            }

            return VideoCodecFamily.Unknown;
        }

        public static string FamilyLabel(VideoCodecFamily family)
        {
            switch (family)
            {
                case VideoCodecFamily.H264:
                    return "H.264";
                case VideoCodecFamily.HEVC:
                    return "HEVC";
                case VideoCodecFamily.AV1:
                    return "AV1";
                case VideoCodecFamily.ProRes:
                    return "ProRes";
                default:
                    return "Unknown";
            }
        }
    }
}
