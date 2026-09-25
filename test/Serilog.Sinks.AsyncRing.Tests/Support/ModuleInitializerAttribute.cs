#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    // TUnit's generated code registers the tests from a module initializer. .NET Framework runs module
    // initializers, but doesn't define the attribute the compiler looks for.
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute
    {
    }
}
#endif
