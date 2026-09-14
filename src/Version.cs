using System.Reflection;

[assembly: AssemblyTitle("Codex Usage Sentinel")]
[assembly: AssemblyDescription("Private Telegram notifications for Codex usage limits")]
[assembly: AssemblyVersion(CodexUsageSentinel.BuildInfo.Version+".0")]
[assembly: AssemblyFileVersion(CodexUsageSentinel.BuildInfo.Version+".0")]
[assembly: AssemblyInformationalVersion(CodexUsageSentinel.BuildInfo.Version)]

namespace CodexUsageSentinel {
    public static class BuildInfo { public const string Version="1.6.0"; }
}
