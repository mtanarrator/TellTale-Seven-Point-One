using System;
using System.Runtime.InteropServices;
using System.Text;

namespace VegasSurround71
{
    /// <summary>
    /// Discrete 8-channel 7.1 gain set. Layout is byte-for-byte identical to the
    /// Rust <c>ChannelGains71</c> struct (32 bytes, 4-byte aligned).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ChannelGains71
    {
        public float fl;
        public float fr;
        public float fc;
        public float lfe;
        public float bl;
        public float br;
        public float sl;
        public float sr;

        /// <summary>Managed size of the struct. Must be 32 bytes.</summary>
        public static int MarshalSize
        {
            get { return Marshal.SizeOf(typeof(ChannelGains71)); }
        }

        /// <summary>Returns the gain for a canonical bus index (0..7).</summary>
        public float this[int busIndex]
        {
            get
            {
                switch (busIndex)
                {
                    case 0: return fl;
                    case 1: return fr;
                    case 2: return fc;
                    case 3: return lfe;
                    case 4: return bl;
                    case 5: return br;
                    case 6: return sl;
                    case 7: return sr;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(busIndex), "Bus index must be 0..7.");
                }
            }
        }

        /// <summary>Flattens the gains into canonical bus order (FL..SR).</summary>
        public float[] ToArray()
        {
            return new[] { fl, fr, fc, lfe, bl, br, sl, sr };
        }
    }

    /// <summary>
    /// Spherical source position consumed by the native HOA encoder. Layout is
    /// byte-for-byte identical to the Rust <c>HoaSourcePosition</c> struct
    /// (20 bytes: four <c>float</c> values followed by one <c>uint</c>).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HoaSourcePosition
    {
        /// <summary>Azimuth in degrees: 0 = front, +90 = right, 180 = back.</summary>
        public float azimuth_deg;

        /// <summary>Elevation in degrees: -90 = floor, +90 = overhead.</summary>
        public float elevation_deg;

        /// <summary>Normalized distance in the range 0..1.</summary>
        public float distance;

        /// <summary>Diffusion / source width in the range 0..1.</summary>
        public float spread;

        /// <summary>Ambisonics order: 1, 2, or 3.</summary>
        public uint order;

        /// <summary>Managed size of the struct. Must be 20 bytes.</summary>
        public static int MarshalSize
        {
            get { return Marshal.SizeOf(typeof(HoaSourcePosition)); }
        }
    }

    /// <summary>
    /// Extended HOA render/preview options. Layout matches the Rust
    /// <c>HoaRenderOptions</c> struct (48 bytes, 4-byte aligned).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HoaRenderOptions
    {
        public uint order;
        public float azimuth_deg;
        public float elevation_deg;
        public float distance;
        public float spread;
        public uint distance_model;
        public float air_absorption;
        public uint head_model;
        public float head_width;
        public float room_amount;
        public float room_size;
        public int preview_mode;

        public static int MarshalSize
        {
            get { return Marshal.SizeOf(typeof(HoaRenderOptions)); }
        }
    }

    /// <summary>
    /// Royalty-free delivery audio codecs. Discriminants are part of the native
    /// ABI; old patent-encumbered slots (0, 1) are deliberately left unused.
    /// </summary>
    public enum AudioCodec71
    {
        /// <summary>Lossless 7.1 FLAC.</summary>
        Flac = 2,

        /// <summary>Uncompressed 24-bit 7.1 PCM.</summary>
        Pcm24 = 3,

        /// <summary>Lossy 7.1 Opus at 768 kbps.</summary>
        Opus = 4,

        /// <summary>Lossy 7.1 Vorbis.</summary>
        Vorbis = 5,

        /// <summary>Lossless 7.1 WavPack.</summary>
        WavPack = 6
    }

    /// <summary>
    /// UTF-8 marshaling helpers for native path arguments. Paths are encoded as
    /// NUL-terminated UTF-8 byte arrays so Unicode characters never depend on the
    /// process ANSI code page.
    /// </summary>
    public static class Utf8Path
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>
        /// Encodes <paramref name="path"/> as UTF-8 followed by a single NUL byte.
        /// Returns <c>null</c> when <paramref name="path"/> is <c>null</c> so the
        /// native side observes a NULL pointer and returns -1.
        /// </summary>
        public static byte[] ToNullTerminatedUtf8(string path)
        {
            if (path == null)
            {
                return null;
            }

            if (path.IndexOf('\0') >= 0)
            {
                throw new ArgumentException("Paths must not contain NUL characters.", nameof(path));
            }

            byte[] payload = StrictUtf8.GetBytes(path);
            byte[] buffer = new byte[payload.Length + 1];
            Buffer.BlockCopy(payload, 0, buffer, 0, payload.Length);
            return buffer;
        }

        /// <summary>Decodes a NUL-terminated UTF-8 buffer (verification helper).</summary>
        public static string FromNullTerminatedUtf8(byte[] buffer)
        {
            if (buffer == null)
            {
                return null;
            }

            int length = Array.IndexOf(buffer, (byte)0);
            if (length < 0)
            {
                length = buffer.Length;
            }

            return StrictUtf8.GetString(buffer, 0, length);
        }
    }

    /// <summary>
    /// P/Invoke bridge to <c>telltale_7point1_core.dll</c>.
    /// </summary>
    public static class NativeBridge
    {
        public const string DllFileName = "telltale_7point1_core.dll";

        // Status codes mirrored from rust_core/src/muxer.rs.
        public const int MUX_OK = 0;
        public const int MUX_ERR_INVALID_ARGUMENT = -1;
        public const int MUX_ERR_MISSING_INPUT = -2;
        public const int MUX_ERR_FFMPEG = -3;
        public const int MUX_ERR_INVALID_CODEC = -4;
        public const int MUX_ERR_PANIC = -99;

        // Status codes mirrored from rust_core/src/hoa.rs.
        public const int HOA_OK = 0;
        public const int HOA_ERR_NULL_POINTER = -1;
        public const int HOA_ERR_INVALID_ORDER = -2;
        public const int HOA_ERR_BUFFER_TOO_SMALL = -3;
        public const int HOA_ERR_PANIC = -99;

        public const int HoaMinOrder = 1;
        public const int HoaMaxOrder = 3;

        // Status codes mirrored from rust_core/src/hoa_render.rs.
        public const int HOA_RENDER_OK = 0;
        public const int HOA_RENDER_ERR_INVALID_ARGUMENT = -1;
        public const int HOA_RENDER_ERR_MISSING_INPUT = -2;
        public const int HOA_RENDER_ERR_FFMPEG = -3;
        public const int HOA_RENDER_ERR_UNSUPPORTED_FORMAT = -4;
        public const int HOA_RENDER_ERR_IO = -5;
        public const int HOA_RENDER_ERR_INVALID_ORDER = -6;
        public const int HOA_RENDER_ERR_PANIC = -99;

        public const int HoaPreviewBinaural = 0;
        public const int HoaPreviewStereo = 1;
        public const int HoaPreviewBypass = 2;

        public const uint HoaDistanceInverse = 0;
        public const uint HoaDistanceInverseSquare = 1;
        public const uint HoaDistanceLinear = 2;
        public const uint HoaDistanceNone = 3;

        public const uint HoaHeadBoth = 0;
        public const uint HoaHeadItdOnly = 1;
        public const uint HoaHeadIldOnly = 2;
        public const uint HoaHeadOff = 3;

        /// <summary>
        /// Calculates 7.1 VBAP gains. <paramref name="lfeSend"/> and
        /// <paramref name="spread"/> are in the range 0..1.
        /// </summary>
        [DllImport(DllFileName, CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi, ExactSpelling = true)]
        public static extern ChannelGains71 calculate_71_gains(
            float x, float y, float lfeSend, float spread);

        /// <summary>
        /// Muxes a video file with the discrete 8-channel <c>.w64</c> render.
        /// Path arguments must be NUL-terminated UTF-8 byte arrays produced by
        /// <see cref="Utf8Path.ToNullTerminatedUtf8"/>.
        /// </summary>
        [DllImport(DllFileName, CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi, ExactSpelling = true)]
        public static extern int mux_71_master(
            byte[] videoPathUtf8,
            byte[] audioW64PathUtf8,
            byte[] outputPathUtf8,
            int codecType,
            [MarshalAs(UnmanagedType.U1)] bool applyLfeLowpass);

        /// <summary>
        /// Returns the ACN channel count for an Ambisonics order (4, 9, or 16),
        /// or 0 when the order is unsupported.
        /// </summary>
        [DllImport(DllFileName, CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi, ExactSpelling = true)]
        public static extern int hoa_channel_count(uint order);

        /// <summary>
        /// Encodes one spherical source position into ACN/SN3D Ambisonics
        /// weights. <paramref name="weights"/> must hold
        /// <c>(order + 1)^2</c> floats.
        /// </summary>
        [DllImport(DllFileName, CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi, ExactSpelling = true)]
        public static extern int calculate_hoa_weights(
            ref HoaSourcePosition position,
            [Out] float[] weights,
            uint maxChannels);

        /// <summary>
        /// Renders an AmbiX HOA master from a mono/stereo/multichannel PCM
        /// WAV/W64 stem. The output extension selects WAV, W64, or FLAC.
        /// </summary>
        [DllImport(DllFileName, CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi, ExactSpelling = true)]
        public static extern int render_hoa_master(
            byte[] inputPathUtf8,
            byte[] outputPathUtf8,
            uint order,
            float azimuthDeg,
            float elevationDeg,
            float distance,
            float spread);

        /// <summary>
        /// Renders a 2-channel stereo or binaural preview WAV.
        /// </summary>
        [DllImport(DllFileName, CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi, ExactSpelling = true)]
        public static extern int render_hoa_preview(
            byte[] inputPathUtf8,
            byte[] outputPathUtf8,
            uint order,
            float azimuthDeg,
            float elevationDeg,
            float distance,
            float spread,
            int previewMode);

        /// <summary>
        /// Extended AmbiX master render with distance/air/monitoring options.
        /// </summary>
        [DllImport(DllFileName, CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi, ExactSpelling = true)]
        public static extern int render_hoa_master_ex(
            ref HoaRenderOptions options,
            byte[] inputPathUtf8,
            byte[] outputPathUtf8);

        /// <summary>
        /// Extended HOA preview render with head-model/room options.
        /// </summary>
        [DllImport(DllFileName, CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi, ExactSpelling = true)]
        public static extern int render_hoa_preview_ex(
            ref HoaRenderOptions options,
            byte[] inputPathUtf8,
            byte[] outputPathUtf8);

        /// <summary>String-friendly extended master render wrapper.</summary>
        public static int RenderHoaMasterEx(
            HoaRenderOptions options, string inputPath, string outputPath)
        {
            return render_hoa_master_ex(
                ref options,
                Utf8Path.ToNullTerminatedUtf8(inputPath),
                Utf8Path.ToNullTerminatedUtf8(outputPath));
        }

        /// <summary>String-friendly extended preview render wrapper.</summary>
        public static int RenderHoaPreviewEx(
            HoaRenderOptions options, string inputPath, string outputPath)
        {
            return render_hoa_preview_ex(
                ref options,
                Utf8Path.ToNullTerminatedUtf8(inputPath),
                Utf8Path.ToNullTerminatedUtf8(outputPath));
        }

        /// <summary>String-friendly AmbiX master render wrapper.</summary>
        public static int RenderHoaMaster(
            string inputPath,
            string outputPath,
            int order,
            double azimuthDegrees,
            double elevationDegrees,
            double distance,
            double spread)
        {
            return render_hoa_master(
                Utf8Path.ToNullTerminatedUtf8(inputPath),
                Utf8Path.ToNullTerminatedUtf8(outputPath),
                (uint)order,
                (float)azimuthDegrees,
                (float)elevationDegrees,
                (float)distance,
                (float)spread);
        }

        /// <summary>String-friendly stereo/binaural preview wrapper.</summary>
        public static int RenderHoaPreview(
            string inputPath,
            string outputPath,
            int order,
            double azimuthDegrees,
            double elevationDegrees,
            double distance,
            double spread,
            int previewMode)
        {
            return render_hoa_preview(
                Utf8Path.ToNullTerminatedUtf8(inputPath),
                Utf8Path.ToNullTerminatedUtf8(outputPath),
                (uint)order,
                (float)azimuthDegrees,
                (float)elevationDegrees,
                (float)distance,
                (float)spread,
                previewMode);
        }

        /// <summary>Human-readable HOA render status.</summary>
        public static string DescribeHoaRenderStatus(int status)
        {
            switch (status)
            {
                case HOA_RENDER_OK:
                    return "HOA render complete";
                case HOA_RENDER_ERR_INVALID_ARGUMENT:
                    return "Invalid HOA render argument (null/empty/invalid path)";
                case HOA_RENDER_ERR_MISSING_INPUT:
                    return "The HOA input stem is missing or unreadable";
                case HOA_RENDER_ERR_FFMPEG:
                    return "FFmpeg failed or was not found on PATH";
                case HOA_RENDER_ERR_UNSUPPORTED_FORMAT:
                    return "Unsupported HOA output format (use WAV, W64, or FLAC)";
                case HOA_RENDER_ERR_IO:
                    return "HOA audio read/write/rename failed";
                case HOA_RENDER_ERR_INVALID_ORDER:
                    return "Unsupported HOA order (expected 1, 2, or 3)";
                case HOA_RENDER_ERR_PANIC:
                    return "Internal native panic was contained at the FFI boundary";
                default:
                    return "Unknown HOA render status " + status.ToString();
            }
        }

        /// <summary>
        /// Convenience wrapper that allocates the correct weight buffer and
        /// reports a readable error instead of throwing when the HOA export is
        /// unavailable in an older native DLL.
        /// </summary>
        public static bool TryCalculateHoaWeights(
            HoaSourcePosition position,
            out float[] weights,
            out string error)
        {
            int count = HoaChannelCount((int)position.order);
            if (count <= 0)
            {
                weights = new float[0];
                error = "Unsupported HOA order " + position.order + ".";
                return false;
            }

            var buffer = new float[count];
            try
            {
                int status = calculate_hoa_weights(ref position, buffer, (uint)count);
                if (status != HOA_OK)
                {
                    weights = new float[0];
                    error = DescribeHoaStatus(status);
                    return false;
                }

                weights = buffer;
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                weights = new float[0];
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Safe channel-count probe: falls back to the order formula when the
        /// native HOA export is not present.
        /// </summary>
        public static int HoaChannelCount(int order)
        {
            try
            {
                return hoa_channel_count((uint)order);
            }
            catch (Exception)
            {
                return order >= HoaMinOrder && order <= HoaMaxOrder
                    ? (order + 1) * (order + 1)
                    : 0;
            }
        }

        /// <summary>Human-readable description of a <c>HOA_ERR_*</c> status code.</summary>
        public static string DescribeHoaStatus(int status)
        {
            switch (status)
            {
                case HOA_OK:
                    return "Success";
                case HOA_ERR_NULL_POINTER:
                    return "Null pointer passed to the HOA encoder";
                case HOA_ERR_INVALID_ORDER:
                    return "Unsupported HOA order (expected 1, 2, or 3)";
                case HOA_ERR_BUFFER_TOO_SMALL:
                    return "HOA weight buffer is too small";
                case HOA_ERR_PANIC:
                    return "Internal native panic was contained at the FFI boundary";
                default:
                    return "Unknown HOA status code " + status.ToString();
            }
        }

        /// <summary>
        /// Convenience wrapper that performs UTF-8 path marshaling and uses the
        /// strongly typed codec enum.
        /// </summary>
        public static int Mux71Master(
            string videoPath,
            string audioW64Path,
            string outputPath,
            AudioCodec71 codec,
            bool applyLfeLowpass)
        {
            return mux_71_master(
                Utf8Path.ToNullTerminatedUtf8(videoPath),
                Utf8Path.ToNullTerminatedUtf8(audioW64Path),
                Utf8Path.ToNullTerminatedUtf8(outputPath),
                (int)codec,
                applyLfeLowpass);
        }

        /// <summary>Returns <c>true</c> for the supported royalty-free codecs.</summary>
        public static bool IsCodecSupported(AudioCodec71 codec)
        {
            return codec == AudioCodec71.Flac ||
                   codec == AudioCodec71.Pcm24 ||
                   codec == AudioCodec71.Opus ||
                   codec == AudioCodec71.Vorbis ||
                   codec == AudioCodec71.WavPack;
        }

        /// <summary>Human-readable description of a <c>MUX_ERR_*</c> status code.</summary>
        public static string DescribeMuxStatus(int status)
        {
            switch (status)
            {
                case MUX_OK:
                    return "Success";
                case MUX_ERR_INVALID_ARGUMENT:
                    return "Invalid argument (null pointer, empty path, or invalid UTF-8)";
                case MUX_ERR_MISSING_INPUT:
                    return "Input video or .w64 audio file is missing";
                case MUX_ERR_FFMPEG:
                    return "FFmpeg failed or was not found. Install FFmpeg separately and " +
                           "ensure ffmpeg.exe is available on PATH, then retry Pass 3.";
                case MUX_ERR_INVALID_CODEC:
                    return "Invalid or unsupported codec";
                case MUX_ERR_PANIC:
                    return "Internal native panic was contained at the FFI boundary";
                default:
                    return "Unknown mux status code " + status.ToString();
            }
        }

        /// <summary>
        /// Probes the native DLL so the UI can report a clear deployment error
        /// instead of failing on the first real render.
        /// </summary>
        public static bool IsNativeAvailable(out string error)
        {
            try
            {
                calculate_71_gains(0f, 0f, 0f, 0f);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
