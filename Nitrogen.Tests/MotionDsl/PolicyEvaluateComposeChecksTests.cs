using Xunit;
using static Nitrogen.Tests.PolicyValueChecksTests;

namespace Nitrogen.Tests;

/// <summary>PV0008 and compose's checks on hand-written documents (issue 241, Plan 3). Every case also runs through the hand pipeline.</summary>
public class PolicyEvaluateComposeChecksTests
{
    static string Edit(string source, string edits)
    {
        if (edits.Length == 0) return source;
        foreach (string edit in edits.Split(" ;; "))
        {
            if (edit.StartsWith('-')) { source = PolicyStructureChecksTests.Without(source, edit[1..]); continue; }
            // " ;; " strips the space after a trailing " -> ", so split at " ->" and drop one space.
            int arrow = edit.IndexOf(" ->", StringComparison.Ordinal);
            string replace = edit[(arrow + 3)..];
            if (replace.StartsWith(' ')) replace = replace[1..];
            source = Mutate(source, edit[..arrow], replace.Replace("\\n", "\n"));
        }
        return source;
    }

    [Theory]
    [InlineData("R", "start reference report -> start standing report", "PV0008")]
    [InlineData("R", "start reference report -> start spawn report", "")]
    [InlineData("R", "report success } -> report falls }", "PV0008")]
    [InlineData("R", "episodes 4 -> episodes 4 seeds 2", "PV0008")]
    [InlineData("R", "episodes 4 -> episodes 0", "PV0008")]
    [InlineData("R", "-perturb { ;; report success } -> report success perturb on }", "PV0008")]
    [InlineData("R", "report success } -> report success perturb on }", "")]
    [InlineData("R", "report success } -> report success randomize { pose_jitter 0.1 } }", "PV0008")]
    [InlineData("R", "episodes 4 -> seeds 4 ;; report success -> scenarios cruise report falls", "PV0008 PV0008")]
    [InlineData("S", "episodes 4 -> seeds 4 ;; report success -> scenarios cruise report falls", "")]
    [InlineData("S", "report success -> scenarios cruise report falls", "PV0008")]
    [InlineData("S", "episodes 4 -> episodes 0 ;; report success -> scenarios cruise report falls", "PV0008")]
    [InlineData("S", "episodes 4 -> seeds 4 ;; report success -> scenarios cruise report success", "PV0008")]
    public void Evaluate_against_the_goal(string base_, string edits, string codes) =>
        Expect(codes, Edit(base_ == "R" ? Reference : Stepper, edits));

    internal const string Compose = """
        compose locomote {
            requires scene "s.scene"
            walk "walk.policy"
            getup "getup.policy" when supine sitting
            getup "roll.policy" when prone onside
            commands {
                forward { v_forward 0.6 }
            }
            rules {
                stand_to_walk 1.0
                hand_over 0.5
                settle_cap 1.5
                retries 3
            }
        }
        """;

    [Theory]
    [InlineData("", "")]
    [InlineData("prone onside -> prone supine", "PV0009")]
    [InlineData("stand_to_walk 1.0 -> stand_to_walk 0", "PV0009")]
    [InlineData("retries 3 -> retries -1", "PV0009")]
    [InlineData("retries 3 -> retries 2.5", "PV0002")]
    [InlineData("v_forward 0.6 -> v_forward 1e999", "PV0009")]
    [InlineData("walk \"walk.policy\" -> ", "PV0001")]
    [InlineData("requires scene \"s.scene\" -> ", "PV0001")]
    [InlineData("getup \"getup.policy\" when supine sitting -> ;; getup \"roll.policy\" when prone onside -> ", "PV0001")]
    [InlineData("settle_cap 1.5 -> settle_cap 1.5\\n    }\\n    rules {\\n        settle_cap 0", "PV0009")]
    [InlineData("settle_cap 1.5 -> settle_cap 0\\n    }\\n    rules {\\n        settle_cap 1.5", "")]
    public void Compose_documents(string edits, string codes) => Expect(codes, Edit(Compose, edits), "c.compose");
}
