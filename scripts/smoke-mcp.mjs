import { spawn } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { readdirSync, statSync } from 'node:fs';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const publishRoot = join(root, 'artifacts', 'publish');
const candidates = [];
for (const entry of readdirSync(publishRoot, { withFileTypes: true })) {
  const candidate = entry.isDirectory() ? join(publishRoot, entry.name, 'CubaseCompleteMcp.Server.exe') : null;
  if (candidate) { try { candidates.push({ path: candidate, modified: statSync(candidate).mtimeMs }); } catch { /* incomplete publish */ } }
}
const flat = join(publishRoot, 'CubaseCompleteMcp.Server.exe');
try { candidates.push({ path: flat, modified: statSync(flat).mtimeMs }); } catch { /* no legacy flat publish */ }
const server = candidates.sort((a, b) => b.modified - a.modified)[0]?.path;
if (!server) throw new Error('No published CubaseCompleteMcp.Server.exe was found.');
const child = spawn(server, [], { stdio: ['pipe', 'pipe', 'pipe'] });
let buffer = '';
let timeout;
const responses = new Map();

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
    }
    if (responses.has(2) && responses.has(3) && responses.has(4) && responses.has(5) && responses.has(6)) {
      const tools = responses.get(2).result.tools;
      const resources = responses.get(3).result.resources;
      const status = responses.get(4).result;
      const commandsEnvelope = JSON.parse(responses.get(5).result.contents[0].text);
      const safetyEnvelope = JSON.parse(responses.get(6).result.content[0].text);
      const commandCount = Array.isArray(commandsEnvelope.data) ? commandsEnvelope.data.length : -1;
      const safe = tools.length === 12 && resources.length === 4 && !status.isError
        && commandCount === 2777 && safetyEnvelope.status === 'RequiresConfirmation';
      const output = { tool_count: tools.length, tool_names: tools.map(tool => tool.name).sort(),
        resource_uris: resources.map(resource => resource.uri).sort(), command_resource_count: commandCount,
        dangerous_command_status: safetyEnvelope.status, status };
      finish(safe ? 0 : 2, output);
    }
  }
});

child.on('error', error => finish(2, { error: error.message }));
send({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'cbmcp-smoke', version: '1.0' } } });
timeout = setTimeout(() => finish(3, { error: 'MCP smoke test timed out' }), 15000);
