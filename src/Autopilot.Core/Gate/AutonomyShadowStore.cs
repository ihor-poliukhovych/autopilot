using System.Globalization;

namespace Autopilot.Core.Gate;

/// <summary>One shadow record: Autopilot's would-be verdict next to the person's.</summary>
public sealed record ShadowRecord(
    long Id,
    string ProjectKey,
    string TaskOrRun,
    AutonomyDecisionType DecisionType,
    string AutopilotVerdict,
    string PersonVerdict,
    DateTimeOffset At);

/// <summary>Writes and reads shadow records in the workspace database. It never affects a gate verdict.</summary>
public sealed class AutonomyShadowStore(Workspace.Workspace workspace)
{
    public ShadowRecord Record(string projectKey, string taskOrRun, AutonomyDecisionType decisionType, string autopilotVerdict, string personVerdict, DateTimeOffset? at = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskOrRun);
        ArgumentException.ThrowIfNullOrWhiteSpace(autopilotVerdict);
        ArgumentException.ThrowIfNullOrWhiteSpace(personVerdict);
        if (decisionType != AutonomyDecisionType.GateVerdict)
        {
            throw new ArgumentOutOfRangeException(nameof(decisionType), decisionType, "Unsupported decision type.");
        }

        var time = at ?? DateTimeOffset.UtcNow;
        using var connection = workspace.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO shadow_records (project_key, task_or_run, decision_type, autopilot_verdict, person_verdict, at) VALUES ($p, $t, $d, $a, $v, $at); SELECT last_insert_rowid()";
        command.Parameters.AddWithValue("$p", projectKey);
        command.Parameters.AddWithValue("$t", taskOrRun);
        command.Parameters.AddWithValue("$d", decisionType.ToString());
        command.Parameters.AddWithValue("$a", autopilotVerdict);
        command.Parameters.AddWithValue("$v", personVerdict);
        command.Parameters.AddWithValue("$at", time.ToString("o", CultureInfo.InvariantCulture));
        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return new ShadowRecord(id, projectKey, taskOrRun, decisionType, autopilotVerdict, personVerdict, time);
    }

    /// <summary>The records of a project for a decision type, oldest first.</summary>
    public IReadOnlyList<ShadowRecord> List(string projectKey, AutonomyDecisionType decisionType = AutonomyDecisionType.GateVerdict)
    {
        using var connection = workspace.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, project_key, task_or_run, decision_type, autopilot_verdict, person_verdict, at FROM shadow_records WHERE project_key = $p AND decision_type = $d ORDER BY id";
        command.Parameters.AddWithValue("$p", projectKey);
        command.Parameters.AddWithValue("$d", decisionType.ToString());
        using var reader = command.ExecuteReader();
        var list = new List<ShadowRecord>();
        while (reader.Read())
        {
            list.Add(new ShadowRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                Enum.Parse<AutonomyDecisionType>(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
        }

        return list;
    }
}
