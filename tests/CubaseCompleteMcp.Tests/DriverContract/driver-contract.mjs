// Driver-contract harness for the Cubase Complete MCP MIDI Remote driver (driver contract 2).
//
// Loads the real generated ES5 driver inside a vm context that substitutes a mock MIDI Remote host, and drives it
// only through its production SysEx entry point (midiInput.mOnSysex). A mock success is not evidence that the real
// Steinberg API behaves the same way; it proves the driver's own contract logic. Checks are grouped by acceptance ID.
//
// Usage: node driver-contract.mjs <path-to-generated-driver.js>
// Prints one JSON summary line {checks, failures, groups} and exits 0 only when every check passes.

import fs from 'node:fs';
import vm from 'node:vm';

const driverPath = process.argv[2];
if (!driverPath) { console.error('driver path is required'); process.exit(2); }
const driverSource = fs.readFileSync(driverPath, 'utf8');

const failures = [];
const groups = {};
let checks = 0;
let currentGroup = 'setup';
function group(name) { currentGroup = name; groups[name] = groups[name] || 0; }
function check(name, ok, detail) {
  checks += 1;
  groups[currentGroup] = (groups[currentGroup] || 0) + 1;
  if (!ok) { failures.push(currentGroup + ': ' + name + (detail === undefined ? '' : ' :: ' + JSON.stringify(detail))); }
}

function param(tag, title) { return { tag: tag, title: title, value: 0, display: '0', locked: false }; }
function node(id, title, uniqueId, params) { return { id: id, title: title, uniqueId: uniqueId, params: params || [], children: [] }; }
function indexTree(root, table) { const map = table || {}; map[root.id] = root; for (const child of root.children) { indexTree(child, map); } return map; }
function findParam(entry, tag) {
  const found = entry.params.filter(function (item) { return item.tag === tag; });
  if (found.length === 0) { throw new Error('unknown parameter tag ' + tag + ' on object ' + entry.id); }
  return found[0];
}

function makeChannel(n, title, params) {
  const tree = node(2000 + n * 10, title, 'uid-ch-' + n, params || [param(0, 'Volume'), param(1, 'Pan'), param(2, 'Mute')]);
  tree.children.push(node(2001 + n * 10, 'Channel EQ', 'uid-eq-' + n, [param(0, 'EQ Low Gain')]));
  return { uid: 'uid-ch-' + n, runtimeId: 700 + n, tree: tree, index: indexTree(tree) };
}

