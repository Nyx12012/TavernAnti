namespace TavernAnti.Core;

/// <summary>The escalation ladder, in order. Config "enforcement" names the highest step allowed.</summary>
public enum Enforcement
{
    Log,
    Alert,
    Kick,
    Ban,
}
