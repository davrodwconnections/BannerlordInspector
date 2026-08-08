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

      // This used to assert an exact count, which had to be edited by hand every time a tool was
      // added - and of course was not, so the test failed silently for every new tool while still
      // printing a list that looked perfectly healthy. A test whose failure is invisible is worse
      // than no test.
      //
      // So: check the things that actually indicate breakage. A tool missing from the core set
      // means a route or schema failed to register; a duplicate name means two tools were defined
      // with the same key and one silently shadowed the other; a malformed schema means the tool
      // exists but cannot be called.
      const required = [
        "bannerlord_status", "bannerlord_doctor", "bannerlord_eval", "bannerlord_errors",
        "bannerlord_equipment", "bannerlord_world", "bannerlord_groupby", "bannerlord_modlog",
        "bannerlord_snapshot", "bannerlord_checklist",
      ];

      const names = tools.map((t) => t.name);
      const missing = required.filter((r) => !names.includes(r));
      const duplicates = names.filter((n, i) => names.indexOf(n) !== i);
      const malformed = tools
        .filter((t) => !t.description || t.inputSchema?.type !== "object")
        .map((t) => t.name);

      let ok = true;
      if (missing.length) { console.error("MISSING:", missing.join(", ")); ok = false; }
      if (duplicates.length) { console.error("DUPLICATE NAMES:", duplicates.join(", ")); ok = false; }
      if (malformed.length) { console.error("BAD SCHEMA:", malformed.join(", ")); ok = false; }
      if (toolCount < required.length) { console.error("suspiciously few tools"); ok = false; }

      console.log(ok ? "OK" : "FAILED");
      process.exit(ok ? 0 : 1);
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