function createHost(options) {
  const opts = options || {};
  const outbox = [];
  let loaded = false;
  const calls = { pulses: {}, surfaceWrites: [], slotAssignments: [], directWrites: [], tempo: [], setTime: [] };
  const bindingsByName = {};
  const device = { id: 'mock-device' };
  const mapping = { id: 'mock-mapping' };

  // Bank slots hold channels; a reorder moves channels between slots without changing their identity.
  const slots = [];
  const channelCount = opts.channelCount === undefined ? 4 : opts.channelCount;
  for (let i = 0; i < 64; i += 1) { slots.push({ slot: i, current: i < channelCount ? makeChannel(i, 'Track ' + i) : null }); }
  const state = { selected: slots[0].current };

  const selectedTree = node(100, 'Selected', 'uid-selected-root', [param(0, 'Volume')]);
  // The selected-track root reports the unique ID of whatever track is selected (empty when none is).
  Object.defineProperty(selectedTree, 'uniqueId', { get: function () { return state.selected ? state.selected.uid : ''; } });
  Object.defineProperty(selectedTree, 'title', { get: function () { return state.selected ? state.selected.tree.title : ''; } });
  selectedTree.children.push(node(102, 'Insert 1', 'uid-selected-insert', [param(0, 'Bypass')]));
  const selectedIndex = indexTree(selectedTree);
  const slotManager = {
    trySetSlotPlugin: function (m, objectId, uid, dontAskDiscard) {
      calls.slotAssignments.push({ objectId: objectId, pluginUid: uid, dontAskDiscard: dontAskDiscard });
      selectedIndex[objectId].title = 'Assigned ' + uid; selectedIndex[objectId].uniqueId = 'assigned-' + uid;
      return true;
    }
  };

  function accessFor(resolveIndex, baseId, manager) {
    function entry(objectId) { const index = resolveIndex(); if (!index || !index[objectId]) { throw new Error('unknown object ' + objectId); } return index[objectId]; }
    return {
      mPluginManager: manager,
      activate: function () {}, deactivate: function () {}, update: function () {},
      getBaseObjectID: function () { return typeof baseId === 'function' ? baseId() : baseId; },
      getNumberOfChildObjects: function (m, objectId) { return entry(objectId).children.length; },
      getChildObjectID: function (m, objectId, i) { return entry(objectId).children[i].id; },
      getObjectUniqueName: function (m, objectId) { return entry(objectId).uniqueId; },
      getObjectUniqueIDString: function (m, objectId) { return entry(objectId).uniqueId; },
      getObjectTitle: function (m, objectId) { return entry(objectId).title; },
      getNumberOfParameters: function (m, objectId) { return entry(objectId).params.length; },
      getParameterTagByIndex: function (m, objectId, i) { return entry(objectId).params[i].tag; },
      getParameterTitle: function (m, objectId, tag, maxLength) { const t = findParam(entry(objectId), tag).title; return maxLength && maxLength < t.length ? t.slice(0, maxLength) : t; },
      getParameterProcessValue: function (m, objectId, tag) { return findParam(entry(objectId), tag).value; },
      getParameterDisplayValue: function (m, objectId, tag) { return findParam(entry(objectId), tag).display; },
      getParameterDisplayUnits: function () { return ''; },
      getParameterEditLockState: function (m, objectId, tag) { return findParam(entry(objectId), tag).locked; },
      setParameterEditLockState: function (m, objectId, tag, locked) { findParam(entry(objectId), tag).locked = locked; },
      setParameterProcessValue: function (m, objectId, tag, value) {
        const p = findParam(entry(objectId), tag); p.value = value; p.display = String(value);
        calls.directWrites.push({ objectId: objectId, tag: tag, value: value });
      },
      setParameterDisplayValue: function (m, objectId, tag, value) { findParam(entry(objectId), tag).display = value; calls.directWrites.push({ objectId: objectId, tag: tag, display: value }); }
    };
  }

  const bankChannels = slots.map(function (slot) {
    return {
      slot: slot,
      getUniqueIDString: function () { calls.hostIdGetter = (calls.hostIdGetter || 0) + 1; return slot.current ? slot.current.uid : ''; },
      getRuntimeID: function () { calls.hostIdGetter = (calls.hostIdGetter || 0) + 1; return slot.current ? slot.current.runtimeId : 0; }
    };
  });
  let bankCursor = 0;
  const bankObject = { makeMixerBankChannel: function () { return bankChannels[bankCursor++]; } };
  const selectedChannel = {
    mValue: {}, getUniqueIDString: function () { calls.hostIdGetter = (calls.hostIdGetter || 0) + 1; return state.selected ? state.selected.uid : ''; },
    getRuntimeID: function () { calls.hostIdGetter = (calls.hostIdGetter || 0) + 1; return state.selected ? state.selected.runtimeId : 0; }
  };

  function makeDirectAccess(hostObject) {
    // Real hosts only allow host objects to be created while the script loads; a runtime call destabilized Cubase.
    if (loaded) { calls.runtimeHostObjectCreation = (calls.runtimeHostObjectCreation || 0) + 1; throw new Error('makeDirectAccess called after script load'); }
    if (hostObject === selectedChannel) { return accessFor(function () { return selectedIndex; }, function () { return state.selected ? 100 : -1; }, opts.api13 ? slotManager : null); }
    if (hostObject && hostObject.slot) {
      return accessFor(function () { return hostObject.slot.current ? hostObject.slot.current.index : null; },
        function () { return hostObject.slot.current ? hostObject.slot.current.tree.id : -1; }, null);
    }
    const empty = node(9000, 'Root', 'uid-root', []);
    // The mixer root is the VST Mixer: its children are the current channels, in mixer order.
    function mixerTree() { const bank = node(900, 'VST Mixer', 'uid-vst-mixer', []); slots.forEach(function (slot) { if (slot.current) { bank.children.push(slot.current.tree); } }); return indexTree(bank); }
    return hostObject === bankObject ? accessFor(mixerTree, 900, null) : accessFor(function () { return indexTree(empty); }, 9000, null);
  }

  function makeVariable(name) {
    return {
      name: name,
      setProcessValue: function (activeDevice, value) {
        calls.surfaceWrites.push({ name: name, value: value });
        if (value === 1) { calls.pulses[name] = (calls.pulses[name] || 0) + 1; }
        if (value === 1 && opts.autoToggle && opts.autoToggle[name] && bindingsByName[opts.autoToggle[name]]) {
          const key = { Record: 'recording', Play: 'playing', Cycle: 'cycle', Metronome: 'metronome' }[name];
          const current = activeContext.runtime.state[key];
          bindingsByName[opts.autoToggle[name]].mOnValueChange(activeDevice, mapping, current === true ? 0 : 1);
        }
      }
    };
  }
  const transportValues = { mStart: { name: 'Play' }, mStop: { name: 'Stop' }, mRecord: { name: 'Record' }, mCycleActive: { name: 'Cycle' }, mMetronomeActive: { name: 'Metronome' } };
  ['mVolume', 'mPan', 'mMute', 'mSolo', 'mMonitorEnable', 'mRecordEnable'].forEach(function (key) { selectedChannel.mValue[key] = { name: key }; });
  function locator(name) { return { mOnChange: null, setTime: function (m, text) { calls.setTime.push({ name: name, text: text }); } }; }
  const timeDisplay = {
    mOnChangeTempoBPM: null, setTempoBPM: function (m, bpm) { calls.tempo.push(bpm); },
    mPrimary: { mTransportLocator: locator('position'), mCycleLocatorLeft: locator('left'), mCycleLocatorRight: locator('right') }
  };
  let activeContext = null;
  const page = {
    mHostAccess: {
      mTransport: { mValue: transportValues, mTimeDisplay: timeDisplay },
      mTrackSelection: { mMixerChannel: selectedChannel, mAction: { mPrevTrack: {}, mNextTrack: {} } },
      mMixConsole: { makeMixerBankZone: function () { return bankObject; } },
      mControlRoom: {}, mFocusedQuickControls: {}, makeDirectAccess: makeDirectAccess
    },
    makeValueBinding: function (value, target) {
      const binding = { value: value, target: target, mOnValueChange: null, setTypeToggle: function () { return binding; } };
      bindingsByName[target && target.name ? target.name : value.name] = binding;
      return binding;
    },
    makeActionBinding: function () { return { mOnValueChange: null }; },
    makeCommandBinding: function () { return { canPerform: opts.api13 ? function () { return true; } : undefined }; },
    mOnActivate: null, mOnDeactivate: null
  };
  const midiInput = { mOnSysex: null };
  const midiOutput = { sendMidi: function (activeDevice, message) { outbox.push(Array.prototype.slice.call(message)); } };
  const api = {
    makeDeviceDriver: function () {
      return {
        mPorts: { makeMidiInput: function () { return midiInput; }, makeMidiOutput: function () { return midiOutput; } },
        mSurface: { makeCustomValueVariable: makeVariable },
        makeDetectionUnit: function () { return { detectPortPair: function () { return { expectInputNameEquals: function () {}, expectOutputNameEquals: function () {} }; }, mPlatformFilter: { expectPlatformWindows: function () {} } }; },
        mMapping: { makePage: function () { return page; } }
      };
    },
    mDefaults: { mAppVersion: { getVersionString: function () { return opts.hostVersion || '15.0.10'; } } }
  };
  return {
    api: api, page: page, device: device, mapping: mapping, calls: calls, outbox: outbox, bindingsByName: bindingsByName,
    setContext: function (value) { activeContext = value; }, markLoaded: function () { loaded = true; }, bankChannels: bankChannels, slots: slots, state: state, midiInput: midiInput,
    selectedIndex: selectedIndex, timeDisplay: timeDisplay
  };
}

