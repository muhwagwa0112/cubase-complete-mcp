// Driver-contract harness for the Cubase Complete MCP MIDI Remote driver.
//
// Loads the real generated ES5 driver inside a vm context that substitutes a mock
// MIDI Remote host, then drives the driver through its own SysEx codec. The checks
// cover the driver-side contracts that a text search cannot prove:
//   F01  plug-in slot assignment must not silently skip the host discard prompt
//   F02  transport record must honour an explicit target state
//   F03  mixer writes with track_id must reach that channel and fail loudly otherwise
//   F08  snapshot truncation must be reported per axis, and max_depth=0 must survive
//
// Usage: node driver-contract.mjs <path-to-generated-driver.js>
// Exits 0 when every check passes and prints a JSON summary line.

import fs from 'node:fs';
import vm from 'node:vm';

const driverPath = process.argv[2];
if (!driverPath) { console.error('driver path is required'); process.exit(2); }
const driverSource = fs.readFileSync(driverPath, 'utf8');

const failures = [];
let checks = 0;
function check(name, ok, detail) {
  checks += 1;
  if (!ok) { failures.push(name + (detail === undefined ? '' : ' :: ' + JSON.stringify(detail))); }
}
function parse(text) { return JSON.parse(text); }

function param(tag, title, units) {
  return { tag: tag, title: title, units: units || '', value: 0, display: '0', locked: false };
}
function node(id, title, uniqueId, type, params) {
  return { id: id, title: title, uniqueId: uniqueId, type: type, params: params || [], children: [] };
}
function indexTree(root, table) {
  const map = table || {};
  map[root.id] = root;
  for (const child of root.children) { indexTree(child, map); }
  return map;
}
function findParam(entry, tag) {
  const found = entry.params.filter(function (item) { return item.tag === tag; });
  if (found.length === 0) { throw new Error('unknown parameter tag ' + tag + ' on object ' + entry.id); }
  return found[0];
}

