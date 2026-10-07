const state = {
  commands: [],
  logs: [],
  lastLogId: 0,
  toastTimer: null
};

const elements = {
  serialStatus: document.querySelector('#serialStatus'),
  serialDetails: document.querySelector('#serialDetails'),
  oscStatus: document.querySelector('#oscStatus'),
  oscDetails: document.querySelector('#oscDetails'),
  oscPing: document.querySelector('#oscPing'),
  balloonNumber: document.querySelector('#balloonNumber'),
  balloonSpeed: document.querySelector('#balloonSpeed'),
  sendBalloonSequence: document.querySelector('#sendBalloonSequence'),
  sectorDevice: document.querySelector('#sectorDevice'),
  sectorNumber: document.querySelector('#sectorNumber'),
  sectorRoutePreview: document.querySelector('#sectorRoutePreview'),
  sendSectorSequence: document.querySelector('#sendSectorSequence'),
  searchInput: document.querySelector('#searchInput'),
  categoryFilter: document.querySelector('#categoryFilter'),
  customHex: document.querySelector('#customHex'),
  customSend: document.querySelector('#customSend'),
  commandCount: document.querySelector('#commandCount'),
  commandsBody: document.querySelector('#commandsBody'),
  logsBody: document.querySelector('#logsBody'),
  emptyLogs: document.querySelector('#emptyLogs'),
  pauseLogs: document.querySelector('#pauseLogs'),
  channelFilter: document.querySelector('#channelFilter'),
  clearLogs: document.querySelector('#clearLogs'),
  toast: document.querySelector('#toast')
};

async function request(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) {
    let message = `Błąd HTTP ${response.status}`;
    try { message = (await response.json()).error || message; } catch { /* empty */ }
    throw new Error(message);
  }
  return response.status === 204 ? null : response.json();
}

async function loadCommands() {
  state.commands = await request('/api/commands');
  const categories = [...new Set(state.commands.map(item => item.category))].sort((a, b) => a.localeCompare(b, 'pl'));
  for (const category of categories) {
    const option = document.createElement('option');
    option.value = category;
    option.textContent = category;
    elements.categoryFilter.append(option);
  }
  renderCommands();
}

function renderCommands() {
  const phrase = elements.searchInput.value.trim().toLocaleLowerCase('pl');
  const category = elements.categoryFilter.value;
  const visible = state.commands.filter(command => {
    if (category && command.category !== category) return false;
    if (!phrase) return true;
    return [command.dataHex, command.name, command.wire, command.note, command.oscAddress, command.oscRelation]
      .filter(Boolean)
      .some(value => value.toLocaleLowerCase('pl').includes(phrase));
  });

  elements.commandCount.textContent = `${visible.length} z ${state.commands.length} pozycji`;
  elements.commandsBody.innerHTML = visible.map(command => `
    <tr class="${command.isReserved ? 'reserved' : ''}">
      <td><span class="hex">${escapeHtml(command.dataHex)}</span></td>
      <td>
        <span class="command-name">${escapeHtml(command.name)}</span>
        <span class="meta">
          <span class="tag">${escapeHtml(command.category)}</span>
          ${command.wire ? `<span class="tag">przewód ${escapeHtml(command.wire)}</span>` : ''}
          ${command.isReserved ? '<span class="tag">rezerwa</span>' : ''}
        </span>
        ${command.note ? `<span class="note">${escapeHtml(command.note)}</span>` : ''}
      </td>
      <td>
        <span class="raw">${escapeHtml(command.frameHex)}</span>
        <span class="meta">${command.expectedAck ? `oczekiwany ACK ${escapeHtml(command.expectedAck)}` : 'bez oczekiwania na ACK'}</span>
      </td>
      <td class="osc-map">
        ${escapeHtml(command.oscRelation)}
        <span class="osc-address">${escapeHtml(command.oscAddress)}</span>
      </td>
      <td><button class="button primary send-command" data-command="${command.dataHex}" ${command.isSendable ? '' : 'disabled'}>Wyślij UART</button></td>
    </tr>`).join('');
}

async function sendCommand(value) {
  const normalized = value.trim().replace(/^0x/i, '').padStart(2, '0').toUpperCase();
  if (!/^[0-9A-F]{2}$/.test(normalized)) {
    showToast('Podaj dokładnie jeden bajt hex, np. 54.', true);
    return;
  }

  try {
    const result = await request(`/api/commands/${normalized}/send`, { method: 'POST' });
    showToast(`Wysłano ${result.command}: ${result.name} — ${result.frameHex}`);
  } catch (error) {
    showToast(error.message, true);
  }
}

