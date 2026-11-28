namespace Autopilot.Core.Gate;

/// <summary>How much Autopilot may decide on its own. Ordered: a higher value means more autonomy.</summary>
public enum AutonomyLevel
{
    /// <summary>Shadow mode: Autopilot records what it would have decided; the person decides.</summary>
    ObserveOnly = 0,
}

/// <summary>The kinds of decision that have an autonomy level. Only the gate verdict for now.</summary>
public enum AutonomyDecisionType
{
    GateVerdict = 0,
}

public static class Autonomy
{
    /// <summary>The level of a decision type until someone raises it: observe-only.</summary>
    public static AutonomyLevel DefaultLevel(AutonomyDecisionType decisionType) => decisionType switch
    {
        AutonomyDecisionType.GateVerdict => AutonomyLevel.ObserveOnly,
        _ => throw new ArgumentOutOfRangeException(nameof(decisionType), decisionType, "Unsupported decision type."),
    };

    /// <summary>All levels, lowest autonomy first.</summary>
    public static IReadOnlyList<AutonomyLevel> Ladder { get; } = Enum.GetValues<AutonomyLevel>().OrderBy(l => (int)l).ToArray();
}
