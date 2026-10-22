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
            public List<string> stack = [];
        }

        /// <summary>
        /// Converts raw multiline trace dump text into structured TraceEntry elements.
        /// </summary>
        public static List<TraceEntry> ParseTraces(string text)
        {
            var result = new List<TraceEntry>();
            if (string.IsNullOrEmpty(text))
                return result;

            string[] lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
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
                    var parts = payload.Split([' '], StringSplitOptions.RemoveEmptyEntries);

                    var seedVal = parts.Length > 0 ? parts[^1] : "0";
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
        /// Aligns trace streams using trace sequence IDs to prevent false Step #0 divergence when comparing windowed server slices.
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
                for (int i = 0; i < entry.stack.Count; i++)
                    sb.AppendLine($"    {entry.stack[i]}");

                return sb.ToString();
            }

            // Two-pointer sequential alignment by trace sequence ID.
            // Eliminates zero-index false positives when host returns an 81-trace radius window around diffAt.
            int hostPtr = 0;
            int clientPtr = 0;
            int commonStepsEvaluated = 0;

            TraceEntry divergeHost = null;
            TraceEntry divergeClient = null;
            TraceEntry lastMatchingHost = null;
            TraceEntry lastMatchingClient = null;

            while (hostPtr < host.Count && clientPtr < client.Count)
            {
                var h = host[hostPtr];
                var c = client[clientPtr];

                if (h.index < c.index)
                {
                    hostPtr++;
                    continue;
                }

                if (c.index < h.index)
                {
                    clientPtr++;
                    continue;
                }

                // Indices match: evaluate simulation invariants at identical logical step
                commonStepsEvaluated++;

                bool stackMismatch = h.stack.Count > 0 && c.stack.Count > 0 && h.stack[0] != c.stack[0];
                bool diverged = h.tick != c.tick || h.hash != c.hash || stackMismatch;

                if (diverged)
                {
                    divergeHost = h;
                    divergeClient = c;
                    break;
                }

                lastMatchingHost = h;
                lastMatchingClient = c;
                hostPtr++;
                clientPtr++;
            }

            if (commonStepsEvaluated == 0)
            {
                sb.AppendLine("[!] NOTICE: TRACE INDEX RANGES DO NOT OVERLAP");
                sb.AppendLine($"Host Index Range:   [{host[0].index} .. {host[^1].index}]");
                sb.AppendLine($"Client Index Range: [{client[0].index} .. {client[^1].index}]");
                sb.AppendLine();
                sb.AppendLine("DIAGNOSIS: Host sent an 81-trace window around an index outside client trace range.");
                return sb.ToString();
            }

            if (divergeHost == null && divergeClient == null)
            {
                sb.AppendLine($"[OK] All {commonStepsEvaluated} overlapping trace steps are IDENTICAL.");
                sb.AppendLine("Root cause is likely an unmonitored collection mutation, async thread state, or floating-point divergence.");
                return sb.ToString();
            }

            // Detect asymmetric execution steps
            bool isHostExtra = false;
            bool isClientExtra = false;

            if (hostPtr + 1 < host.Count && host[hostPtr + 1].hash == divergeClient.hash)
                isHostExtra = true;
            else if (clientPtr + 1 < client.Count && client[clientPtr + 1].hash == divergeHost.hash)
                isClientExtra = true;

            int divergeStep = divergeHost.index;

            sb.AppendLine($"[!] FIRST DIVERGENCE DETECTED AT STEP #{divergeStep}");
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

            if (lastMatchingHost != null)
            {
                sb.AppendLine($"--- Last Matching Step #{lastMatchingHost.index} (Tick {lastMatchingHost.tick}) ---");
                sb.AppendLine($"  Entity: {lastMatchingHost.entity} | Seed: {lastMatchingHost.seed}");
                sb.AppendLine($"  Method: {(lastMatchingHost.stack.Count > 0 ? lastMatchingHost.stack[0] : "N/A")}");
                sb.AppendLine();
            }

            sb.AppendLine($"----------------------- HOST: Step #{divergeStep} -----------------------");
            sb.AppendLine($"Tick: {divergeHost.tick} | Hash: {divergeHost.hash} | Seed: {divergeHost.seed}");
            sb.AppendLine($"Entity: {divergeHost.entity}");
            sb.AppendLine("Callstack:");
            for (int i = 0; i < divergeHost.stack.Count; i++)
                sb.AppendLine($"  {divergeHost.stack[i]}");
            sb.AppendLine();

            sb.AppendLine($"---------------------- CLIENT: Step #{divergeStep} ----------------------");
            sb.AppendLine($"Tick: {divergeClient.tick} | Hash: {divergeClient.hash} | Seed: {divergeClient.seed}");
            sb.AppendLine($"Entity: {divergeClient.entity}");
            sb.AppendLine("Callstack:");
            for (int i = 0; i < divergeClient.stack.Count; i++)
                sb.AppendLine($"  {divergeClient.stack[i]}");

            sb.AppendLine("================================================================================");
            return sb.ToString();
        }
    }
}