function createHost(options) {
  const opts = options || {};
  const outbox = [];
  const calls = { recordPulses: 0, surfaceWrites: [], slotAssignments: [], directWrites: [], tempo: [] };
  const bindingsByName = {};
  const device = { id: 'mock-device' };
  const mapping = { id: 'mock-mapping' };

  const selectedTree = node(100, 'Selected Track', 'uid-selected', 'Mixer Channel',
    [param(0, 'Volume'), param(1, 'Pan'), param(2, 'Mute')]);
  selectedTree.children.push(node(101, 'Channel EQ', 'uid-selected-eq', 'Channel EQ',
    [param(0, 'EQ Low Gain'), param(1, 'EQ High Gain'), param(2, 'EQ Freq')]));
  selectedTree.children.push(node(102, 'Insert 1', 'uid-selected-insert', 'Insert',
    [param(0, 'Bypass')]));

  const bankTree = node(900, 'MixConsole Bank', 'uid-bank', 'Mixer Bank', []);
  const channelTrees = [];
  const channels = [];
  for (let i = 0; i < 64; i += 1) {
    const tree = node(200 + i * 10, 'Channel ' + i, 'uid-channel-' + i, 'Mixer Channel',
      [param(0, 'Volume'), param(1, 'Pan'), param(2, 'Mute')]);
    tree.children.push(node(201 + i * 10, 'Channel EQ', 'uid-eq-' + i, 'Channel EQ',
      [param(0, 'EQ Low Gain'), param(1, 'EQ High Gain'), param(2, 'EQ Freq')]));
    channelTrees.push(tree);
    channels.push({
      index: i,
      tree: tree,
      getUniqueIDString: function () { return 'uid-channel-' + i; },
      getRuntimeID: function () { return 500 + i; }
    });
    if (i < 8) { bankTree.children.push(tree); }
  }

  const slotManager = {
    trySetSlotPlugin: function (m, objectId, pluginTypeUID, dontAskDiscard) {
      calls.slotAssignments.push({ objectId: objectId, pluginUid: pluginTypeUID, dontAskDiscard: dontAskDiscard });
      const entry = selectedIndex[objectId];
      entry.title = 'Assigned ' + pluginTypeUID;
      entry.uniqueId = 'assigned-' + pluginTypeUID;
      return true;
    }
  };

  const selectedIndex = indexTree(selectedTree);
  const bankIndex = indexTree(bankTree);
  const channelIndexes = channelTrees.map(function (tree) { return indexTree(tree); });

  function makeDirectAccess(hostObject) {
    // The base object id must be tracked explicitly: JavaScript orders integer-like keys numerically, so
    // Object.keys(index)[0] is not the tree root when a parent id is larger than a child id.
    if (hostObject && hostObject.tree === selectedTree) { return makeAccess(selectedIndex, opts.api13 ? slotManager : null, selectedTree.id); }
    if (hostObject === bankObject) { return makeAccess(bankIndex, null, bankTree.id); }
    if (hostObject && typeof hostObject.index === 'number' && channelIndexes[hostObject.index]) {
      return makeAccess(channelIndexes[hostObject.index], null, channelTrees[hostObject.index].id);
    }
    return makeAccess(indexTree(node(700, 'Empty Root', 'uid-empty', 'Mixed', [])), null, 700);
  }

  function makeAccess(index, manager, baseId) {
    return {
      mPluginManager: manager,
      activate: function () {}, deactivate: function () {}, update: function () {},
      getBaseObjectID: function () { return baseId; },
      getNumberOfChildObjects: function (m, objectId) { return index[objectId].children.length; },
      getChildObjectID: function (m, objectId, i) { return index[objectId].children[i].id; },
      getObjectUniqueName: function (m, objectId) { return index[objectId].uniqueId; },
      getObjectUniqueIDString: function (m, objectId) { return index[objectId].uniqueId; },
      getObjectTitle: function (m, objectId) { return index[objectId].title; },
      getObjectTypeName: function (m, objectId) { return index[objectId].type; },
      getNumberOfParameters: function (m, objectId) { return index[objectId].params.length; },
      getParameterTagByIndex: function (m, objectId, i) { return index[objectId].params[i].tag; },
      getParameterTitle: function (m, objectId, tag, maxLength) {
        const title = findParam(index[objectId], tag).title;
        return maxLength && maxLength < title.length ? title.slice(0, maxLength) : title;
      },
      getParameterProcessValue: function (m, objectId, tag) { return findParam(index[objectId], tag).value; },
      getParameterDisplayValue: function (m, objectId, tag) { return findParam(index[objectId], tag).display; },
      getParameterDisplayUnits: function (m, objectId, tag) { return findParam(index[objectId], tag).units; },
      getParameterProcessValueType: function () { return 'float'; },
      isParameterAutomatable: function () { return true; },
      getParameterEditLockState: function (m, objectId, tag) { return findParam(index[objectId], tag).locked; },
      setParameterEditLockState: function (m, objectId, tag, locked) { findParam(index[objectId], tag).locked = locked; },
      convertParameterProcessValueToPlain: function (m, objectId, tag, value) { return value * 100; },
      convertParameterPlainToProcessValue: function (m, objectId, tag, value) { return value / 100; },
      setParameterProcessValue: function (m, objectId, tag, value) {
        findParam(index[objectId], tag).value = value;
        findParam(index[objectId], tag).display = String(value);
        calls.directWrites.push({ objectId: objectId, tag: tag, value: value, kind: 'process' });
      },
      setParameterDisplayValue: function (m, objectId, tag, value) {
        findParam(index[objectId], tag).display = value;
        calls.directWrites.push({ objectId: objectId, tag: tag, value: value, kind: 'display' });
      }
    };
  }

  const surfaceWrites = calls.surfaceWrites;
  function makeVariable(name) {
    return {
      name: name,
      setProcessValue: function (activeDevice, value) {
        surfaceWrites.push({ name: name, value: value });
        if (name === 'Record' && value === 1) {
          calls.recordPulses += 1;
          if (opts.autoToggleRecord && bindingsByName.Record && bindingsByName.Record.mOnValueChange) {
            const current = activeContext && activeContext.runtime ? activeContext.runtime.state.recording : null;
            bindingsByName.Record.mOnValueChange(activeDevice, mapping, current === true ? 0 : 1);
          }
        }
      }
    };
  }

  const transportValues = {
    mStart: { name: 'mStart' }, mStop: { name: 'mStop' }, mRecord: { name: 'mRecord' },
    mCycleActive: { name: 'mCycleActive' }, mMetronomeActive: { name: 'mMetronomeActive' }
  };
  const selectedValues = {
    mVolume: { name: 'mVolume' }, mPan: { name: 'mPan' }, mMute: { name: 'mMute' },
    mSolo: { name: 'mSolo' }, mMonitorEnable: { name: 'mMonitorEnable' }, mRecordEnable: { name: 'mRecordEnable' }
  };
  const timeDisplay = {
    mOnChangeTempoBPM: null,
    setTempoBPM: function (m, bpm) { calls.tempo.push(bpm); },
    mPrimary: {
      mTransportLocator: { mOnChange: null, setTime: function () {} },
      mCycleLocatorLeft: { mOnChange: null, setTime: function () {} },
      mCycleLocatorRight: { mOnChange: null, setTime: function () {} }
    }
  };
  const selectedChannel = {
    tree: selectedTree, mValue: selectedValues,
    getUniqueIDString: function () { return 'uid-selected'; }, getRuntimeID: function () { return 5000; }
  };
  const bankObject = { tree: bankTree, makeMixerBankChannel: function () { return channels[channelCursor++]; } };
  let activeContext = null;

  const page = {
    mHostAccess: {
      mTransport: { mValue: transportValues, mTimeDisplay: timeDisplay },
      mTrackSelection: { mMixerChannel: selectedChannel, mAction: { mPrevTrack: {}, mNextTrack: {} } },
      mMixConsole: { makeMixerBankZone: function () { return bankObject; } },
      mControlRoom: {},
      mFocusedQuickControls: {},
      makeDirectAccess: makeDirectAccess
    },
    makeValueBinding: function (value, target) {
      const binding = { value: value, target: target, mOnValueChange: null, setTypeToggle: function () { return binding; } };
      bindingsByName[value.name] = binding;
      return binding;
    },
    makeActionBinding: function () { return { mOnValueChange: null }; },
    makeCommandBinding: function () { return { canPerform: opts.api13 ? function () { return true; } : undefined }; },
    mOnActivate: null,
    mOnDeactivate: null
  };
  let channelCursor = 0;

  const api = {
    makeDeviceDriver: function () {
      return {
        mPorts: {
          makeMidiInput: function () { return midiInput; },
          makeMidiOutput: function () { return midiOutput; }
        },
        mSurface: { makeCustomValueVariable: makeVariable },
        makeDetectionUnit: function () {
          return {
            detectPortPair: function () { return { expectInputNameEquals: function () {}, expectOutputNameEquals: function () {} }; },
            mPlatformFilter: { expectPlatformWindows: function () {} }
          };
        },
        mMapping: { makePage: function () { return page; } }
      };
    },
    mDefaults: { mAppVersion: { getVersionString: function () { return opts.hostVersion || '15.0.20'; } } }
  };
  const midiInput = { mOnSysex: null };
  const midiOutput = { sendMidi: function (activeDevice, message) { outbox.push(Array.prototype.slice.call(message)); } };

  return {
    opts: opts, api: api, page: page, device: device, mapping: mapping, calls: calls,
    outbox: outbox, bindingsByName: bindingsByName,
    setContext: function (value) { activeContext = value; },
    bankObject: bankObject, channels: channels, selectedTree: selectedTree,
    midiInput: midiInput
  };
}

