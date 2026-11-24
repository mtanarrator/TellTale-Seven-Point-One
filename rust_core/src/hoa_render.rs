//! HOA audio rendering, AmbiX export, and stereo/binaural preview.
//!
//! This module is intentionally separate from the 7.1 VBAP/mux pipeline. It
//! reads a mono or multichannel PCM WAV/Wave64 stem, encodes it to ACN/SN3D
//! Ambisonics (1st/2nd/3rd order), and writes an AmbiX `.wav`/`.w64` master or
//! a lossless `.flac` (FFmpeg) file. It can also render a 2-channel stereo or
//! binaural preview.
//!
//! All file writes are atomic: data is written to a sibling
//! `<stem>.tmp.<pid>.<ext>` file and renamed onto the requested output only on
//! success. Any failure removes the temporary file.
//!
//! Nothing in this module reads or writes the 7.1 bus topology or its send
//! envelopes.

use crate::hoa::{calculate_weights, channel_count, HoaSourcePosition};
use std::ffi::CStr;
use std::fs::{self, File};
use std::io::{BufWriter, Write};
use std::os::raw::c_char;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

/// Render completed and the output was published atomically.
pub const HOA_RENDER_OK: i32 = 0;

/// A null pointer, empty path, or invalid UTF-8 path was supplied.
pub const HOA_RENDER_ERR_INVALID_ARGUMENT: i32 = -1;

/// The input stem does not exist or cannot be read.
pub const HOA_RENDER_ERR_MISSING_INPUT: i32 = -2;

/// FFmpeg failed to spawn, exited non-zero, or could not publish the output.
pub const HOA_RENDER_ERR_FFMPEG: i32 = -3;

/// The output extension or PCM format is not supported.
pub const HOA_RENDER_ERR_UNSUPPORTED_FORMAT: i32 = -4;

/// A filesystem read/write/rename operation failed.
pub const HOA_RENDER_ERR_IO: i32 = -5;

/// The Ambisonics order is outside `1..=3`.
pub const HOA_RENDER_ERR_INVALID_ORDER: i32 = -6;

/// An internal panic was contained at the FFI boundary.
pub const HOA_RENDER_ERR_PANIC: i32 = -99;

/// Distance attenuation model: `1 / (1 + d)`.
pub const HOA_DISTANCE_INVERSE: u32 = 0;
/// Distance attenuation model: `1 / (1 + d)^2`.
pub const HOA_DISTANCE_INVERSE_SQUARE: u32 = 1;
/// Distance attenuation model: `1 - d`.
pub const HOA_DISTANCE_LINEAR: u32 = 2;
/// No distance attenuation.
pub const HOA_DISTANCE_NONE: u32 = 3;

/// Binaural head model: ITD + ILD (default).
pub const HOA_HEAD_BOTH: u32 = 0;
/// Binaural head model: ITD only.
pub const HOA_HEAD_ITD_ONLY: u32 = 1;
/// Binaural head model: ILD/head-shadow only.
pub const HOA_HEAD_ILD_ONLY: u32 = 2;
/// Binaural head model: no HRTF (stereo pan fallback).
pub const HOA_HEAD_OFF: u32 = 3;

/// Preview mode: binaural headphones.
pub const HOA_PREVIEW_BINAURAL: i32 = 0;
/// Preview mode: stereo downmix.
pub const HOA_PREVIEW_STEREO: i32 = 1;
/// Preview mode: dry reference (mono duplicated).
pub const HOA_PREVIEW_BYPASS: i32 = 2;

/// Extended HOA render/preview options.
///
/// The layout is fixed at 48 bytes (`u32`/`f32` fields only) and mirrors the
/// C# `HoaRenderOptions` struct byte-for-byte.
#[repr(C)]
#[derive(Debug, Copy, Clone, PartialEq)]
pub struct HoaRenderOptions {
    pub order: u32,
    pub azimuth_deg: f32,
    pub elevation_deg: f32,
    pub distance: f32,
    pub spread: f32,
    pub distance_model: u32,
    pub air_absorption: f32,
    pub head_model: u32,
    pub head_width: f32,
    pub room_amount: f32,
    pub room_size: f32,
    pub preview_mode: i32,
}

impl Default for HoaRenderOptions {
    fn default() -> Self {
        Self {
            order: 1,
            azimuth_deg: 0.0,
            elevation_deg: 0.0,
            distance: 0.0,
            spread: 0.0,
            distance_model: HOA_DISTANCE_INVERSE,
            air_absorption: 0.0,
            head_model: HOA_HEAD_BOTH,
            head_width: 1.0,
            room_amount: 0.0,
            room_size: 0.5,
            preview_mode: HOA_PREVIEW_BINAURAL,
        }
    }
}

impl HoaRenderOptions {
    fn position(&self) -> HoaSourcePosition {
        HoaSourcePosition {
            azimuth_deg: self.azimuth_deg,
            elevation_deg: self.elevation_deg,
            distance: self.distance,
            spread: self.spread,
            order: self.order,
        }
    }

    fn validate(&self) -> Result<(), i32> {
        if self.distance_model > HOA_DISTANCE_NONE {
            return Err(HOA_RENDER_ERR_UNSUPPORTED_FORMAT);
        }
        if self.head_model > HOA_HEAD_OFF {
            return Err(HOA_RENDER_ERR_UNSUPPORTED_FORMAT);
        }
        if self.preview_mode < HOA_PREVIEW_BINAURAL || self.preview_mode > HOA_PREVIEW_BYPASS {
            return Err(HOA_RENDER_ERR_UNSUPPORTED_FORMAT);
        }
        channel_count(self.order).ok_or(HOA_RENDER_ERR_INVALID_ORDER)?;
        Ok(())
    }
}

const FLAC_MAX_CHANNELS: usize = 8;

#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

// Wave64 GUIDs (the first four bytes are the ASCII chunk id).
const W64_RIFF_GUID: [u8; 16] = [
    0x72, 0x69, 0x66, 0x66, 0x2E, 0x91, 0xCF, 0x11, 0xA5, 0xD6, 0x28, 0xDB, 0x04, 0xC1, 0x00, 0x00,
];
const W64_WAVE_GUID: [u8; 16] = [
    0x77, 0x61, 0x76, 0x65, 0xF3, 0xAC, 0xD3, 0x11, 0x8C, 0xD1, 0x00, 0xC0, 0x4F, 0x8E, 0xDB, 0x8A,
];
const W64_FMT_GUID: [u8; 16] = [
    0x66, 0x6D, 0x74, 0x20, 0xF3, 0xAC, 0xD3, 0x11, 0x8C, 0xD1, 0x00, 0xC0, 0x4F, 0x8E, 0xDB, 0x8A,
];
const W64_DATA_GUID: [u8; 16] = [
    0x64, 0x61, 0x74, 0x61, 0xF3, 0xAC, 0xD3, 0x11, 0x8C, 0xD1, 0x00, 0xC0, 0x4F, 0x8E, 0xDB, 0x8A,
];

/// Deinterleaved PCM audio read from a WAV/W64 file.
#[derive(Debug)]
struct PcmAudio {
    sample_rate: u32,
    channels: Vec<Vec<f32>>,
}

impl PcmAudio {
    fn frame_count(&self) -> usize {
        self.channels.first().map_or(0, |channel| channel.len())
    }

    /// Averages all source channels into a mono stem.
    fn to_mono(&self) -> Vec<f32> {
        if self.channels.is_empty() {
            return Vec::new();
        }

        if self.channels.len() == 1 {
            return self.channels[0].clone();
        }

        let frames = self.frame_count();
        let mut mono = vec![0.0_f32; frames];
        for channel in &self.channels {
            for (index, sample) in channel.iter().enumerate().take(frames) {
                mono[index] += *sample;
            }
        }

        let scale = 1.0 / self.channels.len() as f32;
        for sample in &mut mono {
            *sample *= scale;
        }

        mono
    }
}

