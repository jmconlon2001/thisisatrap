using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace MailTrap.Tests;

/// <summary>
/// MailTrap's core promise: mail is never delivered anywhere. These tests fail the build if the app
/// ever gains a way to send mail, by inspecting what the compiled assembly references.
/// </summary>
public sealed class NeverSendTests
{
    private static readonly Assembly App = typeof(MailTrap.MailStore).Assembly;

    [Fact]
    public void App_does_not_reference_an_smtp_client_library()
    {
        var refs = App.GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.DoesNotContain("MailKit", refs);
        Assert.DoesNotContain("System.Net.Mail", refs);
    }

    [Fact]
    public void App_does_not_use_any_type_from_System_Net_Mail()
    {
        using var stream = File.OpenRead(App.Location);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();

        var used = md.TypeReferences
            .Select(h => md.GetTypeReference(h))
            .Select(t => md.GetString(t.Namespace) + "." + md.GetString(t.Name))
            .Where(n => n.StartsWith("System.Net.Mail.", StringComparison.Ordinal) || n.StartsWith("MailKit.", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(used);
    }
}
