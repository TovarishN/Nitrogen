namespace Nitrogen.MotionDsl;

/// <summary>The .motion files of the Gravity asset store ($GRAVITY_ASSETS, default ~/work/GravityAssets).</summary>
public static class MotionCorpus
{
    public static string Root
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("GRAVITY_ASSETS");
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "work", "GravityAssets")
                : configured;
        }
    }

    /// <summary>Every .motion file under the root, variants included, in ordinal path order; empty when there is no asset store.</summary>
    public static IReadOnlyList<string> Files() => Find("*.motion");

    /// <summary>Every .skill file under the root, variants included, in ordinal path order.</summary>
    public static IReadOnlyList<string> SkillFiles() => Find("*.skill");

    /// <summary>Every .policy file under the root, in ordinal path order.</summary>
    public static IReadOnlyList<string> PolicyFiles() => Find("*.policy");

    /// <summary>Every .compose file under the root, in ordinal path order.</summary>
    public static IReadOnlyList<string> ComposeFiles() => Find("*.compose");

    static IReadOnlyList<string> Find(string pattern) =>
        Directory.Exists(Root)
            ? Directory.GetFiles(Root, pattern, SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToArray()
            : [];
}