#[derive(Debug, Copy, Clone)]
struct WavFormat {
    format_tag: u16,
    channels: u16,
    sample_rate: u32,
    bits_per_sample: u16,
}

/// Atomically renders an AmbiX HOA master from `input` to `output`.
///
/// The extension selects the container/encoder:
/// - `.wav`  -> 24-bit PCM RIFF WAVE (N channels)
/// - `.w64`  -> 24-bit PCM Wave64 (N channels)
/// - `.flac` -> 24-bit PCM temp WAV encoded through FFmpeg
pub fn render_hoa_amix_master(
    input: &Path,
    output: &Path,
    options: &HoaRenderOptions,
) -> Result<(), i32> {
    if !input.is_file() {
        return Err(HOA_RENDER_ERR_MISSING_INPUT);
    }

    options.validate()?;
    let position = options.position();
    let count = channel_count(position.order).ok_or(HOA_RENDER_ERR_INVALID_ORDER)?;
    let weights = calculate_weights(&position).map_err(|_| HOA_RENDER_ERR_INVALID_ORDER)?;
    let audio = read_audio(input)?;
    let mono = audio.to_mono();
    let mono = apply_air_absorption(
        &mono,
        audio.sample_rate,
        options.air_absorption,
        options.distance,
    );
    let model_gain = distance_model_factor(options.distance_model, options.distance);

    let mut interleaved = Vec::with_capacity(mono.len() * count);
    for &sample in &mono {
        for &weight in &weights {
            interleaved.push(sample * weight * model_gain);
        }
    }

    let extension = output_extension(output);
    match extension.as_str() {
        "wav" => write_atomic(output, |temp| {
            write_wav_pcm(temp, audio.sample_rate, count as u16, 24, &interleaved)
        }),
        "w64" => write_atomic(output, |temp| {
            write_w64_pcm(temp, audio.sample_rate, count as u16, &interleaved)
        }),
        "flac" => {
            if count > FLAC_MAX_CHANNELS {
                return Err(HOA_RENDER_ERR_UNSUPPORTED_FORMAT);
            }

            write_flac(output, audio.sample_rate, count as u16, &interleaved)
        }
        _ => Err(HOA_RENDER_ERR_UNSUPPORTED_FORMAT),
    }
}

/// Atomically renders a 2-channel stereo or binaural preview.
///
/// `preview_mode`: `0` = binaural headphones, `1` = stereo downmix.
pub fn render_hoa_preview_file(
    input: &Path,
    output: &Path,
    options: &HoaRenderOptions,
) -> Result<(), i32> {
    if !input.is_file() {
        return Err(HOA_RENDER_ERR_MISSING_INPUT);
    }

    options.validate()?;
    if output_extension(output) != "wav" {
        return Err(HOA_RENDER_ERR_UNSUPPORTED_FORMAT);
    }

    let position = options.position();
    let weights = calculate_weights(&position).map_err(|_| HOA_RENDER_ERR_INVALID_ORDER)?;
    let audio = read_audio(input)?;
    let mono = audio.to_mono();
    let mono = apply_air_absorption(
        &mono,
        audio.sample_rate,
        options.air_absorption,
        options.distance,
    );
    let model_gain = distance_model_factor(options.distance_model, options.distance);
    let head_model = if options.head_model == HOA_HEAD_OFF {
        HOA_HEAD_OFF
    } else {
        options.head_model
    };

    let (mut left, mut right) = if options.preview_mode == HOA_PREVIEW_BYPASS {
        (mono.clone(), mono.clone())
    } else if options.preview_mode == HOA_PREVIEW_BINAURAL && head_model != HOA_HEAD_OFF {
        decode_binaural(&mono, audio.sample_rate, options, &weights)
    } else {
        decode_stereo(&mono, options, &weights)
    };

    for sample in &mut left {
        *sample *= model_gain;
    }
    for sample in &mut right {
        *sample *= model_gain;
    }

    if options.preview_mode != HOA_PREVIEW_BYPASS && options.room_amount > 0.0 {
        apply_room(
            &mut left,
            &mut right,
            audio.sample_rate,
            options.room_amount,
            options.room_size,
        );
    }

    let mut interleaved = Vec::with_capacity(mono.len() * 2);
    for index in 0..mono.len() {
        interleaved.push(left[index]);
        interleaved.push(right[index]);
    }

    write_atomic(output, |temp| {
        write_wav_pcm(temp, audio.sample_rate, 2, 16, &interleaved)
    })
}

/// Applies the selected distance model relative to the default inverse law
/// already present in `calculate_weights`.
fn distance_model_factor(model: u32, distance: f32) -> f32 {
    let d = distance.clamp(0.0, 1.0);
    let default = 1.0 / (1.0 + d);
    let target = match model {
        HOA_DISTANCE_INVERSE_SQUARE => 1.0 / ((1.0 + d) * (1.0 + d)),
        HOA_DISTANCE_LINEAR => (1.0 - d).max(0.0),
        HOA_DISTANCE_NONE => 1.0,
        _ => default,
    };

    if default > 1e-9 {
        target / default
    } else {
        1.0
    }
}

/// A one-pole air-absorption low-pass. The cutoff falls with both distance and
/// the user's air-absorption amount.
fn apply_air_absorption(
    mono: &[f32],
    sample_rate: u32,
    air_absorption: f32,
    distance: f32,
) -> Vec<f32> {
    let amount = air_absorption.clamp(0.0, 1.0) * distance.clamp(0.0, 1.0);
    if amount <= 1e-4 {
        return mono.to_vec();
    }

    let cutoff = (20_000.0 * (1.0 - 0.9 * amount)).clamp(200.0, 20_000.0);
    let alpha = 1.0 - (-2.0 * std::f32::consts::PI * cutoff / sample_rate as f32).exp();
    let mut state = 0.0_f32;
    let mut output = Vec::with_capacity(mono.len());
    for &sample in mono {
        state += alpha * (sample - state);
        output.push(state);
    }

    output
}

/// Equal-power stereo downmix driven by the HOA W/Y/X components.
fn decode_stereo(
    mono: &[f32],
    options: &HoaRenderOptions,
    weights: &[f32],
) -> (Vec<f32>, Vec<f32>) {
    let (w, pan, front) = directional_components(weights);
    let angle = (pan + 1.0) * std::f32::consts::FRAC_PI_4;
    let front_back_gain = 0.8 + 0.2 * front;
    let elevation_gain = 0.9 + 0.1 * (options.elevation_deg.clamp(-90.0, 90.0) / 90.0);
    let left_gain = angle.cos() * front_back_gain * elevation_gain;
    let right_gain = angle.sin() * front_back_gain * elevation_gain;

    // A tiny amount of W-dependent level keeps a fully spread source from
    // collapsing to the same loudness as a focused source.
    let spread_level = 0.85 + 0.15 * w;
    let left = mono
        .iter()
        .map(|sample| *sample * left_gain * spread_level)
        .collect();
    let right = mono
        .iter()
        .map(|sample| *sample * right_gain * spread_level)
        .collect();

    (left, right)
}