function loadDriver(host) {
  const sandbox = {
    require: function (name) { if (name !== 'midiremote_api_v1') { throw new Error('unexpected module: ' + name); } return host.api; },
    console: console, Date: Date, Math: Math, JSON: JSON, String: String, Number: Number, Object: Object,
    Array: Array, Error: Error, isFinite: isFinite, parseInt: parseInt, parseFloat: parseFloat
  };
  const context = vm.createContext(sandbox);
  host.setContext(context);
  new vm.Script(driverSource, { filename: 'CBMCP_CompleteBridge.js' }).runInContext(context);
  host.markLoaded();
  host.context = context;
  context.page.mOnActivate(host.device, host.mapping);
  deliverTitles(host);
  return context;
}
// The host reports channel titles through each bank channel's mOnTitleChange callback.
function deliverTitles(host) {
  host.bankChannels.forEach(function (channel) {
    if (channel.mOnTitleChange) { channel.mOnTitleChange(host.device, host.mapping, channel.slot.current ? channel.slot.current.tree.title : ''); }
  });
}

function drain(host) {
  const messages = host.outbox.splice(0, host.outbox.length);
  const pending = {};
  const envelopes = [];
  for (const message of messages) {
    const frame = host.context.decodeChunk(message);
    const key = frame.kind + ':' + frame.sessionId + ':' + frame.requestId + ':' + frame.opcode;
    let entry = pending[key];
    if (!entry) { entry = { frame: frame, parts: [], received: 0 }; pending[key] = entry; }
    if (entry.parts[frame.chunkIndex] === undefined) { entry.parts[frame.chunkIndex] = frame.payload; entry.received += 1; }
    if (entry.received !== entry.frame.chunkCount) { continue; }
    const joined = [];
    for (let i = 0; i < entry.received; i += 1) { Array.prototype.push.apply(joined, entry.parts[i]); }
    envelopes.push({ kind: entry.frame.kind, sessionId: entry.frame.sessionId, opcode: entry.frame.opcode, data: JSON.parse(Buffer.from(joined).toString('utf8')) });
  }
  return envelopes;
}
// Sends a request frame with an explicit session and request ID through the driver's own SysEx entry point.
function sendRaw(host, operation, args, sessionId, requestId) {
  const payload = Array.from(Buffer.from(JSON.stringify({ operation: operation, arguments: args || {} }), 'utf8'));
  const saved = host.context.runtime.sessionId;
  host.context.runtime.sessionId = sessionId; // encodeChunk stamps runtime.sessionId into the header
  const wire = host.context.encodeChunk(1, requestId, host.context.OPCODE[operation], 0, 1, payload);
  host.context.runtime.sessionId = saved;
  host.midiInput.mOnSysex(host.device, wire);
  const drained = drain(host);
  const terminal = drained.filter(function (item) { return item.kind === 2 || item.kind === 5; });
  return { all: drained, terminal: terminal.length === 0 ? null : terminal[terminal.length - 1] };
}
function send(host, operation, args) {
  host.requestId = (host.requestId || 100) + 1;
  return sendRaw(host, operation, args, host.context.runtime.sessionId, host.requestId);
}
function ok(host, name, operation, args) {
  const result = send(host, operation, args);
  check(name + ' responds', !!result.terminal && result.terminal.kind === 2, result.terminal && result.terminal.data);
  return result.terminal && result.terminal.kind === 2 ? result.terminal.data.data : null;
}
function refused(host, name, operation, args, code) {
  const result = send(host, operation, args);
  const error = result.terminal && result.terminal.data && result.terminal.data.error;
  check(name + ' is refused', !!result.terminal && result.terminal.kind === 5, result.terminal && result.terminal.data);
  check(name + ' reports ' + code, !!error && error.code === code, error);
  return error;
}
function pulses(host, name) { return host.calls.pulses[name] || 0; }
function mixerIndex(host, slot) { return host.slots.slice(0, slot).filter(function (item) { return item.current; }).length; }
function channelTarget(host, slot) { const c = host.slots[slot].current; return { unique_id: c.uid, runtime_id: c.tree.id, bank_index: mixerIndex(host, slot) }; }
function selectedGuard(host) { return { unique_id: host.state.selected.uid, runtime_id: host.state.selected.tree.id }; }
let runtimeCreations = 0;
let hostIdGetterCalls = 0;
function scenario(label, options, body) { const host = createHost(options || {}); loadDriver(host); group(label); body(host); runtimeCreations += host.calls.runtimeHostObjectCreation || 0; hostIdGetterCalls += host.calls.hostIdGetter || 0; }

