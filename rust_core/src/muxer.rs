//! FFmpeg Muxer Module: multichannel Wave64 + video muxing pipeline.
//!
//! The orchestrator builds a deterministic FFmpeg command line that:
//! 1. takes the rendered video stream as input `0`,
//! 2. takes the discrete 8-channel VEGAS `.w64` render as input `1`,
//! 3. remaps and (optionally) LFE-filters the audio via `-filter_complex`,
//! 4. copies the video stream without re-encoding (`-c:v copy`),
//! 5. encodes the 7.1 audio with the requested codec and muxes the result.
//!
//! # Delivery safety
//! - FFmpeg is told to write to a sibling temporary file (`<stem>.tmp.<ext>`).
//!   The temporary file is atomically renamed onto the requested output path
//!   only after FFmpeg exits with status `0`; on any failure it is deleted so a
//!   failed render can never leave partial media behind.
//! - Delivery codecs are limited to royalty-free FLAC and Opus. Patent-
//!   encumbered AAC/E-AC-3 paths are deliberately absent from this build.
//!
//! All public entry points are panic-safe: internal panics are converted into
//! the deterministic status code [`MUX_ERR_PANIC`] so nothing can unwind across
//! the C-ABI boundary into the VEGAS Pro host process.

use std::ffi::OsString;
use std::fs;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

use crate::dsp::{build_71_filter_complex, DEFAULT_LFE_CUTOFF_HZ};

/// Executable invoked for the mux job (resolved through `PATH` or a packaged
/// sidecar placed next to the host application).
pub const FFMPEG_BINARY: &str = "ffmpeg";

/// `CREATE_NO_WINDOW` - prevents a console window from flashing when FFmpeg is
/// spawned by the VEGAS Pro GUI process.
#[cfg(windows)]
pub const CREATE_NO_WINDOW: u32 = 0x0800_0000;

// Deterministic status codes shared with the C# host.
/// Muxing completed successfully.
pub const MUX_OK: i32 = 0;
/// Null pointer, empty path, or path that is not valid UTF-8.
pub const MUX_ERR_INVALID_ARGUMENT: i32 = -1;
/// The input video or `.w64` audio file does not exist on disk.
pub const MUX_ERR_MISSING_INPUT: i32 = -2;
/// FFmpeg could not be spawned, exited non-zero, or the atomic rename failed.
pub const MUX_ERR_FFMPEG: i32 = -3;
/// Invalid codec discriminant.
pub const MUX_ERR_INVALID_CODEC: i32 = -4;
/// An internal panic was caught at the FFI boundary.
pub const MUX_ERR_PANIC: i32 = -99;

/// Royalty-free delivery audio codecs for the 7.1 master.
///
/// Discriminants are part of the C-ABI contract with the C# host. Old
/// patent-encumbered slots (0, 1, 3) are intentionally left unused; a stale
/// native DLL will reject the new Opus value instead of silently producing a
/// licensed codec.
#[repr(C)]
#[derive(Debug, Copy, Clone, PartialEq, Eq)]
pub enum AudioCodec71 {
    /// Lossless 7.1 FLAC (`-c:a flac`).
    Flac = 2,
    /// Lossy 7.1 Opus (`-c:a libopus -mapping_family 1 -b:a 768k`).
    Opus = 4,
    /// Uncompressed 24-bit 7.1 PCM (`-c:a pcm_s24le`).
    Pcm24 = 3,
    /// Lossy 7.1 Vorbis (`-c:a libvorbis -q:a 8`).
    Vorbis = 5,
    /// Lossless 7.1 WavPack (`-c:a wavpack`).
    WavPack = 6,
}

impl AudioCodec71 {
    /// Returns `true` when this codec can be muxed by the FFmpeg pipeline.
    pub fn is_supported(self) -> bool {
        matches!(
            self,
            Self::Flac | Self::Opus | Self::Pcm24 | Self::Vorbis | Self::WavPack
        )
    }

    /// All remaining delivery codecs carry discrete 7.1.
    pub fn is_discrete_71(self) -> bool {
        true
    }
}

impl TryFrom<i32> for AudioCodec71 {
    type Error = i32;

    fn try_from(value: i32) -> Result<Self, Self::Error> {
        match value {
            2 => Ok(Self::Flac),
            3 => Ok(Self::Pcm24),
            4 => Ok(Self::Opus),
            5 => Ok(Self::Vorbis),
            6 => Ok(Self::WavPack),
            _ => Err(MUX_ERR_INVALID_CODEC),
        }
    }
}