async function refreshStatus() {
  try {
    const status = await request('/api/status');
    elements.serialStatus.textContent = status.serialConnected ? 'Połączony' : 'Rozłączony';
    elements.serialStatus.className = status.serialConnected ? 'online' : 'offline';
    elements.serialDetails.textContent = status.serialConnected
      ? `${status.serialPort} · ${status.baudRate} 8N1`
      : status.availablePorts.length
        ? `Dostępne: ${status.availablePorts.join(', ')} · ustaw Serial.PortName`
        : 'Brak portów · automatyczne ponowne łączenie';
    elements.oscStatus.textContent = status.oscOnline ? 'Millumin odpowiada' : 'Brak odpowiedzi';
    elements.oscStatus.className = status.oscOnline ? 'online' : 'offline';
    const feedback = status.lastOscReceivedAt
      ? `ostatni RX ${new Date(status.lastOscReceivedAt).toLocaleTimeString('pl-PL')}`
      : `feedback UDP ${status.oscFeedbackPort}`;
    elements.oscDetails.textContent = `${status.oscTarget} · ${feedback}`;
  } catch {
    elements.serialStatus.textContent = 'Panel offline';
    elements.serialStatus.className = 'offline';
  }
}

async function refreshLogs() {
  try {
    const incoming = await request(`/api/logs?after=${state.lastLogId}`);
    if (incoming.length) {
      state.logs.push(...incoming);
      state.lastLogId = incoming.at(-1).id;
      if (state.logs.length > 1000) state.logs.splice(0, state.logs.length - 1000);
      if (!elements.pauseLogs.checked) renderLogs();
    }
  } catch { /* status refresh reports service availability */ }
}

function renderLogs() {
  const channel = elements.channelFilter.value;
  const visible = state.logs.filter(item => !channel || item.channel === channel).slice().reverse();
  elements.emptyLogs.hidden = visible.length > 0;
  elements.logsBody.innerHTML = visible.map(item => {
    const time = new Date(item.timestamp).toLocaleTimeString('pl-PL', {
      hour: '2-digit', minute: '2-digit', second: '2-digit',
      fractionalSecondDigits: 3, hour12: false
    });
    return `<tr>
      <td class="raw">${escapeHtml(time)}</td>
      <td>${escapeHtml(item.channel)}</td>
      <td><span class="direction ${item.direction.toLowerCase()}">${escapeHtml(item.direction)}</span></td>
      <td><span class="hex">${escapeHtml(item.summary)}</span></td>
      <td>${escapeHtml(item.translation)}</td>
      <td><span class="raw">${escapeHtml(item.rawBytes || '—')}</span></td>
    </tr>`;
  }).join('');
}

async function clearLogs() {
  try {
    await request('/api/logs/clear', { method: 'POST' });
    state.logs = [];
    state.lastLogId = 0;
    renderLogs();
  } catch (error) {
    showToast(error.message, true);
  }
}

async function testOsc() {
  try {
    await request('/api/osc/ping', { method: 'POST' });
    showToast('Wysłano OSC /ping. Status zmieni się po odebraniu feedbacku z Millumina.');
  } catch (error) {
    showToast(error.message, true);
  }
}

async function sendOscModel(address) {
  try {
    await request('/api/osc/model', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ address })
    });
    showToast(`Przyjęto ${address}`);
  } catch (error) {
    showToast(error.message, true);
  }
}

function updateSectorSequence() {
  const selectedDevice = elements.sectorDevice.selectedOptions[0];
  const maxSector = Number(selectedDevice.dataset.maxSector);
  for (const option of elements.sectorNumber.options) {
    option.disabled = Number(option.value) > maxSector;
  }
  if (Number(elements.sectorNumber.value) > maxSector) {
    elements.sectorNumber.value = String(maxSector);
  }
  elements.sectorRoutePreview.textContent =
    `${elements.sectorDevice.value}/sektor/${elements.sectorNumber.value}/on`;
}

function showToast(message, error = false) {
  clearTimeout(state.toastTimer);
  elements.toast.textContent = message;
  elements.toast.className = `toast show${error ? ' error' : ''}`;
  state.toastTimer = setTimeout(() => { elements.toast.className = 'toast'; }, 3500);
}

function escapeHtml(value) {
  return String(value)
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#039;');
}

elements.searchInput.addEventListener('input', renderCommands);
elements.categoryFilter.addEventListener('change', renderCommands);
elements.commandsBody.addEventListener('click', event => {
  const button = event.target.closest('.send-command');
  if (button) sendCommand(button.dataset.command);
});
elements.customSend.addEventListener('click', () => sendCommand(elements.customHex.value));
elements.customHex.addEventListener('keydown', event => {
  if (event.key === 'Enter') sendCommand(elements.customHex.value);
});
elements.channelFilter.addEventListener('change', renderLogs);
elements.pauseLogs.addEventListener('change', () => { if (!elements.pauseLogs.checked) renderLogs(); });
elements.clearLogs.addEventListener('click', clearLogs);
elements.oscPing.addEventListener('click', testOsc);
elements.sendBalloonSequence.addEventListener('click', () =>
  sendOscModel(`/makieta/balon/${elements.balloonNumber.value}/predkosc/${elements.balloonSpeed.value}`));
elements.sectorDevice.addEventListener('change', updateSectorSequence);
elements.sectorNumber.addEventListener('change', updateSectorSequence);
elements.sendSectorSequence.addEventListener('click', () =>
  sendOscModel(`${elements.sectorDevice.value}/sektor/${elements.sectorNumber.value}/on`));
updateSectorSequence();

Promise.all([loadCommands(), refreshStatus(), refreshLogs()]).catch(error => showToast(error.message, true));
setInterval(refreshStatus, 1500);
setInterval(refreshLogs, 600);