/// A compact virtual-head binaural decoder.
///
/// The near ear receives the direct signal; the far ear receives an ITD delay,
/// a head-shadow low-pass, and an ILD attenuation. Negative `front` (rear)
/// darkens both ears, and elevation applies a gentle spectral tilt.
fn decode_binaural(
    mono: &[f32],
    sample_rate: u32,
    options: &HoaRenderOptions,
    weights: &[f32],
) -> (Vec<f32>, Vec<f32>) {
    let (_w, pan, front) = directional_components(weights);
    let elevation = options.elevation_deg.clamp(-90.0, 90.0) / 90.0;
    let head_width = options.head_width.clamp(0.5, 1.5);
    let itd_enabled =
        options.head_model == HOA_HEAD_BOTH || options.head_model == HOA_HEAD_ITD_ONLY;
    let ild_enabled =
        options.head_model == HOA_HEAD_BOTH || options.head_model == HOA_HEAD_ILD_ONLY;

    let itd_samples = if itd_enabled {
        ((0.00065 * sample_rate as f32 * pan.abs() * head_width).round() as usize).min(96)
    } else {
        0
    };
    let delay_len = itd_samples + 1;
    let mut history = vec![0.0_f32; delay_len];
    let mut write_index = 0_usize;

    let left_delay = if pan > 0.0 { itd_samples } else { 0 };
    let right_delay = if pan < 0.0 { itd_samples } else { 0 };
    let shadow = if ild_enabled {
        (pan.abs() * head_width).clamp(0.0, 1.0)
    } else {
        0.0
    };
    let far_alpha = (1.0 - 0.75 * shadow).clamp(0.12, 1.0);
    let far_gain = 1.0 - 0.45 * shadow;

    let rear = (-front).max(0.0);
    let rear_alpha = (1.0 - 0.5 * rear).clamp(0.15, 1.0);
    let rear_gain = 1.0 - 0.35 * rear;
    let elevation_gain = 0.9 + 0.1 * elevation;

    let mut left_lp = 0.0_f32;
    let mut right_lp = 0.0_f32;
    let mut rear_lp_l = 0.0_f32;
    let mut rear_lp_r = 0.0_f32;
    let mut tilt_lp_l = 0.0_f32;
    let mut tilt_lp_r = 0.0_f32;

    let mut left = Vec::with_capacity(mono.len());
    let mut right = Vec::with_capacity(mono.len());

    for &sample in mono {
        history[write_index] = sample;
        let left_raw = history[(write_index + delay_len - left_delay) % delay_len];
        let right_raw = history[(write_index + delay_len - right_delay) % delay_len];
        write_index = (write_index + 1) % delay_len;

        // Head shadow / ILD on the far ear.
        let left_shadowed = if pan > 0.0 {
            left_lp += far_alpha * (left_raw - left_lp);
            left_lp * far_gain
        } else {
            left_raw
        };
        let right_shadowed = if pan < 0.0 {
            right_lp += far_alpha * (right_raw - right_lp);
            right_lp * far_gain
        } else {
            right_raw
        };

        // Rear sources are darker and slightly quieter.
        rear_lp_l += rear_alpha * (left_shadowed - rear_lp_l);
        rear_lp_r += rear_alpha * (right_shadowed - rear_lp_r);
        let rear_mix_l = if rear > 0.0 { rear_lp_l } else { left_shadowed };
        let rear_mix_r = if rear > 0.0 {
            rear_lp_r
        } else {
            right_shadowed
        };

        // Gentle elevation tilt: brighten overhead, darken below the horizon.
        tilt_lp_l += 0.15 * (rear_mix_l - tilt_lp_l);
        tilt_lp_r += 0.15 * (rear_mix_r - tilt_lp_r);
        let tilt = 0.15 * elevation;
        let left_out = (rear_mix_l + tilt * (rear_mix_l - tilt_lp_l)) * rear_gain * elevation_gain;
        let right_out = (rear_mix_r + tilt * (rear_mix_r - tilt_lp_r)) * rear_gain * elevation_gain;

        left.push(left_out.clamp(-1.0, 1.0));
        right.push(right_out.clamp(-1.0, 1.0));
    }

    (left, right)
}

/// A simple stereo room: early-reflection taps plus two damped feedback combs
/// per channel. The effect is applied to the monitor preview only.
fn apply_room(
    left: &mut [f32],
    right: &mut [f32],
    sample_rate: u32,
    room_amount: f32,
    room_size: f32,
) {
    let amount = room_amount.clamp(0.0, 1.0);
    if amount <= 1e-4 || left.is_empty() {
        return;
    }

    let sample_rate = sample_rate.max(8_000) as f32;
    let scale = 0.6 + 0.8 * room_size.clamp(0.0, 1.0);
    let mut taps_left = [
        TapLine::new((0.011 * scale * sample_rate) as usize),
        TapLine::new((0.017 * scale * sample_rate) as usize),
        TapLine::new((0.023 * scale * sample_rate) as usize),
    ];
    let mut taps_right = [
        TapLine::new((0.013 * scale * sample_rate) as usize),
        TapLine::new((0.019 * scale * sample_rate) as usize),
        TapLine::new((0.027 * scale * sample_rate) as usize),
    ];
    let mut comb_left = [
        CombLine::new((0.037 * scale * sample_rate) as usize),
        CombLine::new((0.043 * scale * sample_rate) as usize),
    ];
    let mut comb_right = [
        CombLine::new((0.041 * scale * sample_rate) as usize),
        CombLine::new((0.047 * scale * sample_rate) as usize),
    ];
    let tap_gains = [0.45_f32, 0.30, 0.20];
    let dry = 1.0 - 0.35 * amount;

    for index in 0..left.len() {
        let input_left = left[index];
        let input_right = right[index];

        let mut early_left = 0.0_f32;
        let mut early_right = 0.0_f32;
        for (tap, gain) in taps_left.iter_mut().zip(tap_gains.iter()) {
            early_left += tap.process(input_left) * *gain;
        }
        for (tap, gain) in taps_right.iter_mut().zip(tap_gains.iter()) {
            early_right += tap.process(input_right) * *gain;
        }

        let mut tail_left = 0.0_f32;
        let mut tail_right = 0.0_f32;
        for comb in &mut comb_left {
            tail_left += comb.process(input_left);
        }
        for comb in &mut comb_right {
            tail_right += comb.process(input_right);
        }

        let wet_left = early_left * 0.65 + tail_left * 0.20;
        let wet_right = early_right * 0.65 + tail_right * 0.20;
        left[index] = (input_left * dry + wet_left * amount).clamp(-1.0, 1.0);
        right[index] = (input_right * dry + wet_right * amount).clamp(-1.0, 1.0);
    }
}

struct TapLine {
    buffer: Vec<f32>,
    index: usize,
}

impl TapLine {
    fn new(delay_samples: usize) -> Self {
        Self {
            buffer: vec![0.0; delay_samples.max(1)],
            index: 0,
        }
    }

    fn process(&mut self, input: f32) -> f32 {
        let output = self.buffer[self.index];
        self.buffer[self.index] = input;
        self.index = (self.index + 1) % self.buffer.len();
        output
    }
}

struct CombLine {
    buffer: Vec<f32>,
    index: usize,
    store: f32,
}

impl CombLine {
    fn new(delay_samples: usize) -> Self {
        Self {
            buffer: vec![0.0; delay_samples.max(1)],
            index: 0,
            store: 0.0,
        }
    }

    fn process(&mut self, input: f32) -> f32 {
        let output = self.buffer[self.index];
        self.store = output * 0.7 + self.store * 0.3;
        self.buffer[self.index] = (input + self.store * 0.55).clamp(-2.0, 2.0);
        self.index = (self.index + 1) % self.buffer.len();
        output
    }
}

/// Extracts a normalized pan (`Y/W`) and front/back (`X/W`) pair.
fn directional_components(weights: &[f32]) -> (f32, f32, f32) {
    let w = weights.first().copied().unwrap_or(0.0).abs().max(1e-6);
    let pan = weights.get(1).copied().unwrap_or(0.0) / w;
    let front = weights.get(3).copied().unwrap_or(0.0) / w;
    (w, pan.clamp(-1.0, 1.0), front.clamp(-1.0, 1.0))
}

fn output_extension(path: &Path) -> String {
    path.extension()
        .and_then(|extension| extension.to_str())
        .unwrap_or("")
        .to_ascii_lowercase()
}