scenario('hello and capabilities', { api13: true }, function (host) {
  const hello = ok(host, 'hello', 'system.hello', { client_nonce: 'n1' });
  check('hello echoes nonce and session', hello && hello.client_nonce === 'n1' && hello.session_id === host.context.runtime.sessionId, hello);
  check('hello declares driver contract 2', hello && hello.driver_contract_version === 2, hello);
  check('hello carries the embedded build id', hello && typeof hello.driver_build_id === 'string' && hello.driver_build_id.length > 0, hello);
  check('plug-in catalog is not claimed', hello && hello.capabilities.plugin_catalog === false, hello && hello.capabilities);
});

scenario('SES-08 activation resets the mirror', { autoToggle: { Record: 'Record' } }, function (host) {
  const initial = ok(host, 'transport.get after activation', 'transport.get', {});
  check('recording is unknown (null), not false, after activation', initial && initial.state.recording === null, initial && initial.state);
  refused(host, 'record_stop with unknown state', 'transport.set', { action: 'record_stop' }, 'STATE_UNKNOWN');
  check('no pulse was sent for the refused toggle', pulses(host, 'Record') === 0);
  host.bindingsByName.Record.mOnValueChange(host.device, host.mapping, 1);
  const known = ok(host, 'transport.get after callback', 'transport.get', {});
  check('host callback makes the state known', known && known.state.recording === true && known.state_revision.recording > 0, known);
  const oldSession = host.context.runtime.sessionId;
  host.context.page.mOnDeactivate(host.device, host.mapping);
  host.context.page.mOnActivate(host.device, host.mapping);
  const reset = ok(host, 'transport.get after re-activation', 'transport.get', {});
  check('re-activation starts a new session', host.context.runtime.sessionId !== oldSession);
  check('re-activation forgets the previous state', reset && reset.state.recording === null && reset.state_revision.recording === undefined, reset);
});

