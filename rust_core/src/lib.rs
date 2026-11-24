//! telltale_7point1_core - Native C-ABI DLL for VEGAS Pro 7.1 Surround Extension
//!
//! Provides high-performance 2D VBAP spatialization, Linkwitz-Riley LFE filtering,
//! multichannel channel-mask mapping, FFmpeg muxing orchestration, and the
//! ACN/SN3D Higher-Order Ambisonics (HOA) weight encoder plus the AmbiX
//! master/preview PCM render engine used by the HOA / 3D Spatial workspace.
//!
//! # Panic safety
//! Every `#[no_mangle] pub extern "C"` export wraps its body in
//! [`std::panic::catch_unwind`]. A panic therefore never unwinds across the
//! C-ABI boundary into the VEGAS Pro C# host process; it is converted into a
//! deterministic sentinel value instead.

pub mod dsp;
pub mod hoa;
pub mod hoa_render;
pub mod muxer;
pub mod vbap;

pub use hoa::HoaSourcePosition;
pub use muxer::AudioCodec71;
pub use vbap::ChannelGains71;

use std::ffi::CStr;
use std::os::raw::c_char;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::PathBuf;

/// Runs an FFI closure, converting any caught panic into [`muxer::MUX_ERR_PANIC`].
fn run_ffi_status<F>(body: F) -> i32
where
    F: FnOnce() -> i32,
{
    match catch_unwind(AssertUnwindSafe(body)) {
        Ok(status) => status,
        Err(_) => muxer::MUX_ERR_PANIC,
    }
}

/// Converts a C string pointer into an owned [`PathBuf`].
///
/// # Safety
/// When `ptr` is non-null it must point to a valid NUL-terminated C string.
///
/// Returns [`muxer::MUX_ERR_INVALID_ARGUMENT`] for null pointers, empty strings,
/// or sequences that are not valid UTF-8.
unsafe fn path_from_c(ptr: *const c_char) -> Result<PathBuf, i32> {
    if ptr.is_null() {
        return Err(muxer::MUX_ERR_INVALID_ARGUMENT);
    }

    // SAFETY: caller guarantees a valid NUL-terminated string.
    let c_str = unsafe { CStr::from_ptr(ptr) };
    let as_str = c_str
        .to_str()
        .map_err(|_| muxer::MUX_ERR_INVALID_ARGUMENT)?;

    if as_str.is_empty() {
        return Err(muxer::MUX_ERR_INVALID_ARGUMENT);
    }

    Ok(PathBuf::from(as_str))
}

/// Calculates discrete 7.1 channel gains via 2D Vector Base Amplitude Panning (VBAP).
///
/// # C-ABI Signature:
/// ```c
/// typedef struct {
///     float fl;
///     float fr;
///     float fc;
///     float lfe;
///     float bl;
///     float br;
///     float sl;
///     float sr;
/// } ChannelGains71;
///
/// ChannelGains71 calculate_71_gains(float x, float y, float lfe_send, float spread);
/// ```
///
/// On an internal panic an all-zero [`ChannelGains71`] is returned (silence),
/// which is the only safe failure mode for a real-time audio gain callback.
#[no_mangle]
pub extern "C" fn calculate_71_gains(x: f32, y: f32, lfe_send: f32, spread: f32) -> ChannelGains71 {
    // A caught panic yields an all-zero (silent) gain set.
    catch_unwind(AssertUnwindSafe(|| {
        vbap::calculate_gains(x, y, lfe_send, spread)
    }))
    .unwrap_or_default()
}

/// Muxes a rendered video stream with a discrete 8-channel Wave64 render into a
/// 7.1 delivery container via FFmpeg.
///
/// # C-ABI Signature:
/// ```c
/// int mux_71_master(
///     const char* video_path,
///     const char* audio_w64_path,
///     const char* output_path,
///     int codec_type,          // 2 = FLAC, 4 = OPUS (royalty-free)
///     bool apply_lfe_lowpass   // C `_Bool` / 1-byte boolean
/// );
/// ```
///
/// # Status codes
/// | Code | Meaning |
/// |-----:|---------|
/// | `0`   | Success |
/// | `-1`  | Null pointer, empty path, or invalid UTF-8 |
/// | `-2`  | Input video or `.w64` file does not exist |
/// | `-3`  | FFmpeg failed to spawn, exited non-zero, or atomic rename failed |
/// | `-4`  | Invalid codec value |
/// | `-99` | Internal panic caught at the FFI boundary |
///
/// # Safety
/// All three pointers must be either null or valid NUL-terminated C strings.
/// Null pointers are rejected with `-1` rather than dereferenced.
///
/// # C# marshalling note
/// `apply_lfe_lowpass` is a 1-byte C `_Bool`. For strict byte-for-byte ABI
/// matching declare it as `byte` or annotate the P/Invoke parameter with
/// `[MarshalAs(UnmanagedType.U1)]`.
#[no_mangle]
#[allow(clippy::not_unsafe_ptr_arg_deref)]
pub extern "C" fn mux_71_master(
    video_path: *const c_char,
    audio_w64_path: *const c_char,
    output_path: *const c_char,
    codec_type: i32,
    apply_lfe_lowpass: bool,
) -> i32 {
    run_ffi_status(|| {
        let video = match unsafe { path_from_c(video_path) } {
            Ok(path) => path,
            Err(code) => return code,
        };
        let audio = match unsafe { path_from_c(audio_w64_path) } {
            Ok(path) => path,
            Err(code) => return code,
        };
        let output = match unsafe { path_from_c(output_path) } {
            Ok(path) => path,
            Err(code) => return code,
        };

        muxer::execute_mux_job(&video, &audio, &output, codec_type, apply_lfe_lowpass)
    })
}

