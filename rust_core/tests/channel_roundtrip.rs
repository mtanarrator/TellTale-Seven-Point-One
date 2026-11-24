//! End-to-end 8-channel round-trip verification for the FFmpeg 7.1 muxer.
//!
//! Requires `ffmpeg` on `PATH`; the tests skip cleanly (with a `SKIP:` notice)
//! when the binary is unavailable, so `cargo test` stays hermetic on build
//! agents without FFmpeg.
//!
//! Pipeline under test:
//!
//! ```text
//! synthetic 8 x mono sine (.w64, c0..c7)
//!         + dummy video
//!                 |
//!          mux_71_master (FLAC 7.1 / .mkv)
//!                 |
//!         ffmpeg decode -> 8-ch f32le PCM
//!                 |
//!          Hann-windowed DFT per channel
//! ```

use std::ffi::CString;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Command, Output};

use telltale_7point1_core::mux_71_master;
use telltale_7point1_core::muxer::temp_output_path;

const SAMPLE_RATE: u32 = 48_000;
const CODE_FLAC: i32 = 2;

/// `c0..c7` -> `FL, FR, FC, LFE, BL, BR, SL, SR`.
/// Every bus carries a distinct, well-separated sine tone.
const IDENTITY_FREQS: [f64; 8] = [440.0, 554.0, 659.0, 80.0, 880.0, 1046.0, 1318.0, 1568.0];

/// Channel index of the LFE bus (`c3`).
const LFE_INDEX: usize = 3;

struct TestDir {
    path: PathBuf,
}

impl TestDir {
    fn new(tag: &str) -> Self {
        let path = std::env::temp_dir().join(format!(
            "vegas_surround71_roundtrip_{}_{}",
            std::process::id(),
            tag
        ));
        let _ = fs::remove_dir_all(&path);
        fs::create_dir_all(&path).expect("create round-trip test directory");
        Self { path }
    }

    fn file(&self, name: &str) -> PathBuf {
        self.path.join(name)
    }
}

impl Drop for TestDir {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.path);
    }
}

fn ffmpeg_available() -> bool {
    Command::new("ffmpeg")
        .arg("-version")
        .output()
        .map(|output| output.status.success())
        .unwrap_or(false)
}

fn run_tool(args: &[String], context: &str) -> Output {
    let output = Command::new("ffmpeg")
        .args(args)
        .output()
        .unwrap_or_else(|error| panic!("{context}: failed to spawn ffmpeg: {error}"));
    assert!(
        output.status.success(),
        "{context}: ffmpeg exited with {:?}\nstderr:\n{}",
        output.status,
        String::from_utf8_lossy(&output.stderr)
    );
    output
}

fn s(value: &str) -> String {
    value.to_string()
}

/// Generates a 1-second 160x90 dummy MPEG-4 video stream.
fn generate_video(path: &Path) {
    let args: Vec<String> = vec![
        s("-y"),
        s("-f"),
        s("lavfi"),
        s("-i"),
        s("color=c=black:s=160x90:r=10:d=1"),
        s("-c:v"),
        s("mpeg4"),
        s("-q:v"),
        s("5"),
        s("-pix_fmt"),
        s("yuv420p"),
        s(path.to_str().unwrap()),
    ];
    run_tool(&args, "generate dummy video");
}

/// Generates an 8-channel 24-bit Wave64 fixture with one mono sine per bus.
///
/// A single `aevalsrc` source is used instead of `join`/`amerge` so the
/// channel order is exactly the declared 7.1 layout order (FL, FR, FC, LFE, BL,
/// BR, SL, SR) with no implicit remapping or gain normalization.
fn generate_w64(freqs: &[f64; 8], path: &Path) {
    let expressions: Vec<String> = freqs
        .iter()
        .map(|frequency| format!("sin(2*PI*{frequency}*t)"))
        .collect();
    let source = format!(
        "aevalsrc={}:s={SAMPLE_RATE}:d=1:c=7.1",
        expressions.join("|")
    );

    let args: Vec<String> = vec![
        s("-y"),
        s("-f"),
        s("lavfi"),
        s("-i"),
        source,
        s("-map"),
        s("0:a:0"),
        s("-c:a"),
        s("pcm_s24le"),
        s(path.to_str().unwrap()),
    ];

    run_tool(&args, "generate 8-channel w64 fixture");
}

/// Invokes the C-ABI entrypoint exactly as the C# host will.
fn mux(video: &Path, audio: &Path, output: &Path, codec: i32, apply_lfe_lowpass: bool) {
    let video = CString::new(video.to_str().unwrap()).unwrap();
    let audio = CString::new(audio.to_str().unwrap()).unwrap();
    let output_c = CString::new(output.to_str().unwrap()).unwrap();

    let status = mux_71_master(
        video.as_ptr(),
        audio.as_ptr(),
        output_c.as_ptr(),
        codec,
        apply_lfe_lowpass,
    );

    assert_eq!(status, 0, "mux_71_master failed with status {status}");
    assert!(output.exists(), "published output is missing");
    assert!(
        !temp_output_path(output).exists(),
        "successful mux must not leave a temporary file behind"
    );
}

