using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Multiplayer.Client.Desyncs
{
    /// <summary>
    /// Forensic analyzer that parses and compares synchronized callstack trace dumps between host and client.
    /// Pinpoints exact step index, tick, RNG seed, and method divergence.
    /// </summary>
    public static class DesyncDiffAnalyzer
    {
        private static readonly Regex HeaderPattern = new(
            @"^(\d+)\s+Tick:(\d+)\s+Hash:(-?\d+)\s+'(.*)'\s*$",
            RegexOptions.Compiled
        );

        /// <summary>
        /// Represents a parsed trace step containing callstack metadata.
        /// </summary>
        public class TraceEntry
        {
            public int index;
            public int tick;
            public int hash;
            public string rawPayload;
            public string seed = "0";
            public string entity = "Unknown";
            public List<string> stack = new();
        }

        /// <summary>
        /// Converts raw multiline trace dump text into structured TraceEntry elements.
        /// </summary>
        public static List<TraceEntry> ParseTraces(string text)
        {
            var result = new List<TraceEntry>();
            if (string.IsNullOrEmpty(text))
                return result;

            string[] lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            TraceEntry current = null;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd();
                var match = HeaderPattern.Match(line);

                if (match.Success)
                {
                    if (current != null)
                        result.Add(current);

                    var payload = match.Groups[4].Value.Trim();
                    var parts = payload.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                    var seedVal = parts.Length > 0 ? parts[parts.Length - 1] : "0";
                    var entityName = parts.Length >= 3 ? string.Join(" ", parts, 0, parts.Length - 2) : payload;

                    current = new TraceEntry
                    {
                        index = int.Parse(match.Groups[1].Value),
                        tick = int.Parse(match.Groups[2].Value),
                        hash = int.Parse(match.Groups[3].Value),
                        rawPayload = payload,
                        seed = seedVal,
                        entity = string.IsNullOrEmpty(entityName) ? "World/System" : entityName
                    };
                }
                else if (current != null)
                {
                    var trimmed = line.Trim();
                    if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("Context traces:") && !trimmed.StartsWith("Trace count:"))
                    {
                        current.stack.Add(trimmed);
                    }
                }
            }

            if (current != null)
                result.Add(current);

            return result;
        }

        /// <summary>
        /// Generates a comprehensive human-readable forensic report contrasting host and client simulation streams.
        /// </summary>
        public static string GenerateDiffReport(string hostRawTraces, string clientRawTraces, SaveableDesyncInfo desyncInfo)
        {
            var host = ParseTraces(hostRawTraces);
            var client = ParseTraces(clientRawTraces);

            var sb = new StringBuilder();
            sb.AppendLine("================================================================================");
            sb.AppendLine("                   MULTIPLAYER DESYNC FORENSIC REPORT                           ");
            sb.AppendLine("================================================================================");
            sb.AppendLine($"Local Start Tick:    {desyncInfo.local?.startTick ?? 0}");
            sb.AppendLine($"Remote Start Tick:   {desyncInfo.remote?.startTick ?? 0}");
            sb.AppendLine($"Reported Diff Index: {desyncInfo.diffAt}");
            sb.AppendLine($"Host Traces Count:   {host.Count}");
            sb.AppendLine($"Client Traces Count: {client.Count}");
            sb.AppendLine($"Is Local Server:     {Multiplayer.LocalServer != null}");
            sb.AppendLine();

            // Guard against empty trace buffers
            if (host.Count == 0 && client.Count == 0)
            {
                sb.AppendLine("[!] DIAGNOSIS: NO CALLSTACK TRACES RECORDED");
                sb.AppendLine("Reasons:");
                sb.AppendLine(" 1. Desync Tracing Mode is set to 'None' in Multiplayer Settings.");
                sb.AppendLine(" 2. Desync window was spawned manually via Debug Action ('Show desync window'), which uses empty mock data.");
                sb.AppendLine(" 3. The desync occurred on raw map/world hash comparison where RNG stack tracing was not triggered.");
                sb.AppendLine();
                sb.AppendLine("HASH STATE SUMMARY:");
                sb.AppendLine($"  Local Hashes Count:  {desyncInfo.local?.desyncStackTraceHashes.Count ?? 0}");
                sb.AppendLine($"  Remote Hashes Count: {desyncInfo.remote?.desyncStackTraceHashes.Count ?? 0}");
                return sb.ToString();
            }

            // Display active stream when only one side recorded traces
            if (host.Count == 0 || client.Count == 0)
            {
                sb.AppendLine(host.Count == 0
                    ? "[!] NOTICE: HOST TRACES ARE MISSING (Running on Host or packet delayed)"
                    : "[!] NOTICE: CLIENT TRACES ARE MISSING");
                sb.AppendLine("Displaying available stream traces for the active side:\n");

                var available = host.Count > 0 ? host : client;
                string side = host.Count > 0 ? "HOST" : "CLIENT";

                int targetIdx = Math.Min(Math.Max(0, desyncInfo.diffAt), available.Count - 1);
                var entry = available[targetIdx];

                sb.AppendLine($"--- {side} TRACE AT STEP #{targetIdx} (Tick {entry.tick}) ---");
                sb.AppendLine($"  Hash:   {entry.hash}");
                sb.AppendLine($"  Seed:   {entry.seed}");
                sb.AppendLine($"  Entity: {entry.entity}");
                sb.AppendLine("  Callstack:");
                foreach (var line in entry.stack)
                    sb.AppendLine($"    {line}");

                return sb.ToString();
            }

            // Scan sequentially for the first point of divergence
            int minLen = Math.Min(host.Count, client.Count);
            int divergeIdx = -1;

            for (int i = 0; i < minLen; i++)
            {
                bool stackMismatch = host[i].stack.Count > 0 && client[i].stack.Count > 0 && host[i].stack[0] != client[i].stack[0];
                if (host[i].tick != client[i].tick || host[i].hash != client[i].hash || stackMismatch)
                {
                    divergeIdx = i;
                    break;
                }
            }

            if (divergeIdx == -1)
            {
                if (host.Count == client.Count)
                {
                    sb.AppendLine("[OK] Trace hashes and call stacks are IDENTICAL within the recorded radius.");
                    sb.AppendLine("Root cause is likely an unmonitored collection mutation, async thread state, or floating-point divergence.");
                    return sb.ToString();
                }
                divergeIdx = minLen;
            }

            // Detect asymmetric calls where one side executed extra simulation ticks
            bool isHostExtra = false;
            bool isClientExtra = false;

            if (divergeIdx < host.Count - 1 && divergeIdx < client.Count)
            {
                if (host[divergeIdx + 1].hash == client[divergeIdx].hash)
                    isHostExtra = true;
            }

            if (!isHostExtra && divergeIdx < client.Count - 1 && divergeIdx < host.Count)
            {
                if (client[divergeIdx + 1].hash == host[divergeIdx].hash)
                    isClientExtra = true;
            }

            sb.AppendLine($"[!] FIRST DIVERGENCE DETECTED AT STEP #{divergeIdx}");
            if (isHostExtra)
            {
                sb.AppendLine("DIAGNOSIS: Extra call on HOST (Host evaluated simulation/RNG code that was skipped on client)");
            }
            else if (isClientExtra)
            {
                sb.AppendLine("DIAGNOSIS: Extra call on CLIENT (Client evaluated simulation/RNG code locally without server sync)");
            }
            else
            {
                sb.AppendLine("DIAGNOSIS: Simulation State Divergence (RNG seeds diverged or internal entity logic branched)");
            }
            sb.AppendLine();

            if (divergeIdx > 0 && divergeIdx - 1 < host.Count)
            {
                var p = host[divergeIdx - 1];
                sb.AppendLine($"--- Last Matching Step #{divergeIdx - 1} (Tick {p.tick}) ---");
                sb.AppendLine($"  Entity: {p.entity} | Seed: {p.seed}");
                sb.AppendLine($"  Method: {(p.stack.Count > 0 ? p.stack[0] : "N/A")}");
                sb.AppendLine();
            }

            sb.AppendLine($"----------------------- HOST: Step #{divergeIdx} -----------------------");
            if (divergeIdx < host.Count)
            {
                var h = host[divergeIdx];
                sb.AppendLine($"Tick: {h.tick} | Hash: {h.hash} | Seed: {h.seed}");
                sb.AppendLine($"Entity: {h.entity}");
                sb.AppendLine("Callstack:");
                foreach (var line in h.stack)
                    sb.AppendLine($"  {line}");
            }
            else
            {
                sb.AppendLine("  [No trace entry - Host stream ended earlier]");
            }
            sb.AppendLine();

            sb.AppendLine($"---------------------- CLIENT: Step #{divergeIdx} ----------------------");
            if (divergeIdx < client.Count)
            {
                var c = client[divergeIdx];
                sb.AppendLine($"Tick: {c.tick} | Hash: {c.hash} | Seed: {c.seed}");
                sb.AppendLine($"Entity: {c.entity}");
                sb.AppendLine("Callstack:");
                foreach (var line in c.stack)
                    sb.AppendLine($"  {line}");
            }
            else
            {
                sb.AppendLine("  [No trace entry - Client stream ended earlier]");
            }

            sb.AppendLine("================================================================================");
            return sb.ToString();
        }
    }
}