#[cfg(test)]
mod ffi_tests {
    use super::*;
    use std::ffi::CString;
    use std::fs;
    use std::path::Path;
    use std::ptr;

    /// Throwaway on-disk fixture used by the FFI error-path tests.
    struct FfiFixture {
        dir: PathBuf,
        video: PathBuf,
        audio: PathBuf,
        output: PathBuf,
    }

    impl FfiFixture {
        fn new(tag: &str) -> Self {
            let dir = std::env::temp_dir().join(format!(
                "vegas_surround71_ffi_{}_{}",
                std::process::id(),
                tag
            ));
            fs::create_dir_all(&dir).expect("create FFI fixture directory");

            let video = dir.join("source.mp4");
            let audio = dir.join("render.w64");
            let output = dir.join("master_71.mp4");
            fs::write(&video, b"stub video").expect("write stub video");
            fs::write(&audio, b"stub w64").expect("write stub w64");

            Self {
                dir,
                video,
                audio,
                output,
            }
        }

        fn c(&self, path: &Path) -> CString {
            CString::new(path.to_str().expect("fixture path is UTF-8")).expect("no interior NUL")
        }
    }

    impl Drop for FfiFixture {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.dir);
        }
    }

    #[test]
    fn test_c_abi_signature_and_struct_size() {
        assert_eq!(
            std::mem::size_of::<ChannelGains71>(),
            32,
            "ChannelGains71 must be exactly 32 bytes (8 x f32)"
        );
        assert_eq!(
            std::mem::align_of::<ChannelGains71>(),
            4,
            "ChannelGains71 alignment must be 4 bytes"
        );
        assert_eq!(std::mem::size_of::<AudioCodec71>(), 4);

        let gains = calculate_71_gains(0.0, 1.0, 0.5, 0.0);
        assert!((gains.fc - 1.0).abs() < 1e-4);
        assert!((gains.lfe - 0.5).abs() < 1e-4);
    }

    #[test]
    fn test_mux_71_master_rejects_null_pointers() {
        let fx = FfiFixture::new("null");
        let video = fx.c(&fx.video);
        let audio = fx.c(&fx.audio);
        let output = fx.c(&fx.output);

        assert_eq!(
            mux_71_master(ptr::null(), audio.as_ptr(), output.as_ptr(), 0, false),
            -1
        );
        assert_eq!(
            mux_71_master(video.as_ptr(), ptr::null(), output.as_ptr(), 0, false),
            -1
        );
        assert_eq!(
            mux_71_master(video.as_ptr(), audio.as_ptr(), ptr::null(), 0, false),
            -1
        );
        assert_eq!(
            mux_71_master(ptr::null(), ptr::null(), ptr::null(), 0, false),
            -1
        );
    }

    #[test]
    fn test_mux_71_master_rejects_invalid_utf8_and_empty_paths() {
        let fx = FfiFixture::new("utf8");
        let video = fx.c(&fx.video);
        let audio = fx.c(&fx.audio);
        let output = fx.c(&fx.output);

        let invalid = CString::new(vec![0xFF, 0xFE, b'.', b'w', b'6', b'4']).unwrap();
        assert_eq!(
            mux_71_master(video.as_ptr(), invalid.as_ptr(), output.as_ptr(), 0, false),
            -1
        );

        let empty = CString::new("").unwrap();
        assert_eq!(
            mux_71_master(empty.as_ptr(), audio.as_ptr(), output.as_ptr(), 0, false),
            -1
        );
    }

    #[test]
    fn test_mux_71_master_missing_input_files_returns_minus_two() {
        let fx = FfiFixture::new("missing");
        let video = fx.c(&fx.video);
        let audio = fx.c(&fx.audio);
        let output = fx.c(&fx.output);

        let missing_audio = fx.c(&fx.dir.join("absent.w64"));
        assert_eq!(
            mux_71_master(
                video.as_ptr(),
                missing_audio.as_ptr(),
                output.as_ptr(),
                2,
                false
            ),
            -2
        );

        let missing_video = fx.c(&fx.dir.join("absent.mp4"));
        assert_eq!(
            mux_71_master(
                missing_video.as_ptr(),
                audio.as_ptr(),
                output.as_ptr(),
                2,
                false
            ),
            -2
        );
    }

    #[test]
    fn test_mux_71_master_rejects_invalid_codec_values() {
        let fx = FfiFixture::new("codec");
        let video = fx.c(&fx.video);
        let audio = fx.c(&fx.audio);
        let output = fx.c(&fx.output);

        assert_eq!(
            mux_71_master(video.as_ptr(), audio.as_ptr(), output.as_ptr(), 42, false),
            -4
        );
        assert_eq!(
            mux_71_master(video.as_ptr(), audio.as_ptr(), output.as_ptr(), -7, true),
            -4
        );
    }

    #[test]
    fn test_panic_guard_returns_minus_99() {
        let status = run_ffi_status(|| panic!("simulated internal DSP panic"));
        assert_eq!(status, -99);
    }
}