scenario('SES-05/06 stale session and request cache', { autoToggle: { Cycle: 'Cycle' } }, function (host) {
  host.bindingsByName.Cycle.mOnValueChange(host.device, host.mapping, 0);
  const session = host.context.runtime.sessionId;
  const first = sendRaw(host, 'transport.set', { action: 'cycle', enabled: true }, session, 500);
  check('first write responds', first.terminal && first.terminal.kind === 2, first.terminal);
  check('first write pulses once', pulses(host, 'Cycle') === 1);
  const duplicate = sendRaw(host, 'transport.set', { action: 'cycle', enabled: true }, session, 500);
  check('same id and payload returns the cached reply', duplicate.terminal && duplicate.terminal.kind === 2 && JSON.stringify(duplicate.terminal.data) === JSON.stringify(first.terminal.data));
  check('cached reply does not re-apply the write', pulses(host, 'Cycle') === 1);
  const conflict = sendRaw(host, 'transport.set', { action: 'cycle', enabled: false }, session, 500);
  check('same id with a different payload is a conflict', conflict.terminal && conflict.terminal.kind === 5 && conflict.terminal.data.error.code === 'REQUEST_ID_CONFLICT', conflict.terminal);
  check('the conflicting request is not executed', pulses(host, 'Cycle') === 1);
  host.context.page.mOnDeactivate(host.device, host.mapping);
  host.context.page.mOnActivate(host.device, host.mapping);
  const stale = sendRaw(host, 'transport.set', { action: 'cycle', enabled: true }, session, 500);
  check('old-session request is refused as STALE_SESSION before the cache', stale.terminal && stale.terminal.data.error && stale.terminal.data.error.code === 'STALE_SESSION', stale.terminal);
  check('stale request executes nothing', pulses(host, 'Cycle') === 1);
});

scenario('VER-05 record target states', { autoToggle: { Record: 'Record' } }, function (host) {
  host.bindingsByName.Record.mOnValueChange(host.device, host.mapping, 1);
  const stop = ok(host, 'record enabled=false while recording', 'transport.set', { action: 'record', enabled: false });
  check('stop is a single pulse', pulses(host, 'Record') === 1 && stop.mutation_sent === true && stop.action === 'record_stop', stop);
  check('receipt carries dispatch revision and desired state, not a verdict', stop && typeof stop.dispatch_revision === 'number' && stop.desired === false && stop.verified === undefined, stop);
  const again = ok(host, 'redundant stop', 'transport.set', { action: 'record_stop' });
  check('redundant stop sends nothing', again && again.mutation_sent === false && pulses(host, 'Record') === 1, again);
  const start = ok(host, 'record_start', 'transport.set', { action: 'record_start' });
  check('start pulses once', start && start.mutation_sent === true && pulses(host, 'Record') === 2, start);
});

scenario('VER-06 tempo and positions', {}, function (host) {
  host.context.timeDisplay.mOnChangeTempoBPM(host.device, host.mapping, 120);
  const tempo = ok(host, 'tempo change', 'transport.set', { action: 'tempo', bpm: 128 });
  check('tempo is sent to the host once', host.calls.tempo.length === 1 && tempo.mutation_sent === true, host.calls.tempo);
  const same = ok(host, 'tempo no-op', 'transport.set', { action: 'tempo', bpm: 120.001 });
  check('tempo within half the tolerance is a no-op', same && same.mutation_sent === false && host.calls.tempo.length === 1, same);
  refused(host, 'numeric position value', 'transport.set', { action: 'position', value: 12 }, 'INVALID_ARGUMENT');
  refused(host, 'position before a time format is known', 'transport.set', { action: 'position', position_text: '2.1.1.0', time_format: 'Bars+Beats' }, 'STATE_UNKNOWN');
  host.context.timeDisplay.mPrimary.mTransportLocator.mOnChange(host.device, host.mapping, '1.1.1.0', 'Bars+Beats');
  refused(host, 'position in another time format', 'transport.set', { action: 'position', position_text: '0:00:02.000', time_format: 'Seconds' }, 'UNSUPPORTED_TIME_FORMAT');
  const moved = ok(host, 'position in the host format', 'transport.set', { action: 'position', position_text: '2.1.1.0', time_format: 'Bars+Beats' });
  check('position is set once with the given text', host.calls.setTime.length === 1 && host.calls.setTime[0].text === '2.1.1.0' && moved.time_format === 'Bars+Beats', host.calls.setTime);
  refused(host, 'unknown transport action', 'transport.set', { action: 'warp' }, 'INVALID_ACTION');
});

