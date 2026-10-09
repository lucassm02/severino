using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Severino.Helper;

namespace Severino.Tests.Helper;

public sealed class PipeAccessTests
{
    private static readonly SecurityIdentifier User = new("S-1-5-21-1111111111-2222222222-3333333333-1001");

    [Fact]
    public void Acl_allows_only_system_and_the_configured_user_and_denies_network()
    {
        var rules = PipeAccess.CreateSecurity(User)
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Select(r => (Sid: (SecurityIdentifier)r.IdentityReference, r.AccessControlType, r.PipeAccessRights))
            .ToList();

        Assert.Equal(3, rules.Count);
        Assert.Contains(rules, r => r.Sid.IsWellKnown(WellKnownSidType.NetworkSid) && r.AccessControlType == AccessControlType.Deny);
        Assert.Contains(rules, r => r.Sid.IsWellKnown(WellKnownSidType.LocalSystemSid) && r.AccessControlType == AccessControlType.Allow);

        var user = Assert.Single(rules, r => r.Sid == User);
        Assert.Equal(AccessControlType.Allow, user.AccessControlType);
        Assert.False(user.PipeAccessRights.HasFlag(PipeAccessRights.ChangePermissions));
        Assert.False(user.PipeAccessRights.HasFlag(PipeAccessRights.TakeOwnership));
    }

    [Fact]
    public void Acl_does_not_inherit()
    {
        Assert.True(PipeAccess.CreateSecurity(User).AreAccessRulesProtected);
    }
}
