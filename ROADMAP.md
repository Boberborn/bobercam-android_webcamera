# BobrCam roadmap

## Product direction

BobrCam keeps the Windows PC focused on H.264 hardware decoding, preview, and
virtual-camera output. Camera tuning, beauty effects, masks, and avatars run on
the Android phone before hardware H.264 encoding.

The effect modes are exclusive:

- **Off**: direct Camera2 output to the MediaCodec input surface.
- **Beauty**: phone GPU compositor with face-aware correction.
- **Mask**: phone GPU compositor with a 2D/3D mask.
- **Avatar**: phone GPU compositor with a calibrated face avatar.

Beauty cannot run together with Mask or Avatar. This limits phone heat and
protects PC gaming performance.

## Performance targets

- Recommended PC gaming impact: no more than 3 FPS.
- Hard PC gaming impact limit: 5 FPS on Ryzen 5 3600, RTX 3060, 16 GB RAM.
- No effect inference, landmark tracking, or compositing on the PC.
- Preserve the current hardware H.264 encoder and D3D11VA decoder.
- Keep the direct Camera2-to-MediaCodec path when effects are off.
- Avoid per-frame managed allocations and CPU RGB/YUV conversion.
- Adapt effect resolution and tracking frequency to phone capability and heat.

## Phase 1 — Versioned controls and capabilities

Status: implemented; device validation in progress.

- Version Camera2 control messages.
- Report per-camera flash, exposure, zoom, and white-balance capabilities.
- Report current values after connection and camera switching.
- Add compact Windows controls that enable only supported features.
- Apply changes to the active repeating Camera2 request without restarting the
  H.264 encoder or rebuilding the capture session.

Exit criteria:

- Android and Windows builds succeed.
- USB and Wi-Fi reconnect remain stable.
- Huawei-class legacy phones ignore unsupported controls safely.
- Exposure, zoom, white balance, and flash update during a live stream.

## Phase 2 — Complete native camera controls

- Add tap/autofocus, focus lock, and supported manual focus distance.
- Add stabilization selection: off, optical, or electronic when available.
- Expose AE/AWB lock and reset-to-auto.
- Map resolution and FPS choices to actual Camera2/encoder combinations.
- Use Camera2 edge, noise-reduction, and tonemap modes only when the phone
  exposes useful hardware support.
- Persist controls per physical camera without assuming front/rear parity.

Exit criteria:

- No encoder restart for settings that Camera2 can update live.
- Unsupported settings are hidden or disabled.
- Camera switching reports new limits and safely clamps saved values.

## Phase 3 — Phone GPU compositor with zero-cost bypass

Status: first production slice implemented; live device validation in progress.

- Keep the existing direct camera-to-encoder surfaces for effect mode Off.
- For Beauty, Mask, or Avatar, route Camera2 to an external GPU texture.
- Render the texture with OpenGL ES to both the MediaCodec input surface and
  the local preview surface.
- Use texture/surface synchronization without CPU readback.
- Implement reusable shader stages for crop, rotation, mirroring, color,
  brightness, contrast, saturation, sharpness, and vignette.
- Add a runtime capability test and automatically return to direct mode if GPU
  initialization fails.

Implemented now:

- OpenGL ES external-texture compositor writing directly to MediaCodec.
- Direct Camera2-to-MediaCodec bypass remains unchanged while effects are Off.
- Live switching between Off, Beauty, and Mask without restarting the encoder.
- GPU failure falls back to the direct H.264 path.

Exit criteria:

- No CPU frame conversion or bitmap creation.
- Effect mode Off has no extra render pass.
- Effect mode can enter and leave without breaking auto-reconnection.

## Phase 4 — Face tracking and avatar calibration

- Use a phone-side landmark tracker with GPU acceleration where supported.
- Add a guided scan: look forward, left, right, up, and down.
- Build a normalized personal face mesh and store calibration locally.
- Smooth landmarks temporally while minimizing added latency.
- Detect tracking loss, multiple faces, occlusion, and extreme pose.
- Provide a reduced fallback on old phones: basic face placement at 720p30,
  without detailed geometry changes.

