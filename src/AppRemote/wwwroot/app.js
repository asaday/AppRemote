(() => {
  const image = document.getElementById('screen');
  const video = document.getElementById('video');
  const message = document.getElementById('message');
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

  function show(text) { message.textContent = text; message.classList.remove('hidden'); }
  function hide() { message.classList.add('hidden'); }
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

  async function startVideo() {
    if (!window.RTCPeerConnection) return;
    const current = new RTCPeerConnection({ iceServers: [] });
    peer = current;
    current.addTransceiver('video', { direction: 'recvonly' });
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
      const response = await fetch('/offer?key=' + encodeURIComponent(key), {
        method: 'POST', headers: { 'Content-Type': 'application/sdp' }, body: current.localDescription.sdp
      });
      if (!response.ok) throw new Error('WebRTC: ' + response.status);
      await current.setRemoteDescription({ type: 'answer', sdp: await response.text() });
    } catch (error) {
      console.warn(error);
      if (peer === current) peer = null;
      current.close();
    }
  }

  video.addEventListener('playing', () => {
    videoReady = true;
    video.style.display = 'block';
    hide();
  });
  function release() {
    if (held && single?.last) send({ type: 'up', ...single.last });
    held = false;
    single = null;
  }

  function connect() {
    const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
    socket = new WebSocket(`${scheme}//${location.host}/ws?key=${encodeURIComponent(key)}`);
    socket.binaryType = 'blob';
    socket.onopen = () => {
      show('Choose a window on your PC');
      startVideo();
    };
    socket.onmessage = event => {
      if (typeof event.data === 'string') {
        try { if (JSON.parse(event.data).type === 'waiting') show('Choose a window on your PC'); } catch { }
        return;
      }
      if (videoReady) return;
      const next = URL.createObjectURL(event.data);
      const previous = objectUrl;
      image.onload = () => { hide(); if (previous) URL.revokeObjectURL(previous); };
      image.onerror = () => URL.revokeObjectURL(next);
      objectUrl = next;
      image.src = next;
    };
    socket.onclose = () => {
      release();
      peer?.close();
      peer = null;
      videoReady = false;
      video.style.display = 'none';
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
  document.addEventListener('visibilitychange', () => { if (document.hidden) release(); });
  connect();
})();