fn temp_output_path(output: &Path) -> PathBuf {
    let parent = output.parent().unwrap_or_else(|| Path::new("."));
    let stem = output
        .file_stem()
        .map(|value| value.to_string_lossy().into_owned())
        .unwrap_or_else(|| "hoa".to_string());
    let extension = output
        .extension()
        .map(|value| value.to_string_lossy().into_owned())
        .unwrap_or_else(|| "tmp".to_string());
    parent.join(format!("{}.tmp.{}.{}", stem, std::process::id(), extension))
}

fn write_atomic<F>(output: &Path, writer: F) -> Result<(), i32>
where
    F: FnOnce(&Path) -> Result<(), i32>,
{
    let temp = temp_output_path(output);
    let _ = fs::remove_file(&temp);

    if let Err(status) = writer(&temp) {
        let _ = fs::remove_file(&temp);
        return Err(status);
    }

    if fs::rename(&temp, output).is_err() {
        let _ = fs::remove_file(&temp);
        return Err(HOA_RENDER_ERR_IO);
    }

    Ok(())
}

fn write_wav_pcm(
    path: &Path,
    sample_rate: u32,
    channels: u16,
    bits_per_sample: u16,
    samples: &[f32],
) -> Result<(), i32> {
    if channels == 0 || (bits_per_sample != 16 && bits_per_sample != 24) {
        return Err(HOA_RENDER_ERR_UNSUPPORTED_FORMAT);
    }

    let bytes_per_sample = (bits_per_sample / 8) as usize;
    let frame_count = samples.len() / channels as usize;
    let data_size_u64 = frame_count as u64 * channels as u64 * bytes_per_sample as u64;
    if data_size_u64 > u32::MAX as u64 {
        return Err(HOA_RENDER_ERR_IO);
    }

    let data_size = data_size_u64 as u32;
    let byte_rate = sample_rate
        .saturating_mul(channels as u32)
        .saturating_mul(bytes_per_sample as u32);

    let file = File::create(path).map_err(|_| HOA_RENDER_ERR_IO)?;
    let mut writer = BufWriter::new(file);

    write_all(&mut writer, b"RIFF")?;
    write_u32(&mut writer, 36 + data_size)?;
    write_all(&mut writer, b"WAVE")?;
    write_all(&mut writer, b"fmt ")?;
    write_u32(&mut writer, 16)?;
    write_u16(&mut writer, 1)?;
    write_u16(&mut writer, channels)?;
    write_u32(&mut writer, sample_rate)?;
    write_u32(&mut writer, byte_rate)?;
    write_u16(&mut writer, channels * bytes_per_sample as u16)?;
    write_u16(&mut writer, bits_per_sample)?;
    write_all(&mut writer, b"data")?;
    write_u32(&mut writer, data_size)?;

    for &sample in samples {
        let value = if sample.is_finite() {
            sample.clamp(-1.0, 1.0)
        } else {
            0.0
        };

        if bits_per_sample == 16 {
            let integer = (value * 32_767.0).round() as i16;
            writer
                .write_all(&integer.to_le_bytes())
                .map_err(|_| HOA_RENDER_ERR_IO)?;
        } else {
            let integer = (value * 8_388_607.0).round() as i32;
            let bytes = integer.to_le_bytes();
            writer
                .write_all(&bytes[0..3])
                .map_err(|_| HOA_RENDER_ERR_IO)?;
        }
    }

    writer.flush().map_err(|_| HOA_RENDER_ERR_IO)?;
    Ok(())
}

fn write_w64_pcm(path: &Path, sample_rate: u32, channels: u16, samples: &[f32]) -> Result<(), i32> {
    if channels == 0 {
        return Err(HOA_RENDER_ERR_UNSUPPORTED_FORMAT);
    }

    let bytes_per_sample = 3_u64;
    let frame_count = samples.len() / channels as usize;
    let data_size = frame_count as u64 * channels as u64 * bytes_per_sample;
    let padding = (8 - (data_size % 8)) % 8;
    let total_size = 104 + data_size + padding;
    let byte_rate = sample_rate
        .saturating_mul(channels as u32)
        .saturating_mul(3);

    let file = File::create(path).map_err(|_| HOA_RENDER_ERR_IO)?;
    let mut writer = BufWriter::new(file);

    writer
        .write_all(&W64_RIFF_GUID)
        .map_err(|_| HOA_RENDER_ERR_IO)?;
    write_u64(&mut writer, total_size)?;
    writer
        .write_all(&W64_WAVE_GUID)
        .map_err(|_| HOA_RENDER_ERR_IO)?;

    writer
        .write_all(&W64_FMT_GUID)
        .map_err(|_| HOA_RENDER_ERR_IO)?;
    write_u64(&mut writer, 40)?;
    write_u16(&mut writer, 1)?;
    write_u16(&mut writer, channels)?;
    write_u32(&mut writer, sample_rate)?;
    write_u32(&mut writer, byte_rate)?;
    write_u16(&mut writer, channels * 3)?;
    write_u16(&mut writer, 24)?;

    writer
        .write_all(&W64_DATA_GUID)
        .map_err(|_| HOA_RENDER_ERR_IO)?;
    write_u64(&mut writer, 24 + data_size)?;

    for &sample in samples {
        let value = if sample.is_finite() {
            sample.clamp(-1.0, 1.0)
        } else {
            0.0
        };
        let integer = (value * 8_388_607.0).round() as i32;
        let bytes = integer.to_le_bytes();
        writer
            .write_all(&bytes[0..3])
            .map_err(|_| HOA_RENDER_ERR_IO)?;
    }

    for _ in 0..padding {
        writer.write_all(&[0]).map_err(|_| HOA_RENDER_ERR_IO)?;
    }

    writer.flush().map_err(|_| HOA_RENDER_ERR_IO)?;
    Ok(())
}

fn write_flac(output: &Path, sample_rate: u32, channels: u16, samples: &[f32]) -> Result<(), i32> {
    let parent = output.parent().unwrap_or_else(|| Path::new("."));
    let stem = output
        .file_stem()
        .map(|value| value.to_string_lossy().into_owned())
        .unwrap_or_else(|| "hoa".to_string());
    let temp_wav = parent.join(format!("{}.tmp.{}.source.wav", stem, std::process::id()));
    let temp_flac = temp_output_path(output);
    let _ = fs::remove_file(&temp_wav);
    let _ = fs::remove_file(&temp_flac);

    if let Err(status) = write_wav_pcm(&temp_wav, sample_rate, channels, 24, samples) {
        let _ = fs::remove_file(&temp_wav);
        return Err(status);
    }

    let mut command = Command::new("ffmpeg");
    command
        .arg("-y")
        .arg("-hide_banner")
        .arg("-loglevel")
        .arg("error")
        .arg("-i")
        .arg(&temp_wav)
        .arg("-c:a")
        .arg("flac")
        .arg("-compression_level")
        .arg("8")
        .arg(&temp_flac)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());

    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(CREATE_NO_WINDOW);
    }

    let status = command.status();
    let result = match status {
        Ok(exit) if exit.success() && temp_flac.is_file() => {
            fs::rename(&temp_flac, output).map_err(|_| HOA_RENDER_ERR_IO)
        }
        _ => Err(HOA_RENDER_ERR_FFMPEG),
    };

    let _ = fs::remove_file(&temp_wav);
    if result.is_err() {
        let _ = fs::remove_file(&temp_flac);
    }

    result
}

fn read_audio(path: &Path) -> Result<PcmAudio, i32> {
    let bytes = fs::read(path).map_err(|_| HOA_RENDER_ERR_IO)?;
    if bytes.len() < 12 {
        return Err(HOA_RENDER_ERR_UNSUPPORTED_FORMAT);
    }

    if &bytes[0..4] == b"RIFF" && &bytes[8..12] == b"WAVE" {
        parse_riff_wav(&bytes)
    } else if bytes.len() >= 40 && bytes[0..16] == W64_RIFF_GUID {
        parse_wave64(&bytes)
    } else {
        Err(HOA_RENDER_ERR_UNSUPPORTED_FORMAT)
    }
}

