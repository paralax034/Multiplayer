namespace Multiplayer.Client;

using RimWorld;
using Verse;

/// <summary>
/// Provides deterministic helper methods to identify the dedicated multiplayer spectator faction and its pawns.
/// </summary>
public static class SpectatorUtility
{
    /// <summary>
    /// Reserved internal faction name for non-participating multiplayer observers.
    /// </summary>
    public const string SpectatorFactionName = "Spectator";

    /// <summary>
    /// Evaluates whether the currently active local player session belongs to the spectator faction.
    /// </summary>
    public static bool IsSpectator =>
        Multiplayer.Client != null && IsSpectatorFaction(Multiplayer.RealPlayerFaction);

    /// <summary>
    /// Validates if a faction matches the spectator faction reference or reserved name, guarded against uninitialized world state.
    /// </summary>
    /// <param name="faction">The faction instance to test.</param>
    /// <returns>True if the faction represents the spectator faction; otherwise, false.</returns>
    public static bool IsSpectatorFaction(Faction faction)
    {
        if (faction == null) return false;

        // Check instance equality first when the world component reference is loaded
        var spectatorRef = Multiplayer.WorldComp?.spectatorFaction;
        if (spectatorRef != null && faction == spectatorRef)
            return true;

        // Fall back to identifier comparison during early initialization or reload boundaries
        return faction.Name == SpectatorFactionName;
    }

    /// <summary>
    /// Determines whether a pawn belongs directly to the spectator faction to suppress AI, ticking, and rendering.
    /// </summary>
    /// <param name="pawn">The pawn to evaluate.</param>
    /// <returns>True if the pawn is owned by the spectator faction; otherwise, false.</returns>
    public static bool IsSpectatorPawn(Pawn pawn)
    {
        return pawn != null && IsSpectatorFaction(pawn.Faction);
    }
}