/// Returns the sibling temporary output path used for atomic delivery.
///
/// For `master.mkv` this yields `master.tmp.mkv`; the temporary file always
/// lives in the same directory as the final output, guaranteeing that the final
/// [`fs::rename`] stays on one volume and is atomic.
pub fn temp_output_path(output_path: &Path) -> PathBuf {
    let mut name = output_path
        .file_stem()
        .map(|stem| stem.to_os_string())
        .unwrap_or_else(|| OsString::from("output"));
    name.push(".tmp");
    if let Some(extension) = output_path.extension() {
        name.push(".");
        name.push(extension);
    }
    output_path.with_file_name(name)
}

/// Returns `true` when `path` carries the `.mp4` extension (ASCII
/// case-insensitive).
#[cfg(test)]
fn has_mp4_extension(path: &Path) -> bool {
    path.extension()
        .and_then(|ext| ext.to_str())
        .map(|ext| ext.eq_ignore_ascii_case("mp4"))
        .unwrap_or(false)
}

/// Appends the codec-specific encoder flags for `codec` to `args`.
fn push_codec_args(args: &mut Vec<String>, codec: AudioCodec71, _output_path: &Path) {
    match codec {
        AudioCodec71::Flac => {
            args.push("-c:a".to_string());
            args.push("flac".to_string());
        }
        AudioCodec71::Opus => {
            args.push("-c:a".to_string());
            args.push("libopus".to_string());
            // Surround/multichannel Opus uses channel-mapping family 1.
            args.push("-mapping_family".to_string());
            args.push("1".to_string());
            args.push("-b:a".to_string());
            args.push("768k".to_string());
        }
        AudioCodec71::Pcm24 => {
            args.push("-c:a".to_string());
            args.push("pcm_s24le".to_string());
            args.push("-channel_layout".to_string());
            args.push("7.1".to_string());
        }
        AudioCodec71::Vorbis => {
            args.push("-c:a".to_string());
            args.push("libvorbis".to_string());
            args.push("-q:a".to_string());
            args.push("8".to_string());
        }
        AudioCodec71::WavPack => {
            args.push("-c:a".to_string());
            args.push("wavpack".to_string());
        }
    }
}

/// Builds the complete FFmpeg argument vector (excluding the `ffmpeg`
/// executable itself).
///
/// The final argument is the **temporary** sibling output path
/// ([`temp_output_path`]); [`execute_mux_job`] atomically renames it onto the
/// requested output path after a successful encode.
///
/// # Errors
/// - [`MUX_ERR_INVALID_CODEC`] for any unsupported codec value. Only FLAC and
///   Opus are accepted.
/// - [`MUX_ERR_MISSING_INPUT`] if `video_path` or `audio_w64_path` does not
///   exist.
/// - [`MUX_ERR_INVALID_ARGUMENT`] if any path cannot be represented as UTF-8
///   (FFmpeg command-line arguments on Windows are UTF-8/ANSI, and the FFI
///   entrypoint already guarantees valid UTF-8).
pub fn build_ffmpeg_args(
    video_path: &Path,
    audio_w64_path: &Path,
    output_path: &Path,
    codec: AudioCodec71,
    apply_lfe_lowpass: bool,
) -> Result<Vec<String>, i32> {
    // Only the royalty-free Flac and Opus codecs are accepted.
    if !codec.is_supported() {
        return Err(MUX_ERR_INVALID_CODEC);
    }

    if !video_path.exists() || !audio_w64_path.exists() {
        return Err(MUX_ERR_MISSING_INPUT);
    }

    let video = video_path.to_str().ok_or(MUX_ERR_INVALID_ARGUMENT)?;
    let audio = audio_w64_path.to_str().ok_or(MUX_ERR_INVALID_ARGUMENT)?;
    let temp_output = temp_output_path(output_path);
    let temp_output = temp_output.to_str().ok_or(MUX_ERR_INVALID_ARGUMENT)?;

    let filter = build_71_filter_complex(apply_lfe_lowpass, DEFAULT_LFE_CUTOFF_HZ);

    let mut args: Vec<String> = Vec::with_capacity(20);
    args.push("-y".to_string());

    args.push("-i".to_string());
    args.push(video.to_string());

    args.push("-i".to_string());
    args.push(audio.to_string());

    args.push("-filter_complex".to_string());
    args.push(filter);

    // Video passthrough + explicit graph output mapping.
    args.push("-map".to_string());
    args.push("0:v:0".to_string());
    args.push("-map".to_string());
    args.push("[aout]".to_string());

    args.push("-c:v".to_string());
    args.push("copy".to_string());

    push_codec_args(&mut args, codec, output_path);

    args.push(temp_output.to_string());

    Ok(args)
}