scenario('TGT-06 track list scope', { channelCount: 4 }, function (host) {
  const list = ok(host, 'track.list', 'track.list', {});
  check('only occupied bank slots are listed', list && list.channels.length === 4 && list.available_channel_count === 4, list && list.channels.length);
  check('each channel carries identity and slot', list && list.channels.every(function (c) { return c.unique_id && typeof c.runtime_id === 'number' && typeof c.bank_index === 'number'; }), list && list.channels[0]);
  check('titles come from DirectAccess object titles', list && list.channels[2].title === 'Track 2', list && list.channels[2]);
  check('runtime_id is the object ID in the mixer root', list && list.channels[1].runtime_id === host.slots[1].current.tree.id, list && list.channels[1]);
  check('listing never creates host objects at runtime', !host.calls.runtimeHostObjectCreation, host.calls.runtimeHostObjectCreation);
  check('EQ child objects are not listed as tracks', list && list.channels.every(function (c) { return c.unique_id.indexOf('uid-eq') !== 0; }));
  check('a partly filled bank is not truncated', list && list.bank_truncated === false, list);
});
scenario('TGT-06 large mixer is not capped at 64', { channelCount: 64 }, function (host) {
  const list = ok(host, 'track.list', 'track.list', {});
  check('all 64 channels are listed without truncation', list && list.channels.length === 64 && list.bank_truncated === false, list && list.channels.length);
  check('listing scope is the VST Mixer', list && list.scope === 'vst_mixer', list && list.scope);
});

scenario('TGT-01 target-addressed mixer read and write', {}, function (host) {
  const a = host.slots[0].current;
  const b = host.slots[1].current;
  const write = ok(host, 'mixer.set on B', 'mixer.set', { target: channelTarget(host, 1), parameter: 'volume', process_value: 0.75 });
  check('the write lands on B', host.calls.directWrites.length === 1 && host.calls.directWrites[0].objectId === b.tree.id, host.calls.directWrites);
  check('A is untouched', findParam(a.tree, 0).value === 0 && findParam(b.tree, 0).value === 0.75);
  check('the receipt reports B identity and a same-callback read-back', write && write.target.unique_id === b.uid && write.after === 0.75 && write.mutation_sent === true, write);
  const read = ok(host, 'mixer.get on B', 'mixer.get', { target: channelTarget(host, 1), parameter_tag: 0 });
  check('the read is a fresh read of B', read && read.fresh === true && read.process_value === 0.75 && read.target.unique_id === b.uid, read);
  const noop = ok(host, 'same value again', 'mixer.set', { target: channelTarget(host, 1), parameter: 'volume', process_value: 0.75 });
  check('an already-satisfied write is a no-op', noop && noop.mutation_sent === false && host.calls.directWrites.length === 1, noop);
  refused(host, 'protocol-1 track_id', 'mixer.set', { track_id: 1, parameter: 'volume', value: 0.2 }, 'LEGACY_TARGET_AMBIGUOUS');
  refused(host, 'write without a target', 'mixer.set', { parameter: 'volume', value: 0.2 }, 'TARGET_REQUIRED');
});

scenario('TGT-03 reorder and replacement', {}, function (host) {
  const planned = channelTarget(host, 1);
  const b = host.slots[1].current;
  const a = host.slots[0].current;
  host.slots[1].current = a; host.slots[0].current = b; // Cubase reordered the bank
  deliverTitles(host);
  const write = ok(host, 'write after reorder', 'mixer.set', { target: planned, parameter: 'pan', process_value: 0.2 });
  check('the write follows identity to the new slot', write && write.target.unique_id === b.uid && write.target.bank_index === 0 && write.target.relocated === true, write && write.target);
  check('the channel now at the old slot is untouched', findParam(a.tree, 1).value === 0 && findParam(b.tree, 1).value === 0.2);
  const replaced = makeChannel(9, 'Replacement'); replaced.uid = b.uid; replaced.tree.uniqueId = b.uid; replaced.index = indexTree(replaced.tree);
  host.slots[0].current = replaced; // object replaced: same unique ID, new object ID
  const writesBefore = host.calls.directWrites.length;
  refused(host, 'write after the object was replaced', 'mixer.set', { target: planned, parameter: 'pan', process_value: 0.9 }, 'STALE_TARGET');
  check('no write reached any channel', host.calls.directWrites.length === writesBefore);
  host.slots[0].current = null;
  refused(host, 'write after the channel left the mixer', 'mixer.set', { target: { unique_id: b.uid, runtime_id: replaced.tree.id, bank_index: 0 }, parameter: 'pan', process_value: 0.9 }, 'STALE_TARGET');
});

