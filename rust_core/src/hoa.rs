//! Higher-Order Ambisonics (HOA) source encoding weights.
//!
//! The 3D spatial workspace uses this module to turn a spherical source
//! position into real spherical-harmonic (SH) weights. The channel order is
//! **ACN** and the normalization is **SN3D**, which is the layout used by AmbiX
//! (`ambiX` WAV/FLAC) files.
//!
//! Supported orders:
//! - 1st order: 4 channels  (W, Y, Z, X)
//! - 2nd order: 9 channels  (+ R, S, T, U, V)
//! - 3rd order: 16 channels (+ K, L, M, N, O, P, Q)
//!
//! Conventions:
//! - `azimuth_deg` is in `[-180, 180]`, `0` = front, `+90` = right.
//! - `elevation_deg` is in `[-90, 90]`, `+90` = overhead.
//! - `distance` is a normalized `0..1` distance and applies a smooth
//!   `1 / (1 + distance)` attenuation.
//! - `spread` is a `0..1` diffusion control. It cross-fades the directional SH
//!   pattern towards an omnidirectional (W-only) field while preserving the
//!   nominal ambisonic energy.
//!
//! Nothing in this module touches the 7.1 VBAP engine or the `[7.1] FL..SR`
//! bus topology; HOA weights are a separate spatial representation.

use std::f32::consts::PI;
use std::panic::{catch_unwind, AssertUnwindSafe};

/// Lowest supported Ambisonics order.
pub const HOA_MIN_ORDER: u32 = 1;

/// Highest supported Ambisonics order.
pub const HOA_MAX_ORDER: u32 = 3;

/// Success status for [`calculate_hoa_weights`].
pub const HOA_OK: i32 = 0;

/// A required pointer argument was null.
pub const HOA_ERR_NULL_POINTER: i32 = -1;

/// The requested Ambisonics order is outside `1..=3`.
pub const HOA_ERR_INVALID_ORDER: i32 = -2;

/// The caller's output buffer cannot hold `(order + 1)^2` weights.
pub const HOA_ERR_BUFFER_TOO_SMALL: i32 = -3;

/// An internal panic was contained at the FFI boundary.
pub const HOA_ERR_PANIC: i32 = -99;

/// Spherical source position consumed by the C-ABI HOA encoder.
///
/// The layout is fixed (`20` bytes: four `f32` values followed by one `u32`)
/// and mirrors the C# `[StructLayout(LayoutKind.Sequential)]` struct
/// byte-for-byte.
#[repr(C)]
#[derive(Debug, Copy, Clone, PartialEq)]
pub struct HoaSourcePosition {
    /// Horizontal angle in degrees: `0` front, `+90` right, `180` back.
    pub azimuth_deg: f32,
    /// Vertical angle in degrees: `-90` floor, `+90` overhead.
    pub elevation_deg: f32,
    /// Normalized distance in `0..1`.
    pub distance: f32,
    /// Diffusion / source width in `0..1`.
    pub spread: f32,
    /// Ambisonics order: `1`, `2`, or `3`.
    pub order: u32,
}

impl Default for HoaSourcePosition {
    fn default() -> Self {
        Self {
            azimuth_deg: 0.0,
            elevation_deg: 0.0,
            distance: 0.0,
            spread: 0.0,
            order: HOA_MIN_ORDER,
        }
    }
}

/// Returns `true` when `order` is one of the supported HOA orders.
pub fn is_supported_order(order: u32) -> bool {
    (HOA_MIN_ORDER..=HOA_MAX_ORDER).contains(&order)
}

/// Number of ACN channels for `order`, or `None` for an unsupported order.
pub fn channel_count(order: u32) -> Option<usize> {
    if !is_supported_order(order) {
        return None;
    }

    let side = order as usize + 1;
    Some(side * side)
}

/// Converts a spherical direction into a unit vector.
///
/// The result is `(front, right, up)`; front-center `(0°, 0°)` maps to
/// `(1, 0, 0)` and overhead `(0°, +90°)` maps to `(0, 0, 1)`.
fn unit_vector(azimuth_deg: f32, elevation_deg: f32) -> (f32, f32, f32) {
    let azimuth = azimuth_deg * PI / 180.0;
    let elevation = elevation_deg.clamp(-90.0, 90.0) * PI / 180.0;
    let cos_elevation = elevation.cos();
    (
        cos_elevation * azimuth.cos(),
        cos_elevation * azimuth.sin(),
        elevation.sin(),
    )
}

