// Read-only MCP contract smoke against an explicit release candidate:
//   node scripts/smoke-mcp.mjs --release <release-dir> [--manifest-sha256 <hash>]
// It lists tools/resources, reads status and the capability registry, checks that a destructive command is
// confirmation-gated (nothing is executed), and creates a session-scoped MIDI document (no Cubase change).
import { spawn } from 'node:child_process';
import { selectRelease } from './release-candidate.mjs';

const selected = selectRelease();
const child = spawn(selected.server, [], { stdio: ['pipe', 'pipe', 'pipe'] });
let buffer = '';
const responses = new Map();
const timeout = setTimeout(() => finish(3, { error: 'MCP smoke timed out after 60 s' }), 60000);

function send(message) { child.stdin.write(JSON.stringify(message) + '\n'); }
function finish(code, value) {
  clearTimeout(timeout);
  if (value) process.stdout.write(JSON.stringify(value, null, 2) + '\n');
  child.kill();
  setTimeout(() => process.exit(code), 50);
}

child.stderr.pipe(process.stderr);
child.stdout.on('data', chunk => {
  buffer += chunk.toString();
  let newline;
  while ((newline = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, newline).trim();
    buffer = buffer.slice(newline + 1);
    if (!line) continue;
    const message = JSON.parse(line);
    if (message.id !== undefined) responses.set(message.id, message);
    if (message.id === 1) {
      send({ jsonrpc: '2.0', method: 'notifications/initialized' });
      send({ jsonrpc: '2.0', id: 2, method: 'tools/list', params: {} });
      send({ jsonrpc: '2.0', id: 3, method: 'resources/list', params: {} });
      send({ jsonrpc: '2.0', id: 4, method: 'tools/call', params: { name: 'cubase_status', arguments: {} } });
      send({ jsonrpc: '2.0', id: 5, method: 'resources/read', params: { uri: 'cubase://commands' } });
      send({ jsonrpc: '2.0', id: 6, method: 'tools/call', params: { name: 'cubase_command', arguments: { category: 'File', name: 'Close', action: 'execute' } } });
      send({ jsonrpc: '2.0', id: 7, method: 'resources/read', params: { uri: 'cubase://capabilities' } });
      send({ jsonrpc: '2.0', id: 8, method: 'tools/call', params: { name: 'cubase_midi', arguments: { action: 'create_document', name: 'MCP Contract Smoke', options: { ppq: 480, tempo_bpm: 120 } } } });
    }
    if ([2, 3, 4, 5, 6, 7, 8].every(id => responses.has(id))) {
      const tools = responses.get(2).result.tools;
      const resources = responses.get(3).result.resources;
      const status = JSON.parse(responses.get(4).result.content[0].text);
      const commands = JSON.parse(responses.get(5).result.contents[0].text);
      const gated = JSON.parse(responses.get(6).result.content[0].text);
      const capabilities = JSON.parse(responses.get(7).result.contents[0].text);
      const midi = JSON.parse(responses.get(8).result.content[0].text);
      const summary = capabilities.data?.summary ?? {};
      const batch = tools.find(tool => tool.name === 'cubase_batch');
      const commandCount = Array.isArray(commands.data) ? commands.data.length : -1;
      const checks = {
        tool_count: tools.length === 14,
        resource_count: resources.length === 4,
        status_has_execution_state: typeof status.execution_state === 'string',
        batch_schema_options: ['continue_on_unverified', 'timeout_ms', 'operation_id', 'resume_operation_id']
          .every(name => batch?.inputSchema?.properties?.[name] !== undefined),
        destructive_command_gated: gated.status === 'RequiresConfirmation' && gated.execution_state === 'not_started',
        catalog_consistent: commandCount > 0 && summary.command_capabilities === commandCount
          && summary.classified_commands + summary.unclassified_commands === commandCount
          && summary.unclassified_effective_risk === 'Confirm',
        midi_document: midi.status === 'Ok' && midi.data?.summary?.tracks === 1
      };
      finish(Object.values(checks).every(Boolean) ? 0 : 2, {
        release_id: selected.manifest.release_id, candidate_git_sha: selected.manifest.candidate_git_sha,
        manifest_sha256: selected.manifestSha256, checks, tool_names: tools.map(tool => tool.name).sort(),
        command_count: commandCount, capability_summary: summary, status
      });
    }
  }
});
child.on('error', error => finish(2, { error: error.message }));
send({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'cbmcp-smoke', version: '2.0' } } });