fn parse_riff_wav(bytes: &[u8]) -> Result<PcmAudio, i32> {
    let mut format = None;
    let mut data = None;
    let mut offset = 12_usize;

    while offset + 8 <= bytes.len() {
        let id = &bytes[offset..offset + 4];
        let size = u32::from_le_bytes([
            bytes[offset + 4],
            bytes[offset + 5],
            bytes[offset + 6],
            bytes[offset + 7],
        ]) as usize;
        let payload_start = offset + 8;
        let payload_end = payload_start.saturating_add(size).min(bytes.len());

        if id == b"fmt " {
            format = Some(parse_format(&bytes[payload_start..payload_end])?);
        } else if id == b"data" {
            data = Some(&bytes[payload_start..payload_end]);
        }

        offset = payload_start.saturating_add(size);
        if size % 2 == 1 {
            offset = offset.saturating_add(1);
        }
    }

    let format = format.ok_or(HOA_RENDER_ERR_UNSUPPORTED_FORMAT)?;
    let data = data.ok_or(HOA_RENDER_ERR_UNSUPPORTED_FORMAT)?;
    decode_pcm(&format, data)
}

fn parse_wave64(bytes: &[u8]) -> Result<PcmAudio, i32> {
    let mut format = None;
    let mut data = None;
    let mut offset = 40_usize;

    while offset + 24 <= bytes.len() {
        let guid = &bytes[offset..offset + 16];
        let size = u64::from_le_bytes([
            bytes[offset + 16],
            bytes[offset + 17],
            bytes[offset + 18],
            bytes[offset + 19],
            bytes[offset + 20],
            bytes[offset + 21],
            bytes[offset + 22],
            bytes[offset + 23],
        ]);
        if size < 24 {
            break;
        }

        let payload_size = (size - 24) as usize;
        let payload_start = offset + 24;
        let payload_end = payload_start.saturating_add(payload_size).min(bytes.len());

        if guid == W64_FMT_GUID {
            format = Some(parse_format(&bytes[payload_start..payload_end])?);
        } else if guid == W64_DATA_GUID {
            data = Some(&bytes[payload_start..payload_end]);
        }

        offset = payload_start.saturating_add(payload_size);
        offset = (offset + 7) & !7_usize;
    }

    let format = format.ok_or(HOA_RENDER_ERR_UNSUPPORTED_FORMAT)?;
    let data = data.ok_or(HOA_RENDER_ERR_UNSUPPORTED_FORMAT)?;
    decode_pcm(&format, data)
}

fn parse_format(payload: &[u8]) -> Result<WavFormat, i32> {
    if payload.len() < 16 {
        return Err(HOA_RENDER_ERR_UNSUPPORTED_FORMAT);
    }

    let mut format_tag = u16::from_le_bytes([payload[0], payload[1]]);
    let channels = u16::from_le_bytes([payload[2], payload[3]]);
    let sample_rate = u32::from_le_bytes([payload[4], payload[5], payload[6], payload[7]]);
    let bits_per_sample = u16::from_le_bytes([payload[14], payload[15]]);

    // WAVE_FORMAT_EXTENSIBLE stores the real format tag in the subformat GUID.
    if format_tag == 0xFFFE && payload.len() >= 40 {
        format_tag = u16::from_le_bytes([payload[24], payload[25]]);
    }

    if channels == 0 || sample_rate == 0 {
        return Err(HOA_RENDER_ERR_UNSUPPORTED_FORMAT);
    }

    Ok(WavFormat {
        format_tag,
        channels,
        sample_rate,
        bits_per_sample,
    })
}

fn decode_pcm(format: &WavFormat, data: &[u8]) -> Result<PcmAudio, i32> {
    let channels = format.channels as usize;
    let bytes_per_sample = (format.bits_per_sample / 8) as usize;
    if channels == 0 || bytes_per_sample == 0 {
        return Err(HOA_RENDER_ERR_UNSUPPORTED_FORMAT);
    }

    let frame_bytes = channels * bytes_per_sample;
    if frame_bytes == 0 {
        return Err(HOA_RENDER_ERR_UNSUPPORTED_FORMAT);
    }

    let frames = data.len() / frame_bytes;
    let mut output: Vec<Vec<f32>> = (0..channels).map(|_| Vec::with_capacity(frames)).collect();

    for frame in 0..frames {
        let base = frame * frame_bytes;
        for (channel, channel_output) in output.iter_mut().enumerate() {
            let offset = base + channel * bytes_per_sample;
            let sample = match (format.format_tag, format.bits_per_sample) {
                (1, 8) => (data[offset] as f32 - 128.0) / 128.0,
                (1, 16) => i16::from_le_bytes([data[offset], data[offset + 1]]) as f32 / 32_768.0,
                (1, 24) => {
                    let raw =
                        i32::from_le_bytes([0, data[offset], data[offset + 1], data[offset + 2]])
                            >> 8;
                    raw as f32 / 8_388_608.0
                }
                (1, 32) => {
                    i32::from_le_bytes([
                        data[offset],
                        data[offset + 1],
                        data[offset + 2],
                        data[offset + 3],
                    ]) as f32
                        / 2_147_483_648.0
                }
                (3, 32) => f32::from_le_bytes([
                    data[offset],
                    data[offset + 1],
                    data[offset + 2],
                    data[offset + 3],
                ]),
                (3, 64) => f64::from_le_bytes([
                    data[offset],
                    data[offset + 1],
                    data[offset + 2],
                    data[offset + 3],
                    data[offset + 4],
                    data[offset + 5],
                    data[offset + 6],
                    data[offset + 7],
                ]) as f32,
                _ => return Err(HOA_RENDER_ERR_UNSUPPORTED_FORMAT),
            };

            channel_output.push(sample);
        }
    }

    Ok(PcmAudio {
        sample_rate: format.sample_rate,
        channels: output,
    })
}

fn write_all<W: Write>(writer: &mut W, bytes: &[u8]) -> Result<(), i32> {
    writer.write_all(bytes).map_err(|_| HOA_RENDER_ERR_IO)
}

fn write_u16<W: Write>(writer: &mut W, value: u16) -> Result<(), i32> {
    write_all(writer, &value.to_le_bytes())
}

fn write_u32<W: Write>(writer: &mut W, value: u32) -> Result<(), i32> {
    write_all(writer, &value.to_le_bytes())
}

fn write_u64<W: Write>(writer: &mut W, value: u64) -> Result<(), i32> {
    write_all(writer, &value.to_le_bytes())
}

/// Converts a C string pointer into an owned [`PathBuf`].
///
/// # Safety
/// A non-null `ptr` must point to a valid NUL-terminated C string.
unsafe fn path_from_c(ptr: *const c_char) -> Result<PathBuf, i32> {
    if ptr.is_null() {
        return Err(HOA_RENDER_ERR_INVALID_ARGUMENT);
    }

    let c_str = unsafe { CStr::from_ptr(ptr) };
    let value = c_str
        .to_str()
        .map_err(|_| HOA_RENDER_ERR_INVALID_ARGUMENT)?;

    if value.is_empty() {
        return Err(HOA_RENDER_ERR_INVALID_ARGUMENT);
    }

    Ok(PathBuf::from(value))
}

fn run_render_ffi<F>(body: F) -> i32
where
    F: FnOnce() -> i32,
{
    catch_unwind(AssertUnwindSafe(body)).unwrap_or(HOA_RENDER_ERR_PANIC)
}

