(() => {
  const image = document.getElementById('screen');
  const video = document.getElementById('video');
  const message = document.getElementById('message');
  const notice = document.getElementById('notice');
  const keyboard = document.getElementById('keyboard');
  const key = new URLSearchParams(location.search).get('key');
  if (!key) { message.textContent = 'Open the QR code shown on your PC'; return; }

  const manifest = document.createElement('link');
  manifest.rel = 'manifest';
  manifest.href = '/manifest.json?key=' + encodeURIComponent(key);
  document.head.appendChild(manifest);

  let socket;
  let objectUrl;
  let single = null;
  let multiple = null;
  let held = false;
  let lastMove = 0;
  let reconnectTimer;
  let composing = false;
  let peer;
  let videoReady = false;
  let waitingForWindow = false;
  let replaced = false;
  let currentClientId;
  let requestedMode = 'Auto';
  let modeVersion = -1;
  let noticeTimer;
  let startupTimer;

  function show(text) { message.textContent = text; message.classList.remove('hidden'); }
  function hide() { message.classList.add('hidden'); }
  function showNotice(text) {
    notice.textContent = text;
    notice.classList.remove('hidden');
    clearTimeout(noticeTimer);
    noticeTimer = setTimeout(() => notice.classList.add('hidden'), 6000);
  }
  function send(payload) {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(payload));
  }
  function point(touch) {
    const sourceWidth = videoReady ? video.videoWidth : image.naturalWidth;
    const sourceHeight = videoReady ? video.videoHeight : image.naturalHeight;
    if (!sourceWidth || !sourceHeight) return null;
    const view = image.getBoundingClientRect();
    const scale = Math.min(view.width / sourceWidth, view.height / sourceHeight);
    const width = sourceWidth * scale;
    const height = sourceHeight * scale;
    const left = view.left + (view.width - width) / 2;
    const top = view.top + (view.height - height) / 2;
    const x = (touch.clientX - left) / width;
    const y = (touch.clientY - top) / height;
    if (x < 0 || x > 1 || y < 0 || y > 1) return null;
    return { x, y };
  }

  async function startVideo(clientId, version, h264Only = false) {
    if (!window.RTCPeerConnection) {
      showNotice('WebRTC is unavailable; using JPEG');
      return;
    }
    const current = new RTCPeerConnection({ iceServers: [] });
    peer = current;
    const transceiver = current.addTransceiver('video', { direction: 'recvonly' });
    if (h264Only) {
      const codecs = window.RTCRtpReceiver?.getCapabilities?.('video')?.codecs
        ?.filter(codec => codec.mimeType.toLowerCase() === 'video/h264') || [];
      if (!codecs.length || !transceiver.setCodecPreferences) {
        current.close();
        peer = null;
        showNotice('H.264 is unavailable; using JPEG');
        return;
      }
      try { transceiver.setCodecPreferences(codecs); }
      catch {
        current.close();
        peer = null;
        showNotice('H.264 is unavailable; using JPEG');
        return;
      }
    }
    current.ontrack = event => {
      video.srcObject = event.streams[0] || new MediaStream([event.track]);
      video.play().catch(() => {});
    };
    current.onconnectionstatechange = () => {
      if (['failed', 'closed', 'disconnected'].includes(current.connectionState) && peer === current) {
        videoReady = false;
        video.style.display = 'none';
      }
    };
    try {
      await current.setLocalDescription(await current.createOffer());
      if (current.iceGatheringState !== 'complete') await new Promise(resolve => {
        const timeout = setTimeout(resolve, 3000);
        current.addEventListener('icegatheringstatechange', () => {
          if (current.iceGatheringState === 'complete') { clearTimeout(timeout); resolve(); }
        });
      });
      const response = await fetch('/offer?key=' + encodeURIComponent(key) + '&client=' + encodeURIComponent(clientId) + '&version=' + version, {
        method: 'POST', headers: { 'Content-Type': 'application/sdp' }, body: current.localDescription.sdp
      });
      if (!response.ok) throw new Error('WebRTC: ' + response.status);
      if (peer !== current || modeVersion !== version) { current.close(); return; }
      const answer = await response.text();
      const negotiatedH265 = answer.includes('H265/90000');
      await current.setRemoteDescription({ type: 'answer', sdp: answer });
      clearTimeout(startupTimer);
      startupTimer = setTimeout(() => {
        if (peer !== current || modeVersion !== version || videoReady) return;
        current.close();
        peer = null;
        video.srcObject = null;
        if (requestedMode === 'Auto' && negotiatedH265 && !h264Only) {
          showNotice('H.265 could not start; trying H.264');
          startVideo(clientId, version, true);
        } else {
          showNotice('Video could not start; using JPEG');
        }
      }, 8000);
    } catch (error) {
      console.warn(error);
      if (peer === current && modeVersion === version) {
        peer = null;
        showNotice(requestedMode === 'Auto' ? 'WebRTC is unavailable; using JPEG' :
          requestedMode.replace('H264', 'H.264').replace('H265', 'H.265') + ' is unavailable; using JPEG');
      }
      current.close();
    }
  }

  function applyVideoMode(status) {
    if (status.version < modeVersion) return;
    clearTimeout(startupTimer);
    requestedMode = status.mode;
    modeVersion = status.version;
    peer?.close();
    peer = null;
    video.srcObject = null;
    videoReady = false;
    video.style.display = 'none';
    if (requestedMode !== 'Jpeg' && currentClientId) startVideo(currentClientId, modeVersion);
  }

  video.addEventListener('playing', () => {
    if (!peer || requestedMode === 'Jpeg') return;
    clearTimeout(startupTimer);
    videoReady = true;
    video.style.display = 'block';
    waitingForWindow = false;
    hide();
  });
  if (video.requestVideoFrameCallback) {
    const onFrame = () => {
      if (videoReady && waitingForWindow) {
        waitingForWindow = false;
        hide();
      }
      video.requestVideoFrameCallback(onFrame);
    };
    video.requestVideoFrameCallback(onFrame);
  }
  function release() {
    if (held && single?.last) send({ type: 'up', ...single.last });
    held = false;
    single = null;
  }

  function connect() {
    replaced = false;
    modeVersion = -1;
    const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
    socket = new WebSocket(`${scheme}//${location.host}/ws?key=${encodeURIComponent(key)}`);
    socket.binaryType = 'blob';
    socket.onopen = () => {
      show('Choose a window on your PC');
    };
    socket.onmessage = event => {
      if (typeof event.data === 'string') {
        try {
          const status = JSON.parse(event.data);
          if (status.type === 'ready') {
            currentClientId = status.clientId;
            applyVideoMode(status);
          } else if (status.type === 'videoMode') {
            applyVideoMode(status);
          } else if (status.type === 'replaced') {
            replaced = true;
            show('Another device is connected');
            socket.close();
          } else if (status.type === 'waiting') {
            waitingForWindow = true;
            show('Choose a window on your PC');
          }
        } catch { }
        return;
      }
      if (videoReady) return;
      const next = URL.createObjectURL(event.data);
      const previous = objectUrl;
      image.onload = () => { waitingForWindow = false; hide(); if (previous) URL.revokeObjectURL(previous); };
      image.onerror = () => URL.revokeObjectURL(next);
      objectUrl = next;
      image.src = next;
    };
    socket.onclose = event => {
      release();
      clearTimeout(startupTimer);
      peer?.close();
      peer = null;
      currentClientId = null;
      videoReady = false;
      waitingForWindow = false;
      video.style.display = 'none';
      if (replaced || event.code === 4001) {
        show('Another device is connected');
        return;
      }
      show('Disconnected. Reconnecting...');
      clearTimeout(reconnectTimer);
      reconnectTimer = setTimeout(connect, 1500);
    };
    socket.onerror = () => socket.close();
  }

  document.addEventListener('touchstart', event => {
    event.preventDefault();
    keyboard.blur();
    if (event.touches.length === 1) {
      const start = point(event.touches[0]);
      single = start && { start, last: start, startTime: performance.now(), startX: event.touches[0].clientX, startY: event.touches[0].clientY };
      multiple = null;
    } else if (event.touches.length === 2) {
      release();
      multiple = { y: (event.touches[0].clientY + event.touches[1].clientY) / 2, moved: false };
    }
  }, { passive: false });

  document.addEventListener('touchmove', event => {
    event.preventDefault();
    if (event.touches.length === 2 && multiple) {
      const y = (event.touches[0].clientY + event.touches[1].clientY) / 2;
      const delta = y - multiple.y;
      if (Math.abs(delta) > 2) {
        multiple.moved = true;
        send({ type: 'wheel', delta: Math.round(delta * 5) });
        multiple.y = y;
      }
      return;
    }
    if (event.touches.length !== 1 || !single) return;
    const touch = event.touches[0];
    const current = point(touch);
    if (!current) return;
    const distance = Math.hypot(touch.clientX - single.startX, touch.clientY - single.startY);
    if (!held && distance > 7) { send({ type: 'down', ...single.start }); held = true; }
    single.last = current;
    if (held && performance.now() - lastMove > 25) {
      send({ type: 'move', ...current });
      lastMove = performance.now();
    }
  }, { passive: false });

  document.addEventListener('touchend', event => {
    event.preventDefault();
    if (multiple) {
      if (event.touches.length === 0) {
        if (!multiple.moved) keyboard.focus(); // Two-finger tap opens the phone keyboard.
        multiple = null;
      }
      return;
    }
    if (!single || event.touches.length) return;
    const ended = event.changedTouches[0];
    const last = point(ended) || single.last;
    if (held) send({ type: 'up', ...last });
    else if (performance.now() - single.startTime >= 550) send({ type: 'right', ...last });
    else { send({ type: 'down', ...last }); send({ type: 'up', ...last }); }
    held = false;
    single = null;
  }, { passive: false });
  document.addEventListener('touchcancel', event => { event.preventDefault(); release(); multiple = null; }, { passive: false });
  let mouseHeld = false;
  let mouseLast = null;
  document.addEventListener('pointerdown', event => {
    if (event.pointerType !== 'mouse') return;
    const current = point(event);
    if (!current) return;
    event.preventDefault();
    if (event.button === 2) {
      send({ type: 'right', ...current });
    } else if (event.button === 0) {
      mouseHeld = true;
      mouseLast = current;
      send({ type: 'down', ...current });
    }
  });
  document.addEventListener('pointermove', event => {
    if (event.pointerType !== 'mouse' || !mouseHeld) return;
    const current = point(event);
    if (current) {
      mouseLast = current;
      if (performance.now() - lastMove > 25) {
        send({ type: 'move', ...current });
        lastMove = performance.now();
      }
    }
  });
  document.addEventListener('pointerup', event => {
    if (event.pointerType !== 'mouse' || event.button !== 0 || !mouseHeld) return;
    const current = point(event) || mouseLast;
    if (current) send({ type: 'up', ...current });
    mouseHeld = false;
    mouseLast = null;
  });
  document.addEventListener('pointercancel', event => {
    if (event.pointerType !== 'mouse' || !mouseHeld) return;
    if (mouseLast) send({ type: 'up', ...mouseLast });
    mouseHeld = false;
    mouseLast = null;
  });
  document.addEventListener('wheel', event => {
    if (!point(event)) return;
    event.preventDefault();
    send({ type: 'wheel', delta: Math.round(-event.deltaY * (event.deltaMode === 1 ? 40 : event.deltaMode === 2 ? 400 : 1)) });
  }, { passive: false });
  document.addEventListener('contextmenu', event => event.preventDefault());
  document.addEventListener('gesturestart', event => event.preventDefault());

  keyboard.addEventListener('compositionstart', () => { composing = true; });
  keyboard.addEventListener('compositionend', () => {
    composing = false;
    if (keyboard.value) { send({ type: 'text', value: keyboard.value }); keyboard.value = ''; }
  });
  keyboard.addEventListener('input', () => {
    if (!composing && keyboard.value) { send({ type: 'text', value: keyboard.value }); keyboard.value = ''; }
  });
  keyboard.addEventListener('keydown', event => {
    if (['Backspace', 'Enter', 'Tab', 'Escape', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) {
      event.preventDefault();
      send({ type: 'key', value: event.key });
    }
  });
  document.addEventListener('keydown', event => {
    if (event.target === keyboard || event.isComposing || event.ctrlKey || event.altKey || event.metaKey) return;
    if (['Backspace', 'Enter', 'Tab', 'Escape', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) {
      event.preventDefault();
      send({ type: 'key', value: event.key });
    } else if (event.key.length === 1) {
      event.preventDefault();
      send({ type: 'text', value: event.key });
    }
  });
  window.addEventListener('blur', () => {
    release();
    if (mouseHeld && mouseLast) send({ type: 'up', ...mouseLast });
    mouseHeld = false;
    mouseLast = null;
  });
  document.addEventListener('visibilitychange', () => { if (document.hidden) release(); });
  connect();
})();