function loadDriver(host) {
  const sandbox = {
    require: function (name) {
      if (name !== 'midiremote_api_v1') { throw new Error('unexpected module: ' + name); }
      return host.api;
    },
    console: console,
    Date: Date, Math: Math, JSON: JSON, String: String, Number: Number, Object: Object,
    Array: Array, Error: Error, isFinite: isFinite, parseInt: parseInt, parseFloat: parseFloat
  };
  const context = vm.createContext(sandbox);
  host.setContext(context);
  new vm.Script(driverSource, { filename: 'CBMCP_CompleteBridge.js' }).runInContext(context);
  host.context = context;
  context.page.mOnActivate(host.device, host.mapping);
  return context;
}

// Responses larger than one SysEx payload arrive as chunks; reassemble them before decoding the envelope.
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
    envelopes.push({ kind: entry.frame.kind, opcode: entry.frame.opcode, data: parse(Buffer.from(joined).toString('utf8')) });
  }
  return envelopes;
}
function send(host, operation, args) {
  host.requestId = (host.requestId || 0) + 1;
  const payload = Array.from(Buffer.from(JSON.stringify({ operation: operation, arguments: args || {} }), 'utf8'));
  const wire = host.context.encodeChunk(1, host.requestId, host.context.OPCODE[operation], 0, 1, payload);
  const emitted = [];
  host.midiInput.mOnSysex(host.device, wire);
  const drained = drain(host);
  for (const item of drained) { emitted.push(item); }
  const terminal = drained.filter(function (item) { return item.kind === 2 || item.kind === 5; });
  return { all: emitted, terminal: terminal.length === 0 ? null : terminal[terminal.length - 1] };
}
function expectOk(host, name, operation, args) {
  const result = send(host, operation, args);
  const terminal = result.terminal;
  check(name + ' responds', !!terminal && terminal.kind === 2, terminal && terminal.data);
  return terminal && terminal.kind === 2 ? terminal.data.data : null;
}
function expectError(host, name, operation, args, fragment) {
  const result = send(host, operation, args);
  const terminal = result.terminal;
  const message = terminal && terminal.data && terminal.data.error ? terminal.data.error.message : null;
  check(name + ' fails', !!terminal && terminal.kind === 5, terminal && terminal.data);
  check(name + ' explains why', !!message && message.indexOf(fragment) >= 0, message);
  return message;
}
function surfaceWritesFor(host, name) {
  return host.calls.surfaceWrites.filter(function (item) { return item.name === name; });
}