/// Renders an HOA AmbiX master.
///
/// # C-ABI Signature
/// ```c
/// int render_hoa_master(
///     const char* input_path,
///     const char* output_path,
///     unsigned int order,       // 1, 2, or 3
///     float azimuth_deg,
///     float elevation_deg,
///     float distance,
///     float spread
/// );
/// ```
///
/// `output_path` must end in `.wav`, `.w64`, or `.flac`. FLAC is limited to
/// 8 channels by the format, so 2nd/3rd order masters must use WAV or W64.
///
/// # Safety
/// Both pointers must be null or valid NUL-terminated UTF-8 C strings.
#[no_mangle]
#[allow(clippy::not_unsafe_ptr_arg_deref)]
pub extern "C" fn render_hoa_master(
    input_path: *const c_char,
    output_path: *const c_char,
    order: u32,
    azimuth_deg: f32,
    elevation_deg: f32,
    distance: f32,
    spread: f32,
) -> i32 {
    run_render_ffi(|| {
        let input = match unsafe { path_from_c(input_path) } {
            Ok(path) => path,
            Err(status) => return status,
        };
        let output = match unsafe { path_from_c(output_path) } {
            Ok(path) => path,
            Err(status) => return status,
        };

        let options = HoaRenderOptions {
            order,
            azimuth_deg,
            elevation_deg,
            distance,
            spread,
            ..HoaRenderOptions::default()
        };

        match render_hoa_amix_master(&input, &output, &options) {
            Ok(()) => HOA_RENDER_OK,
            Err(status) => status,
        }
    })
}

/// Extended AmbiX master render with distance, air, and monitoring options.
///
/// # C-ABI Signature
/// ```c
/// int render_hoa_master_ex(
///     const HoaRenderOptions* options,
///     const char* input_path,
///     const char* output_path
/// );
/// ```
///
/// # Safety
/// `options` must point to a readable `HoaRenderOptions`, and the paths must be
/// null or valid NUL-terminated UTF-8 C strings.
#[no_mangle]
#[allow(clippy::not_unsafe_ptr_arg_deref)]
pub extern "C" fn render_hoa_master_ex(
    options: *const HoaRenderOptions,
    input_path: *const c_char,
    output_path: *const c_char,
) -> i32 {
    run_render_ffi(|| {
        if options.is_null() {
            return HOA_RENDER_ERR_INVALID_ARGUMENT;
        }

        let input = match unsafe { path_from_c(input_path) } {
            Ok(path) => path,
            Err(status) => return status,
        };
        let output = match unsafe { path_from_c(output_path) } {
            Ok(path) => path,
            Err(status) => return status,
        };

        let options = unsafe { *options };
        match render_hoa_amix_master(&input, &output, &options) {
            Ok(()) => HOA_RENDER_OK,
            Err(status) => status,
        }
    })
}

/// Renders a 2-channel stereo (`1`) or binaural (`0`) HOA preview.
///
/// # C-ABI Signature
/// ```c
/// int render_hoa_preview(
///     const char* input_path,
///     const char* output_path,   // must be .wav
///     unsigned int order,
///     float azimuth_deg,
///     float elevation_deg,
///     float distance,
///     float spread,
///     int preview_mode            // 0 = binaural, 1 = stereo
/// );
/// ```
///
/// # Safety
/// Both pointers must be null or valid NUL-terminated UTF-8 C strings.
#[no_mangle]
#[allow(clippy::not_unsafe_ptr_arg_deref)]
pub extern "C" fn render_hoa_preview(
    input_path: *const c_char,
    output_path: *const c_char,
    order: u32,
    azimuth_deg: f32,
    elevation_deg: f32,
    distance: f32,
    spread: f32,
    preview_mode: i32,
) -> i32 {
    run_render_ffi(|| {
        let input = match unsafe { path_from_c(input_path) } {
            Ok(path) => path,
            Err(status) => return status,
        };
        let output = match unsafe { path_from_c(output_path) } {
            Ok(path) => path,
            Err(status) => return status,
        };

        let options = HoaRenderOptions {
            order,
            azimuth_deg,
            elevation_deg,
            distance,
            spread,
            preview_mode,
            ..HoaRenderOptions::default()
        };

        match render_hoa_preview_file(&input, &output, &options) {
            Ok(()) => HOA_RENDER_OK,
            Err(status) => status,
        }
    })
}

