using System.Reflection;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class SlowTraitConventionTests
{
    // build/check-trx.py cannot see xunit traits in a trx, so the release-checks Slow job recognises its tests by a
    // method name ending in "_OnSlow". A Slow test without the suffix would run but not count towards --min, and a
    // suffixed test without the trait would never be selected by the Category=Slow filter.
    [Fact]
    public void EverySlowTest_HasTheTraitAndTheNameSuffix() // @M4-T16b merge-fix 7
    {
        var methods = typeof(SlowTraitConventionTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes<FactAttribute>().Any())
            .ToList();

        var slow = methods.Where(HasSlowTrait).ToList();
        var suffixed = methods.Where(m => m.Name.EndsWith("_OnSlow", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(slow);
        Assert.All(slow, m => Assert.EndsWith("_OnSlow", m.Name));
        Assert.All(suffixed, m => Assert.True(HasSlowTrait(m), $"{m.DeclaringType?.Name}.{m.Name} ends in _OnSlow but has no Category=Slow trait."));
    }

    // xunit v2's TraitAttribute exposes no properties, so read the constructor arguments. A class-level trait counts too,
    // because the CI filter would select every method of such a class.
    private static bool HasSlowTrait(MethodInfo method) =>
        IsSlow(method.CustomAttributes) || (method.DeclaringType is { } type && IsSlow(type.CustomAttributes));

    private static bool IsSlow(IEnumerable<CustomAttributeData> attributes) =>
        attributes.Any(a => a.AttributeType == typeof(TraitAttribute)
            && (string?)a.ConstructorArguments[0].Value == "Category"
            && (string?)a.ConstructorArguments[1].Value == "Slow");
}