/// Decodes the first audio stream to interleaved 8-channel `f32le` PCM.
fn decode_to_f32le(master: &Path, raw: &Path) {
    let args: Vec<String> = vec![
        s("-y"),
        s("-i"),
        s(master.to_str().unwrap()),
        s("-map"),
        s("0:a:0"),
        s("-vn"),
        s("-f"),
        s("f32le"),
        s("-acodec"),
        s("pcm_f32le"),
        s(raw.to_str().unwrap()),
    ];
    run_tool(&args, "decode 7.1 FLAC back to f32le PCM");
}

fn read_f32le(path: &Path) -> Vec<f32> {
    let bytes = fs::read(path).expect("read decoded f32le PCM");
    let (chunks, remainder) = bytes.as_chunks::<4>();
    assert!(
        remainder.is_empty(),
        "decoded PCM length is not f32-aligned"
    );
    chunks
        .iter()
        .map(|chunk| f32::from_le_bytes(*chunk))
        .collect()
}

fn deinterleave(samples: &[f32]) -> Vec<Vec<f32>> {
    let (frames, remainder) = samples.as_chunks::<8>();
    assert!(
        remainder.is_empty(),
        "decoded stream is not 8-channel ({} samples)",
        samples.len()
    );

    let mut channels: Vec<Vec<f32>> = (0..8)
        .map(|_| Vec::with_capacity(frames.len()))
        .collect();
    for frame in frames {
        for (channel, sample) in frame.iter().enumerate() {
            channels[channel].push(*sample);
        }
    }
    channels
}

/// Trims encoder/container edges: keeps 0.1 s .. 0.9 s of the 1 s fixture.
fn analysis_slice(channel: &[f32]) -> &[f32] {
    let start = (SAMPLE_RATE as f64 * 0.1) as usize;
    let end = ((SAMPLE_RATE as f64 * 0.9) as usize).min(channel.len());
    assert!(
        end > start + 1000,
        "decoded channel too short for analysis ({} samples)",
        channel.len()
    );
    &channel[start..end]
}

/// Hann-windowed single-bin DFT amplitude estimate (coherent-gain corrected).
fn tone_amplitude(samples: &[f32], frequency: f64) -> f64 {
    let count = samples.len();
    if count < 2 {
        return 0.0;
    }

    let denominator = (count - 1) as f64;
    let mut real = 0.0;
    let mut imaginary = 0.0;
    let mut window_sum = 0.0;

    for (index, sample) in samples.iter().enumerate() {
        let window = 0.5 - 0.5 * (2.0 * std::f64::consts::PI * index as f64 / denominator).cos();
        let phase =
            2.0 * std::f64::consts::PI * frequency * index as f64 / SAMPLE_RATE as f64;
        let value = *sample as f64 * window;
        real += value * phase.cos();
        imaginary -= value * phase.sin();
        window_sum += window;
    }

    if window_sum <= 0.0 {
        0.0
    } else {
        2.0 * (real * real + imaginary * imaginary).sqrt() / window_sum
    }
}

fn decode_channel_amplitudes(master: &Path, raw: &Path, frequency: f64) -> Vec<f64> {
    decode_to_f32le(master, raw);
    let samples = read_f32le(raw);
    let channels = deinterleave(&samples);
    channels
        .iter()
        .map(|channel| tone_amplitude(analysis_slice(channel), frequency))
        .collect()
}