/// Extended binaural/stereo preview with head-model, air, and room options.
///
/// # C-ABI Signature
/// ```c
/// int render_hoa_preview_ex(
///     const HoaRenderOptions* options,
///     const char* input_path,
///     const char* output_path
/// );
/// ```
///
/// # Safety
/// `options` must point to a readable `HoaRenderOptions`, and the paths must be
/// null or valid NUL-terminated UTF-8 C strings.
#[no_mangle]
#[allow(clippy::not_unsafe_ptr_arg_deref)]
pub extern "C" fn render_hoa_preview_ex(
    options: *const HoaRenderOptions,
    input_path: *const c_char,
    output_path: *const c_char,
) -> i32 {
    run_render_ffi(|| {
        if options.is_null() {
            return HOA_RENDER_ERR_INVALID_ARGUMENT;
        }

        let input = match unsafe { path_from_c(input_path) } {
            Ok(path) => path,
            Err(status) => return status,
        };
        let output = match unsafe { path_from_c(output_path) } {
            Ok(path) => path,
            Err(status) => return status,
        };

        let options = unsafe { *options };
        match render_hoa_preview_file(&input, &output, &options) {
            Ok(()) => HOA_RENDER_OK,
            Err(status) => status,
        }
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    struct Fixture {
        dir: PathBuf,
    }

    impl Fixture {
        fn new(tag: &str) -> Self {
            let dir = std::env::temp_dir().join(format!(
                "vegas_surround71_hoa_{}_{}",
                std::process::id(),
                tag
            ));
            let _ = fs::remove_dir_all(&dir);
            fs::create_dir_all(&dir).expect("create HOA fixture directory");
            Self { dir }
        }

        fn path(&self, name: &str) -> PathBuf {
            self.dir.join(name)
        }
    }

    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.dir);
        }
    }

    fn sine_at(frames: usize, sample_rate: u32, frequency: f32) -> Vec<f32> {
        (0..frames)
            .map(|index| {
                let phase =
                    2.0 * std::f32::consts::PI * frequency * index as f32 / sample_rate as f32;
                0.5 * phase.sin()
            })
            .collect()
    }

    fn sine_mono(frames: usize, sample_rate: u32) -> Vec<f32> {
        sine_at(frames, sample_rate, 440.0)
    }

    fn write_test_stem(fixture: &Fixture, mono: &[f32]) -> PathBuf {
        let path = fixture.path("stem.wav");
        write_wav_pcm(&path, 48_000, 1, 16, mono).expect("write test stem");
        path
    }

    fn rms(samples: &[f32]) -> f32 {
        if samples.is_empty() {
            return 0.0;
        }

        let energy: f32 = samples.iter().map(|sample| sample * sample).sum();
        (energy / samples.len() as f32).sqrt()
    }

    fn options(azimuth: f32, elevation: f32, order: u32) -> HoaRenderOptions {
        HoaRenderOptions {
            order,
            azimuth_deg: azimuth,
            elevation_deg: elevation,
            ..HoaRenderOptions::default()
        }
    }

    fn preview_options(
        azimuth: f32,
        elevation: f32,
        order: u32,
        preview_mode: i32,
    ) -> HoaRenderOptions {
        HoaRenderOptions {
            preview_mode,
            ..options(azimuth, elevation, order)
        }
    }

    #[test]
    fn test_master_channel_counts_wav() {
        let fixture = Fixture::new("counts_wav");
        let mono = sine_mono(2048, 48_000);
        let stem = write_test_stem(&fixture, &mono);

        for order in 1..=3_u32 {
            let output = fixture.path(&format!("master_order_{}.wav", order));
            render_hoa_amix_master(&stem, &output, &options(15.0, 10.0, order))
                .expect("render HOA master");
            let audio = read_audio(&output).expect("read HOA master");
            assert_eq!(audio.channels.len(), channel_count(order).unwrap());
            assert_eq!(audio.sample_rate, 48_000);
            assert_eq!(audio.frame_count(), mono.len());
        }
    }

    #[test]
    fn test_master_channel_counts_w64() {
        let fixture = Fixture::new("counts_w64");
        let mono = sine_mono(1024, 48_000);
        let stem = write_test_stem(&fixture, &mono);
        let output = fixture.path("master_order_2.w64");

        render_hoa_amix_master(&stem, &output, &options(0.0, 0.0, 2))
            .expect("render W64 HOA master");
        let audio = read_audio(&output).expect("read W64 HOA master");
        assert_eq!(audio.channels.len(), 9);
        assert_eq!(audio.sample_rate, 48_000);
        assert_eq!(audio.frame_count(), mono.len());
    }

    #[test]
    fn test_front_center_directional_energy() {
        let fixture = Fixture::new("front");
        let mono = sine_mono(4096, 48_000);
        let stem = write_test_stem(&fixture, &mono);
        let output = fixture.path("front_center.wav");

        render_hoa_amix_master(&stem, &output, &options(0.0, 0.0, 1)).expect("render front-center");
        let audio = read_audio(&output).expect("read front-center");
        let w = rms(&audio.channels[0]);
        let y = rms(&audio.channels[1]);
        let z = rms(&audio.channels[2]);
        let x = rms(&audio.channels[3]);

        assert!(w > 0.2, "W energy missing: {}", w);
        assert!(x > 0.2, "X energy missing: {}", x);
        assert!(y < 0.01, "Y should be silent: {}", y);
        assert!(z < 0.01, "Z should be silent: {}", z);
    }

    #[test]
    fn test_overhead_directional_energy() {
        let fixture = Fixture::new("overhead");
        let mono = sine_mono(4096, 48_000);
        let stem = write_test_stem(&fixture, &mono);
        let output = fixture.path("overhead.wav");

        render_hoa_amix_master(&stem, &output, &options(0.0, 90.0, 1)).expect("render overhead");
        let audio = read_audio(&output).expect("read overhead");
        let w = rms(&audio.channels[0]);
        let y = rms(&audio.channels[1]);
        let z = rms(&audio.channels[2]);
        let x = rms(&audio.channels[3]);

        assert!(w > 0.2, "W energy missing: {}", w);
        assert!(z > 0.2, "Z energy missing: {}", z);
        assert!(x < 0.01, "X should be silent: {}", x);
        assert!(y < 0.01, "Y should be silent: {}", y);
    }

    fn preview_rms_at(fixture: &Fixture, azimuth: f32, elevation: f32, mode: i32) -> (f32, f32) {
        let mono = sine_mono(4096, 48_000);
        let stem = write_test_stem(fixture, &mono);
        let output = fixture.path(&format!("preview_{}_{}_{}.wav", azimuth, elevation, mode));
        render_hoa_preview_file(
            &stem,
            &output,
            &preview_options(azimuth, elevation, 1, mode),
        )
        .expect("render preview");
        let audio = read_audio(&output).expect("read preview");
        assert_eq!(audio.channels.len(), 2);
        assert_eq!(audio.sample_rate, 48_000);
        (rms(&audio.channels[0]), rms(&audio.channels[1]))
    }

    fn preview_rms(fixture: &Fixture, azimuth: f32, mode: i32) -> (f32, f32) {
        preview_rms_at(fixture, azimuth, 0.0, mode)
    }

    fn render_preview_for(
        fixture: &Fixture,
        stem: &Path,
        name: &str,
        options: &HoaRenderOptions,
    ) -> (Vec<f32>, Vec<f32>) {
        let output = fixture.path(name);
        render_hoa_preview_file(stem, &output, options).expect("render preview");
        let audio = read_audio(&output).expect("read preview");
        assert_eq!(audio.channels.len(), 2);
        (audio.channels[0].clone(), audio.channels[1].clone())
    }

    #[test]
    fn test_render_options_layout() {
        assert_eq!(std::mem::size_of::<HoaRenderOptions>(), 48);
        assert_eq!(std::mem::align_of::<HoaRenderOptions>(), 4);
    }

    #[test]
    fn test_distance_models_scale_master() {
        let fixture = Fixture::new("distance_models");
        let mono = sine_mono(2048, 48_000);
        let stem = write_test_stem(&fixture, &mono);
        let mut levels = [0.0_f32; 4];

        for (index, model) in [
            HOA_DISTANCE_INVERSE,
            HOA_DISTANCE_INVERSE_SQUARE,
            HOA_DISTANCE_LINEAR,
            HOA_DISTANCE_NONE,
        ]
        .iter()
        .enumerate()
        {
            let options = HoaRenderOptions {
                order: 1,
                distance: 1.0,
                distance_model: *model,
                ..HoaRenderOptions::default()
            };
            let output = fixture.path(&format!("distance_{}.wav", index));
            render_hoa_amix_master(&stem, &output, &options).expect("render distance model");
            let audio = read_audio(&output).expect("read distance model");
            levels[index] = rms(&audio.channels[0]);
        }

        assert!(
            levels[1] < levels[0] * 0.6,
            "inverse-square should be quieter"
        );
        assert!(levels[2] < 1e-4, "linear at distance 1 should be silent");
        assert!(levels[3] > levels[0] * 1.8, "none should restore level");
    }

    #[test]
    fn test_air_absorption_attenuates_high_frequencies() {
        let fixture = Fixture::new("air_absorption");
        let stem = write_test_stem(&fixture, &sine_at(4096, 48_000, 8_000.0));
        let base = HoaRenderOptions {
            order: 1,
            distance: 1.0,
            distance_model: HOA_DISTANCE_NONE,
            preview_mode: HOA_PREVIEW_BYPASS,
            air_absorption: 0.0,
            ..HoaRenderOptions::default()
        };
        let wet = HoaRenderOptions {
            air_absorption: 1.0,
            ..base
        };

        let (dry_left, _) = render_preview_for(&fixture, &stem, "air_dry.wav", &base);
        let (wet_left, _) = render_preview_for(&fixture, &stem, "air_wet.wav", &wet);
        let dry_rms = rms(&dry_left);
        let wet_rms = rms(&wet_left);

        assert!(dry_rms > 0.01);
        assert!(
            wet_rms < dry_rms * 0.8,
            "air absorption should attenuate 8 kHz: dry={} wet={}",
            dry_rms,
            wet_rms
        );
    }

    #[test]
    fn test_binaural_head_models() {
        let fixture = Fixture::new("head_models");
        let stem = write_test_stem(&fixture, &sine_mono(4096, 48_000));
        let base = HoaRenderOptions {
            order: 1,
            azimuth_deg: 90.0,
            preview_mode: HOA_PREVIEW_BINAURAL,
            ..HoaRenderOptions::default()
        };

        let (itd_l, itd_r) = render_preview_for(
            &fixture,
            &stem,
            "head_itd.wav",
            &HoaRenderOptions {
                head_model: HOA_HEAD_ITD_ONLY,
                ..base
            },
        );
        assert!((rms(&itd_l) - rms(&itd_r)).abs() < rms(&itd_r) * 0.15);

        let (ild_l, ild_r) = render_preview_for(
            &fixture,
            &stem,
            "head_ild.wav",
            &HoaRenderOptions {
                head_model: HOA_HEAD_ILD_ONLY,
                ..base
            },
        );
        assert!(rms(&ild_r) > rms(&ild_l) * 1.5);

        let (both_l, both_r) = render_preview_for(
            &fixture,
            &stem,
            "head_both.wav",
            &HoaRenderOptions {
                head_model: HOA_HEAD_BOTH,
                ..base
            },
        );
        assert!(rms(&both_r) > rms(&both_l) * 1.2);

        let (off_l, off_r) = render_preview_for(
            &fixture,
            &stem,
            "head_off.wav",
            &HoaRenderOptions {
                head_model: HOA_HEAD_OFF,
                ..base
            },
        );
        assert!(rms(&off_l) < rms(&off_r) * 0.2);
    }

    #[test]
    fn test_room_preview_changes_output_and_stays_finite() {
        let fixture = Fixture::new("room");
        let stem = write_test_stem(&fixture, &sine_mono(4096, 48_000));
        let dry_options = HoaRenderOptions {
            order: 1,
            preview_mode: HOA_PREVIEW_BINAURAL,
            room_amount: 0.0,
            ..HoaRenderOptions::default()
        };
        let wet_options = HoaRenderOptions {
            room_amount: 0.6,
            room_size: 0.7,
            ..dry_options
        };

        let (dry_left, dry_right) =
            render_preview_for(&fixture, &stem, "room_dry.wav", &dry_options);
        let (wet_left, wet_right) =
            render_preview_for(&fixture, &stem, "room_wet.wav", &wet_options);

        for sample in wet_left.iter().chain(wet_right.iter()) {
            assert!(sample.is_finite());
        }

        let difference: f32 = dry_left
            .iter()
            .zip(wet_left.iter())
            .chain(dry_right.iter().zip(wet_right.iter()))
            .map(|(dry, wet)| (dry - wet).abs())
            .sum();
        assert!(difference > 0.1, "room should change the preview");
        assert!(rms(&wet_left) > 0.01);
        assert!(rms(&wet_right) > 0.01);
    }

    #[test]
    fn test_stereo_preview_lr_balance() {
        let fixture = Fixture::new("stereo_balance");
        let (left_right, right_right) = preview_rms(&fixture, 90.0, 1);
        assert!(right_right > left_right * 2.0);
        let (left_left, right_left) = preview_rms(&fixture, -90.0, 1);
        assert!(left_left > right_left * 2.0);
    }

    #[test]
    fn test_binaural_preview_lr_balance() {
        let fixture = Fixture::new("binaural_balance");
        let (left_right, right_right) = preview_rms(&fixture, 90.0, 0);
        println!("binaural +90: L={:.6} R={:.6}", left_right, right_right);
        assert!(left_right > 0.001, "left ear must not be silent");
        assert!(right_right > 0.001, "right ear must not be silent");
        assert!(right_right > left_right * 1.2);

        let (left_left, right_left) = preview_rms(&fixture, -90.0, 0);
        println!("binaural -90: L={:.6} R={:.6}", left_left, right_left);
        assert!(left_left > 0.001, "left ear must not be silent");
        assert!(right_left > 0.001, "right ear must not be silent");
        assert!(left_left > right_left * 1.2);
    }

    #[test]
    fn test_binaural_overhead_is_centered() {
        let fixture = Fixture::new("binaural_overhead");
        let (left, right) = preview_rms_at(&fixture, 91.3, 90.0, 0);
        println!("binaural overhead: L={:.6} R={:.6}", left, right);
        assert!(left > 0.001);
        assert!(right > 0.001);
        assert!(
            (left - right).abs() < 1e-4,
            "overhead should be centered: L={} R={}",
            left,
            right
        );
    }

    #[test]
    fn test_preview_rejects_non_wav_extension() {
        let fixture = Fixture::new("preview_ext");
        let stem = write_test_stem(&fixture, &sine_mono(256, 48_000));
        let output = fixture.path("preview.flac");
        assert_eq!(
            render_hoa_preview_file(&stem, &output, &preview_options(0.0, 0.0, 1, 0)),
            Err(HOA_RENDER_ERR_UNSUPPORTED_FORMAT)
        );
    }

    #[test]
    fn test_master_rejects_unsupported_order_and_extension() {
        let fixture = Fixture::new("master_errors");
        let stem = write_test_stem(&fixture, &sine_mono(256, 48_000));

        assert_eq!(
            render_hoa_amix_master(&stem, &fixture.path("bad.wav"), &options(0.0, 0.0, 4)),
            Err(HOA_RENDER_ERR_INVALID_ORDER)
        );
        assert_eq!(
            render_hoa_amix_master(&stem, &fixture.path("bad.mp3"), &options(0.0, 0.0, 1)),
            Err(HOA_RENDER_ERR_UNSUPPORTED_FORMAT)
        );
    }

    #[test]
    fn test_flac_master_first_order_when_ffmpeg_available() {
        let ffmpeg_available = Command::new("ffmpeg")
            .arg("-version")
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status()
            .map(|status| status.success())
            .unwrap_or(false);

        if !ffmpeg_available {
            eprintln!("SKIP: ffmpeg is not available on PATH");
            return;
        }

        let fixture = Fixture::new("flac_first");
        let stem = write_test_stem(&fixture, &sine_mono(2048, 48_000));
        let output = fixture.path("master_1st.flac");
        render_hoa_amix_master(&stem, &output, &options(20.0, 15.0, 1))
            .expect("render first-order FLAC");

        assert!(output.is_file());
        let bytes = fs::read(&output).expect("read FLAC header");
        assert!(bytes.len() > 4);
        assert_eq!(&bytes[0..4], b"fLaC");
    }

    #[test]
    fn test_master_rejects_flac_above_eight_channels() {
        let fixture = Fixture::new("flac_channels");
        let stem = write_test_stem(&fixture, &sine_mono(256, 48_000));
        assert_eq!(
            render_hoa_amix_master(&stem, &fixture.path("bad.flac"), &options(0.0, 0.0, 2)),
            Err(HOA_RENDER_ERR_UNSUPPORTED_FORMAT)
        );
    }

    #[test]
    fn test_c_abi_null_and_invalid_order() {
        let fixture = Fixture::new("ffi_errors");
        let stem = write_test_stem(&fixture, &sine_mono(256, 48_000));
        let stem_c = std::ffi::CString::new(stem.to_string_lossy().into_owned())
            .expect("stem path has no NUL");
        let output = fixture.path("ffi.wav");
        let output_c = std::ffi::CString::new(output.to_string_lossy().into_owned())
            .expect("output path has no NUL");

        assert_eq!(
            render_hoa_master(std::ptr::null(), output_c.as_ptr(), 1, 0.0, 0.0, 0.0, 0.0),
            HOA_RENDER_ERR_INVALID_ARGUMENT
        );
        assert_eq!(
            render_hoa_master(stem_c.as_ptr(), std::ptr::null(), 1, 0.0, 0.0, 0.0, 0.0),
            HOA_RENDER_ERR_INVALID_ARGUMENT
        );
        assert_eq!(
            render_hoa_master(stem_c.as_ptr(), output_c.as_ptr(), 4, 0.0, 0.0, 0.0, 0.0),
            HOA_RENDER_ERR_INVALID_ORDER
        );
        assert_eq!(
            render_hoa_preview(
                std::ptr::null(),
                output_c.as_ptr(),
                1,
                0.0,
                0.0,
                0.0,
                0.0,
                0
            ),
            HOA_RENDER_ERR_INVALID_ARGUMENT
        );
    }

    #[test]
    fn test_master_output_is_published_without_temp_file() {
        let fixture = Fixture::new("atomic");
        let stem = write_test_stem(&fixture, &sine_mono(512, 48_000));
        let output = fixture.path("atomic.wav");
        render_hoa_amix_master(&stem, &output, &options(0.0, 0.0, 1))
            .expect("render atomic output");

        assert!(output.is_file());
        let entries = fs::read_dir(&fixture.dir).expect("read fixture dir");
        for entry in entries {
            let path = entry.expect("dir entry").path();
            let name = path.file_name().unwrap().to_string_lossy();
            assert!(
                !name.contains(".tmp."),
                "temporary file was not cleaned up: {}",
                name
            );
        }
    }
}