function scenario(label, options, body) {
  const host = createHost(options || {});
  loadDriver(host);
  body(host);
}

scenario('api13 host', { api13: true, autoToggleRecord: true }, function (host) {
  const context = host.context;
  check('driver activates the host mapping', context.runtime.active === true);
  check('driver exposes the declared operations', context.runtime.device !== null);

  // ---- F02: explicit record target ---------------------------------------------
  const unknownStop = send(host, 'transport.set', { action: 'record_stop' });
  const unknownMessage = unknownStop.terminal && unknownStop.terminal.data.error ? unknownStop.terminal.data.error.message : '';
  check('record_stop refuses to blind-toggle an unknown mirror state',
    unknownStop.terminal !== null && unknownStop.terminal.kind === 5 && unknownMessage.indexOf('cannot be guaranteed to stop recording') >= 0,
    unknownStop.terminal && unknownStop.terminal.data);
  check('record_stop with an unknown mirror state sends no pulse', host.calls.recordPulses === 0);

  host.bindingsByName.Record.mOnValueChange(host.device, host.mapping, 1);
  check('record mirror tracks the host value', context.runtime.state.recording === true);
  const stopPulsesBefore = host.calls.recordPulses;
  const stopped = expectOk(host, 'record with enabled=false while recording', 'transport.set', { action: 'record', enabled: false });
  check('record enabled=false stops instead of starting', stopped.action === 'record_stop', stopped);
  check('record enabled=false pulses the toggle exactly once', host.calls.recordPulses === stopPulsesBefore + 1);
  check('record enabled=false pulses the toggle to stop', stopped.pulsed === true, stopped);
  check('record enabled=false verifies the observed state', stopped.verified === true, stopped);
  check('record enabled=false leaves the mirror stopped', context.runtime.state.recording === false);

  const idlePulsesBefore = host.calls.recordPulses;
  const alreadyStopped = expectOk(host, 'record enabled=false while stopped', 'transport.set', { action: 'record', enabled: false });
  check('redundant record stop sends no pulse', alreadyStopped.pulsed === false && host.calls.recordPulses === idlePulsesBefore, alreadyStopped);
  check('redundant record stop is verified without a pulse', alreadyStopped.verified === true && alreadyStopped.already_stopped === true, alreadyStopped);

  const started = expectOk(host, 'record_start', 'transport.set', { action: 'record_start' });
  check('record_start pulses the toggle', started.pulsed === true && started.verified === true, started);
  const alreadyRecording = expectOk(host, 'record_start while recording', 'transport.set', { action: 'record_start' });
  check('redundant record start sends no pulse', alreadyRecording.pulsed === false && alreadyRecording.already_recording === true, alreadyRecording);

  // ---- F03: mixer channel targeting --------------------------------------------
  const volumeWritesBefore = surfaceWritesFor(host, 'Selected Volume').length;
  const eqWritesBefore = surfaceWritesFor(host, 'Volume').length;
  const targeted = expectOk(host, 'mixer.set track_id', 'mixer.set', { track_id: 3, parameter: 'volume', value: 0.5 });
  check('mixer.set target_id resolves the addressed channel', targeted && targeted.target && targeted.target.scope === 'track_index' && targeted.target.track_id === 3, targeted && targeted.target);
  check('mixer.set track_id writes the addressed channel object', targeted && targeted.target && targeted.target.object_id === 230, targeted && targeted.target);
  check('mixer.set track_id never touches the selected-track binding', surfaceWritesFor(host, 'Selected Volume').length === volumeWritesBefore);
  check('mixer.set track_id writes the resolved channel parameter',
    host.calls.directWrites.length > 0 && host.calls.directWrites[host.calls.directWrites.length - 1].objectId === 230, host.calls.directWrites);
  check('mixer.set track_id verifies the parameter value', targeted && targeted.verified === true, targeted);
  check('mixer.set track_id reports identity stability', targeted && targeted.target && targeted.target.identity_stable === true, targeted && targeted.target);

  expectError(host, 'mixer.set track_id above the bank', 'mixer.set', { track_id: 64, parameter: 'volume', value: 0.5 }, 'between 0 and 63');
  expectError(host, 'mixer.set track_id below the bank', 'mixer.set', { track_id: -1, parameter: 'volume', value: 0.5 }, 'between 0 and 63');
  expectError(host, 'mixer.set unknown named parameter on track_id', 'mixer.set', { track_id: 2, parameter: 'send 3', value: 0.5 }, 'track_id targeting supports the named parameters');
  expectError(host, 'mixer.set mismatched object_id', 'mixer.set', { track_id: 2, object_id: 250 }, 'resolves to object_id');
  expectError(host, 'mixer.set unsupported selected-track parameter', 'mixer.set', { parameter: 'send 3', value: 0.5 }, 'unsupported mixer parameter');
  expectError(host, 'mixer.set out-of-range value', 'mixer.set', { parameter: 'volume', value: 4 }, 'normalized between 0 and 1');

  const selectedWrite = expectOk(host, 'mixer.set selected track', 'mixer.set', { parameter: 'volume', value: 0.25 });
  check('mixer.set selected track reports its scope', selectedWrite && selectedWrite.target && selectedWrite.target.scope === 'selected_track', selectedWrite && selectedWrite.target);
  check('mixer.set selected track writes the selected binding', surfaceWritesFor(host, 'Selected Volume').length === volumeWritesBefore + 1);
  check('mixer.set selected track reports identity stability', selectedWrite && selectedWrite.target && selectedWrite.target.identity_stable === true, selectedWrite && selectedWrite.target);

  // ---- F01: plug-in slot assignment --------------------------------------------
  const inspected = expectOk(host, 'plugin inspect_slot', 'plugin.set', { action: 'inspect_slot', object_id: 102 });
  check('plugin inspect_slot reports the current slot', inspected && inspected.slot && inspected.slot.title === 'Insert 1', inspected);
  check('plugin inspect_slot reports api 1.3 availability', inspected && inspected.api_1_3_plugin_manager === true, inspected);

  const assigned = expectOk(host, 'plugin assign without confirm_replace', 'plugin.set',
    { action: 'assign', object_id: 102, plugin_uid: 'uid.plugin.one' });
  check('plugin assign runs', assigned && assigned.assigned === true, assigned);
  check('plugin assign without confirm_replace never skips the discard prompt',
    host.calls.slotAssignments.length === 1 && host.calls.slotAssignments[0].dontAskDiscard === false, host.calls.slotAssignments);
  check('plugin assign echoes the discard flag it used', assigned && assigned.dont_ask_discard === false, assigned);
  check('plugin assign captures the prior slot state', assigned && assigned.prior_slot && assigned.prior_slot.title === 'Insert 1', assigned && assigned.prior_slot);
  check('plugin assign reports the resulting slot change', assigned && assigned.slot_changed === true && assigned.slot.title === 'Assigned uid.plugin.one', assigned && assigned.slot);
  check('plugin assign stays unverified until the host confirms', assigned && assigned.verified === false, assigned);

  const confirmed = expectOk(host, 'plugin assign with confirm_replace', 'plugin.set',
    { action: 'assign', object_id: 102, plugin_uid: 'uid.plugin.two', confirm_replace: true });
  check('plugin assign honours an explicit confirm_replace',
    host.calls.slotAssignments.length === 2 && host.calls.slotAssignments[1].dontAskDiscard === true, host.calls.slotAssignments);
  check('plugin assign explains the discard choice', confirmed && confirmed.note.indexOf('confirm_replace was true') >= 0, confirmed && confirmed.note);

  expectError(host, 'plugin assign without plugin_uid', 'plugin.set', { action: 'assign', object_id: 102 }, 'plugin_uid is required');
  expectError(host, 'plugin assign without object_id', 'plugin.set', { action: 'assign', plugin_uid: 'uid.x' }, 'object_id of the target plug-in slot is required');
  expectError(host, 'plugin action outside the supported set', 'plugin.set', { action: 'bypass', object_id: 102 }, 'supported actions are set_parameter, inspect_slot and assign');

  // ---- F08: snapshot truncation -------------------------------------------------
  const shallow = expectOk(host, 'direct.get max_depth=0', 'direct.get', { root: 'mixer', max_depth: 0 });
  check('direct.get preserves an explicit max_depth of zero', shallow && shallow.limits.max_depth === 0, shallow && shallow.limits);
  check('direct.get reports depth truncation', shallow && shallow.truncation.depth_limit_children > 0 && shallow.truncated === true, shallow && shallow.truncation);
  check('direct.get visits only the base object at depth zero',
    shallow && shallow.nodes.length === 1 && shallow.nodes[0].depth === 0, shallow && shallow.nodes.length);

  const narrow = expectOk(host, 'direct.get max_nodes', 'direct.get', { root: 'mixer', max_nodes: 2 });
  check('direct.get reports node-limit truncation', narrow && narrow.truncation.node_limit_children > 0 && narrow.truncated === true,
    narrow && narrow.truncation);
  check('direct.get honours the node limit', narrow && narrow.nodes.length <= 2, narrow && narrow.nodes.length);

  const thin = expectOk(host, 'direct.get max_parameters', 'direct.get', { root: 'mixer', max_parameters: 1 });
  check('direct.get reports parameter truncation per node',
    thin && thin.truncation.nodes_with_truncated_parameters > 0 && thin.nodes.some(function (item) { return item.parameters_truncated === true; }), thin && thin.truncation);
  check('direct.get records the parameter total before truncating',
    thin && thin.nodes.some(function (item) { return item.parameter_total === 3 && item.parameters.length === 1; }), thin && thin.nodes[1]);

  const omitted = expectOk(host, 'direct.get include_parameters=false', 'direct.get', { root: 'mixer', include_parameters: false, max_depth: 0 });
  check('direct.get can omit parameters explicitly', omitted && omitted.nodes[0].parameters_omitted === true, omitted && omitted.nodes[0]);

  const full = expectOk(host, 'direct.get default', 'direct.get', { root: 'mixer' });
  check('direct.get reports a complete read as untruncated', full && full.truncated === false, full && full.truncation);
});

scenario('api12 host', { api13: false, autoToggleRecord: true }, function (host) {
  check('driver reports api 1.3 as unavailable', host.context.capabilities().api_1_3_plugin_manager === false);
  expectError(host, 'plugin assign without a plug-in manager', 'plugin.set',
    { action: 'assign', object_id: 102, plugin_uid: 'uid.plugin.one' }, 'requires MIDI Remote API 1.3');
  const result = send(host, 'command.can_perform', { id: 'cmd-missing' });
  check('command.can_perform rejects an unknown command id',
    result.terminal !== null && result.terminal.kind === 5, result.terminal && result.terminal.data);
});

console.log(JSON.stringify({ checks: checks, failures: failures }));
process.exit(failures.length === 0 ? 0 : 1);
