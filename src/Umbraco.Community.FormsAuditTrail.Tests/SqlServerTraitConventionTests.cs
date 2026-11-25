using System.Reflection;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class SqlServerTraitConventionTests
{
    // build/check-trx.py cannot see xunit traits in a trx, so it recognises SQL Server tests by a method name ending
    // in "OnSqlServer". This keeps that name, the trait the CI filter selects on, and the attribute in step.
    [Fact]
    public void EverySqlServerFact_HasTheTraitAndTheNameSuffix()
    {
        var methods = typeof(SqlServerTraitConventionTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<SqlServerFactAttribute>() is not null)
            .ToList();

        Assert.NotEmpty(methods);
        foreach (var method in methods)
        {
            Assert.EndsWith("OnSqlServer", method.Name);
            // xunit v2's TraitAttribute exposes no properties, so read the constructor arguments.
            Assert.Contains(
                method.CustomAttributes.Where(a => a.AttributeType == typeof(TraitAttribute)),
                a => (string?)a.ConstructorArguments[0].Value == "Category" && (string?)a.ConstructorArguments[1].Value == "SqlServer");
        }
    }
}
