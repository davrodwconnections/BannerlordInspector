/**
 * Protocol smoke test: starts server.js over stdio, performs the MCP handshake, and lists tools.
 * Verifies the bridge itself is wired correctly without needing Bannerlord to be running.
 *
 *   node smoketest.js
 */
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const child = spawn(process.execPath, [join(here, "server.js")], { stdio: ["pipe", "pipe", "inherit"] });

const send = (msg) => child.stdin.write(JSON.stringify(msg) + "\n");

let buffer = "";
let toolCount = null;

child.stdout.on("data", (chunk) => {
  buffer += chunk.toString();

  let newline;
  while ((newline = buffer.indexOf("\n")) >= 0) {
    const line = buffer.slice(0, newline).trim();
    buffer = buffer.slice(newline + 1);
    if (!line) continue;

    let msg;
    try {
      msg = JSON.parse(line);
    } catch {
      continue;
    }

    if (msg.id === 1) {
      console.log("handshake ok:", msg.result?.serverInfo?.name, msg.result?.serverInfo?.version);
      send({ jsonrpc: "2.0", id: 2, method: "tools/list", params: {} });
    }

    if (msg.id === 2) {
      const tools = msg.result?.tools ?? [];
      toolCount = tools.length;
      console.log(`tools exposed: ${toolCount}`);
      for (const t of tools) console.log("  -", t.name);
      child.kill();
      // Bumped as tools were added; a drop means a route or schema failed to register.
      process.exit(toolCount === 17 ? 0 : 1);
    }
  }
});

send({
  jsonrpc: "2.0",
  id: 1,
  method: "initialize",
  params: {
    protocolVersion: "2024-11-05",
    capabilities: {},
    clientInfo: { name: "smoketest", version: "1.0.0" },
  },
});

setTimeout(() => {
  console.error("timed out waiting for the bridge to answer");
  child.kill();
  process.exit(1);
}, 10000);
