using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Benchmark.Config;
using Benchmark.Util;

namespace Benchmark.Batch;

/// <summary>Keep the private benchmark tree unreadable to the separate elevated sandbox identity during this batch.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SandboxReadBoundary(
    string recoveryPath, IReadOnlyList<SandboxReadBoundary.SavedAcl> originals) : IDisposable
{
    private const AccessControlSections Access = AccessControlSections.Access;
    internal sealed record SavedAcl(string Path, string Descriptor);

    public static SandboxReadBoundary Protect(string harness, string evidence)
    {
        SecurityIdentifier group;
        try
        {
            group = (SecurityIdentifier)new NTAccount(Environment.MachineName, "CodexSandboxUsers").Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            throw new BenchmarkException("The Codex elevated sandbox group is missing. Complete Windows worker setup first.");
        }
        string recoveryPath = Path.Combine(evidence, "isolation-recovery.json");
        var originals = new[] { harness, evidence }.Select(path =>
        {
            var directory = new DirectoryInfo(path);
            return new SavedAcl(directory.FullName, directory.GetAccessControl(Access).GetSecurityDescriptorSddlForm(Access));
        }).ToList();
        var boundary = new SandboxReadBoundary(recoveryPath, originals);
        Json.Write(recoveryPath, originals);
        try
        {
            foreach (var saved in originals)
            {
                var directory = new DirectoryInfo(saved.Path);
                var acl = directory.GetAccessControl(Access);
                // Public feeds have their own ACLs. Deny directory listing and file
                // reads here while allowing traversal to a separately shared feed.
                acl.AddAccessRule(new FileSystemAccessRule(group, FileSystemRights.ReadData,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny));
                directory.SetAccessControl(acl);
            }
            return boundary;
        }
        catch
        {
            boundary.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        var failures = new List<string>();
        foreach (var saved in originals.Reverse())
        {
            try
            {
                var directory = new DirectoryInfo(saved.Path);
                var acl = directory.GetAccessControl(Access);
                acl.SetSecurityDescriptorSddlForm(saved.Descriptor, Access);
                directory.SetAccessControl(acl);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{saved.Path}: {error.Message}");
            }
        }
        if (failures.Count > 0)
            throw new BenchmarkException($"Could not restore benchmark read permissions. Original ACLs are in {recoveryPath}. {string.Join(" ", failures)}");
        File.Delete(recoveryPath);
    }
}