/// Executes the FFmpeg mux job and returns a deterministic status code.
///
/// `codec_type` is the raw C-ABI discriminant; invalid values yield
/// [`MUX_ERR_INVALID_CODEC`] without spawning a process.
///
/// The encode targets [`temp_output_path`]; on exit code `0` the file is
/// atomically renamed onto `output_path`. On any failure the temporary file is
/// removed and nothing is published.
///
/// # Status codes
/// | Code | Meaning |
/// |-----:|---------|
/// | `0`   | Success |
/// | `-1`  | Null pointer / invalid UTF-8 argument |
/// | `-2`  | Input video or `.w64` file missing |
/// | `-3`  | FFmpeg failed to spawn/exited non-zero, or rename failed |
/// | `-4`  | Invalid or unsupported codec value |
/// | `-99` | Caught internal panic |
pub fn execute_mux_job(
    video_path: &Path,
    audio_w64_path: &Path,
    output_path: &Path,
    codec_type: i32,
    apply_lfe_lowpass: bool,
) -> i32 {
    match catch_unwind(AssertUnwindSafe(|| {
        execute_mux_job_inner(
            video_path,
            audio_w64_path,
            output_path,
            codec_type,
            apply_lfe_lowpass,
        )
    })) {
        Ok(status) => status,
        Err(_) => MUX_ERR_PANIC,
    }
}

