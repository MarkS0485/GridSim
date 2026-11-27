# Privacy

The GridSim plugin runs entirely on your machine.

- **No network.** The MCP server talks to the AI tool over stdin and stdout only. It opens no socket, listens on no
  port and calls no web service. The plugin declares no network hosts. The only download is the pinned MCP SDK, which
  `uv` fetches once when the server first starts.
- **No telemetry.** The server sends no usage data, crash reports or analytics. The SDK's OpenTelemetry hook (inert
  without an exporter) is removed at start-up, and `OTEL_*` settings are cleared from the server's environment and
  from GridSim's.
- **Your files.** The server reads and writes inside the workspace folder, and reads case files from the project
  folder. Study records, results and saved cases stay there until you delete them. GridSim's own run journal for each
  session is written there too (it holds file paths from your machine; review it before sharing).
- **Your licence.** The licence file path is passed to GridSim. The plugin never opens, prints or records the
  licence; the study record says only whether a licence was configured.
- **What the AI sees.** Tool results go to the AI tool you use, which may send them to its model provider under that
  tool's own terms. Do not load data you are not allowed to share with it.
