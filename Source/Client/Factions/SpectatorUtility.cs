namespace Multiplayer.Client;

using RimWorld;
using Verse;

public static class SpectatorUtility
{
    public const string SpectatorFactionName = "Spectator";

    public static bool IsSpectator =>
        Multiplayer.Client != null && IsSpectatorFaction(Multiplayer.RealPlayerFaction);

    public static bool IsSpectatorFaction(Faction faction)
    {
        if (faction == null) return false;

        var spectatorRef = Multiplayer.WorldComp?.spectatorFaction;
        if (spectatorRef != null && faction == spectatorRef)
            return true;

        return faction.Name == SpectatorFactionName;
    }

    public static bool IsSpectatorPawn(Pawn pawn)
    {
        return pawn != null && IsSpectatorFaction(pawn.Faction);
    }
}