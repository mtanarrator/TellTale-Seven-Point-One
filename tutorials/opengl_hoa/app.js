/* global window, document, requestAnimationFrame, DevicePixelRatio */
(() => {
  'use strict';

  // -------------------------------------------------------------------------
  // Constants and small helpers
  // -------------------------------------------------------------------------

  const DEG = Math.PI / 180;
  const TAU = Math.PI * 2;
  const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
  const $ = (id) => document.getElementById(id);

  const state = {
    az: 83.5,
    el: 10.0,
    dist: 0.96,
    spread: 0.10,
    order: 1,
    preview: 'binaural',
    distanceModel: 'inverse',
    air: 0.0,
    headModel: 'both',
    headWidth: 1.0,
    roomAmount: 0.0,
    roomSize: 0.5,
    snap: 0,
    basisChannel: 0,
    chapter: 0,
    showBasis: false
  };

  const camera = {
    yaw: 0.55,
    pitch: 0.30,
    radius: 3.75,
    dragging: false,
    lastX: 0,
    lastY: 0
  };

  const ACN_NAMES = ['W', 'Y', 'Z', 'X', 'V', 'T', 'R', 'S', 'U', 'Q', 'O', 'M', 'K', 'L', 'N', 'P'];

  const SPEAKERS = [
    { name: 'FC', label: 'FC', angle: 0 },
    { name: 'FR', label: 'FR', angle: 30 },
    { name: 'SR', label: 'SR', angle: 90 },
    { name: 'BR', label: 'BR', angle: 150 },
    { name: 'BL', label: 'BL', angle: 210 },
    { name: 'SL', label: 'SL', angle: 270 },
    { name: 'FL', label: 'FL', angle: 330 }
  ];

  const SPEAKER_ORDER = ['FL', 'FR', 'FC', 'LFE', 'BL', 'BR', 'SL', 'SR'];

  const LESSONS = [
    {
      kicker: '01 · COORDINATES',
      title: 'Azimuth, elevation, distance',
      body: 'The listener sits at the origin. The source is a vector on the unit sphere: azimuth rotates around Y, elevation lifts toward Y, and distance scales the vector. The cyan puck is the source; the floor rings are horizontal distance references.',
      equation: 'x = d·cos(el)·sin(az)\ny = d·sin(el)\nz = d·cos(el)·cos(az)'
    },
    {
      kicker: '02 · STEREO DOWNMIX',
      title: 'From W/Y to an equal-power pair',
      body: 'The HOA encoder gives a first-order W/Y/X description. The stereo monitor turns the Y/W ratio into a pan, then applies equal-power cos/sin gains. X/W provides the front/back gain. A hard +90° source is intentionally one-sided.',
      equation: 'pan = Y / W\nangle = (pan + 1)·π/4\nL = cos(angle) · frontback\nR = sin(angle) · frontback'
    },
    {
      kicker: '03 · 7.1 / VBAP',
      title: 'Find a pair, invert, normalize',
      body: 'The 7.1 speaker ring is seven satellites plus LFE. VBAP finds the adjacent speaker pair around the azimuth, solves the 2×2 matrix for pair gains, clamps negatives, and power-normalizes the pair. The highlighted speakers are the active pair.',
      equation: 'p = L₁₂ · g\ng = L₁₂⁻¹ · p\nΣgᵢ² = 1'
    },
    {
      kicker: '04 · HOA',
      title: 'ACN order + SN3D normalization',
      body: 'HOA encodes the source into real spherical-harmonic channels. Order 1 is 4 channels (W,Y,Z,X), order 2 is 9, order 3 is 16. ACN is the channel order; SN3D is the normalization. The colored lobe is the selected SH basis around the listener.',
      equation: 'order 1 → 4 ch\norder 2 → 9 ch\norder 3 → 16 ch\nΣgᵢ² = (order+1)/(1+d)²'
    },
    {
      kicker: '05 · BINAURAL',
      title: 'ITD, ILD and head shadow',
      body: 'Binaural monitoring keeps both ears. The far ear receives an interaural time delay, a head-shadow low-pass, and an ILD attenuation; the near ear is direct. Rear sources darken both ears, and elevation adds a gentle spectral tilt. The teal spheres are the left and right ears.',
      equation: 'ITD ≈ 0.65 ms · |pan| · headWidth\nfar ear: delayed + low-pass + gain\nnear ear: direct'
    },
    {
      kicker: '06 · DISTANCE & AIR',
      title: 'Rolloff and air absorption',
      body: 'Distance models change the level law: inverse 1/(1+d), inverse square, linear, or none. Air absorption is a one-pole low-pass whose cutoff falls with both distance and the air amount. The rings and the cutoff readout show the effect.',
      equation: 'inverse: 1 / (1 + d)\ninverse²: 1 / (1 + d)²\nlinear: 1 - d\nair cutoff ≈ 20k·(1 − 0.9·air·d)'
    },
    {
      kicker: '07 · PIPELINE',
      title: 'Source stem to AmbiX or preview',
      body: 'The native renderer receives a mono/stereo stem, applies ACN/SN3D weights (plus distance/air), writes an AmbiX master in 24-bit WAV or Wave64, or calls FFmpeg for FLAC. The preview path decodes the same source to stereo or binaural. HOA is a parallel representation and never routes through the [7.1] FL..SR buses.',
      equation: 'Selected Track / Full Mix\n        ↓  VEGAS render\n   mono/stereo stem\n        ↓  ACN/SN3D encoder\n AmbiX .wav/.w64/.flac   or   stereo/binaural preview'
    }
  ];

  // -------------------------------------------------------------------------
  // Math: HOA / VBAP / monitor
  // -------------------------------------------------------------------------

  function direction(azDeg, elDeg) {
    const az = azDeg * DEG;
    const el = clamp(elDeg, -90, 90) * DEG;
    const ce = Math.cos(el);
    return {
      x: ce * Math.cos(az), // front
      y: ce * Math.sin(az), // right
      z: Math.sin(el)       // up
    };
  }

  function worldPosition(azDeg, elDeg, distance) {
    const dir = direction(azDeg, elDeg);
    const d = clamp(distance, 0, 1);
    return { x: dir.y * d, y: dir.z * d, z: dir.x * d };
  }

  function shValues(order, x, y, z) {
    const sqrt3 = Math.sqrt(3);
    const z2 = z * z;
    const values = new Array(16).fill(0);
    values[0] = 1;
    values[1] = y;
    values[2] = z;
    values[3] = x;
    if (order >= 2) {
      values[4] = sqrt3 * x * y;
      values[5] = sqrt3 * y * z;
      values[6] = (3 * z2 - 1) / 2;
      values[7] = sqrt3 * x * z;
      values[8] = sqrt3 / 2 * (x * x - y * y);
    }
    if (order >= 3) {
      values[9] = Math.sqrt(5 / 8) * y * (3 * x * x - y * y);
      values[10] = Math.sqrt(15) * x * y * z;
      values[11] = Math.sqrt(3 / 8) * y * (5 * z2 - 1);
      values[12] = z * (5 * z2 - 3) / 2;
      values[13] = Math.sqrt(3 / 8) * x * (5 * z2 - 1);
      values[14] = Math.sqrt(15) / 2 * z * (x * x - y * y);
      values[15] = Math.sqrt(5 / 8) * x * (x * x - 3 * y * y);
    }
    return values;
  }

  function shValue(channel, x, y, z) {
    const values = shValues(3, x, y, z);
    return channel >= 0 && channel < values.length ? values[channel] : 0;
  }

  function channelCount(order) {
    return order >= 1 && order <= 3 ? (order + 1) * (order + 1) : 0;
  }

  function hoaWeights() {
    const dir = direction(state.az, state.el);
    const order = state.order;
    const count = channelCount(order);
    const raw = shValues(order, dir.x, dir.y, dir.z);
    const weights = raw.slice(0, count);
    const spread = clamp(state.spread, 0, 1);
    const nominalEnergy = order + 1;
    if (spread > 0) {
      const diffuseW = Math.sqrt(nominalEnergy);
      for (let i = 0; i < weights.length; i++) {
        weights[i] *= 1 - spread;
      }
      weights[0] += spread * diffuseW;
    }
    const defaultGain = 1 / (1 + clamp(state.dist, 0, 1));
    const targetEnergy = nominalEnergy * defaultGain * defaultGain;
    let energy = weights.reduce((sum, value) => sum + value * value, 0);
    if (energy > 1e-12) {
      const scale = Math.sqrt(targetEnergy / energy);
      for (let i = 0; i < weights.length; i++) {
        weights[i] *= scale;
      }
    }
    energy = weights.reduce((sum, value) => sum + value * value, 0);
    return { weights, energy, dir };
  }

  function distanceModelGain() {
    const d = clamp(state.dist, 0, 1);
    const inverse = 1 / (1 + d);
    switch (state.distanceModel) {
      case 'inverse-square': return 1 / ((1 + d) * (1 + d));
      case 'linear': return Math.max(0, 1 - d);
      case 'none': return 1;
      default: return inverse;
    }
  }

  function airCutoff() {
    const amount = clamp(state.air, 0, 1) * clamp(state.dist, 0, 1);
    if (amount <= 1e-4) return 20000;
    return clamp(20000 * (1 - 0.9 * amount), 200, 20000);
  }

  function speakerGains(azDeg) {
    const az = ((azDeg % 360) + 360) % 360;
    const ring = SPEAKERS.map((speaker) => {
      const rad = speaker.angle * DEG;
      return { ...speaker, rad, x: Math.sin(rad), z: Math.cos(rad) };
    });
    let a = ring[0];
    let b = ring[1];
    for (let i = 0; i < ring.length; i++) {
      const next = ring[(i + 1) % ring.length];
      const start = ring[i].angle;
      const end = i === ring.length - 1 ? 360 : next.angle;
      if (az >= start && az < end) {
        a = ring[i];
        b = next;
        break;
      }
    }
    const ux = Math.sin(az * DEG);
    const uz = Math.cos(az * DEG);
    const det = a.x * b.z - b.x * a.z;
    let ga = 0;
    let gb = 0;
    if (Math.abs(det) > 1e-7) {
      ga = Math.max(0, (b.z * ux - b.x * uz) / det);
      gb = Math.max(0, (-a.z * ux + a.x * uz) / det);
    } else {
      ga = 1;
    }
    const pairPower = ga * ga + gb * gb;
    if (pairPower > 1e-12) {
      const norm = 1 / Math.sqrt(pairPower);
      ga *= norm;
      gb *= norm;
    }
    const gains = { FL: 0, FR: 0, FC: 0, LFE: 0, BL: 0, BR: 0, SL: 0, SR: 0 };
    gains[a.name] = ga;
    gains[b.name] = gb;
    return { gains, pair: [a.name, b.name] };
  }

  function directionalComponents(weights) {
    const w = Math.max(Math.abs(weights[0] || 0), 1e-6);
    const pan = clamp((weights[1] || 0) / w, -1, 1);
    const front = clamp((weights[3] || 0) / w, -1, 1);
    return { w, pan, front };
  }

  function stereoGains(weights) {
    const { w, pan, front } = directionalComponents(weights);
    const angle = (pan + 1) * Math.PI / 4;
    const fb = 0.8 + 0.2 * front;
    const elevation = 0.9 + 0.1 * (state.el / 90);
    const spreadLevel = 0.85 + 0.15 * w;
    return {
      left: Math.cos(angle) * fb * elevation * spreadLevel,
      right: Math.sin(angle) * fb * elevation * spreadLevel,
      itd: 0,
      pan,
      front
    };
  }

  function binauralGains(weights) {
    if (state.headModel === 'off' || state.preview === 'stereo') {
      return stereoGains(weights);
    }
    const { pan, front } = directionalComponents(weights);
    const headWidth = clamp(state.headWidth, 0.5, 1.5);
    const itdEnabled = state.headModel === 'both' || state.headModel === 'itd';
    const ildEnabled = state.headModel === 'both' || state.headModel === 'ild';
    const itd = itdEnabled ? 0.00065 * Math.abs(pan) * headWidth : 0;
    const shadow = ildEnabled ? Math.min(1, Math.abs(pan) * headWidth) : 0;
    const farGain = 1 - 0.45 * shadow;
    const rear = Math.max(-front, 0);
    const rearGain = 1 - 0.35 * rear;
    const base = 0.707;
    const fb = 0.8 + 0.2 * front;
    const elevation = 0.9 + 0.1 * (state.el / 90);
    const level = base * rearGain * fb * elevation;
    return {
      left: pan > 0 ? level * farGain : level,
      right: pan < 0 ? level * farGain : level,
      itd,
      pan,
      front
    };
  }

  function monitorGains(weights) {
    if (state.preview === 'bypass') {
      const base = 0.707;
      return { left: base, right: base, itd: 0, pan: 0, front: 0 };
    }
    return state.preview === 'stereo'
      ? stereoGains(weights)
      : binauralGains(weights);
  }

  function formatDeg(value) {
    return (value >= 0 ? '+' : '') + value.toFixed(1) + '°';
  }

  function formatUnit(value) {
    return value.toFixed(2);
  }

  function formatDb(value) {
    if (!isFinite(value) || value <= 0.0001) return '-∞';
    return (20 * Math.log10(value)).toFixed(1) + ' dB';
  }

  function meterFraction(db) {
    return clamp((db + 60) / 60, 0, 1);
  }

  // -------------------------------------------------------------------------
  // WebGL setup
  // -------------------------------------------------------------------------

  const canvas = $('gl');
  const wrap = $('canvasWrap');
  const labelsHost = $('labels');
  let gl = null;
  let projection = null;
  let view = null;
  let dpr = 1;

  let meshProgram = null;
  let lineProgram = null;
  let pointProgram = null;
  let sphereBuffers = null;
  let shWire = null;
  let lineBuffer = null;
  let pointBuffer = null;

  const MESH_VS = `
    attribute vec3 aPosition;
    attribute vec3 aNormal;
    attribute vec3 aColor;
    uniform mat4 uProjection;
    uniform mat4 uView;
    uniform mat4 uModel;
    uniform vec3 uColor;
    uniform float uUseVertexColor;
    varying vec3 vNormal;
    varying vec3 vColor;
    void main() {
      vec4 world = uModel * vec4(aPosition, 1.0);
      vNormal = (uModel * vec4(aNormal, 0.0)).xyz;
      vColor = mix(uColor, aColor, uUseVertexColor);
      gl_Position = uProjection * uView * world;
    }
  `;

  const MESH_FS = `
    precision mediump float;
    uniform float uAlpha;
    varying vec3 vNormal;
    varying vec3 vColor;
    void main() {
      vec3 n = normalize(vNormal);
      vec3 l = normalize(vec3(0.45, 0.8, 0.55));
      float diffuse = max(dot(n, l), 0.0);
      vec3 color = vColor * (0.30 + 0.70 * diffuse);
      gl_FragColor = vec4(color, uAlpha);
    }
  `;

  const LINE_VS = `
    attribute vec3 aPosition;
    attribute vec4 aColor;
    uniform mat4 uProjection;
    uniform mat4 uView;
    varying vec4 vColor;
    void main() {
      vColor = aColor;
      gl_Position = uProjection * uView * vec4(aPosition, 1.0);
    }
  `;

  const LINE_FS = `
    precision mediump float;
    varying vec4 vColor;
    void main() { gl_FragColor = vColor; }
  `;

  const POINT_VS = `
    attribute vec3 aPosition;
    attribute vec4 aColor;
    uniform mat4 uProjection;
    uniform mat4 uView;
    uniform float uSize;
    varying vec4 vColor;
    void main() {
      vColor = aColor;
      gl_Position = uProjection * uView * vec4(aPosition, 1.0);
      gl_PointSize = uSize;
    }
  `;

  const POINT_FS = `
    precision mediump float;
    varying vec4 vColor;
    void main() {
      vec2 p = gl_PointCoord - vec2(0.5);
      if (dot(p, p) > 0.25) discard;
      gl_FragColor = vColor;
    }
  `;

  function compileShader(type, source) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
      const log = gl.getShaderInfoLog(shader);
      gl.deleteShader(shader);
      throw new Error('Shader compile failed: ' + log);
    }
    return shader;
  }

  function createProgram(vertexSource, fragmentSource) {
    const program = gl.createProgram();
    const vs = compileShader(gl.VERTEX_SHADER, vertexSource);
    const fs = compileShader(gl.FRAGMENT_SHADER, fragmentSource);
    gl.attachShader(program, vs);
    gl.attachShader(program, fs);
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
      const log = gl.getProgramInfoLog(program);
      throw new Error('Program link failed: ' + log);
    }
    return {
      program,
      attributes: {
        position: gl.getAttribLocation(program, 'aPosition'),
        normal: gl.getAttribLocation(program, 'aNormal'),
        color: gl.getAttribLocation(program, 'aColor')
      },
      uniforms: {
        projection: gl.getUniformLocation(program, 'uProjection'),
        view: gl.getUniformLocation(program, 'uView'),
        model: gl.getUniformLocation(program, 'uModel'),
        color: gl.getUniformLocation(program, 'uColor'),
        useVertexColor: gl.getUniformLocation(program, 'uUseVertexColor'),
        alpha: gl.getUniformLocation(program, 'uAlpha'),
        size: gl.getUniformLocation(program, 'uSize')
      }
    };
  }

  function initGL() {
    gl = canvas.getContext('webgl2', {
      antialias: true,
      alpha: false,
      powerPreference: 'high-performance'
    }) || canvas.getContext('webgl', {
      antialias: true,
      alpha: false
    }) || canvas.getContext('experimental-webgl');

    if (!gl) {
      $('glStatus').textContent = 'WebGL unavailable';
      $('sceneStatus').textContent = 'WebGL could not be created in this browser.';
      return false;
    }

    meshProgram = createProgram(MESH_VS, MESH_FS);
    lineProgram = createProgram(LINE_VS, LINE_FS);
    pointProgram = createProgram(POINT_VS, POINT_FS);

    sphereBuffers = createSphereGeometry(18, 28);
    lineBuffer = gl.createBuffer();
    pointBuffer = gl.createBuffer();

    gl.clearColor(0.045, 0.050, 0.065, 1);
    gl.enable(gl.DEPTH_TEST);
    gl.depthFunc(gl.LEQUAL);
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);

    const debug = gl.getExtension('WEBGL_debug_renderer_info');
    const renderer = debug
      ? gl.getParameter(debug.UNMASKED_RENDERER_WEBGL)
      : gl.getParameter(gl.RENDERER);
    $('glStatus').textContent = String(gl.getParameter(gl.VERSION)).startsWith('WebGL 2')
      ? 'WebGL 2 ready'
      : 'WebGL 1 ready';
    $('glStatus').title = renderer || '';
    $('sceneStatus').textContent = 'OpenGL context ready · ' + (renderer || 'renderer');
    return true;
  }

  function createSphereGeometry(latSegments, lonSegments) {
    const positions = [];
    const normals = [];
    const colors = [];
    const indices = [];

    for (let lat = 0; lat <= latSegments; lat++) {
      const phi = lat * Math.PI / latSegments;
      const y = Math.cos(phi);
      const r = Math.sin(phi);
      for (let lon = 0; lon <= lonSegments; lon++) {
        const theta = lon * TAU / lonSegments;
        const x = r * Math.cos(theta);
        const z = r * Math.sin(theta);
        positions.push(x, y, z);
        normals.push(x, y, z);
        colors.push(1, 1, 1);
      }
    }

    for (let lat = 0; lat < latSegments; lat++) {
      for (let lon = 0; lon < lonSegments; lon++) {
        const a = lat * (lonSegments + 1) + lon;
        const b = a + lonSegments + 1;
        indices.push(a, b, a + 1, b, b + 1, a + 1);
      }
    }

    return uploadGeometry(positions, normals, colors, indices);
  }

  function uploadGeometry(positions, normals, colors, indices) {
    const positionBuffer = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, positionBuffer);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(positions), gl.STATIC_DRAW);

    const normalBuffer = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, normalBuffer);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(normals), gl.STATIC_DRAW);

    const colorBuffer = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, colorBuffer);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(colors), gl.STATIC_DRAW);

    const indexBuffer = gl.createBuffer();
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, indexBuffer);
    gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, new Uint16Array(indices), gl.STATIC_DRAW);

    return {
      positionBuffer,
      normalBuffer,
      colorBuffer,
      indexBuffer,
      count: indices.length
    };
  }

  // The SH basis is drawn as a wireframe balloon rather than a solid surface so
  // it annotates the scene without hiding the source, speakers or ears.
  function buildShWire(channel) {
    const data = [];

    // Normalize the balloon by the channel's true peak so higher-order lobes
    // keep their shape instead of clipping into a blob.
    let peak = 1e-6;
    for (let lat = -75; lat <= 75; lat += 15) {
      const el = lat * DEG;
      for (let lon = -180; lon < 180; lon += 15) {
        const az = lon * DEG;
        const ce = Math.cos(el);
        peak = Math.max(peak, Math.abs(shValue(
          channel,
          ce * Math.cos(az),
          ce * Math.sin(az),
          Math.sin(el)
        )));
      }
    }

    const lobePoint = (azDeg, elDeg) => {
      const az = azDeg * DEG;
      const el = elDeg * DEG;
      const ce = Math.cos(el);
      const x = ce * Math.cos(az);
      const y = Math.sin(el);
      const z = ce * Math.sin(az);
      const value = shValue(channel, x, z, y);
      const magnitude = Math.min(1, Math.abs(value) / peak);
      const radius = 0.26 + 0.34 * magnitude;
      const positive = value >= 0;
      const r = positive ? 0.20 + 0.10 * magnitude : 0.94;
      const g = positive ? 0.66 + 0.20 * magnitude : 0.45 + 0.20 * magnitude;
      const b = positive ? 0.98 : 0.10;
      // HOA vectors are (front, right, up); the scene uses (right, up, front).
      return { p: [z * radius, y * radius, x * radius], c: [r, g, b] };
    };

    const pushSegment = (a, b) => {
      const ca = a.c;
      const cb = b.c;
      data.push(a.p[0], a.p[1], a.p[2], ca[0], ca[1], ca[2], 0.62);
      data.push(b.p[0], b.p[1], b.p[2], cb[0], cb[1], cb[2], 0.62);
    };

    const lonSegments = 48;
    for (let lat = -80; lat <= 80; lat += 12) {
      let previous = null;
      for (let lon = 0; lon <= lonSegments; lon++) {
        const point = lobePoint(-180 + 360 * lon / lonSegments, lat);
        if (previous) pushSegment(previous, point);
        previous = point;
      }
    }

    const latSegments = 30;
    for (let lon = 0; lon < 360; lon += 22.5) {
      let previous = null;
      for (let lat = 0; lat <= latSegments; lat++) {
        const point = lobePoint(lon, -90 + 180 * lat / latSegments);
        if (previous) pushSegment(previous, point);
        previous = point;
      }
    }

    if (shWire) gl.deleteBuffer(shWire.buffer);
    const buffer = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(data), gl.STATIC_DRAW);
    shWire = { buffer, count: data.length / 7 };
  }

  // -------------------------------------------------------------------------
  // Matrices
  // -------------------------------------------------------------------------

  function mat4Perspective(fovDegrees, aspect, near, far) {
    const f = 1 / Math.tan((fovDegrees * DEG) / 2);
    const range = 1 / (near - far);
    return new Float32Array([
      f / aspect, 0, 0, 0,
      0, f, 0, 0,
      0, 0, (near + far) * range, -1,
      0, 0, 2 * near * far * range, 0
    ]);
  }

  function mat4LookAt(eye, target, up) {
    let zx = eye[0] - target[0];
    let zy = eye[1] - target[1];
    let zz = eye[2] - target[2];
    let len = Math.hypot(zx, zy, zz) || 1;
    zx /= len; zy /= len; zz /= len;

    let xx = up[1] * zz - up[2] * zy;
    let xy = up[2] * zx - up[0] * zz;
    let xz = up[0] * zy - up[1] * zx;
    len = Math.hypot(xx, xy, xz) || 1;
    xx /= len; xy /= len; xz /= len;

    const yx = zy * xz - zz * xy;
    const yy = zz * xx - zx * xz;
    const yz = zx * xy - zy * xx;

    return new Float32Array([
      xx, yx, zx, 0,
      xy, yy, zy, 0,
      xz, yz, zz, 0,
      -(xx * eye[0] + xy * eye[1] + xz * eye[2]),
      -(yx * eye[0] + yy * eye[1] + yz * eye[2]),
      -(zx * eye[0] + zy * eye[1] + zz * eye[2]),
      1
    ]);
  }

  function mat4TranslationScale(position, scale) {
    return new Float32Array([
      scale, 0, 0, 0,
      0, scale, 0, 0,
      0, 0, scale, 0,
      position[0], position[1], position[2], 1
    ]);
  }

  function projectPoint(point) {
    const viewSpace = viewPoint(point);
    const clipX = projection[0] * viewSpace[0] + projection[4] * viewSpace[1] + projection[8] * viewSpace[2] + projection[12];
    const clipY = projection[1] * viewSpace[0] + projection[5] * viewSpace[1] + projection[9] * viewSpace[2] + projection[13];
    const clipZ = projection[2] * viewSpace[0] + projection[6] * viewSpace[1] + projection[10] * viewSpace[2] + projection[14];
    const clipW = projection[3] * viewSpace[0] + projection[7] * viewSpace[1] + projection[11] * viewSpace[2] + projection[15];
    if (clipW <= 0) return null;
    const ndcX = clipX / clipW;
    const ndcY = clipY / clipW;
    if (Math.abs(ndcX) > 1.2 || Math.abs(ndcY) > 1.2) return null;
    const rect = wrap.getBoundingClientRect();
    return {
      x: (ndcX * 0.5 + 0.5) * rect.width,
      y: (1 - (ndcY * 0.5 + 0.5)) * rect.height
    };
  }

  // View matrix transform for points before projection.
  function viewPoint(point) {
    const out = [0, 0, 0];
    out[0] = view[0] * point[0] + view[4] * point[1] + view[8] * point[2] + view[12];
    out[1] = view[1] * point[0] + view[5] * point[1] + view[9] * point[2] + view[13];
    out[2] = view[2] * point[0] + view[6] * point[1] + view[10] * point[2] + view[14];
    return out;
  }

  // -------------------------------------------------------------------------
  // Scene drawing
  // -------------------------------------------------------------------------

  const lineVertices = [];
  const pointVertices = [];

  function pushLine(a, b, color, alpha = 1) {
    lineVertices.push(a[0], a[1], a[2], color[0], color[1], color[2], alpha);
    lineVertices.push(b[0], b[1], b[2], color[0], color[1], color[2], alpha);
  }

  function pushPoint(p, color, alpha = 1) {
    pointVertices.push(p[0], p[1], p[2], color[0], color[1], color[2], alpha);
  }

  function addCircle(center, radius, segments, color, alpha, axis = 'y') {
    let previous = null;
    for (let i = 0; i <= segments; i++) {
      const angle = i * TAU / segments;
      let point;
      if (axis === 'y') {
        point = [center[0] + Math.cos(angle) * radius, center[1], center[2] + Math.sin(angle) * radius];
      } else if (axis === 'z') {
        point = [center[0] + Math.cos(angle) * radius, center[1] + Math.sin(angle) * radius, center[2]];
      } else {
        point = [center[0], center[1] + Math.sin(angle) * radius, center[2] + Math.cos(angle) * radius];
      }
      if (previous) pushLine(previous, point, color, alpha);
      previous = point;
    }
  }

  function addArc(center, radius, startAz, endAz, segments, color, alpha) {
    let previous = null;
    for (let i = 0; i <= segments; i++) {
      const az = (startAz + (endAz - startAz) * i / segments) * DEG;
      const point = [center[0] + Math.sin(az) * radius, center[1], center[2] + Math.cos(az) * radius];
      if (previous) pushLine(previous, point, color, alpha);
      previous = point;
    }
  }

  function addElevationArc(azDeg, radius, color, alpha) {
    let previous = null;
    for (let i = 0; i <= 36; i++) {
      const el = -0.0 + (state.el * i / 36);
      const dir = direction(azDeg, el);
      const point = [dir.y * radius, dir.z * radius, dir.x * radius];
      if (previous) pushLine(previous, point, color, alpha);
      previous = point;
    }
  }

  function addGreatSphereWire() {
    const color = [0.20, 0.20, 0.25];
    for (let lat = -60; lat <= 60; lat += 30) {
      const y = Math.sin(lat * DEG);
      const r = Math.cos(lat * DEG);
      addCircle([0, y, 0], r, 48, color, 0.45);
    }
    for (let az = 0; az < 180; az += 30) {
      let previous = null;
      for (let i = 0; i <= 48; i++) {
        const el = -90 + 180 * i / 48;
        const dir = direction(az, el);
        const point = [dir.y, dir.z, dir.x];
        if (previous) pushLine(previous, point, color, 0.30);
        previous = point;
      }
    }
  }

  function drawScene() {
    if (!gl) return;

    const width = canvas.width;
    const height = canvas.height;
    gl.viewport(0, 0, width, height);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);

    const aspect = width / Math.max(1, height);
    projection = mat4Perspective(aspect < 0.8 ? 50 : 44, aspect, 0.1, 20);
    const eye = [
      Math.sin(camera.yaw) * Math.cos(camera.pitch) * camera.radius,
      Math.sin(camera.pitch) * camera.radius,
      Math.cos(camera.yaw) * Math.cos(camera.pitch) * camera.radius
    ];
    view = mat4LookAt(eye, [0, 0, 0], [0, 1, 0]);

    const weights = hoaWeights().weights;
    const speaker = speakerGains(state.az);
    const source = worldPosition(state.az, state.el, state.dist);
    const sourceVec = [source.x, source.y, source.z];

    lineVertices.length = 0;
    pointVertices.length = 0;

    // Floor and sphere frame.
    addCircle([0, 0, 0], 1.0, 72, [0.24, 0.24, 0.30], 0.85);
    addCircle([0, 0, 0], 0.75, 64, [0.18, 0.18, 0.23], 0.55);
    addCircle([0, 0, 0], 0.50, 56, [0.18, 0.18, 0.23], 0.45);
    addCircle([0, 0, 0], 0.25, 48, [0.18, 0.18, 0.23], 0.35);
    for (let az = 0; az < 360; az += 45) {
      const dir = direction(az, 0);
      pushLine([0, 0, 0], [dir.y, 0, dir.x], [0.18, 0.18, 0.23], 0.35);
    }
    addGreatSphereWire();

    // Azimuth and elevation guides.
    addArc([0, 0, 0], 0.92, 0, state.az, 48, [0.22, 0.74, 0.97], 0.80);
    addElevationArc(state.az, 0.92, [0.18, 0.77, 0.71], 0.75);
    pushLine([0, 0, 0], sourceVec, [0.22, 0.74, 0.97], 0.55);

    // Speaker nodes and VBAP gain lines.
    for (const speakerDef of SPEAKERS) {
      const rad = speakerDef.angle * DEG;
      const position = [Math.sin(rad), 0, Math.cos(rad)];
      const gain = speaker.gains[speakerDef.name] || 0;
      pushLine([0, 0, 0], position, [0.00, 0.48, 0.80], 0.06 + 0.65 * gain);
    }

    // Active pair arc.
    const pairNames = speaker.pair;
    const pairA = SPEAKERS.find((s) => s.name === pairNames[0]);
    const pairB = SPEAKERS.find((s) => s.name === pairNames[1]);
    if (pairA && pairB) {
      let startAngle = pairA.angle;
      let endAngle = pairB.angle;
      if (endAngle < startAngle) endAngle += 360;
      addArc([0, 0.002, 0], 1.035, startAngle, endAngle, 36, [0.22, 0.74, 0.97], 0.95);
    }

    // Spread ring around the source.
    if (state.spread > 0.001) {
      const dir = direction(state.az, state.el);
      const n = [dir.y, dir.z, dir.x];
      let tangent = [1, 0, 0];
      if (Math.abs(n[0]) > 0.9) tangent = [0, 0, 1];
      let bitangent = [
        n[1] * tangent[2] - n[2] * tangent[1],
        n[2] * tangent[0] - n[0] * tangent[2],
        n[0] * tangent[1] - n[1] * tangent[0]
      ];
      const bLen = Math.hypot(...bitangent) || 1;
      bitangent = bitangent.map((v) => v / bLen);
      tangent = [
        bitangent[1] * n[2] - bitangent[2] * n[1],
        bitangent[2] * n[0] - bitangent[0] * n[2],
        bitangent[0] * n[1] - bitangent[1] * n[0]
      ];
      const ringRadius = 0.06 + 0.22 * state.spread;
      let previous = null;
      for (let i = 0; i <= 40; i++) {
        const a = i * TAU / 40;
        const p = [
          sourceVec[0] + ringRadius * (Math.cos(a) * tangent[0] + Math.sin(a) * bitangent[0]),
          sourceVec[1] + ringRadius * (Math.cos(a) * tangent[1] + Math.sin(a) * bitangent[1]),
          sourceVec[2] + ringRadius * (Math.cos(a) * tangent[2] + Math.sin(a) * bitangent[2])
        ];
        if (previous) pushLine(previous, p, [0.22, 0.74, 0.97], 0.65);
        previous = p;
      }
    }

    // Ear markers and binaural guide lines.
    const earL = [-0.13, 0, 0];
    const earR = [0.13, 0, 0];
    const monitor = monitorGains(weights);
    if (state.chapter === 4 || state.preview !== 'bypass') {
      pushLine(sourceVec, earL, [0.18, 0.77, 0.71], 0.18 + 0.65 * clamp(monitor.left, 0, 1));
      pushLine(sourceVec, earR, [0.18, 0.77, 0.71], 0.18 + 0.65 * clamp(monitor.right, 0, 1));
      pushPoint(earL, [0.18, 0.77, 0.71], 0.9);
      pushPoint(earR, [0.18, 0.77, 0.71], 0.9);
    }

    // Draw meshes first.
    gl.useProgram(meshProgram.program);
    gl.uniformMatrix4fv(meshProgram.uniforms.projection, false, projection);
    gl.uniformMatrix4fv(meshProgram.uniforms.view, false, view);
    gl.uniform1f(meshProgram.uniforms.useVertexColor, 0);
    gl.uniform1f(meshProgram.uniforms.alpha, 1);

    for (const speakerDef of SPEAKERS) {
      const rad = speakerDef.angle * DEG;
      const position = [Math.sin(rad), 0, Math.cos(rad)];
      const gain = speaker.gains[speakerDef.name] || 0;
      const active = pairNames.includes(speakerDef.name);
      const color = active ? [0.22, 0.74, 0.97] : [0.00, 0.42, 0.70];
      drawSphere(position, 0.034 + 0.012 * gain, color, 1);
    }

    drawSphere([0, -0.92, 0], 0.035, [0.96, 0.62, 0.04], 1);

    // Source.
    drawSphere(sourceVec, 0.05, [0.22, 0.74, 0.97], 1);
    drawSphere(sourceVec, 0.075, [0.22, 0.74, 0.97], 0.12);

    // Ears.
    drawSphere(earL, 0.026, [0.18, 0.77, 0.71], 1);
    drawSphere(earR, 0.026, [0.18, 0.77, 0.71], 1);

    // Optional SH balloon (wireframe, so the scene stays readable).
    gl.useProgram(lineProgram.program);
    gl.uniformMatrix4fv(lineProgram.uniforms.projection, false, projection);
    gl.uniformMatrix4fv(lineProgram.uniforms.view, false, view);
    gl.disable(gl.DEPTH_TEST);
    if (state.showBasis) {
      if (!shWire) buildShWire(state.basisChannel);
      gl.bindBuffer(gl.ARRAY_BUFFER, shWire.buffer);
      gl.enableVertexAttribArray(lineProgram.attributes.position);
      gl.vertexAttribPointer(lineProgram.attributes.position, 3, gl.FLOAT, false, 28, 0);
      gl.enableVertexAttribArray(lineProgram.attributes.color);
      gl.vertexAttribPointer(lineProgram.attributes.color, 4, gl.FLOAT, false, 28, 12);
      gl.drawArrays(gl.LINES, 0, shWire.count);
      gl.disableVertexAttribArray(lineProgram.attributes.position);
      gl.disableVertexAttribArray(lineProgram.attributes.color);
    }

    // Scene guides.
    uploadDynamic(lineBuffer, lineVertices, 7);
    gl.bindBuffer(gl.ARRAY_BUFFER, lineBuffer);
    gl.enableVertexAttribArray(lineProgram.attributes.position);
    gl.vertexAttribPointer(lineProgram.attributes.position, 3, gl.FLOAT, false, 28, 0);
    gl.enableVertexAttribArray(lineProgram.attributes.color);
    gl.vertexAttribPointer(lineProgram.attributes.color, 4, gl.FLOAT, false, 28, 12);
    gl.drawArrays(gl.LINES, 0, lineVertices.length / 7);
    gl.disableVertexAttribArray(lineProgram.attributes.position);
    gl.disableVertexAttribArray(lineProgram.attributes.color);

    // Points.
    uploadDynamic(pointBuffer, pointVertices, 7);
    gl.useProgram(pointProgram.program);
    gl.uniformMatrix4fv(pointProgram.uniforms.projection, false, projection);
    gl.uniformMatrix4fv(pointProgram.uniforms.view, false, view);
    gl.uniform1f(pointProgram.uniforms.size, 7);
    gl.bindBuffer(gl.ARRAY_BUFFER, pointBuffer);
    gl.enableVertexAttribArray(pointProgram.attributes.position);
    gl.vertexAttribPointer(pointProgram.attributes.position, 3, gl.FLOAT, false, 28, 0);
    gl.enableVertexAttribArray(pointProgram.attributes.color);
    gl.vertexAttribPointer(pointProgram.attributes.color, 4, gl.FLOAT, false, 28, 12);
    gl.drawArrays(gl.POINTS, 0, pointVertices.length / 7);
    gl.enable(gl.DEPTH_TEST);

    updateLabels();
    updateFps();
  }

  function bindGeometry(buffers) {
    gl.bindBuffer(gl.ARRAY_BUFFER, buffers.positionBuffer);
    gl.enableVertexAttribArray(meshProgram.attributes.position);
    gl.vertexAttribPointer(meshProgram.attributes.position, 3, gl.FLOAT, false, 0, 0);
    gl.bindBuffer(gl.ARRAY_BUFFER, buffers.normalBuffer);
    gl.enableVertexAttribArray(meshProgram.attributes.normal);
    gl.vertexAttribPointer(meshProgram.attributes.normal, 3, gl.FLOAT, false, 0, 0);
    gl.bindBuffer(gl.ARRAY_BUFFER, buffers.colorBuffer);
    gl.enableVertexAttribArray(meshProgram.attributes.color);
    gl.vertexAttribPointer(meshProgram.attributes.color, 3, gl.FLOAT, false, 0, 0);
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, buffers.indexBuffer);
  }

  function drawSphere(position, radius, color, alpha) {
    gl.uniformMatrix4fv(
      meshProgram.uniforms.model,
      false,
      mat4TranslationScale(position, radius)
    );
    gl.uniform3f(meshProgram.uniforms.color, color[0], color[1], color[2]);
    gl.uniform1f(meshProgram.uniforms.alpha, alpha);
    bindGeometry(sphereBuffers);
    gl.drawElements(gl.TRIANGLES, sphereBuffers.count, gl.UNSIGNED_SHORT, 0);
  }

  function uploadDynamic(buffer, data, strideFloats) {
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(data), gl.DYNAMIC_DRAW);
  }

  // -------------------------------------------------------------------------
  // Labels
  // -------------------------------------------------------------------------

  const labelDefs = [
    ...SPEAKERS.map((s) => ({ id: s.name, text: s.label, pos: [Math.sin(s.angle * DEG), 0, Math.cos(s.angle * DEG)], cls: '' })),
    { id: 'lfe', text: 'LFE', pos: [0, -0.86, 0], cls: 'lfe' },
    { id: 'source', text: 'SOURCE', pos: [0, 0, 0], cls: 'source' },
    { id: 'earL', text: 'L EAR', pos: [-0.13, 0.06, 0], cls: 'ear' },
    { id: 'earR', text: 'R EAR', pos: [0.13, 0.06, 0], cls: 'ear' }
  ];

  const labelElements = new Map();

  function createLabels() {
    for (const def of labelDefs) {
      const el = document.createElement('div');
      el.className = 'label ' + (def.cls || '');
      el.textContent = def.text;
      labelsHost.appendChild(el);
      labelElements.set(def.id, el);
    }
  }

  function updateLabels() {
    const source = worldPosition(state.az, state.el, state.dist);
    for (const def of labelDefs) {
      const el = labelElements.get(def.id);
      if (!el) continue;
      let pos = def.pos;
      if (def.id === 'source') {
        pos = [source.x, source.y + 0.085, source.z];
      }
      const projected = projectPoint(pos);
      if (!projected) {
        el.style.opacity = '0';
        continue;
      }
      el.style.opacity = '1';
      el.style.left = projected.x + 'px';
      el.style.top = projected.y + 'px';
    }

    const earsVisible = state.chapter === 4 || state.preview !== 'bypass';
    labelElements.get('earL').style.opacity = earsVisible ? '1' : '0';
    labelElements.get('earR').style.opacity = earsVisible ? '1' : '0';
  }

  // -------------------------------------------------------------------------
  // UI readouts
  // -------------------------------------------------------------------------

  let lastWeightCount = -1;
  let weightRows = [];
  let speakerRows = [];

  function buildWeightRows(count) {
    const host = $('weights');
    host.innerHTML = '';
    weightRows = [];
    for (let i = 0; i < count; i++) {
      const row = document.createElement('div');
      row.className = 'weight-row';
      const label = document.createElement('span');
      label.textContent = i < 4 ? ACN_NAMES[i] : 'ACN ' + i;
      const bar = document.createElement('div');
      bar.className = 'weight-bar';
      const fill = document.createElement('i');
      bar.appendChild(fill);
      const value = document.createElement('b');
      row.appendChild(label);
      row.appendChild(bar);
      row.appendChild(value);
      host.appendChild(row);
      weightRows.push({ fill, value });
    }
    lastWeightCount = count;
  }

  function buildSpeakerRows() {
    const host = $('speakerMeters');
    if (!host) return;
    host.innerHTML = '';
    speakerRows = [];
    for (const name of SPEAKER_ORDER) {
      const row = document.createElement('div');
      row.className = 'meter-row';
      const label = document.createElement('span');
      label.textContent = name;
      const meter = document.createElement('div');
      meter.className = 'meter';
      const fill = document.createElement('i');
      meter.appendChild(fill);
      const value = document.createElement('b');
      value.textContent = '0.00';
      row.appendChild(label);
      row.appendChild(meter);
      row.appendChild(value);
      host.appendChild(row);
      speakerRows.push({ name, fill, value });
    }
  }

  function updateUI() {
    const { weights, energy, dir } = hoaWeights();
    const count = channelCount(state.order);
    if (count !== lastWeightCount) {
      buildWeightRows(count);
      populateBasisChannels();
    }

    const maxWeight = Math.max(1e-9, ...weights.map((value) => Math.abs(value)));
    for (let i = 0; i < weightRows.length; i++) {
      const value = weights[i] || 0;
      weightRows[i].fill.style.width = (100 * Math.abs(value) / maxWeight) + '%';
      weightRows[i].fill.style.background = value >= 0 ? 'var(--cyan)' : 'var(--amber)';
      weightRows[i].value.textContent = value.toFixed(3);
      weightRows[i].value.style.color = value >= 0 ? 'var(--muted)' : '#fcd34d';
    }

    const speaker = speakerGains(state.az);
    for (const row of speakerRows) {
      const value = speaker.gains[row.name] || 0;
      row.fill.style.width = (100 * clamp(value, 0, 1)) + '%';
      row.value.textContent = value.toFixed(2);
    }

    const monitor = monitorGains(weights);
    const leftDb = 20 * Math.log10(Math.max(monitor.left, 1e-6));
    const rightDb = 20 * Math.log10(Math.max(monitor.right, 1e-6));
    $('leftDb').textContent = formatDb(monitor.left);
    $('rightDb').textContent = formatDb(monitor.right);
    $('leftBar').style.width = (100 * meterFraction(leftDb)) + '%';
    $('rightBar').style.width = (100 * meterFraction(rightDb)) + '%';
    $('leftBar').style.opacity = monitor.left > 0.0001 ? '1' : '0.15';
    $('rightBar').style.opacity = monitor.right > 0.0001 ? '1' : '0.15';

    const meterPower = monitor.left * monitor.left + monitor.right * monitor.right;
    const correlation = meterPower > 1e-9
      ? (2 * monitor.left * monitor.right) / meterPower
      : 1;
    $('corrText').textContent = correlation.toFixed(2);
    $('itdText').textContent = (monitor.itd * 1e6).toFixed(0) + ' µs';

    $('sumEnergy').textContent = energy.toFixed(2);
    const targetGain = distanceModelGain();
    $('distanceGain').textContent = targetGain <= 0.0001
      ? '-∞ dB'
      : (20 * Math.log10(targetGain)).toFixed(1) + ' dB';
    $('airCutoff').textContent = (airCutoff() / 1000).toFixed(1) + ' kHz';
    $('snapText').textContent = state.snap ? state.snap + '°' : 'Off';

    $('azVal').textContent = formatDeg(state.az);
    $('elVal').textContent = formatDeg(state.el);
    $('distVal').textContent = formatUnit(state.dist);
    $('spreadVal').textContent = formatUnit(state.spread);
    $('airVal').textContent = formatUnit(state.air);
    $('headWidthVal').textContent = formatUnit(state.headWidth);
    $('roomAmountVal').textContent = formatUnit(state.roomAmount);
    $('roomSizeVal').textContent = formatUnit(state.roomSize);

    const pair = speaker.pair.join(' + ');
    $('sceneStatus').textContent =
      'HOA order ' + state.order + ' · ' + count + ' ACN channels · VBAP pair ' + pair +
      ' · monitor ' + state.preview;

    $('basisName').textContent = 'ACN ' + state.basisChannel +
      ' · ' + ACN_NAMES[state.basisChannel];
    drawBasisHeatmap();
    updateAudio();
  }

  // -------------------------------------------------------------------------
  // SH heatmap
  // -------------------------------------------------------------------------

  function drawBasisHeatmap() {
    const canvas2d = $('basisCanvas');
    const ctx = canvas2d.getContext('2d');
    const width = canvas2d.width;
    const height = canvas2d.height;
    const image = ctx.createImageData(width, height);
    const values = new Float32Array(width * height);
    let maxAbs = 1e-9;

    for (let y = 0; y < height; y++) {
      const el = 90 - 180 * y / (height - 1);
      for (let x = 0; x < width; x++) {
        const az = -180 + 360 * x / (width - 1);
        const dir = direction(az, el);
        const value = shValue(state.basisChannel, dir.x, dir.y, dir.z);
        values[y * width + x] = value;
        maxAbs = Math.max(maxAbs, Math.abs(value));
      }
    }

    for (let i = 0; i < values.length; i++) {
      const value = values[i] / maxAbs;
      const magnitude = Math.min(1, Math.abs(value));
      const offset = i * 4;
      if (value >= 0) {
        image.data[offset] = 16 + 24 * magnitude;
        image.data[offset + 1] = 90 + 110 * magnitude;
        image.data[offset + 2] = 150 + 90 * magnitude;
      } else {
        image.data[offset] = 150 + 80 * magnitude;
        image.data[offset + 1] = 55 + 55 * magnitude;
        image.data[offset + 2] = 25;
      }
      image.data[offset + 3] = 255;
    }

    ctx.putImageData(image, 0, 0);
    ctx.strokeStyle = 'rgba(255,255,255,.16)';
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(0, height / 2);
    ctx.lineTo(width, height / 2);
    ctx.moveTo(width / 2, 0);
    ctx.lineTo(width / 2, height);
    ctx.stroke();
    ctx.fillStyle = 'rgba(230,230,230,.65)';
    ctx.font = '9px Consolas, monospace';
    ctx.fillText('-180', 3, height - 3);
    ctx.fillText('+180', width - 28, height - 3);
    ctx.fillText('+90', 3, 10);
    ctx.fillText('-90', 3, height - 14);
  }

  function populateBasisChannels() {
    const select = $('basisChannel');
    const count = channelCount(state.order);
    const previous = state.basisChannel;
    select.innerHTML = '';
    for (let i = 0; i < count; i++) {
      const option = document.createElement('option');
      option.value = String(i);
      option.textContent = 'ACN ' + i + ' · ' + ACN_NAMES[i];
      select.appendChild(option);
    }
    state.basisChannel = Math.min(previous, count - 1);
    select.value = String(state.basisChannel);
    buildShWire(state.basisChannel);
    drawBasisHeatmap();
  }

  // -------------------------------------------------------------------------
  // Audio audition
  // -------------------------------------------------------------------------

  const audio = {
    context: null,
    oscillator: null,
    master: null,
    air: null,
    distance: null,
    leftDelay: null,
    rightDelay: null,
    leftFilter: null,
    rightFilter: null,
    leftGain: null,
    rightGain: null,
    leftDry: null,
    rightDry: null,
    roomSend: null,
    roomConvolver: null,
    roomWet: null,
    irSize: -1,
    started: false,
    playing: false
  };

  // A synthetic stereo impulse response: three early reflections per channel
  // plus a damped noise tail. It approximates the native tap/comb room.
  function buildRoomImpulse() {
    if (!audio.context) return;
    const ctx = audio.context;
    const rate = ctx.sampleRate;
    const size = clamp(state.roomSize, 0, 1);
    const scale = 0.6 + 0.8 * size;
    const seconds = 0.30 + 0.70 * size;
    const length = Math.max(1, Math.floor(rate * seconds));
    const buffer = ctx.createBuffer(2, length, rate);
    const taps = [
      [0.011, 0.017, 0.023],
      [0.013, 0.019, 0.027]
    ];
    const tapGains = [0.32, 0.21, 0.14];

    for (let channel = 0; channel < 2; channel++) {
      const data = buffer.getChannelData(channel);
      for (let i = 0; i < taps[channel].length; i++) {
        const index = Math.min(length - 1, Math.round(taps[channel][i] * scale * rate));
        data[index] += tapGains[i];
      }
      let lowpass = 0;
      const decay = 5.6 - 3.6 * size;
      for (let i = 0; i < length; i++) {
        const t = i / rate;
        lowpass += 0.38 * ((Math.random() * 2 - 1) - lowpass);
        data[i] += lowpass * Math.exp(-t * decay) * 0.10;
      }
    }

    audio.roomConvolver.buffer = buffer;
    audio.irSize = size;
  }

  function initAudioGraph() {
    if (audio.context) return;
    const AudioContext = window.AudioContext || window.webkitAudioContext;
    if (!AudioContext) return;
    const ctx = new AudioContext();
    audio.context = ctx;

    const oscillator = ctx.createOscillator();
    oscillator.type = 'sawtooth';
    oscillator.frequency.value = 220;

    const master = ctx.createGain();
    master.gain.value = 0.0;

    const air = ctx.createBiquadFilter();
    air.type = 'lowpass';
    air.frequency.value = 20000;
    air.Q.value = 0.5;

    const distance = ctx.createGain();
    distance.gain.value = 1;

    const leftDelay = ctx.createDelay(1.0);
    const rightDelay = ctx.createDelay(1.0);
    const leftFilter = ctx.createBiquadFilter();
    const rightFilter = ctx.createBiquadFilter();
    leftFilter.type = 'lowpass';
    rightFilter.type = 'lowpass';
    leftFilter.frequency.value = 20000;
    rightFilter.frequency.value = 20000;
    leftFilter.Q.value = 0.6;
    rightFilter.Q.value = 0.6;

    const leftGain = ctx.createGain();
    const rightGain = ctx.createGain();
    leftGain.gain.value = 0;
    rightGain.gain.value = 0;

    const leftDry = ctx.createGain();
    const rightDry = ctx.createGain();
    leftDry.gain.value = 1;
    rightDry.gain.value = 1;

    const merger = ctx.createChannelMerger(2);
    const roomSend = ctx.createChannelMerger(2);
    const roomConvolver = ctx.createConvolver();
    roomConvolver.normalize = false;
    const roomWet = ctx.createGain();
    roomWet.gain.value = 0;

    oscillator.connect(master);
    master.connect(air);
    air.connect(distance);
    distance.connect(leftDelay);
    distance.connect(rightDelay);
    leftDelay.connect(leftFilter);
    rightDelay.connect(rightFilter);
    leftFilter.connect(leftGain);
    rightFilter.connect(rightGain);
    leftGain.connect(leftDry);
    rightGain.connect(rightDry);
    leftDry.connect(merger, 0, 0);
    rightDry.connect(merger, 0, 1);
    merger.connect(ctx.destination);
    leftDry.connect(roomSend, 0, 0);
    rightDry.connect(roomSend, 0, 1);
    roomSend.connect(roomConvolver);
    roomConvolver.connect(roomWet);
    roomWet.connect(ctx.destination);

    oscillator.start();
    Object.assign(audio, {
      oscillator,
      master,
      air,
      distance,
      leftDelay,
      rightDelay,
      leftFilter,
      rightFilter,
      leftGain,
      rightGain,
      leftDry,
      rightDry,
      roomSend,
      roomConvolver,
      roomWet,
      started: true
    });
  }

  function updateAudio() {
    if (!audio.context || !audio.started) return;
    const now = audio.context.currentTime;
    const { weights } = hoaWeights();
    const monitor = monitorGains(weights);
    const targetGain = distanceModelGain();
    const cutoff = airCutoff();
    const level = audio.playing ? 0.12 : 0;
    audio.master.gain.setTargetAtTime(level, now, 0.02);
    audio.distance.gain.setTargetAtTime(clamp(targetGain, 0, 1.5), now, 0.02);
    audio.air.frequency.setTargetAtTime(cutoff, now, 0.05);

    const binaural = state.preview === 'binaural' && state.headModel !== 'off';
    const headWidth = clamp(state.headWidth, 0.5, 1.5);
    let delayL = 0;
    let delayR = 0;
    let filterL = 20000;
    let filterR = 20000;
    let gainL = monitor.left;
    let gainR = monitor.right;

    if (binaural) {
      const pan = clamp(monitor.pan, -1, 1);
      const itd = state.headModel === 'ild'
        ? 0
        : 0.00065 * Math.abs(pan) * headWidth;
      if (pan > 0) delayL = itd;
      if (pan < 0) delayR = itd;
      const shadow = state.headModel === 'itd'
        ? 0
        : Math.min(1, Math.abs(pan) * headWidth);
      const farCutoff = 20000 * Math.max(0.10, 1 - 0.9 * shadow);
      if (pan > 0) filterL = farCutoff;
      if (pan < 0) filterR = farCutoff;
    }

    audio.leftDelay.delayTime.setTargetAtTime(delayL, now, 0.01);
    audio.rightDelay.delayTime.setTargetAtTime(delayR, now, 0.01);
    audio.leftFilter.frequency.setTargetAtTime(filterL, now, 0.03);
    audio.rightFilter.frequency.setTargetAtTime(filterR, now, 0.03);
    audio.leftGain.gain.setTargetAtTime(clamp(gainL, 0, 1), now, 0.02);
    audio.rightGain.gain.setTargetAtTime(clamp(gainR, 0, 1), now, 0.02);

    const roomAmount = state.preview === 'bypass' ? 0 : clamp(state.roomAmount, 0, 1);
    const roomSize = clamp(state.roomSize, 0, 1);
    if (audio.irSize !== roomSize) buildRoomImpulse();
    const dry = 1 - 0.35 * roomAmount;
    audio.leftDry.gain.setTargetAtTime(dry, now, 0.04);
    audio.rightDry.gain.setTargetAtTime(dry, now, 0.04);
    audio.roomWet.gain.setTargetAtTime(0.85 * roomAmount, now, 0.06);
  }

  async function toggleAudio(play) {
    initAudioGraph();
    if (!audio.context) return;
    if (audio.context.state === 'suspended') {
      await audio.context.resume();
    }
    audio.playing = play;
    $('play').disabled = play;
    $('stop').disabled = !play;
    updateAudio();
  }

  // -------------------------------------------------------------------------
  // Controls and chapter state
  // -------------------------------------------------------------------------

  function applySnap(value) {
    if (!state.snap) return value;
    return Math.round(value / state.snap) * state.snap;
  }

  function bindRange(id, key, transform) {
    const input = $(id);
    input.addEventListener('input', () => {
      let value = parseFloat(input.value);
      if (transform) value = transform(value);
      state[key] = value;
      if (key === 'az' || key === 'el') {
        state[key] = applySnap(state[key]);
        input.value = String(state[key]);
      }
      updateUI();
    });
  }

  function bindSelect(id, key) {
    $(id).addEventListener('change', (event) => {
      const value = event.target.value;
      state[key] = key === 'order' || key === 'snap' || key === 'basisChannel'
        ? parseInt(value, 10)
        : value;
      if (key === 'order') {
        const count = channelCount(state.order);
        state.basisChannel = Math.min(state.basisChannel, count - 1);
        populateBasisChannels();
      }
      if (key === 'basisChannel') {
        buildShWire(state.basisChannel);
        drawBasisHeatmap();
      }
      updateUI();
    });
  }

  let basisAutoEnabled = false;

  function selectChapter(index) {
    state.chapter = index;
    for (const button of document.querySelectorAll('.chapter')) {
      button.classList.toggle('active', parseInt(button.dataset.chapter, 10) === index);
    }
    const lesson = LESSONS[index];
    $('lessonKicker').textContent = lesson.kicker;
    $('lessonTitle').textContent = lesson.title;
    $('lessonBody').textContent = lesson.body;
    $('lessonEquation').innerHTML = lesson.equation.replace(/\n/g, '<br>');
    $('pipeline').classList.toggle('hidden', index !== 6);
    $('labels').style.opacity = index === 6 ? '0' : '1';
    if (index === 3) {
      if (!state.showBasis) {
        state.showBasis = true;
        basisAutoEnabled = true;
      }
      $('showBasis').checked = state.showBasis;
      if (state.basisChannel === 0) {
        state.basisChannel = Math.min(3, channelCount(state.order) - 1);
        populateBasisChannels();
      }
    } else if (basisAutoEnabled) {
      state.showBasis = false;
      $('showBasis').checked = false;
      basisAutoEnabled = false;
    }
    updateUI();
  }

  function bindUI() {
    bindRange('az', 'az');
    bindRange('el', 'el');
    bindRange('dist', 'dist');
    bindRange('spread', 'spread');
    bindRange('air', 'air');
    bindRange('headWidth', 'headWidth');
    bindRange('roomAmount', 'roomAmount');
    bindRange('roomSize', 'roomSize');
    bindSelect('order', 'order');
    bindSelect('preview', 'preview');
    bindSelect('distanceModel', 'distanceModel');
    bindSelect('headModel', 'headModel');
    bindSelect('snap', 'snap');
    bindSelect('basisChannel', 'basisChannel');

    $('showBasis').addEventListener('change', (event) => {
      state.showBasis = event.target.checked;
      basisAutoEnabled = false;
      updateUI();
    });

    for (const button of document.querySelectorAll('.chapter')) {
      button.addEventListener('click', () => {
        selectChapter(parseInt(button.dataset.chapter, 10));
      });
    }

    const presets = {
      front: [0, 0, 0.70, 0.10],
      left: [-90, 0, 0.70, 0.10],
      right: [90, 0, 0.70, 0.10],
      rear: [180, 0, 0.70, 0.10],
      overhead: [0, 90, 0.70, 0.10],
      wide: [0, 0, 0.70, 0.80]
    };
    for (const button of document.querySelectorAll('[data-preset]')) {
      button.addEventListener('click', () => {
        const preset = presets[button.dataset.preset];
        if (!preset) return;
        [state.az, state.el, state.dist, state.spread] = preset;
        $('az').value = String(state.az);
        $('el').value = String(state.el);
        $('dist').value = String(state.dist);
        $('spread').value = String(state.spread);
        updateUI();
      });
    }

    $('resetCamera').addEventListener('click', () => {
      camera.yaw = 0.55;
      camera.pitch = 0.30;
      camera.radius = 3.75;
    });

    $('play').addEventListener('click', () => toggleAudio(true));
    $('stop').addEventListener('click', () => toggleAudio(false));

    canvas.addEventListener('pointerdown', (event) => {
      camera.dragging = true;
      camera.lastX = event.clientX;
      camera.lastY = event.clientY;
      canvas.setPointerCapture(event.pointerId);
      canvas.style.cursor = 'grabbing';
    });
    canvas.addEventListener('pointermove', (event) => {
      if (!camera.dragging) return;
      const dx = event.clientX - camera.lastX;
      const dy = event.clientY - camera.lastY;
      camera.lastX = event.clientX;
      camera.lastY = event.clientY;
      camera.yaw -= dx * 0.008;
      camera.pitch = clamp(camera.pitch + dy * 0.006, -1.25, 1.25);
    });
    canvas.addEventListener('pointerup', (event) => {
      camera.dragging = false;
      canvas.releasePointerCapture(event.pointerId);
      canvas.style.cursor = 'grab';
    });
    canvas.addEventListener('pointercancel', () => {
      camera.dragging = false;
      canvas.style.cursor = 'grab';
    });
    canvas.addEventListener('wheel', (event) => {
      event.preventDefault();
      camera.radius = clamp(camera.radius + event.deltaY * 0.0015, 2.0, 7.0);
    }, { passive: false });
    canvas.style.cursor = 'grab';

    buildSpeakerRows();
  }

  // -------------------------------------------------------------------------
  // Resize, loop, startup
  // -------------------------------------------------------------------------

  function resize() {
    if (!gl) return;
    dpr = Math.min(window.devicePixelRatio || 1, 2);
    const rect = wrap.getBoundingClientRect();
    const width = Math.max(1, Math.round(rect.width * dpr));
    const height = Math.max(1, Math.round(rect.height * dpr));
    if (canvas.width !== width || canvas.height !== height) {
      canvas.width = width;
      canvas.height = height;
    }
  }

  let fpsFrames = 0;
  let fpsTime = performance.now();
  function updateFps() {
    const now = performance.now();
    fpsFrames++;
    if (now - fpsTime > 500) {
      const fps = Math.round(1000 * fpsFrames / (now - fpsTime));
      $('fps').textContent = fps + ' fps';
      fpsFrames = 0;
      fpsTime = now;
    }
  }

  function frame() {
    resize();
    drawScene();
    requestAnimationFrame(frame);
  }

  function startup() {
    createLabels();
    bindUI();
    if (!initGL()) {
      return;
    }
    buildSpeakerRows();
    populateBasisChannels();
    updateUI();
    selectChapter(0);
    requestAnimationFrame(frame);
  }

  startup();

  // Expose a small read-only/test surface for tutorial checks.
  window.hoaTutorial = {
    state,
    setChapter: selectChapter,
    setParam(key, value) {
      if (!(key in state)) return;
      state[key] = value;
      const control = $(key);
      if (control) control.value = String(value);
      if (key === 'order') {
        state.basisChannel = Math.min(state.basisChannel, channelCount(state.order) - 1);
        populateBasisChannels();
      }
      if (key === 'basisChannel') {
        buildShWire(state.basisChannel);
        drawBasisHeatmap();
      }
      updateUI();
    },
    getReadouts() {
      return {
        sumEnergy: $('sumEnergy').textContent,
        distanceGain: $('distanceGain').textContent,
        airCutoff: $('airCutoff').textContent,
        left: $('leftDb').textContent,
        right: $('rightDb').textContent,
        pair: speakerGains(state.az).pair
      };
    },
    audioInfo() {
      if (!audio.context) return null;
      return {
        state: audio.context.state,
        playing: audio.playing,
        irLength: audio.roomConvolver.buffer ? audio.roomConvolver.buffer.length : 0,
        wet: audio.roomWet.gain.value,
        dry: audio.leftDry.gain.value
      };
    }
  };
})();
