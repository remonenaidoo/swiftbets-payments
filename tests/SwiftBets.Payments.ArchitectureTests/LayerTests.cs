using System.Reflection;
using NetArchTest.Rules;

namespace SwiftBets.Payments.ArchitectureTests;

public sealed class LayerTests
{
    private static readonly Assembly Domain = typeof(SwiftBets.Payments.Domain.Deposit).Assembly;
    private static readonly Assembly Application = typeof(SwiftBets.Payments.Application.ApplicationRegistration).Assembly;

    [Fact]
    public void Domain_depends_on_nothing_else_in_the_solution() =>
        Types.InAssembly(Domain).ShouldNot().HaveDependencyOnAny("SwiftBets.Payments.Application", "SwiftBets.Payments.Infrastructure", "SwiftBets.BuildingBlocks", "Microsoft.AspNetCore", "Dapper")
            .GetResult().IsSuccessful.ShouldBeTrue();

    [Fact]
    public void Application_does_not_depend_on_infrastructure_or_wire_formats() =>
        Types.InAssembly(Application).ShouldNot().HaveDependencyOnAny(
                "SwiftBets.Payments.Infrastructure", "SwiftBets.Contracts", "Dapper", "Microsoft.Data.SqlClient", "Confluent.Kafka", "Grpc", "System.Net.Http")
            .GetResult().IsSuccessful.ShouldBeTrue();

    [Fact]
    public void Domain_assembly_references_no_other_project() =>
        Domain.GetReferencedAssemblies().Select(a => a.Name).ShouldNotContain(n => n!.StartsWith("SwiftBets.", StringComparison.Ordinal));
}