/// Fills the ACN/SN3D weight buffer for one source position.
///
/// The buffer length determines the order: `4 -> 1st`, `9 -> 2nd`, `16 -> 3rd`.
fn fill_weights(weights: &mut [f32], x: f32, y: f32, z: f32) {
    weights[0] = 1.0;

    if weights.len() > 1 {
        // 1st order.
        weights[1] = y;
        weights[2] = z;
        weights[3] = x;
    }

    if weights.len() > 4 {
        // 2nd order.
        let sqrt_3 = 3.0_f32.sqrt();
        weights[4] = sqrt_3 * x * y;
        weights[5] = sqrt_3 * y * z;
        weights[6] = (3.0 * z * z - 1.0) * 0.5;
        weights[7] = sqrt_3 * x * z;
        weights[8] = sqrt_3 * 0.5 * (x * x - y * y);
    }

    if weights.len() > 9 {
        // 3rd order.
        let z2 = z * z;
        let sqrt_5_8 = (5.0_f32 / 8.0).sqrt();
        let sqrt_15 = 15.0_f32.sqrt();
        let sqrt_3_8 = (3.0_f32 / 8.0).sqrt();

        weights[9] = sqrt_5_8 * y * (3.0 * x * x - y * y);
        weights[10] = sqrt_15 * x * y * z;
        weights[11] = sqrt_3_8 * y * (5.0 * z2 - 1.0);
        weights[12] = z * (5.0 * z2 - 3.0) * 0.5;
        weights[13] = sqrt_3_8 * x * (5.0 * z2 - 1.0);
        weights[14] = 0.5 * sqrt_15 * z * (x * x - y * y);
        weights[15] = sqrt_5_8 * x * (x * x - 3.0 * y * y);
    }
}

/// Applies spread diffusion and distance attenuation while preserving the
/// nominal ambisonic energy of `(order + 1)` for a unit source.
fn apply_spread_and_distance(weights: &mut [f32], order: u32, spread: f32, distance: f32) {
    let nominal_energy = (order + 1) as f32;
    let blend = spread.clamp(0.0, 1.0);

    if blend > 0.0 {
        // The diffuse field is W-only with the same total energy as the
        // directional pattern.
        let diffuse_w = nominal_energy.sqrt();
        for (index, weight) in weights.iter_mut().enumerate() {
            *weight *= 1.0 - blend;
            if index == 0 {
                *weight += blend * diffuse_w;
            }
        }
    }

    let attenuation = 1.0 / (1.0 + distance.clamp(0.0, 1.0));
    let target_energy = nominal_energy * attenuation * attenuation;
    let energy: f32 = weights.iter().map(|weight| weight * weight).sum();
    if energy > 1e-12 {
        let scale = (target_energy / energy).sqrt();
        for weight in weights.iter_mut() {
            *weight *= scale;
        }
    }
}

/// Calculates ACN/SN3D Ambisonics weights for a spherical source position.
///
/// Returns a buffer with `channel_count(position.order)` entries, or a
/// `HOA_ERR_*` status when the order is unsupported.
pub fn calculate_weights(position: &HoaSourcePosition) -> Result<Vec<f32>, i32> {
    let count = channel_count(position.order).ok_or(HOA_ERR_INVALID_ORDER)?;
    let (x, y, z) = unit_vector(position.azimuth_deg, position.elevation_deg);

    let mut weights = vec![0.0_f32; count];
    fill_weights(&mut weights, x, y, z);
    apply_spread_and_distance(
        &mut weights,
        position.order,
        position.spread,
        position.distance,
    );
    Ok(weights)
}

/// Returns the ACN channel count for `order`, or `0` for an invalid order.
///
/// # C-ABI Signature
/// ```c
/// int hoa_channel_count(unsigned int order);
/// ```
#[no_mangle]
pub extern "C" fn hoa_channel_count(order: u32) -> i32 {
    channel_count(order).map_or(0, |count| count as i32)
}