scenario('TGT-07 parameter resolution', {}, function (host) {
  host.slots[2].current = makeChannel(2, 'Dup', [param(0, 'Volume'), param(1, 'Volume'), param(2, 'Pan')]);
  host.slots[3].current = makeChannel(3, 'Korean', [param(0, '볼륨'), param(1, 'Volume Send 1')]);
  refused(host, 'duplicate titles', 'mixer.set', { target: channelTarget(host, 2), parameter: 'volume', process_value: 0.1 }, 'PARAMETER_AMBIGUOUS');
  const korean = ok(host, 'Korean volume title', 'mixer.set', { target: channelTarget(host, 3), parameter: 'volume', process_value: 0.3 });
  check('a Korean title resolves by exact alias, not by prefix', korean && korean.parameter_tag === 0, korean);
  refused(host, 'prefix-only match', 'mixer.set', { target: channelTarget(host, 3), parameter: 'pan', process_value: 0.3 }, 'PARAMETER_NOT_EXPOSED');
  findParam(host.slots[1].current.tree, 0).locked = true;
  const writes = host.calls.directWrites.length;
  refused(host, 'edit-locked parameter', 'mixer.set', { target: channelTarget(host, 1), parameter: 'volume', process_value: 0.3 }, 'PARAMETER_LOCKED');
  check('the lock is not stolen and nothing is written', host.calls.directWrites.length === writes && findParam(host.slots[1].current.tree, 0).locked === true);
  refused(host, 'out-of-range value', 'mixer.set', { target: channelTarget(host, 0), parameter: 'volume', process_value: 1.5 }, 'INVALID_ARGUMENT');
  refused(host, 'plain value without a host converter', 'mixer.set', { target: channelTarget(host, 0), parameter: 'volume', plain_value: -6 }, 'UNSUPPORTED_CAPABILITY');
  refused(host, 'object outside the target', 'mixer.set', { target: channelTarget(host, 0), object_id: 2011, parameter_tag: 0, process_value: 0.1 }, 'TARGET_SCOPE_MISMATCH');
});

scenario('TGT-02 selection guard in the command callback', {}, function (host) {
  const count = host.context.runtime.commandIds.length;
  check('catalog commands are registered', count > 0);
  const id = host.context.runtime.commandIds[0];
  const expected = selectedGuard(host);
  host.state.selected = host.slots[1].current; // the user selected another track after planning
  refused(host, 'guarded command after a selection change', 'command.execute', { id: id, requires_target: true, expected_selected: expected }, 'TARGET_CHANGED');
  check('no command pulse was sent', pulses(host, 'Command ' + id) === 0);
  refused(host, 'target-requiring command without a guard', 'command.execute', { id: id, requires_target: true }, 'TARGET_CONTEXT_UNSAFE');
  const guarded = ok(host, 'guarded command with the right selection', 'command.execute', { id: id, requires_target: true, expected_selected: selectedGuard(host) });
  check('the guarded command pulses once', pulses(host, 'Command ' + id) === 1 && guarded.mutation_sent === true, guarded);
  refused(host, 'unknown command id', 'command.execute', { id: 'cmd-missing' }, 'COMMAND_NOT_FOUND');
});

scenario('selected-track mixer path', { autoToggle: {} }, function (host) {
  const guard = selectedGuard(host);
  refused(host, 'toggle with unknown mute state', 'mixer.set', { expected_selected: guard, parameter: 'mute', value: true }, 'STATE_UNKNOWN');
  host.bindingsByName.mMute.mOnValueChange = host.bindingsByName.mMute.mOnValueChange || null;
  host.context.selectedValues.mMute.mOnProcessValueChange(host.device, host.mapping, 0);
  const mute = ok(host, 'mute the selected track', 'mixer.set', { expected_selected: guard, parameter: 'mute', value: true });
  check('mute is one pulse on the selected binding', mute && mute.mutation_sent === true && pulses(host, 'Selected Mute') === 1, mute);
  const volume = ok(host, 'selected volume', 'mixer.set', { expected_selected: guard, parameter: 'volume', value: 0.4 });
  check('volume goes to the selected binding with identity recorded', volume && volume.target.unique_id === guard.unique_id, volume);
  host.state.selected = host.slots[2].current;
  refused(host, 'selected write after the selection changed', 'mixer.set', { expected_selected: guard, parameter: 'volume', value: 0.1 }, 'TARGET_CHANGED');
});