Exit criteria:

- Stable landmarks during normal head movement.
- No face images or calibration data leave the phone.
- Tracking automatically lowers frequency before it drops the video stream.

## Phase 5 — Beauty mode

Status: basic GPU beauty controls implemented; landmark geometry remains.

- Skin smoothing with edge preservation.
- Blemish reduction and optional skin-tone correction.
- Exposure/brightness assistance that prefers Camera2 hardware first.
- Eye enlargement, nose sizing, face slimming, and jaw adjustment using the
  calibrated mesh.
- Natural-strength limits and a one-tap reset.
- Disable Mask and Avatar when Beauty is enabled.

Implemented now:

- Edge-aware smoothing, brightness, warmth, and vignette sliders.
- Settings persist on Windows and are restored after phone reconnection.
- Beauty and Mask are mutually exclusive.

Exit criteria:

- Effects stay aligned during movement.
- No visible background warping around the face.
- Quality tiers: Basic, Balanced, and Quality.
- Old phones automatically use fewer landmarks and simpler shaders.

## Phase 6 — Mask and avatar modes

Status: procedural Bobr mask implemented; avatar and mesh work remains.

- 2D masks anchored to face landmarks.
- Lightweight 3D masks with depth-aware occlusion where supported.
- User image import with local face alignment and texture preparation.
- Apply the prepared face texture to the calibrated mesh.
- Validate image format, dimensions, and local storage permissions.
- Disable Beauty whenever Mask or Avatar is enabled.

Implemented now:

- GPU-rendered Bobr face mask with adjustable strength.
- Camera2 face-rectangle tracking on supported phones.
- Centered mask fallback on legacy phones without face detection.

Exit criteria:

- Imported images remain local unless the user explicitly exports them.
- Mask tracking failure falls back cleanly instead of freezing the stream.
- Assets have explicit licenses and predictable memory limits.

## Phase 7 — Polished UI

Windows:

- Keep the 480p-sized preview and compact window.
- Group controls into Camera, Image, Beauty, Mask, and Avatar panels.
- Show the active phone, connection mode, actual resolution/FPS, dropped
  frames, encoder mode, and thermal/quality warning.
- Keep common controls visible; place advanced controls in collapsible panels.
- Preserve keyboard access, DPI scaling, and the BobrCam warm-color palette.

Android:

- Keep the camera preview dominant.
- Show connection, actual stream mode, active effect mode, and thermal state.
- Provide a simple effect strength control and a clear Off button.
- Add the guided avatar scan and local asset library.
- Never force screen autorotation.

## Phase 8 — Performance and stabilization

- Measure PC game FPS with BobrCam off, preview only, and virtual camera active.
- Measure phone render time, encode time, network jitter, dropped frames, heat,
  and battery drain.
- Dynamically reduce landmark rate, shader quality, effect resolution, then
  video FPS—in that order.
- Test 720p30/60, 1080p30/60, 2K, and 4K only where hardware reports support.
- Test USB ADB and Wi-Fi reconnect, camera switching, screen lock, calls, and
  app suspend/resume.
- Maintain a device matrix covering the Huawei Y6 2018, Redmi Note 4, Realme,
  and at least one recent Android device.

Exit criteria:

- Added Windows control/effect logic stays within the 3 FPS recommended target.
- No configuration exceeds the 5 FPS hard limit in the reference PC test.
- Unsupported phone configurations downgrade with a clear status message.
- OBS and the installed Windows virtual camera remain stable.

## Implementation order

1. Finish live validation of Phase 1 and the new GPU effects slice.
2. Complete native Camera2 controls.
3. Add landmark tracking and avatar calibration.
4. Add mesh-based beauty geometry and imported mask/avatar assets.
5. Finish UI polish.
6. Run stabilization and measured performance gates.

Store packaging and publishing remain separate work and start only when
explicitly requested.