/// Each of the eight distinct tones must arrive in its exact SMPTE 7.1 channel,
/// with side surrounds cleanly separated from the back/rear pair.
#[test]
fn test_w64_71_channel_roundtrip_identity() {
    if !ffmpeg_available() {
        eprintln!("SKIP: ffmpeg not found on PATH");
        return;
    }

    let dir = TestDir::new("identity");
    let video = dir.file("dummy_video.mp4");
    let w64 = dir.file("vegas_busses.w64");
    let master = dir.file("master_71.mkv");
    let raw = dir.file("decoded_71.f32le");

    generate_video(&video);
    generate_w64(&IDENTITY_FREQS, &w64);
    mux(&video, &w64, &master, CODE_FLAC, false);

    decode_to_f32le(&master, &raw);
    let samples = read_f32le(&raw);
    let channels = deinterleave(&samples);

    let names = ["FL", "FR", "FC", "LFE", "BL", "BR", "SL", "SR"];

    for (expected, frequency) in IDENTITY_FREQS.iter().enumerate() {
        let amplitudes: Vec<f64> = channels
            .iter()
            .map(|channel| tone_amplitude(analysis_slice(channel), *frequency))
            .collect();

        let (loudest, loudest_amplitude) = amplitudes
            .iter()
            .enumerate()
            .max_by(|a, b| a.1.partial_cmp(b.1).unwrap())
            .expect("non-empty channel set");

        assert_eq!(
            loudest, expected,
            "{} Hz landed in {} (index {}), expected {} (index {}); amplitudes: {:?}",
            frequency,
            names[loudest],
            loudest,
            names[expected],
            expected,
            amplitudes
        );
        assert!(
            *loudest_amplitude > 0.05,
            "{} Hz amplitude in {} too low: {}",
            frequency,
            names[expected],
            loudest_amplitude
        );

        for (index, amplitude) in amplitudes.iter().enumerate() {
            if index != expected {
                assert!(
                    amplitude / loudest_amplitude < 0.01,
                    "crosstalk: {} Hz leaks into {} at {:.2} dB relative to {}",
                    frequency,
                    names[index],
                    20.0 * (amplitude / loudest_amplitude).log10(),
                    names[expected]
                );
            }
        }
    }

    // Explicit side-vs-rear separation: SL (c6) and SR (c7) must not collapse
    // into BL/BR, which is the classic 5.1 downmix failure mode.
    let side_left = tone_amplitude(analysis_slice(&channels[6]), 1318.0);
    let side_right = tone_amplitude(analysis_slice(&channels[7]), 1568.0);
    let back_left_leak = tone_amplitude(analysis_slice(&channels[4]), 1568.0);
    let back_right_leak = tone_amplitude(analysis_slice(&channels[5]), 1318.0);

    assert!(
        side_left > 20.0 * back_left_leak,
        "SL tone not isolated from BL: SL={side_left}, BL(SL tone)={back_left_leak}"
    );
    assert!(
        side_right > 20.0 * back_right_leak,
        "SR tone not isolated from BR: SR={side_right}, BR(SR tone)={back_right_leak}"
    );

    println!(
        "Round-trip identity verified: FL=440 FR=554 FC=659 LFE=80 BL=880 BR=1046 SL=1318 SR=1568 Hz"
    );
}

/// With the LR4 LFE cascade enabled, 500 Hz on `c3` must be attenuated by more
/// than 20 dB while the 80 Hz fixture passes essentially unattenuated.
#[test]
fn test_lfe_lowpass_attenuates_500hz_and_passes_80hz() {
    if !ffmpeg_available() {
        eprintln!("SKIP: ffmpeg not found on PATH");
        return;
    }

    let dir = TestDir::new("lfe");
    let video = dir.file("dummy_video.mp4");
    generate_video(&video);

    // -- 80 Hz reference (identity fixture already has c3 = 80 Hz) ----------
    let w64_80 = dir.file("busses_80hz.w64");
    let flat_80 = dir.file("master_80_flat.mkv");
    let filtered_80 = dir.file("master_80_lowpass.mkv");
    generate_w64(&IDENTITY_FREQS, &w64_80);
    mux(&video, &w64_80, &flat_80, CODE_FLAC, false);
    mux(&video, &w64_80, &filtered_80, CODE_FLAC, true);

    let flat_80_lfe =
        decode_channel_amplitudes(&flat_80, &dir.file("raw_80_flat.f32le"), 80.0)[LFE_INDEX];
    let filtered_80_lfe =
        decode_channel_amplitudes(&filtered_80, &dir.file("raw_80_lp.f32le"), 80.0)[LFE_INDEX];

    let pass_ratio = filtered_80_lfe / flat_80_lfe;
    let pass_db = 20.0 * pass_ratio.log10();
    assert!(
        pass_ratio > 0.5,
        "80 Hz LFE should pass cleanly through the 120 Hz LR4 cascade, got {pass_db:.2} dB"
    );

    // -- 500 Hz fixture: c3 carries 500 Hz --------------------------------
    let mut freqs_500 = IDENTITY_FREQS;
    freqs_500[LFE_INDEX] = 500.0;

    let w64_500 = dir.file("busses_500hz.w64");
    let flat_500 = dir.file("master_500_flat.mkv");
    let filtered_500 = dir.file("master_500_lowpass.mkv");
    generate_w64(&freqs_500, &w64_500);
    mux(&video, &w64_500, &flat_500, CODE_FLAC, false);
    mux(&video, &w64_500, &filtered_500, CODE_FLAC, true);

    let flat_500_lfe =
        decode_channel_amplitudes(&flat_500, &dir.file("raw_500_flat.f32le"), 500.0)[LFE_INDEX];
    let filtered_500_lfe =
        decode_channel_amplitudes(&filtered_500, &dir.file("raw_500_lp.f32le"), 500.0)[LFE_INDEX];

    let attenuation_db = 20.0 * (filtered_500_lfe / flat_500_lfe).log10();
    assert!(
        attenuation_db < -20.0,
        "500 Hz LFE should be attenuated by >20 dB, got {attenuation_db:.2} dB"
    );

    println!(
        "LFE LR4 verified: 80 Hz pass {pass_db:+.2} dB, 500 Hz attenuation {attenuation_db:+.2} dB"
    );
}