/// Encodes one spherical source position into ACN/SN3D Ambisonics weights.
///
/// # C-ABI Signature
/// ```c
/// typedef struct {
///     float azimuth_deg;
///     float elevation_deg;
///     float distance;
///     float spread;
///     unsigned int order;   // 1, 2, or 3
/// } HoaSourcePosition;
///
/// int calculate_hoa_weights(
///     const HoaSourcePosition* position,
///     float* out_weights,
///     unsigned int max_channels
/// );
/// ```
///
/// `out_weights` must have room for `(order + 1)^2` floats. The function
/// returns [`HOA_OK`] on success and a negative `HOA_ERR_*` code otherwise.
/// On a contained panic the output buffer is left untouched and
/// [`HOA_ERR_PANIC`] is returned.
///
/// # Safety
/// `position` must point to a readable [`HoaSourcePosition`] and `out_weights`
/// must point to `max_channels` writable `f32` values. Null pointers are
/// rejected with [`HOA_ERR_NULL_POINTER`].
#[no_mangle]
#[allow(clippy::not_unsafe_ptr_arg_deref)]
pub extern "C" fn calculate_hoa_weights(
    position: *const HoaSourcePosition,
    out_weights: *mut f32,
    max_channels: u32,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        if position.is_null() || out_weights.is_null() {
            return HOA_ERR_NULL_POINTER;
        }

        // SAFETY: the caller guarantees a readable struct pointer; the value
        // is copied immediately and never retained.
        let position = unsafe { *position };
        let count = match channel_count(position.order) {
            Some(count) => count,
            None => return HOA_ERR_INVALID_ORDER,
        };

        if (max_channels as usize) < count {
            return HOA_ERR_BUFFER_TOO_SMALL;
        }

        let weights = match calculate_weights(&position) {
            Ok(weights) => weights,
            Err(status) => return status,
        };

        // SAFETY: the capacity check above guarantees `count` writable slots.
        let out = unsafe { std::slice::from_raw_parts_mut(out_weights, count) };
        out.copy_from_slice(&weights);
        HOA_OK
    }))
    .unwrap_or(HOA_ERR_PANIC)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn energy(weights: &[f32]) -> f32 {
        weights.iter().map(|weight| weight * weight).sum()
    }

    fn position(
        azimuth_deg: f32,
        elevation_deg: f32,
        distance: f32,
        spread: f32,
        order: u32,
    ) -> HoaSourcePosition {
        HoaSourcePosition {
            azimuth_deg,
            elevation_deg,
            distance,
            spread,
            order,
        }
    }

    /// 1st, 2nd, and 3rd order Ambisonics carry 4, 9, and 16 channels.
    #[test]
    fn test_channel_counts_for_supported_orders() {
        assert_eq!(channel_count(1), Some(4));
        assert_eq!(channel_count(2), Some(9));
        assert_eq!(channel_count(3), Some(16));
        assert_eq!(channel_count(0), None);
        assert_eq!(channel_count(4), None);

        assert_eq!(hoa_channel_count(1), 4);
        assert_eq!(hoa_channel_count(2), 9);
        assert_eq!(hoa_channel_count(3), 16);
        assert_eq!(hoa_channel_count(0), 0);
    }

    /// Every supported order normalizes to `order + 1` total energy.
    #[test]
    fn test_energy_normalization_per_order() {
        for order in HOA_MIN_ORDER..=HOA_MAX_ORDER {
            let weights =
                calculate_weights(&position(37.0, 21.0, 0.0, 0.0, order)).expect("supported order");
            assert_eq!(weights.len(), channel_count(order).unwrap());
            assert!(
                (energy(&weights) - (order + 1) as f32).abs() < 1e-4,
                "order {} energy {} should equal {}",
                order,
                energy(&weights),
                order + 1
            );
        }
    }

    /// Front-center `(0°, 0°)` is W = 1, X = 1, Y = Z = 0, with nominal
    /// energy for every order.
    #[test]
    fn test_front_center_normalization() {
        for order in HOA_MIN_ORDER..=HOA_MAX_ORDER {
            let weights =
                calculate_weights(&position(0.0, 0.0, 0.0, 0.0, order)).expect("supported order");

            assert!(
                (weights[0] - 1.0).abs() < 1e-5,
                "order {} W expected 1.0, got {}",
                order,
                weights[0]
            );
            assert!(weights[1].abs() < 1e-5, "Y expected 0");
            assert!(weights[2].abs() < 1e-5, "Z expected 0");
            assert!(
                (weights[3] - 1.0).abs() < 1e-5,
                "order {} X expected 1.0, got {}",
                order,
                weights[3]
            );
            assert!(
                (energy(&weights) - (order + 1) as f32).abs() < 1e-4,
                "order {} energy {}",
                order,
                energy(&weights)
            );
        }
    }

    /// Overhead `(0°, +90°)` is W = 1, Z = 1, X = Y = 0.
    #[test]
    fn test_overhead_normalization() {
        for order in HOA_MIN_ORDER..=HOA_MAX_ORDER {
            let weights =
                calculate_weights(&position(0.0, 90.0, 0.0, 0.0, order)).expect("supported order");

            assert!(
                (weights[0] - 1.0).abs() < 1e-5,
                "order {} W expected 1.0, got {}",
                order,
                weights[0]
            );
            assert!(weights[1].abs() < 1e-5, "Y expected 0");
            assert!(
                (weights[2] - 1.0).abs() < 1e-5,
                "order {} Z expected 1.0, got {}",
                order,
                weights[2]
            );
            assert!(weights[3].abs() < 1e-5, "X expected 0");
            assert!(
                (energy(&weights) - (order + 1) as f32).abs() < 1e-4,
                "order {} energy {}",
                order,
                energy(&weights)
            );
        }
    }

    /// Mirroring the azimuth must preserve magnitudes and flip the odd-m
    /// (Y / sine) channels.
    #[test]
    fn test_azimuth_symmetry() {
        let left = calculate_weights(&position(-37.0, 12.0, 0.0, 0.0, 3)).expect("valid");
        let right = calculate_weights(&position(37.0, 12.0, 0.0, 0.0, 3)).expect("valid");

        assert!((left[0] - right[0]).abs() < 1e-5);
        assert!((left[1] + right[1]).abs() < 1e-5, "Y should flip sign");
        assert!((left[2] - right[2]).abs() < 1e-5);
        assert!((left[3] - right[3]).abs() < 1e-5);

        for (index, (a, b)) in left.iter().zip(right.iter()).enumerate() {
            assert!(
                (a.abs() - b.abs()).abs() < 1e-5,
                "channel {} magnitudes should mirror: {} vs {}",
                index,
                a,
                b
            );
        }
    }

    /// Spread diffuses towards W while keeping the nominal energy.
    #[test]
    fn test_spread_preserves_energy_and_diffuses_to_w() {
        let focused = calculate_weights(&position(25.0, 15.0, 0.0, 0.0, 2)).expect("valid");
        let diffuse = calculate_weights(&position(25.0, 15.0, 0.0, 1.0, 2)).expect("valid");

        assert!((energy(&focused) - 3.0).abs() < 1e-4);
        assert!((energy(&diffuse) - 3.0).abs() < 1e-4);
        assert!(
            (diffuse[0] - 3.0_f32.sqrt()).abs() < 1e-4,
            "fully spread W should be sqrt(3), got {}",
            diffuse[0]
        );
        for channel in &diffuse[1..] {
            assert!(channel.abs() < 1e-5, "diffuse field is W-only");
        }
    }

    /// Distance applies a smooth `1 / (1 + d)` attenuation.
    #[test]
    fn test_distance_attenuation() {
        let near = calculate_weights(&position(0.0, 0.0, 0.0, 0.0, 1)).expect("valid");
        let far = calculate_weights(&position(0.0, 0.0, 1.0, 0.0, 1)).expect("valid");

        assert!((energy(&near) - 2.0).abs() < 1e-4);
        assert!(
            (energy(&far) - 0.5).abs() < 1e-4,
            "distance 1.0 should quarter the energy, got {}",
            energy(&far)
        );
    }

    /// The C-ABI encoder writes exactly `(order + 1)^2` weights.
    #[test]
    fn test_c_abi_calculate_hoa_weights() {
        let source = position(0.0, 0.0, 0.0, 0.0, 3);
        let mut buffer = [0.0_f32; 16];
        let status = calculate_hoa_weights(&source, buffer.as_mut_ptr(), 16);

        assert_eq!(status, HOA_OK);
        assert!((buffer[0] - 1.0).abs() < 1e-5);
        assert!((buffer[3] - 1.0).abs() < 1e-5);
        assert!((energy(&buffer) - 4.0).abs() < 1e-4);
    }

    /// Invalid pointers, orders, and undersized buffers are rejected cleanly.
    #[test]
    fn test_c_abi_rejects_invalid_arguments() {
        let source = position(0.0, 0.0, 0.0, 0.0, 1);
        let mut buffer = [0.0_f32; 4];

        assert_eq!(
            calculate_hoa_weights(std::ptr::null(), buffer.as_mut_ptr(), 4),
            HOA_ERR_NULL_POINTER
        );
        assert_eq!(
            calculate_hoa_weights(&source, std::ptr::null_mut(), 4),
            HOA_ERR_NULL_POINTER
        );

        let invalid_order = position(0.0, 0.0, 0.0, 0.0, 5);
        assert_eq!(
            calculate_hoa_weights(&invalid_order, buffer.as_mut_ptr(), 4),
            HOA_ERR_INVALID_ORDER
        );

        let second_order = position(0.0, 0.0, 0.0, 0.0, 2);
        assert_eq!(
            calculate_hoa_weights(&second_order, buffer.as_mut_ptr(), 4),
            HOA_ERR_BUFFER_TOO_SMALL
        );
    }

    /// A panic inside the FFI body is converted to `HOA_ERR_PANIC`.
    #[test]
    fn test_c_abi_panic_containment() {
        let status = catch_unwind(AssertUnwindSafe(|| panic!("simulated HOA panic")))
            .map(|_| HOA_OK)
            .unwrap_or(HOA_ERR_PANIC);
        assert_eq!(status, HOA_ERR_PANIC);
    }
}
