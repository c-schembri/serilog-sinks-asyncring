extern alias upstream;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace Serilog.Sinks.Async.Tests;

// Checks that the public API is exactly the same as Serilog.Sinks.Async's, so this package can
// replace it without source changes.
public class ApiParityTests
{
    private static readonly Assembly Upstream = typeof(upstream::Serilog.LoggerConfigurationAsyncExtensions).Assembly;
    private static readonly Assembly Ours = typeof(LoggerConfigurationAsyncExtensions).Assembly;

    [Fact]
    public void PublicApiIsIdenticalToSerilogSinksAsync()
    {
        Assert.Equal(Describe(Upstream), Describe(Ours));
    }

    [Fact]
    public void ClsComplianceMatchesSerilogSinksAsync()
    {
        Assert.Equal(
            Upstream.GetCustomAttribute<CLSCompliantAttribute>()?.IsCompliant,
            Ours.GetCustomAttribute<CLSCompliantAttribute>()?.IsCompliant);
    }

    static string[] Describe(Assembly assembly) =>
        assembly.GetExportedTypes()
            .SelectMany(DescribeType)
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToArray();

    static IEnumerable<string> DescribeType(Type type)
    {
        yield return $"{Kind(type)} {type.FullName}";

        foreach (var implemented in type.GetInterfaces())
            yield return $"{type.FullName} : {implemented.FullName}";

        const BindingFlags declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var member in type.GetMembers(declared))
            yield return $"{type.FullName}::{DescribeMember(member)}";
    }

    static string Kind(Type type) =>
        type.IsInterface ? "interface" :
        type.IsValueType ? "struct" :
        type.IsAbstract && type.IsSealed ? "static class" :
        type.IsSealed ? "sealed class" : "class";

    static string DescribeMember(MemberInfo member) => member switch
    {
        MethodInfo method =>
            $"{(method.IsStatic ? "static " : "")}{method.ReturnType.FullName} {method.Name}" +
            $"({string.Join(", ", method.GetParameters().Select(DescribeParameter))})" +
            (method.IsDefined(typeof(ExtensionAttribute)) ? " [extension]" : ""),
        PropertyInfo property =>
            $"{property.PropertyType.FullName} {property.Name} {{{(property.CanRead ? " get;" : "")}{(property.CanWrite ? " set;" : "")} }}",
        ConstructorInfo constructor =>
            $".ctor({string.Join(", ", constructor.GetParameters().Select(DescribeParameter))})",
        _ => $"{member.MemberType} {member.Name}"
    };

    static string DescribeParameter(ParameterInfo parameter) =>
        $"{parameter.ParameterType.FullName} {parameter.Name}" +
        (parameter.IsOptional ? $" = {parameter.DefaultValue ?? "null"}" : "");
}