fn execute_mux_job_inner(
    video_path: &Path,
    audio_w64_path: &Path,
    output_path: &Path,
    codec_type: i32,
    apply_lfe_lowpass: bool,
) -> i32 {
    let codec = match AudioCodec71::try_from(codec_type) {
        Ok(codec) => codec,
        Err(code) => return code,
    };

    let args = match build_ffmpeg_args(
        video_path,
        audio_w64_path,
        output_path,
        codec,
        apply_lfe_lowpass,
    ) {
        Ok(args) => args,
        Err(code) => return code,
    };

    let temp_output = temp_output_path(output_path);

    let mut command = Command::new(FFMPEG_BINARY);
    command
        .args(&args)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());

    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(CREATE_NO_WINDOW);
    }

    match command.status() {
        Ok(status) if status.success() => match fs::rename(&temp_output, output_path) {
            Ok(()) => MUX_OK,
            Err(_) => {
                // Never leave a completed-but-unpublished temp file behind.
                let _ = fs::remove_file(&temp_output);
                MUX_ERR_FFMPEG
            }
        },
        Ok(_) => {
            let _ = fs::remove_file(&temp_output);
            MUX_ERR_FFMPEG
        }
        Err(_) => {
            let _ = fs::remove_file(&temp_output);
            MUX_ERR_FFMPEG
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU32, Ordering};

    static FIXTURE_SEQ: AtomicU32 = AtomicU32::new(0);

    /// Creates a throwaway directory containing a stub video and `.w64` file.
    struct Fixture {
        dir: PathBuf,
        video: PathBuf,
        audio: PathBuf,
    }

    impl Fixture {
        fn new(tag: &str) -> Self {
            let seq = FIXTURE_SEQ.fetch_add(1, Ordering::Relaxed);
            let dir = std::env::temp_dir().join(format!(
                "vegas_surround71_muxer_{}_{}_{}",
                std::process::id(),
                seq,
                tag
            ));
            fs::create_dir_all(&dir).expect("create fixture directory");

            let video = dir.join("source_video.mp4");
            let audio = dir.join("vegas_render.w64");
            fs::write(&video, b"stub video payload").expect("write stub video");
            fs::write(&audio, b"stub wave64 payload").expect("write stub wave64");

            Self { dir, video, audio }
        }

        fn output(&self, name: &str) -> PathBuf {
            self.dir.join(name)
        }
    }

    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.dir);
        }
    }

    fn strings(items: &[&str]) -> Vec<String> {
        items.iter().map(|item| item.to_string()).collect()
    }

    /// Opus is royalty-free and supports discrete 7.1.
    #[test]
    fn test_opus_supports_7_1() {
        let fx = Fixture::new("opus_71");
        let output = fx.output("master_71.mkv");

        let args = build_ffmpeg_args(
            &fx.video, &fx.audio, &output, AudioCodec71::Opus, false)
            .expect("Opus args should build");
        assert!(args.windows(2).any(|w| w[0] == "-c:a" && w[1] == "libopus"));
        assert!(args.windows(2).any(|w| w[0] == "-mapping_family" && w[1] == "1"));
        assert!(args.windows(2).any(|w| w[0] == "-b:a" && w[1] == "768k"));

        assert_eq!(AudioCodec71::Opus as i32, 4);
        assert!(AudioCodec71::Opus.is_supported());
        assert!(AudioCodec71::Opus.is_discrete_71());
        assert!(AudioCodec71::Flac.is_supported());
        assert!(AudioCodec71::Flac.is_discrete_71());
    }

    #[test]
    fn test_other_royalty_free_codec_args() {
        let fx = Fixture::new("other_codecs");
        let output = fx.output("master_71.mkv");

        let vorbis = build_ffmpeg_args(
            &fx.video, &fx.audio, &output, AudioCodec71::Vorbis, false)
            .expect("Vorbis args should build");
        assert!(vorbis.windows(2).any(|w| w[0] == "-c:a" && w[1] == "libvorbis"));

        let wavpack = build_ffmpeg_args(
            &fx.video, &fx.audio, &output, AudioCodec71::WavPack, false)
            .expect("WavPack args should build");
        assert!(wavpack.windows(2).any(|w| w[0] == "-c:a" && w[1] == "wavpack"));

        let pcm = build_ffmpeg_args(
            &fx.video, &fx.audio, &output, AudioCodec71::Pcm24, false)
            .expect("PCM args should build");
        assert!(pcm.windows(2).any(|w| w[0] == "-c:a" && w[1] == "pcm_s24le"));
        assert!(pcm.windows(2).any(|w| w[0] == "-channel_layout" && w[1] == "7.1"));
    }

    #[test]
    fn test_build_ffmpeg_args_opus_exact() {
        let fx = Fixture::new("opus");
        let output = fx.output("master_71.mkv");

        let args = build_ffmpeg_args(
            &fx.video,
            &fx.audio,
            &output,
            AudioCodec71::Opus,
            false,
        )
        .expect("argument construction must succeed");

        let temp = temp_output_path(&output);
        let expected = strings(&[
            "-y",
            "-i",
            fx.video.to_str().unwrap(),
            "-i",
            fx.audio.to_str().unwrap(),
            "-filter_complex",
            &build_71_filter_complex(false, 120),
            "-map",
            "0:v:0",
            "-map",
            "[aout]",
            "-c:v",
            "copy",
            "-c:a",
            "libopus",
            "-mapping_family",
            "1",
            "-b:a",
            "768k",
            temp.to_str().unwrap(),
        ]);

        assert_eq!(args, expected);
        println!("[Opus] ffmpeg {}", args.join(" "));
    }

    #[test]
    fn test_build_ffmpeg_args_flac_exact() {
        let fx = Fixture::new("flac");
        let output = fx.output("master_71.mkv");

        let args = build_ffmpeg_args(
            &fx.video,
            &fx.audio,
            &output,
            AudioCodec71::Flac,
            true,
        )
        .expect("argument construction must succeed");

        let temp = temp_output_path(&output);
        let expected = strings(&[
            "-y",
            "-i",
            fx.video.to_str().unwrap(),
            "-i",
            fx.audio.to_str().unwrap(),
            "-filter_complex",
            &build_71_filter_complex(true, 120),
            "-map",
            "0:v:0",
            "-map",
            "[aout]",
            "-c:v",
            "copy",
            "-c:a",
            "flac",
            temp.to_str().unwrap(),
        ]);

        assert_eq!(args, expected);
        // FLAC in Matroska needs no experimental flag.
        assert!(!args.iter().any(|arg| arg == "-strict"));
        println!("[FLAC/MKV] ffmpeg {}", args.join(" "));
    }

    #[test]
    fn test_temp_output_path_is_sibling_and_preserves_extension() {
        assert_eq!(
            temp_output_path(Path::new(r"D:\renders\master_71.mkv")),
            PathBuf::from(r"D:\renders\master_71.tmp.mkv")
        );
        assert_eq!(
            temp_output_path(Path::new("master_71.MP4")),
            PathBuf::from("master_71.tmp.MP4")
        );
        assert_eq!(
            temp_output_path(Path::new("master_71")),
            PathBuf::from("master_71.tmp")
        );
        assert_eq!(
            temp_output_path(Path::new("archive.tar.mkv")),
            PathBuf::from("archive.tar.tmp.mkv")
        );
        // Always the same directory as the final output (atomic rename).
        assert_eq!(
            temp_output_path(Path::new("out.mkv")).parent(),
            Path::new("out.mkv").parent()
        );
    }

    #[test]
    fn test_missing_inputs_return_minus_two() {
        let fx = Fixture::new("missing");
        let output = fx.output("master_71.mkv");
        let missing = fx.dir.join("does_not_exist.w64");

        assert_eq!(
            build_ffmpeg_args(&fx.video, &missing, &output, AudioCodec71::Flac, false),
            Err(MUX_ERR_MISSING_INPUT)
        );

        let missing_video = fx.dir.join("does_not_exist.mp4");
        assert_eq!(
            build_ffmpeg_args(
                &missing_video,
                &fx.audio,
                &output,
                AudioCodec71::Flac,
                false
            ),
            Err(MUX_ERR_MISSING_INPUT)
        );
    }

    #[test]
    fn test_invalid_codec_returns_minus_four() {
        let fx = Fixture::new("bad_codec");
        let output = fx.output("master_71.mkv");

        assert_eq!(
            execute_mux_job(&fx.video, &fx.audio, &output, 9, false),
            MUX_ERR_INVALID_CODEC
        );
        assert_eq!(
            execute_mux_job(&fx.video, &fx.audio, &output, -1, false),
            MUX_ERR_INVALID_CODEC
        );
    }

    #[test]
    fn test_missing_input_through_execute_returns_minus_two() {
        let fx = Fixture::new("exec_missing");
        let output = fx.output("master_71.mkv");
        let missing = fx.dir.join("absent.w64");

        assert_eq!(
            execute_mux_job(&fx.video, &missing, &output, 2, false),
            MUX_ERR_MISSING_INPUT
        );
    }

    /// A stub (non-media) input makes FFmpeg fail deterministically, and an
    /// absent FFmpeg binary fails to spawn - both must map to `-3` and must
    /// delete any pre-existing temporary output.
    #[test]
    fn test_ffmpeg_failure_returns_minus_three_and_cleans_temp() {
        let fx = Fixture::new("exec_fail");
        let output = fx.output("master_71.mkv");
        let temp = temp_output_path(&output);

        // Simulate debris from a previously crashed render.
        fs::write(&temp, b"stale partial media").expect("seed stale temp file");
        assert!(temp.exists());

        assert_eq!(
            execute_mux_job(&fx.video, &fx.audio, &output, 2, false),
            MUX_ERR_FFMPEG
        );
        assert!(!temp.exists(), "failed render must not leave temp media");
        assert!(!output.exists(), "failed render must not publish output");
    }

    #[test]
    fn test_codec_enum_round_trip() {
        for (raw, codec) in [
            (2, AudioCodec71::Flac),
            (3, AudioCodec71::Pcm24),
            (4, AudioCodec71::Opus),
            (5, AudioCodec71::Vorbis),
            (6, AudioCodec71::WavPack),
        ] {
            assert_eq!(AudioCodec71::try_from(raw), Ok(codec));
            assert_eq!(codec as i32, raw);
        }
        for rejected in [0, 1, 7, 9] {
            assert_eq!(
                AudioCodec71::try_from(rejected),
                Err(MUX_ERR_INVALID_CODEC)
            );
        }
    }

    #[test]
    fn test_mp4_extension_detection() {
        assert!(has_mp4_extension(Path::new("out.mp4")));
        assert!(has_mp4_extension(Path::new("out.MP4")));
        assert!(!has_mp4_extension(Path::new("out.mkv")));
        assert!(!has_mp4_extension(Path::new("out.mov")));
        assert!(!has_mp4_extension(Path::new("out")));
    }
}
