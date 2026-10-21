using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using JetBrains.Annotations;
using Multiplayer.Common;
using Multiplayer.Common.Util;
using RimWorld;
using UnityEngine;
using Verse;
using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace Multiplayer.Client.Desyncs;

public class SaveableDesyncInfo(
    SyncCoordinator coordinator,
    ClientSyncOpinion local,
    ClientSyncOpinion remote,
    int diffAt,
    bool diffAtFound)
{
    public readonly ClientSyncOpinion local = local;
    public readonly ClientSyncOpinion remote = remote;
    public readonly int diffAt = diffAt;
    private readonly Task<string> metadata = Task.Run(MetadataGenerator.Generate);
    private readonly Task<FileInfo> replay = Task.Run(SaveReplayIfApplicable);

    public bool ReadyToSave => metadata.IsCompleted && replay.IsCompleted;

    public void Save([CanBeNull] HostInfo hostInfo)
    {
        var watch = Stopwatch.StartNew();

        try
        {
            var desyncFilePath = FindFileNameForNextDesyncFile();
            using var zip = MpZipFile.Open(Path.Combine(Multiplayer.DesyncsDir, desyncFilePath + ".zip"), ZipArchiveMode.Create);

            zip.AddEntry("desync_info", GetDesyncDetails());

            zip.AddEntry("local_traces.txt", GetLocalTraces());
            zip.AddEntry("host_traces.txt", hostInfo?.Traces ?? "No host traces");

            zip.AddEntry("local_jitted_methods.txt", JittedMethods.GetJittedMethodsString());
            zip.AddEntry("host_jitted_methods.txt", hostInfo?.JittedMethods ?? "No host jitted methods");

            var extraLogs = LogGenerator.PrepareLogData();
            if (extraLogs != null) zip.AddEntry("local_logs.txt", extraLogs);

            zip.AddEntry("local_metadata.txt", metadata.Result);

            // Resolve host vs client trace streams symmetrically depending on host status
            bool isHost = Multiplayer.LocalServer != null;
            string hostTraces = isHost ? GetLocalTraces() : (hostInfo?.Traces ?? string.Empty);
            string clientTraces = isHost ? (hostInfo?.Traces ?? GetRemoteTraces()) : GetLocalTraces();

            var analysisReport = DesyncDiffAnalyzer.GenerateDiffReport(hostTraces, clientTraces, this);
            zip.AddEntry("desync_analysis.txt", analysisReport);

            try
            {
                replay.Wait();
            }
            catch (AggregateException e)
            {
                if (e.InnerExceptions.SingleOrDefault(inner => inner is TaskCanceledException) == null) throw;
            }
            if (replay.IsCompletedSuccessfully)
            {
                var replayFile = replay.Result;
                zip.CreateEntryFromFile(replayFile.FullName, "replay.rwmts", CompressionLevel.NoCompression);
                DeleteFileSilent(replayFile);
            }
        }
        catch (Exception e)
        {
            Log.Error($"Exception writing desync info: {e}");
        }

        Log.Message($"Desync info writing took {watch.ElapsedMilliseconds}");
    }

    /// <summary>
    /// Formats local desync stacktrace hashes and frames into text for diffing and UI inspection.
    /// Uses Math.Min to prevent IndexOutOfRangeException if hash and trace counts diverge.
    /// </summary>
    public string GetLocalTraces()
    {
        if (local == null) return string.Empty;

        var builder = new StringBuilder();
        var count = Math.Min(local.desyncStackTraceHashes.Count, local.desyncStackTraces.Count);
        for (var i = 0; i < count; i++)
        {
            builder.Append(i).Append(' ').Append(local.desyncStackTraces[i]).Append("\n\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Formats remote desync stacktrace hashes and frames into text when available in the peer opinion.
    /// </summary>
    public string GetRemoteTraces()
    {
        if (remote == null) return string.Empty;

        var builder = new StringBuilder();
        var count = Math.Min(remote.desyncStackTraceHashes.Count, remote.desyncStackTraces.Count);
        for (var i = 0; i < count; i++)
        {
            builder.Append(i).Append(' ').Append(remote.desyncStackTraces[i]).Append("\n\n");
        }

        return builder.ToString();
    }

    private string GetDesyncDetails()
    {
        var desyncInfo = new StringBuilder();

        desyncInfo
            .AppendLine("###Tick Data###")
            .AppendLine($"Arbiter Connected And Playing|||{Multiplayer.session.ArbiterPlaying}")
            .AppendLine($"Last Valid Tick - Local|||{coordinator.lastValidTick}")
            .AppendLine($"Last Valid Tick - Arbiter|||{coordinator.arbiterWasPlayingOnLastValidTick}")
            .AppendLine("\n###Version Data###")
            .AppendLine($"Multiplayer Mod Version|||{MpVersion.Version}")
            .AppendLine($"Rimworld Version and Rev|||{VersionControl.CurrentVersionStringWithRev}")
            .AppendLine($"Join Data Mismatch|||{Multiplayer.session.joinDataDiff ?? "none"}")
            .AppendLine("\n###Debug Options###")
            .AppendLine($"Multiplayer Debug Build - Client|||{MpVersion.IsDebug}")
            .AppendLine($"Multiplayer Debug Mode - Host|||{Multiplayer.GameComp.debugMode}")
            .AppendLine($"Rimworld Developer Mode - Client|||{Prefs.DevMode}")
            .AppendLine("\n###Failures###")
            .AppendLine($"Loading Errors - Client|||{Multiplayer.loadingErrorCount}")
            .AppendLine($"Skipped Simulation Errors - Client|||{Multiplayer.session.simulationFailures}")
            .AppendLine($"First Simulation Error - Client|||{Multiplayer.session.firstSimulationFailure ?? "none"}")
            .AppendLine("\n###Server Info###")
            .AppendLine($"Player Count|||{Multiplayer.session.players.Count}")
            .AppendLine($"Async time active|||{Multiplayer.GameComp.asyncTime}")
            .AppendLine($"Multifaction active|||{Multiplayer.GameComp.multifaction}")
            .AppendLine($"Map Count|||{Find.Maps?.Count.ToStringSafe()}")
            .AppendLine("\n###CPU Info###")
            .AppendLine($"Processor Name|||{SystemInfo.processorType}")
            .AppendLine($"Processor Speed (MHz)|||{SystemInfo.processorFrequency}")
            .AppendLine($"Thread Count|||{SystemInfo.processorCount}")
            .AppendLine("\n###GPU Info###")
            .AppendLine($"GPU Family|||{SystemInfo.graphicsDeviceVendor}")
            .AppendLine($"GPU Type|||{SystemInfo.graphicsDeviceType}")
            .AppendLine($"GPU Name|||{SystemInfo.graphicsDeviceName}")
            .AppendLine($"GPU VRAM|||{SystemInfo.graphicsMemorySize}")
            .AppendLine("\n###RAM Info###")
            .AppendLine($"Physical Memory Present|||{SystemInfo.systemMemorySize}")
            .AppendLine("\n###OS Info###")
            .AppendLine($"OS Type|||{SystemInfo.operatingSystemFamily}")
            .AppendLine($"OS Name and Version|||{SystemInfo.operatingSystem}");

        return desyncInfo.ToString();
    }

    private static string FindFileNameForNextDesyncFile()
    {
        const string FilePrefix = "Desync-";
        const string FileExtension = ".zip";

        //Find all current existing desync zips
        var files = new DirectoryInfo(Multiplayer.DesyncsDir).GetFiles($"{FilePrefix}*{FileExtension}");

        const int MaxFiles = 10;

        //Delete any pushing us over the limit, and reserve room for one more
        if (files.Length > MaxFiles - 1)
            files.OrderByDescending(f => f.LastWriteTime).Skip(MaxFiles - 1).Do(DeleteFileSilent);

        //Find the current max desync number
        int max = 0;
        foreach (var f in files)
            if (int.TryParse(
                    f.Name.Substring(FilePrefix.Length, f.Name.Length - FilePrefix.Length - FileExtension.Length),
                    out int result) && result > max)
                max = result;

        return $"{FilePrefix}{max + 1:00}";
    }

    private static Task<FileInfo> SaveReplayIfApplicable()
    {
        if (!Multiplayer.settings.includeReplayInDesync) return Task.FromCanceled<FileInfo>(new CancellationToken(true));

        try
        {
            var tmp = new FileInfo(Path.Combine(Multiplayer.DesyncsDir, "desync-replay.tmp.zip"));
            if (tmp.Exists) tmp.Delete();
            Replay.ForSaving(tmp).WriteData(Multiplayer.session.dataSnapshot);
            return Task.FromResult(tmp);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to save replay for desync: {e}");
            return Task.FromException<FileInfo>(e);
        }
    }

    private static void DeleteFileSilent(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch (IOException)
        {
        }
    }

    public record HostInfo([CanBeNull] string Traces, [CanBeNull] string JittedMethods);
}