scenario('plug-in slots on an API 1.3 host', { api13: true }, function (host) {
  const guard = selectedGuard(host);
  const catalog = ok(host, 'catalog', 'plugin.get', { action: 'catalog' });
  check('no UID catalog is fabricated', catalog && catalog.available === false && catalog.entries.length === 0, catalog);
  const inspected = ok(host, 'inspect_slot', 'plugin.get', { action: 'inspect_slot', expected_selected: guard, object_id: 102 });
  check('inspect_slot reports the slot', inspected && inspected.slot.title === 'Insert 1', inspected);
  refused(host, 'assign after the slot changed', 'plugin.set', { action: 'assign', expected_selected: guard, object_id: 102, plugin_uid: 'u1', expected_slot_unique_id: 'something-else' }, 'TARGET_CHANGED');
  check('nothing was assigned', host.calls.slotAssignments.length === 0);
  const assigned = ok(host, 'assign', 'plugin.set', { action: 'assign', expected_selected: guard, object_id: 102, plugin_uid: 'u1', expected_slot_unique_id: 'uid-selected-insert' });
  check('assignment keeps the host discard prompt by default', host.calls.slotAssignments.length === 1 && host.calls.slotAssignments[0].dontAskDiscard === false, host.calls.slotAssignments);
  check('assignment is a receipt, not a verified result', assigned && assigned.accepted === true && assigned.verified === undefined && assigned.mutation_sent === true, assigned);
  ok(host, 'assign with confirm_replace', 'plugin.set', { action: 'assign', expected_selected: guard, object_id: 102, plugin_uid: 'u2', confirm_replace: true });
  check('confirm_replace is honoured only when given', host.calls.slotAssignments[1].dontAskDiscard === true);
  refused(host, 'assign without a target', 'plugin.set', { action: 'assign', object_id: 102, plugin_uid: 'u3' }, 'TARGET_REQUIRED');
  refused(host, 'bypass is not offered', 'plugin.set', { action: 'bypass', expected_selected: guard, object_id: 102 }, 'INVALID_ACTION');
});

scenario('API 1.2 host', { api13: false }, function (host) {
  const guard = selectedGuard(host);
  check('API 1.3 plug-in manager is reported unavailable', host.context.capabilities().api_1_3_plugin_manager === false);
  refused(host, 'assign without a plug-in manager', 'plugin.set', { action: 'assign', expected_selected: guard, object_id: 102, plugin_uid: 'u1' }, 'UNSUPPORTED_CAPABILITY');
  const capability = ok(host, 'can_perform', 'command.can_perform', { id: host.context.runtime.commandIds[0] });
  check('can_perform is unknown (null) without API 1.3, not true', capability && capability.can_perform === null, capability);
});

scenario('F08 snapshot truncation', {}, function (host) {
  const shallow = ok(host, 'direct.get max_depth=0', 'direct.get', { root: 'mixer', max_depth: 0 });
  check('max_depth zero is preserved', shallow && shallow.limits.max_depth === 0 && shallow.nodes.length === 1, shallow && shallow.limits);
  check('depth truncation is reported', shallow && shallow.truncation.depth_limit_children > 0 && shallow.truncated === true, shallow && shallow.truncation);
  const narrow = ok(host, 'direct.get max_nodes', 'direct.get', { root: 'mixer', max_nodes: 2 });
  check('node-limit truncation is reported', narrow && narrow.truncation.node_limit_children > 0 && narrow.nodes.length <= 2, narrow && narrow.truncation);
  const full = ok(host, 'direct.get default', 'direct.get', { root: 'mixer' });
  check('a complete read is untruncated', full && full.truncated === false, full && full.truncation);
  const targeted = ok(host, 'direct.get fresh parameter read', 'direct.get', { target: channelTarget(host, 0), object_id: 2000, parameter_tag: 0 });
  check('an addressed direct read is fresh', targeted && targeted.fresh === true && targeted.read.parameter_tag === 0, targeted);
  refused(host, 'direct.set without a target', 'direct.set', { root: 'mixer', object_id: 2000, parameter_tag: 0, process_value: 0.1 }, 'TARGET_REQUIRED');
});

group('host object lifetime');
check('no scenario created a host object after script load', runtimeCreations === 0, runtimeCreations);
check('no scenario called HostObject getUniqueIDString/getRuntimeID', hostIdGetterCalls === 0, hostIdGetterCalls);
console.log(JSON.stringify({ checks: checks, failures: failures, groups: groups }));
process.exit(failures.length === 0 ? 0 : 1);
